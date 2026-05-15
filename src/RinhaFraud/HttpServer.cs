namespace RinhaFraud;

using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Threading.Tasks;

internal static class HttpServer
{
    private static readonly byte[] ReadyResponseBytes = "HTTP/1.1 200 OK\r\nContent-Length:2\r\n\r\nOK"u8.ToArray();
    private static readonly byte[] NotFoundResponseBytes = "HTTP/1.1 404 Not Found\r\nContent-Length:9\r\n\r\nnot found"u8.ToArray();
    private static readonly byte[] DefaultResponseBytes = "HTTP/1.1 200 OK\r\nContent-Length:35\r\n\r\n{\"approved\":true,\"fraud_score\":0.0}"u8.ToArray();
    private static readonly byte[] Approved00ResponseBytes = "HTTP/1.1 200 OK\r\nContent-Length:35\r\n\r\n{\"approved\":true,\"fraud_score\":0.0}"u8.ToArray();
    private static readonly byte[] Approved02ResponseBytes = "HTTP/1.1 200 OK\r\nContent-Length:35\r\n\r\n{\"approved\":true,\"fraud_score\":0.2}"u8.ToArray();
    private static readonly byte[] Approved04ResponseBytes = "HTTP/1.1 200 OK\r\nContent-Length:35\r\n\r\n{\"approved\":true,\"fraud_score\":0.4}"u8.ToArray();
    private static readonly byte[] Denied06ResponseBytes = "HTTP/1.1 200 OK\r\nContent-Length:36\r\n\r\n{\"approved\":false,\"fraud_score\":0.6}"u8.ToArray();
    private static readonly byte[] Denied08ResponseBytes = "HTTP/1.1 200 OK\r\nContent-Length:36\r\n\r\n{\"approved\":false,\"fraud_score\":0.8}"u8.ToArray();
    private static readonly byte[] Denied10ResponseBytes = "HTTP/1.1 200 OK\r\nContent-Length:36\r\n\r\n{\"approved\":false,\"fraud_score\":1.0}"u8.ToArray();
    private static ReadOnlySpan<byte> ReadyResponse => ReadyResponseBytes;
    private static ReadOnlySpan<byte> NotFoundResponse => NotFoundResponseBytes;
    private static ReadOnlySpan<byte> DefaultResponse => DefaultResponseBytes;
    private static ReadOnlySpan<byte> Approved00Response => Approved00ResponseBytes;
    private static ReadOnlySpan<byte> Approved02Response => Approved02ResponseBytes;
    private static ReadOnlySpan<byte> Approved04Response => Approved04ResponseBytes;
    private static ReadOnlySpan<byte> Denied06Response => Denied06ResponseBytes;
    private static ReadOnlySpan<byte> Denied08Response => Denied08ResponseBytes;
    private static ReadOnlySpan<byte> Denied10Response => Denied10ResponseBytes;

    public static void Serve()
    {
        var bindAddress = Environment.GetEnvironmentVariable("BIND_ADDR") ?? "0.0.0.0:8080";
        var indexPath = Environment.GetEnvironmentVariable("INDEX_PATH") ?? "/app/data/references.idx";
        var workerCount = Math.Max(1, EnvInt("WORKERS", 1));
        var minThreads = EnvInt("TP_MIN_THREADS", 0);
        var minIoThreads = EnvInt("TP_MIN_IO_THREADS", minThreads);
        var maxThreads = EnvInt("TP_MAX_THREADS", 0);
        var maxIoThreads = EnvInt("TP_MAX_IO_THREADS", maxThreads);
        var keepAliveRequests = Math.Max(0, EnvInt("KEEP_ALIVE_REQUESTS", 0));
        var keepAliveIdleMs = Math.Max(100, EnvInt("KEEP_ALIVE_IDLE_MS", 5000));
        var searchParams = SearchParams.FromEnvironment();
        ApplyGcLatencyMode();

        using var index = BinaryIndex.Open(indexPath);
        if (EnvBool("INDEX_HUGEPAGES", false))
        {
            var advised = index.AdviseHugePages();
            Console.Error.WriteLine($"index hugepage madvise bytes={advised}");
        }

        if (EnvBool("PREFETCH_INDEX", true))
        {
            var checksum = index.Prefault();
            Console.Error.WriteLine($"prefetched index pages, checksum={checksum}");
        }

        ApplyThreadPoolTuning(minThreads, minIoThreads, maxThreads, maxIoThreads);

        var asyncMode = string.Equals(Environment.GetEnvironmentVariable("SERVER_MODE"), "raw-async", StringComparison.OrdinalIgnoreCase);
        if (bindAddress.StartsWith("fd:", StringComparison.Ordinal))
        {
            ServeFdPassing(bindAddress[3..], index, searchParams, workerCount, keepAliveRequests, keepAliveIdleMs);
            return;
        }

        using var listener = CreateListener(bindAddress);

        Console.Error.WriteLine(
            $"serving on {bindAddress}, server_mode={(asyncMode ? "raw-async" : "raw")}, tp_min_threads={minThreads}, tp_min_io_threads={minIoThreads}, tp_max_threads={maxThreads}, tp_max_io_threads={maxIoThreads}, index={indexPath}, workers={workerCount}, keep_alive_requests={keepAliveRequests}, keep_alive_idle_ms={keepAliveIdleMs}, early_candidates={searchParams.EarlyCandidates}, min_candidates={searchParams.MinCandidates}, max_candidates={searchParams.MaxCandidates}, flat={searchParams.Flat}, profile_fastpath={searchParams.ProfileFastPath}, profile_min_count={searchParams.ProfileMinCount}, profile_legit_min_count={searchParams.ProfileLegitMinCount}, profile_fraud_min_count={searchParams.ProfileFraudMinCount}, profile_dominant_fastpath={searchParams.ProfileDominantFastPath}, profile_dominant_min_count={searchParams.ProfileDominantMinCount}, profile_dominant_max_opposite={searchParams.ProfileDominantMaxOpposite}, exact_fallback={searchParams.ExactFallback}, risky_fallback_refs={index.RiskyFallbackCount}");

        for (var i = 0; i < workerCount; i++)
        {
            var thread = new Thread(
                asyncMode
                    ? () => AcceptLoopAsync(listener, index, searchParams, keepAliveRequests, keepAliveIdleMs).GetAwaiter().GetResult()
                    : () => AcceptLoop(listener, index, searchParams, keepAliveRequests, keepAliveIdleMs))
            {
                IsBackground = false,
                Name = $"rinha-worker-{i}"
            };
            thread.Start();
        }

        Thread.Sleep(Timeout.Infinite);
    }

    private static void ApplyGcLatencyMode()
    {
        var mode = Environment.GetEnvironmentVariable("GC_LATENCY_MODE");
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        if (mode.Equals("sustained-low-latency", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("sustainedlowlatency", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("sustained", StringComparison.OrdinalIgnoreCase))
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            Console.Error.WriteLine("gc latency mode=sustained-low-latency");
        }
    }

    private static void ApplyThreadPoolTuning(int minThreads, int minIoThreads, int maxThreads, int maxIoThreads)
    {
        if (minThreads <= 0 && minIoThreads <= 0 && maxThreads <= 0 && maxIoThreads <= 0)
        {
            return;
        }

        ThreadPool.GetMinThreads(out var currentMinWorker, out var currentMinIo);
        ThreadPool.GetMaxThreads(out var currentMaxWorker, out var currentMaxIo);

        var targetMinWorker = minThreads > 0 ? minThreads : currentMinWorker;
        var targetMinIo = minIoThreads > 0 ? minIoThreads : currentMinIo;
        var targetMaxWorker = maxThreads > 0 ? Math.Max(maxThreads, targetMinWorker) : currentMaxWorker;
        var targetMaxIo = maxIoThreads > 0 ? Math.Max(maxIoThreads, targetMinIo) : currentMaxIo;

        if (maxThreads > 0 || maxIoThreads > 0)
        {
            ThreadPool.SetMaxThreads(targetMaxWorker, targetMaxIo);
        }

        if (minThreads > 0 || minIoThreads > 0)
        {
            ThreadPool.SetMinThreads(targetMinWorker, targetMinIo);
        }

        Console.Error.WriteLine($"threadpool min={targetMinWorker}/{targetMinIo}, max={targetMaxWorker}/{targetMaxIo}");
    }

    private static async Task AcceptLoopAsync(Socket listener, BinaryIndex index, SearchParams searchParams, int keepAliveRequests, int keepAliveIdleMs)
    {
        while (true)
        {
            Socket? socket = null;
            try
            {
                socket = await listener.AcceptAsync();
                ConfigureAcceptedSocket(socket, keepAliveIdleMs);
                _ = HandleConnectionAsync(socket, index, searchParams, keepAliveRequests);
                socket = null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"async connection error: {ex.Message}");
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

    private static async Task HandleConnectionAsync(Socket socket, BinaryIndex index, SearchParams searchParams, int keepAliveRequests)
    {
        using var accepted = socket;
        var buffer = ArrayPool<byte>.Shared.Rent(Constants.MaxRequestBytes);
        try
        {
            var used = 0;
            var handled = 0;
            while (true)
            {
                int headerEnd;
                int contentLength;
                while (!RequestComplete(buffer, used, out headerEnd, out contentLength))
                {
                    if (used >= buffer.Length)
                    {
                        await SendAsync(accepted, DefaultResponseBytes);
                        return;
                    }

                    int read;
                    try
                    {
                        read = await accepted.ReceiveAsync(buffer.AsMemory(used, buffer.Length - used), SocketFlags.None);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        return;
                    }

                    if (read <= 0)
                    {
                        return;
                    }

                    used += read;
                }

                await SendAsync(accepted, SelectResponse(buffer, used, headerEnd, contentLength, index, searchParams));
                handled++;
                if (keepAliveRequests > 0 && handled >= keepAliveRequests)
                {
                    return;
                }

                used = 0;
            }
        }
        catch
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AcceptLoop(Socket listener, BinaryIndex index, SearchParams searchParams, int keepAliveRequests, int keepAliveIdleMs)
    {
        while (true)
        {
            Socket? socket = null;
            try
            {
                socket = listener.Accept();
                ConfigureAcceptedSocket(socket, keepAliveIdleMs);
                ThreadPool.UnsafeQueueUserWorkItem(
                    static (ConnectionWork work) =>
                    {
                        using var accepted = work.Socket;
                        try
                        {
                            HandleConnection(accepted, work.Index, work.SearchParams, work.KeepAliveRequests);
                        }
                        catch
                        {
                        }
                    },
                    new ConnectionWork(socket, index, searchParams, keepAliveRequests),
                    preferLocal: false);
                socket = null;
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

    private static void ServeFdPassing(
        string controlPath,
        BinaryIndex index,
        SearchParams searchParams,
        int workerCount,
        int keepAliveRequests,
        int keepAliveIdleMs)
    {
        using var listener = CreateUnixListener(controlPath);
        var receiverCount = Math.Max(1, EnvInt("FD_RECEIVERS", workerCount));
        var rawFd = EnvBool("FD_RAW", false);
        Console.Error.WriteLine(
            $"serving fd control on {controlPath}, receivers={receiverCount}, raw_fd={rawFd}, index={Environment.GetEnvironmentVariable("INDEX_PATH")}, keep_alive_requests={keepAliveRequests}, keep_alive_idle_ms={keepAliveIdleMs}, risky_fallback_refs={index.RiskyFallbackCount}");

        for (var i = 0; i < receiverCount; i++)
        {
            var thread = new Thread(() => AcceptFdControlLoop(listener, index, searchParams, keepAliveRequests, keepAliveIdleMs, rawFd))
            {
                IsBackground = false,
                Name = $"rinha-fd-receiver-{i}"
            };
            thread.Start();
        }

        Thread.Sleep(Timeout.Infinite);
    }

    private static void AcceptFdControlLoop(Socket listener, BinaryIndex index, SearchParams searchParams, int keepAliveRequests, int keepAliveIdleMs, bool rawFd)
    {
        while (true)
        {
            Socket? control = null;
            try
            {
                control = listener.Accept();
                var acceptedControl = control;
                var thread = new Thread(() => ReceiveFdLoop(acceptedControl, index, searchParams, keepAliveRequests, keepAliveIdleMs, rawFd))
                {
                    IsBackground = true,
                    Name = "rinha-fd-control"
                };
                thread.Start();
                control = null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"fd control accept error: {ex.Message}");
            }
            finally
            {
                try
                {
                    control?.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private static void ReceiveFdLoop(Socket control, BinaryIndex index, SearchParams searchParams, int keepAliveRequests, int keepAliveIdleMs, bool rawFd)
    {
        using var acceptedControl = control;
        while (true)
        {
            var fd = ReceiveSocketFd(acceptedControl);
            if (fd < 0)
            {
                return;
            }

            if (rawFd)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static (FdConnectionWork work) =>
                    {
                        HandleConnection(work.Fd, work.Index, work.SearchParams, work.KeepAliveRequests);
                    },
                    new FdConnectionWork(fd, index, searchParams, keepAliveRequests),
                    preferLocal: false);
                continue;
            }

            Socket? socket = null;
            try
            {
                socket = new Socket(new SafeSocketHandle((IntPtr)fd, ownsHandle: true));
                socket.Blocking = true;
                ConfigureAcceptedSocket(socket, keepAliveIdleMs);
                ThreadPool.UnsafeQueueUserWorkItem(
                    static (ConnectionWork work) =>
                    {
                        using var accepted = work.Socket;
                        try
                        {
                            HandleConnection(accepted, work.Index, work.SearchParams, work.KeepAliveRequests);
                        }
                        catch
                        {
                        }
                    },
                    new ConnectionWork(socket, index, searchParams, keepAliveRequests),
                    preferLocal: false);
                socket = null;
            }
            catch
            {
                if (socket is null)
                {
                    CloseFd(fd);
                }
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

    private static void HandleConnection(Socket socket, BinaryIndex index, SearchParams searchParams, int keepAliveRequests)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Constants.MaxRequestBytes);
        try
        {
            var used = 0;
            var handled = 0;
            while (true)
            {
                int headerEnd;
                int contentLength;
                while (!RequestComplete(buffer.AsSpan(0, used), out headerEnd, out contentLength))
                {
                    if (used >= buffer.Length)
                    {
                        Send(socket, DefaultResponse);
                        return;
                    }

                    int read;
                    try
                    {
                        read = socket.Receive(buffer.AsSpan(used, buffer.Length - used));
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        return;
                    }

                    if (read <= 0)
                    {
                        return;
                    }

                    used += read;
                }

                HandleRequest(socket, buffer.AsSpan(0, used), headerEnd, contentLength, index, searchParams);
                handled++;
                if (keepAliveRequests > 0 && handled >= keepAliveRequests)
                {
                    return;
                }

                used = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void HandleConnection(int fd, BinaryIndex index, SearchParams searchParams, int keepAliveRequests)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Constants.MaxRequestBytes);
        try
        {
            var used = 0;
            var handled = 0;
            while (true)
            {
                int headerEnd;
                int contentLength;
                while (!RequestComplete(buffer.AsSpan(0, used), out headerEnd, out contentLength))
                {
                    if (used >= buffer.Length)
                    {
                        Send(fd, DefaultResponse);
                        return;
                    }

                    var read = Receive(fd, buffer.AsSpan(used, buffer.Length - used));
                    if (read <= 0)
                    {
                        return;
                    }

                    used += read;
                }

                Send(fd, SelectResponse(buffer, used, headerEnd, contentLength, index, searchParams).Span);
                handled++;
                if (keepAliveRequests > 0 && handled >= keepAliveRequests)
                {
                    return;
                }

                used = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            CloseFd(fd);
        }
    }

    private readonly struct ConnectionWork
    {
        public ConnectionWork(Socket socket, BinaryIndex index, SearchParams searchParams, int keepAliveRequests)
        {
            Socket = socket;
            Index = index;
            SearchParams = searchParams;
            KeepAliveRequests = keepAliveRequests;
        }

        public Socket Socket { get; }

        public BinaryIndex Index { get; }

        public SearchParams SearchParams { get; }

        public int KeepAliveRequests { get; }
    }

    private readonly struct FdConnectionWork
    {
        public FdConnectionWork(int fd, BinaryIndex index, SearchParams searchParams, int keepAliveRequests)
        {
            Fd = fd;
            Index = index;
            SearchParams = searchParams;
            KeepAliveRequests = keepAliveRequests;
        }

        public int Fd { get; }

        public BinaryIndex Index { get; }

        public SearchParams SearchParams { get; }

        public int KeepAliveRequests { get; }
    }

    private static void HandleRequest(
        Socket socket,
        ReadOnlySpan<byte> request,
        int headerEnd,
        int contentLength,
        BinaryIndex index,
        SearchParams searchParams)
    {
        if (!request.StartsWith("POST /fraud-score "u8) && !request.StartsWith("POST /fraud-score?"u8))
        {
            if (request.StartsWith("GET /ready "u8) || request.StartsWith("GET /ready?"u8))
            {
                Send(socket, ReadyResponse);
                return;
            }

            Send(socket, NotFoundResponse);
            return;
        }

        try
        {
            var bodyStart = headerEnd + 4;
            var body = request.Slice(bodyStart, contentLength);
            Span<short> query = stackalloc short[Constants.PaddedDim];
            if (!QueryBuilder.TryBuildQuery(body, query))
            {
                Send(socket, DefaultResponse);
                return;
            }

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

    private static bool RequestComplete(byte[] buffer, int used, out int headerEnd, out int contentLength)
    {
        return RequestComplete(buffer.AsSpan(0, used), out headerEnd, out contentLength);
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

    [SkipLocalsInit]
    private static ReadOnlyMemory<byte> SelectResponse(
        byte[] buffer,
        int used,
        int headerEnd,
        int contentLength,
        BinaryIndex index,
        SearchParams searchParams)
    {
        var request = buffer.AsSpan(0, used);
        if (!request.StartsWith("POST /fraud-score "u8) && !request.StartsWith("POST /fraud-score?"u8))
        {
            if (request.StartsWith("GET /ready "u8) || request.StartsWith("GET /ready?"u8))
            {
                return ReadyResponseBytes;
            }

            return NotFoundResponseBytes;
        }

        try
        {
            var bodyStart = headerEnd + 4;
            var body = request.Slice(bodyStart, contentLength);
            Span<short> query = stackalloc short[Constants.PaddedDim];
            if (!QueryBuilder.TryBuildQuery(body, query))
            {
                return DefaultResponseBytes;
            }

            var fraudCount = index.ClassifyFraudCount(query, searchParams);
            return fraudCount switch
            {
                <= 0 => Approved00ResponseBytes,
                1 => Approved02ResponseBytes,
                2 => Approved04ResponseBytes,
                3 => Denied06ResponseBytes,
                4 => Denied08ResponseBytes,
                _ => Denied10ResponseBytes
            };
        }
        catch
        {
            return DefaultResponseBytes;
        }
    }

    private static void Send(Socket socket, ReadOnlySpan<byte> response)
    {
        while (!response.IsEmpty)
        {
            int sent;
            try
            {
                sent = socket.Send(response);
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (sent <= 0)
            {
                return;
            }

            response = response[sent..];
        }
    }

    private static unsafe int Receive(int fd, Span<byte> buffer)
    {
        fixed (byte* ptr = buffer)
        {
            while (true)
            {
                var received = recv(fd, ptr, (nuint)buffer.Length, 0);
                if (received >= 0)
                {
                    return (int)received;
                }

                if (Marshal.GetLastPInvokeError() == Eintr)
                {
                    continue;
                }

                return -1;
            }
        }
    }

    private static unsafe void Send(int fd, ReadOnlySpan<byte> response)
    {
        fixed (byte* ptr = response)
        {
            var offset = 0;
            while (offset < response.Length)
            {
                var sent = send(fd, ptr + offset, (nuint)(response.Length - offset), MsgNoSignal);
                if (sent > 0)
                {
                    offset += (int)sent;
                    continue;
                }

                if (sent < 0 && Marshal.GetLastPInvokeError() == Eintr)
                {
                    continue;
                }

                return;
            }
        }
    }

    private static async ValueTask SendAsync(Socket socket, ReadOnlyMemory<byte> response)
    {
        while (!response.IsEmpty)
        {
            int sent;
            try
            {
                sent = await socket.SendAsync(response, SocketFlags.None);
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (sent <= 0)
            {
                return;
            }

            response = response[sent..];
        }
    }

    private static Socket CreateListener(string bindAddress)
    {
        Socket listener;
        if (bindAddress.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
        {
            var path = bindAddress[5..];
            return CreateUnixListener(path);
        }
        else
        {
            var endpoint = ParseEndpoint(bindAddress);
            listener = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            listener.NoDelay = true;
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Bind(endpoint);
        }

        listener.Listen(4096);
        return listener;
    }

    private static Socket CreateUnixListener(string path)
    {
        TryDeleteUnixSocket(path);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        TrySetUnixSocketPermissions(path);
        listener.Listen(4096);
        return listener;
    }

    private static void ConfigureAcceptedSocket(Socket socket, int keepAliveIdleMs)
    {
        if (socket.AddressFamily == AddressFamily.InterNetwork || socket.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.NoDelay = true;
        }

        socket.ReceiveTimeout = keepAliveIdleMs;
        socket.SendTimeout = 3000;
    }

    private static void TryDeleteUnixSocket(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TrySetUnixSocketPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }
        catch
        {
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

    private static unsafe int ReceiveSocketFd(Socket control)
    {
        var sockfd = (int)control.SafeHandle.DangerousGetHandle();
        byte data = 0;
        Span<byte> controlBuffer = stackalloc byte[24];

        fixed (byte* controlPtr = controlBuffer)
        {
            var iov = new IOVec
            {
                Base = &data,
                Len = 1
            };
            var msg = new MsgHdr
            {
                Iov = &iov,
                IovLen = 1,
                Control = controlPtr,
                ControlLen = (nuint)controlBuffer.Length
            };

            var received = recvmsg(sockfd, &msg, 0);
            if (received <= 0)
            {
                return -1;
            }

            if (msg.ControlLen < 20)
            {
                return -1;
            }

            var cmsgLen = Unsafe.ReadUnaligned<nuint>(controlPtr);
            var cmsgLevel = Unsafe.ReadUnaligned<int>(controlPtr + 8);
            var cmsgType = Unsafe.ReadUnaligned<int>(controlPtr + 12);
            if (cmsgLen < 20 || cmsgLevel != SolSocket || cmsgType != ScmRights)
            {
                return -1;
            }

            return Unsafe.ReadUnaligned<int>(controlPtr + 16);
        }
    }

    private static void CloseFd(int fd)
    {
        if (fd >= 0)
        {
            _ = close(fd);
        }
    }

    private const int SolSocket = 1;
    private const int ScmRights = 1;
    private const int Eintr = 4;
    private const int MsgNoSignal = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct IOVec
    {
        public void* Base;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MsgHdr
    {
        public void* Name;
        public uint NameLen;
        public void* Iov;
        public nuint IovLen;
        public void* Control;
        public nuint ControlLen;
        public int Flags;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint recvmsg(int sockfd, MsgHdr* msg, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint recv(int sockfd, void* buffer, nuint length, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint send(int sockfd, void* buffer, nuint length, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

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
