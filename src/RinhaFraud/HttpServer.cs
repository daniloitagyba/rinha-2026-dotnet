namespace RinhaFraud;

using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;

internal static class HttpServer
{
    private static ReadOnlySpan<byte> ReadyResponse => "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nConnection: close\r\nContent-Length: 2\r\n\r\nOK"u8;
    private static ReadOnlySpan<byte> NotFoundResponse => "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 9\r\n\r\nnot found"u8;
    private static ReadOnlySpan<byte> DefaultResponse => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: 35\r\n\r\n{\"approved\":true,\"fraud_score\":0.0}"u8;
    private static ReadOnlySpan<byte> Approved00Response => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: 35\r\n\r\n{\"approved\":true,\"fraud_score\":0.0}"u8;
    private static ReadOnlySpan<byte> Approved02Response => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: 35\r\n\r\n{\"approved\":true,\"fraud_score\":0.2}"u8;
    private static ReadOnlySpan<byte> Approved04Response => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: 35\r\n\r\n{\"approved\":true,\"fraud_score\":0.4}"u8;
    private static ReadOnlySpan<byte> Denied06Response => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: 36\r\n\r\n{\"approved\":false,\"fraud_score\":0.6}"u8;
    private static ReadOnlySpan<byte> Denied08Response => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: 36\r\n\r\n{\"approved\":false,\"fraud_score\":0.8}"u8;
    private static ReadOnlySpan<byte> Denied10Response => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: 36\r\n\r\n{\"approved\":false,\"fraud_score\":1.0}"u8;

    public static void Serve()
    {
        var bindAddress = Environment.GetEnvironmentVariable("BIND_ADDR") ?? "0.0.0.0:8080";
        var indexPath = Environment.GetEnvironmentVariable("INDEX_PATH") ?? "/app/data/references.idx";
        var workerCount = Math.Max(1, EnvInt("WORKERS", 1));
        var searchParams = SearchParams.FromEnvironment();

        using var index = BinaryIndex.Open(indexPath);
        if (EnvBool("PREFETCH_INDEX", true))
        {
            var checksum = index.Prefault();
            Console.Error.WriteLine($"prefetched index pages, checksum={checksum}");
        }

        var endpoint = ParseEndpoint(bindAddress);
        var listener = new TcpListener(endpoint);
        listener.Server.NoDelay = true;
        listener.Start(4096);

        Console.Error.WriteLine(
            $"serving on {bindAddress}, index={indexPath}, workers={workerCount}, early_candidates={searchParams.EarlyCandidates}, min_candidates={searchParams.MinCandidates}, max_candidates={searchParams.MaxCandidates}, flat={searchParams.Flat}, profile_fastpath={searchParams.ProfileFastPath}, profile_min_count={searchParams.ProfileMinCount}");

        for (var i = 0; i < workerCount; i++)
        {
            var thread = new Thread(() => AcceptLoop(listener, index, searchParams))
            {
                IsBackground = false,
                Name = $"rinha-worker-{i}"
            };
            thread.Start();
        }

        Thread.Sleep(Timeout.Infinite);
    }

    private static void AcceptLoop(TcpListener listener, BinaryIndex index, SearchParams searchParams)
    {
        while (true)
        {
            Socket? socket = null;
            try
            {
                socket = listener.AcceptSocket();
                socket.NoDelay = true;
                socket.ReceiveTimeout = 3000;
                socket.SendTimeout = 3000;
                HandleConnection(socket, index, searchParams);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"connection error: {ex.Message}");
            }
            finally
            {
                try
                {
                    socket?.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private static void HandleConnection(Socket socket, BinaryIndex index, SearchParams searchParams)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Constants.MaxRequestBytes);
        var used = 0;
        try
        {
            while (true)
            {
                if (used >= buffer.Length)
                {
                    Send(socket, DefaultResponse);
                    return;
                }

                if (RequestComplete(buffer.AsSpan(0, used), out _, out _))
                {
                    break;
                }

                var read = socket.Receive(buffer.AsSpan(used, buffer.Length - used));
                if (read <= 0)
                {
                    return;
                }

                used += read;
            }

            HandleRequest(socket, buffer.AsSpan(0, used), index, searchParams);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void HandleRequest(Socket socket, ReadOnlySpan<byte> request, BinaryIndex index, SearchParams searchParams)
    {
        if (request.StartsWith("GET /ready "u8) || request.StartsWith("GET /ready?"u8))
        {
            Send(socket, ReadyResponse);
            return;
        }

        if (!request.StartsWith("POST /fraud-score "u8) && !request.StartsWith("POST /fraud-score?"u8))
        {
            Send(socket, NotFoundResponse);
            return;
        }

        try
        {
            if (!RequestComplete(request, out var headerEnd, out var contentLength))
            {
                Send(socket, DefaultResponse);
                return;
            }

            var bodyStart = headerEnd + 4;
            var bodyEnd = Math.Min(bodyStart + contentLength, request.Length);
            var body = request[bodyStart..bodyEnd];
            if (!PayloadParser.TryParse(body, out var payload))
            {
                Send(socket, DefaultResponse);
                return;
            }

            Span<short> query = stackalloc short[Constants.Dim];
            Vectorizer.Vectorize(payload, query);
            var fraudCount = index.ClassifyFraudCount(query, searchParams);
            SendDecision(socket, fraudCount);
        }
        catch
        {
            Send(socket, DefaultResponse);
        }
    }

    private static bool RequestComplete(ReadOnlySpan<byte> bytes, out int headerEnd, out int contentLength)
    {
        headerEnd = IndexOf(bytes, "\r\n\r\n"u8);
        contentLength = 0;
        if (headerEnd < 0)
        {
            return false;
        }

        contentLength = ContentLength(bytes[..(headerEnd + 4)]);
        return bytes.Length >= headerEnd + 4 + contentLength;
    }

    private static int ContentLength(ReadOnlySpan<byte> headers)
    {
        var pos = 0;
        while (pos < headers.Length)
        {
            var lineEnd = IndexOf(headers[pos..], "\r\n"u8);
            if (lineEnd < 0)
            {
                return 0;
            }

            var line = headers.Slice(pos, lineEnd);
            if (AsciiStartsWithIgnoreCase(line, "content-length:"u8))
            {
                return ParsePositiveInt(line[15..]);
            }

            pos += lineEnd + 2;
        }

        return 0;
    }

    private static int ParsePositiveInt(ReadOnlySpan<byte> bytes)
    {
        var value = 0;
        var seen = false;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b is (byte)' ' or (byte)'\t')
            {
                if (seen)
                {
                    break;
                }

                continue;
            }

            var d = b - (byte)'0';
            if ((uint)d > 9)
            {
                break;
            }

            seen = true;
            value = value * 10 + d;
        }

        return value;
    }

    private static void SendDecision(Socket socket, int fraudCount)
    {
        switch (fraudCount)
        {
            case <= 0:
                Send(socket, Approved00Response);
                break;
            case 1:
                Send(socket, Approved02Response);
                break;
            case 2:
                Send(socket, Approved04Response);
                break;
            case 3:
                Send(socket, Denied06Response);
                break;
            case 4:
                Send(socket, Denied08Response);
                break;
            default:
                Send(socket, Denied10Response);
                break;
        }
    }

    private static void Send(Socket socket, ReadOnlySpan<byte> response)
    {
        while (!response.IsEmpty)
        {
            var sent = socket.Send(response);
            if (sent <= 0)
            {
                return;
            }

            response = response[sent..];
        }
    }

    private static IPEndPoint ParseEndpoint(string value)
    {
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
        {
            throw new InvalidOperationException($"invalid BIND_ADDR: {value}");
        }

        var host = value[..colon];
        var port = int.Parse(value[(colon + 1)..]);
        var address = host == "*" ? IPAddress.Any : IPAddress.Parse(host);
        return new IPEndPoint(address, port);
    }

    private static bool EnvBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is null ? fallback : value is "1" or "true" or "TRUE" or "yes" or "YES";
    }

    private static int EnvInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
    }

    private static int IndexOf(ReadOnlySpan<byte> source, ReadOnlySpan<byte> needle)
    {
        return source.IndexOf(needle);
    }

    private static bool AsciiStartsWithIgnoreCase(ReadOnlySpan<byte> source, ReadOnlySpan<byte> prefix)
    {
        if (source.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            var left = source[i];
            var right = prefix[i];
            if (left >= (byte)'A' && left <= (byte)'Z')
            {
                left = (byte)(left + 32);
            }

            if (right >= (byte)'A' && right <= (byte)'Z')
            {
                right = (byte)(right + 32);
            }

            if (left != right)
            {
                return false;
            }
        }

        return true;
    }
}
