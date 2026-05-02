namespace RinhaFraud;

using System;
using System.Buffers;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

internal static class KestrelServer
{
    private static readonly ReadOnlyMemory<byte> ReadyBody = "OK"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> NotFoundBody = "not found"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> DefaultBody = "{\"approved\":true,\"fraud_score\":0.0}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> Approved00Body = "{\"approved\":true,\"fraud_score\":0.0}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> Approved02Body = "{\"approved\":true,\"fraud_score\":0.2}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> Approved04Body = "{\"approved\":true,\"fraud_score\":0.4}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> Denied06Body = "{\"approved\":false,\"fraud_score\":0.6}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> Denied08Body = "{\"approved\":false,\"fraud_score\":0.8}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> Denied10Body = "{\"approved\":false,\"fraud_score\":1.0}"u8.ToArray();

    public static void Serve()
    {
        var bindAddress = Environment.GetEnvironmentVariable("BIND_ADDR") ?? "0.0.0.0:8080";
        var indexPath = Environment.GetEnvironmentVariable("INDEX_PATH") ?? "/app/data/references.idx";
        var searchParams = SearchParams.FromEnvironment();
        var endpoint = ParseEndpoint(bindAddress);
        var minThreads = EnvInt("TP_MIN_THREADS", 0);

        using var index = BinaryIndex.Open(indexPath);
        if (EnvBool("PREFETCH_INDEX", true))
        {
            var checksum = index.Prefault();
            Console.Error.WriteLine($"prefetched index pages, checksum={checksum}");
        }

        if (minThreads > 0)
        {
            ThreadPool.SetMinThreads(minThreads, minThreads);
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(endpoint.Address, endpoint.Port, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
            options.Limits.MaxConcurrentConnections = 4096;
            options.Limits.MaxRequestBodySize = Constants.MaxRequestBytes;
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.MinResponseDataRate = null;
        });

        var app = builder.Build();

        Console.Error.WriteLine(
            $"serving on {bindAddress}, server_mode=kestrel, tp_min_threads={minThreads}, index={indexPath}, early_candidates={searchParams.EarlyCandidates}, min_candidates={searchParams.MinCandidates}, max_candidates={searchParams.MaxCandidates}, flat={searchParams.Flat}, profile_fastpath={searchParams.ProfileFastPath}, profile_min_count={searchParams.ProfileMinCount}, exact_fallback={searchParams.ExactFallback}, risky_fallback_refs={index.RiskyFallbackCount}");

        app.Run(context => HandleRequestAsync(context, index, searchParams));

        app.Run();
    }

    private static async Task HandleRequestAsync(HttpContext context, BinaryIndex index, SearchParams searchParams)
    {
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/ready")
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength = ReadyBody.Length;
            await context.Response.BodyWriter.WriteAsync(ReadyBody, context.RequestAborted);
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.Path != "/fraud-score")
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength = NotFoundBody.Length;
            await context.Response.BodyWriter.WriteAsync(NotFoundBody, context.RequestAborted);
            return;
        }

        byte[]? rented = null;
        try
        {
            var expectedLength = (int)Math.Clamp(context.Request.ContentLength ?? 0L, 1L, Constants.MaxRequestBytes);
            var readResult = await context.Request.BodyReader.ReadAtLeastAsync(expectedLength, context.RequestAborted);
            var buffer = readResult.Buffer;
            if (buffer.Length > Constants.MaxRequestBytes)
            {
                await WriteJsonAsync(context, DefaultBody);
                return;
            }

            ReadOnlySpan<byte> body;
            if (buffer.IsSingleSegment)
            {
                body = buffer.FirstSpan;
            }
            else
            {
                rented = ArrayPool<byte>.Shared.Rent((int)buffer.Length);
                buffer.CopyTo(rented);
                body = rented.AsSpan(0, (int)buffer.Length);
            }

            var responseBody = Classify(body, index, searchParams);
            context.Request.BodyReader.AdvanceTo(buffer.End);
            await WriteJsonAsync(context, responseBody);
        }
        catch
        {
            await WriteJsonAsync(context, DefaultBody);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static ReadOnlyMemory<byte> Classify(ReadOnlySpan<byte> body, BinaryIndex index, SearchParams searchParams)
    {
        if (!PayloadParser.TryParse(body, out var payload))
        {
            return DefaultBody;
        }

        Span<short> query = stackalloc short[Constants.Dim];
        Vectorizer.Vectorize(payload, query);
        var fraudCount = index.ClassifyFraudCount(query, searchParams);
        return fraudCount switch
        {
            <= 0 => Approved00Body,
            1 => Approved02Body,
            2 => Approved04Body,
            3 => Denied06Body,
            4 => Denied08Body,
            _ => Denied10Body
        };
    }

    private static Task WriteJsonAsync(HttpContext context, ReadOnlyMemory<byte> body)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        return context.Response.BodyWriter.WriteAsync(body, context.RequestAborted).AsTask();
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
}
