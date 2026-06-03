namespace RinhaFraud;

using System;
using System.Buffers;
using System.Diagnostics;
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
    private static readonly byte[] WarmupPayloadBytes = "{\"id\":\"warmup\",\"transaction\":{\"amount\":9505.97,\"installments\":10,\"requested_at\":\"2026-03-14T05:15:12Z\"},\"customer\":{\"avg_amount\":81.28,\"tx_count_24h\":20,\"known_merchants\":[\"MERC-008\",\"MERC-007\",\"MERC-005\"]},\"merchant\":{\"id\":\"MERC-068\",\"mcc\":\"7802\",\"avg_amount\":54.86},\"terminal\":{\"is_online\":false,\"card_present\":true,\"km_from_home\":952.27},\"last_transaction\":null}"u8.ToArray();
    private static ReadOnlySpan<byte> ReadyResponse => ReadyResponseBytes;
    private static ReadOnlySpan<byte> NotFoundResponse => NotFoundResponseBytes;
    private static ReadOnlySpan<byte> DefaultResponse => DefaultResponseBytes;
    private static ReadOnlySpan<byte> Approved00Response => Approved00ResponseBytes;
    private static ReadOnlySpan<byte> Approved02Response => Approved02ResponseBytes;
    private static ReadOnlySpan<byte> Approved04Response => Approved04ResponseBytes;
    private static ReadOnlySpan<byte> Denied06Response => Denied06ResponseBytes;
    private static ReadOnlySpan<byte> Denied08Response => Denied08ResponseBytes;
    private static ReadOnlySpan<byte> Denied10Response => Denied10ResponseBytes;
    private static readonly bool AssumeJsonBodyStart = EnvBool("ASSUME_JSON_BODY_START", false);
    private static readonly bool AssumeBodyComplete = EnvBool("ASSUME_BODY_COMPLETE", false);
    private static readonly bool AssumeFraudScorePath = EnvBool("ASSUME_FRAUD_SCORE_PATH", false);
    private static readonly bool FdControlPrebuffer = EnvBool("FD_CONTROL_PREBUFFER", false);
    private static readonly bool FdDedicatedThreads = EnvBool("FD_DEDICATED_THREADS", false);
    private static readonly bool FdEpoll = EnvBool("FD_EPOLL", false);
    private static readonly bool FdEpollSlab = EnvBool("FD_EPOLL_SLAB", false);
    private static readonly int FdEpollSlabClients = Math.Max(64, EnvInt("FD_EPOLL_SLAB_CLIENTS", 512));
    private static readonly int FdEpollSlabControls = Math.Max(2, EnvInt("FD_EPOLL_SLAB_CONTROLS", 8));
    private static readonly int FdEpollTimeoutMs = EnvInt("FD_EPOLL_TIMEOUT_MS", -1);
    private static readonly int FdEpollTimeoutUs = EnvInt("FD_EPOLL_TIMEOUT_US", 0);
    private static readonly int FdEpollSpinUs = Math.Max(0, EnvInt("FD_EPOLL_SPIN_US", 0));
    private static readonly long FdEpollSpinTicks = FdEpollSpinUs <= 0
        ? 0
        : Math.Max(1, (long)FdEpollSpinUs * Stopwatch.Frequency / 1_000_000);
    private static readonly bool FdPreRead = EnvBool("FD_PRE_READ", false);
    private static readonly bool ThreadPoolPreferLocal = EnvBool("TP_PREFER_LOCAL", false);
    private static readonly int FastPathCanaryRequests = Math.Max(0, EnvInt("FASTPATH_CANARY_REQUESTS", 0));
    private static readonly int FastPathCanaryInterval = Math.Max(0, EnvInt("FASTPATH_CANARY_INTERVAL", 0));
    private static long fastPathCanarySeen;
    private static int fastPathsDisabled;
    private static int epollPwait2Disabled;
    private const ulong FdEpollSlabListenerToken = ulong.MaxValue;
    private const ulong FdEpollSlabControlTag = 1UL << 63;
    private const ulong FdEpollSlabClientTag = 1UL << 62;
    private const ulong FdEpollSlabIndexMask = 0xFFFF_FFFFUL;

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
        if (!string.IsNullOrWhiteSpace(index.ReferenceGzipSha256) ||
            !string.IsNullOrWhiteSpace(index.ReferenceJsonSha256))
        {
            Console.Error.WriteLine(
                $"index references_gzip_sha256={index.ReferenceGzipSha256}, references_json_sha256={index.ReferenceJsonSha256}, fastpath_reference_allowed={index.FastPathReferenceAllowed}");
        }

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

        if (EnvBool("MLOCK_CURRENT", false))
        {
            var rc = mlockall(MclCurrent);
            Console.Error.WriteLine(rc == 0
                ? "mlockall current pages ok"
                : $"mlockall current pages failed errno={Marshal.GetLastPInvokeError()}");
        }

        var configuredMinThreads = ApplyThreadPoolTuning(minThreads, minIoThreads, maxThreads, maxIoThreads);
        PrewarmThreadPool(configuredMinThreads);
        PrewarmClassifier(index, searchParams);

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

    private static int ApplyThreadPoolTuning(int minThreads, int minIoThreads, int maxThreads, int maxIoThreads)
    {
        if (minThreads <= 0 && minIoThreads <= 0 && maxThreads <= 0 && maxIoThreads <= 0)
        {
            ThreadPool.GetMinThreads(out var defaultMinWorker, out _);
            return defaultMinWorker;
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
        return targetMinWorker;
    }

    private static void PrewarmThreadPool(int minThreads)
    {
        var target = EnvInt("TP_PREWARM", 0);
        if (target <= 0)
        {
            return;
        }

        target = Math.Min(target, Math.Max(1, minThreads));
        using var started = new CountdownEvent(target);
        using var release = new ManualResetEventSlim(false);

        for (var i = 0; i < target; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static state =>
                {
                    var tuple = ((CountdownEvent Started, ManualResetEventSlim Release))state!;
                    tuple.Started.Signal();
                    tuple.Release.Wait();
                },
                (started, release),
                preferLocal: false);
        }

        _ = started.Wait(TimeSpan.FromMilliseconds(750));
        release.Set();
        Console.Error.WriteLine($"threadpool prewarm requested={target}, started={target - started.CurrentCount}");
    }

    private static void PrewarmClassifier(BinaryIndex index, SearchParams searchParams)
    {
        var target = EnvInt("CLASSIFIER_PREWARM", 0);
        if (target <= 0)
        {
            return;
        }

        Span<short> query = stackalloc short[Constants.PaddedDim];
        if (!QueryBuilder.TryBuildQuery(WarmupPayloadBytes, query))
        {
            return;
        }

        var activeParams = ActiveSearchParams(searchParams);
        var safeParams = activeParams.WithoutFastPaths();
        for (var i = 0; i < target; i++)
        {
            _ = index.TryClassifyFraudCountJson(WarmupPayloadBytes, activeParams, out _);
            _ = index.ClassifyFraudCount(query, activeParams);
            if (activeParams.UsesFastPaths)
            {
                _ = index.ClassifyFraudCount(query, safeParams);
            }
        }

        Console.Error.WriteLine($"classifier prewarm requested={target}");
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
        var receiverCount = Math.Max(1, EnvInt("FD_RECEIVERS", workerCount));
        var rawFd = EnvBool("FD_RAW", false);
        Console.Error.WriteLine(
            $"serving fd control on {controlPath}, receivers={receiverCount}, raw_fd={rawFd}, fd_epoll={FdEpoll}, fd_epoll_slab={FdEpollSlab}, fd_epoll_timeout_ms={FdEpollTimeoutMs}, fd_epoll_timeout_us={FdEpollTimeoutUs}, fd_epoll_spin_us={FdEpollSpinUs}, fd_dedicated_threads={FdDedicatedThreads}, index={Environment.GetEnvironmentVariable("INDEX_PATH")}, keep_alive_requests={keepAliveRequests}, keep_alive_idle_ms={keepAliveIdleMs}, risky_fallback_refs={index.RiskyFallbackCount}");

        using var listener = CreateUnixListener(controlPath);
        if (rawFd && FdEpoll)
        {
            for (var i = 0; i < receiverCount; i++)
            {
                var thread = new Thread(() =>
                    {
                        if (FdEpollSlab)
                        {
                            RunFdEpollSlabLoop(listener, index, searchParams, keepAliveRequests);
                        }
                        else
                        {
                            RunFdEpollLoop(listener, index, searchParams, keepAliveRequests);
                        }
                    })
                {
                    IsBackground = false,
                    Name = $"rinha-fd-epoll-{i}"
                };
                thread.Start();
            }

            Thread.Sleep(Timeout.Infinite);
            return;
        }

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
            var initialBuffer = rawFd && FdControlPrebuffer
                ? ArrayPool<byte>.Shared.Rent(Constants.MaxRequestBytes)
                : null;
            var fd = ReceiveSocketFd(acceptedControl, initialBuffer, out var initialLength);
            if (fd < 0)
            {
                if (initialBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(initialBuffer);
                }

                return;
            }

            if (rawFd)
            {
                if (initialBuffer is null && FdPreRead)
                {
                    initialBuffer = ArrayPool<byte>.Shared.Rent(Constants.MaxRequestBytes);
                    var read = ReceiveDontWait(fd, initialBuffer, out var wouldBlock);
                    if (read > 0)
                    {
                        initialLength = read;
                    }
                    else if (read == 0 || !wouldBlock)
                    {
                        ArrayPool<byte>.Shared.Return(initialBuffer);
                        CloseFd(fd);
                        continue;
                    }
                }

                if (FdDedicatedThreads)
                {
                    var thread = new Thread(
                        RunFdDedicatedConnection,
                        Math.Max(64 * 1024, EnvInt("FD_THREAD_STACK_KB", 128) * 1024))
                    {
                        IsBackground = true,
                        Name = "rinha-fd-client"
                    };
                    thread.Start(new FdConnectionWork(fd, index, searchParams, keepAliveRequests, initialBuffer, initialLength));
                    continue;
                }

                QueueFdToThreadPool(new FdConnectionWork(fd, index, searchParams, keepAliveRequests, initialBuffer, initialLength));
                continue;
            }

            if (initialBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(initialBuffer);
            }

            Socket? socket = null;
            try
            {
                socket = new Socket(new SafeSocketHandle((IntPtr)fd, ownsHandle: true));
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
                while (!RequestComplete(buffer[..used], out headerEnd, out contentLength))
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

    private static void HandleConnection(int fd, BinaryIndex index, SearchParams searchParams, int keepAliveRequests, byte[]? initialBuffer = null, int initialLength = 0)
    {
        var buffer = initialBuffer ?? ArrayPool<byte>.Shared.Rent(Constants.MaxRequestBytes);
        try
        {
            var used = Math.Clamp(initialLength, 0, buffer.Length);
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

    private static unsafe void RunFdEpollLoop(Socket listener, BinaryIndex index, SearchParams searchParams, int keepAliveRequests)
    {
        var listenerFd = (int)listener.SafeHandle.DangerousGetHandle();
        SetNonBlocking(listenerFd);

        var epollFd = epoll_create1(EpollCloexec);
        if (epollFd < 0)
        {
            Console.Error.WriteLine($"epoll_create1 failed errno={Marshal.GetLastPInvokeError()}");
            return;
        }

        var listenerState = new FdEpollState(FdEpollStateKind.Listener, listenerFd, null);
        listenerState.Handle = GCHandle.Alloc(listenerState);
        if (EpollAdd(epollFd, listenerState) < 0)
        {
            Console.Error.WriteLine($"epoll add listener failed errno={Marshal.GetLastPInvokeError()}");
            listenerState.Handle.Free();
            CloseFd(epollFd);
            return;
        }

        Span<EpollEvent> events = stackalloc EpollEvent[128];
        fixed (EpollEvent* eventsPtr = events)
        {
            while (true)
            {
                var ready = EpollWait(epollFd, eventsPtr, events.Length);
                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == Eintr)
                    {
                        continue;
                    }

                    Console.Error.WriteLine($"epoll_wait failed errno={Marshal.GetLastPInvokeError()}");
                    break;
                }

                for (var i = 0; i < ready; i++)
                {
                    var handle = GCHandle.FromIntPtr(events[i].Data);
                    if (handle.Target is not FdEpollState state)
                    {
                        continue;
                    }

                    if (state.Kind == FdEpollStateKind.Listener)
                    {
                        AcceptFdEpollControls(epollFd, listenerFd);
                    }
                    else if (state.Kind == FdEpollStateKind.Control)
                    {
                        if ((events[i].Events & (EpollErr | EpollHup | EpollRdHup)) != 0)
                        {
                            CloseFdEpollState(epollFd, state);
                        }
                        else
                        {
                            ReceiveFdEpollClients(epollFd, state, index, searchParams, keepAliveRequests, FdControlPrebuffer);
                        }
                    }
                    else if ((events[i].Events & (EpollErr | EpollHup)) != 0)
                    {
                        CloseFdEpollState(epollFd, state);
                    }
                    else
                    {
                        HandleFdEpollClient(epollFd, state, index, searchParams, keepAliveRequests);
                    }
                }
            }
        }

        CloseFd(epollFd);
    }

    private static unsafe void RunFdEpollSlabLoop(Socket listener, BinaryIndex index, SearchParams searchParams, int keepAliveRequests)
    {
        var listenerFd = (int)listener.SafeHandle.DangerousGetHandle();
        SetNonBlocking(listenerFd);

        var epollFd = epoll_create1(EpollCloexec);
        if (epollFd < 0)
        {
            Console.Error.WriteLine($"epoll_create1 failed errno={Marshal.GetLastPInvokeError()}");
            return;
        }

        if (EpollAddRaw(epollFd, listenerFd, FdEpollSlabListenerToken) < 0)
        {
            Console.Error.WriteLine($"epoll slab add listener failed errno={Marshal.GetLastPInvokeError()}");
            CloseFd(epollFd);
            return;
        }

        var controlFds = new int[FdEpollSlabControls];
        var clientFds = new int[FdEpollSlabClients];
        var usedBytes = new int[FdEpollSlabClients];
        var handledRequests = new int[FdEpollSlabClients];
        var freeControls = new int[FdEpollSlabControls];
        var freeClients = new int[FdEpollSlabClients];
        Array.Fill(controlFds, -1);
        Array.Fill(clientFds, -1);
        var freeControlCount = InitializeFreeSlots(freeControls);
        var freeClientCount = InitializeFreeSlots(freeClients);
        var bufferBytes = checked((nint)(FdEpollSlabClients * Constants.MaxRequestBytes));
        var bufferBase = Marshal.AllocHGlobal(bufferBytes);

        Console.Error.WriteLine(
            $"fd epoll slab enabled controls={FdEpollSlabControls}, clients={FdEpollSlabClients}, buffer_bytes={bufferBytes}");

        try
        {
            Span<EpollEvent> events = stackalloc EpollEvent[128];
            fixed (EpollEvent* eventsPtr = events)
            {
                while (true)
                {
                    var ready = EpollWait(epollFd, eventsPtr, events.Length);
                    if (ready < 0)
                    {
                        if (Marshal.GetLastPInvokeError() == Eintr)
                        {
                            continue;
                        }

                        Console.Error.WriteLine($"epoll slab wait failed errno={Marshal.GetLastPInvokeError()}");
                        break;
                    }

                    for (var i = 0; i < ready; i++)
                    {
                        var token = (ulong)events[i].Data;
                        if (token == FdEpollSlabListenerToken)
                        {
                            AcceptFdEpollSlabControls(epollFd, listenerFd, controlFds, freeControls, ref freeControlCount);
                            continue;
                        }

                        if ((token & FdEpollSlabControlTag) != 0)
                        {
                            var slot = (int)(token & FdEpollSlabIndexMask);
                            if ((uint)slot >= (uint)controlFds.Length || controlFds[slot] < 0)
                            {
                                continue;
                            }

                            if ((events[i].Events & (EpollErr | EpollHup | EpollRdHup)) != 0)
                            {
                                CloseFdEpollSlabControl(epollFd, slot, controlFds, freeControls, ref freeControlCount);
                            }
                            else
                            {
                                ReceiveFdEpollSlabClients(
                                    epollFd,
                                    controlFds[slot],
                                    clientFds,
                                    usedBytes,
                                    handledRequests,
                                    freeClients,
                                    ref freeClientCount,
                                    bufferBase);
                            }

                            continue;
                        }

                        if ((token & FdEpollSlabClientTag) != 0)
                        {
                            var slot = (int)(token & FdEpollSlabIndexMask);
                            if ((uint)slot >= (uint)clientFds.Length || clientFds[slot] < 0)
                            {
                                continue;
                            }

                            if ((events[i].Events & (EpollErr | EpollHup)) != 0)
                            {
                                CloseFdEpollSlabClient(epollFd, slot, clientFds, usedBytes, handledRequests, freeClients, ref freeClientCount);
                            }
                            else
                            {
                                HandleFdEpollSlabClient(
                                    epollFd,
                                    slot,
                                    clientFds,
                                    usedBytes,
                                    handledRequests,
                                    freeClients,
                                    ref freeClientCount,
                                    bufferBase,
                                    index,
                                    searchParams,
                                    keepAliveRequests);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            for (var i = 0; i < controlFds.Length; i++)
            {
                if (controlFds[i] >= 0)
                {
                    CloseFd(controlFds[i]);
                }
            }

            for (var i = 0; i < clientFds.Length; i++)
            {
                if (clientFds[i] >= 0)
                {
                    CloseFd(clientFds[i]);
                }
            }

            Marshal.FreeHGlobal(bufferBase);
            CloseFd(epollFd);
        }
    }

    private static int InitializeFreeSlots(int[] freeSlots)
    {
        for (var i = 0; i < freeSlots.Length; i++)
        {
            freeSlots[i] = freeSlots.Length - 1 - i;
        }

        return freeSlots.Length;
    }

    private static bool TryRentSlot(int[] freeSlots, ref int freeCount, out int slot)
    {
        if (freeCount <= 0)
        {
            slot = -1;
            return false;
        }

        slot = freeSlots[--freeCount];
        return true;
    }

    private static void ReturnSlot(int[] freeSlots, ref int freeCount, int slot)
    {
        if ((uint)slot < (uint)freeSlots.Length && freeCount < freeSlots.Length)
        {
            freeSlots[freeCount++] = slot;
        }
    }

    private static void AcceptFdEpollSlabControls(
        int epollFd,
        int listenerFd,
        int[] controlFds,
        int[] freeControls,
        ref int freeControlCount)
    {
        while (true)
        {
            var controlFd = accept4(listenerFd, 0, 0, SockCloexec);
            if (controlFd < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == Eintr)
                {
                    continue;
                }

                return;
            }

            if (!TryRentSlot(freeControls, ref freeControlCount, out var slot))
            {
                CloseFd(controlFd);
                continue;
            }

            controlFds[slot] = controlFd;
            if (EpollAddRaw(epollFd, controlFd, FdEpollSlabControlTag | (uint)slot) < 0)
            {
                controlFds[slot] = -1;
                CloseFd(controlFd);
                ReturnSlot(freeControls, ref freeControlCount, slot);
            }
        }
    }

    private static unsafe void ReceiveFdEpollSlabClients(
        int epollFd,
        int controlFd,
        int[] clientFds,
        int[] usedBytes,
        int[] handledRequests,
        int[] freeClients,
        ref int freeClientCount,
        IntPtr bufferBase)
    {
        while (true)
        {
            var clientFd = ReceiveSocketFdRaw(controlFd, null, out var initialLength, MsgDontWait, out var wouldBlock);
            if (clientFd < 0)
            {
                return;
            }

            if (!TryRentSlot(freeClients, ref freeClientCount, out var slot))
            {
                CloseFd(clientFd);
                continue;
            }

            clientFds[slot] = clientFd;
            usedBytes[slot] = Math.Clamp(initialLength, 0, Constants.MaxRequestBytes);
            handledRequests[slot] = 0;

            if (EpollAddRaw(epollFd, clientFd, FdEpollSlabClientTag | (uint)slot) < 0)
            {
                clientFds[slot] = -1;
                usedBytes[slot] = 0;
                handledRequests[slot] = 0;
                CloseFd(clientFd);
                ReturnSlot(freeClients, ref freeClientCount, slot);
                continue;
            }
        }
    }

    private static unsafe void HandleFdEpollSlabClient(
        int epollFd,
        int slot,
        int[] clientFds,
        int[] usedBytes,
        int[] handledRequests,
        int[] freeClients,
        ref int freeClientCount,
        IntPtr bufferBase,
        BinaryIndex? index,
        SearchParams searchParams,
        int keepAliveRequests)
    {
        if ((uint)slot >= (uint)clientFds.Length || clientFds[slot] < 0 || index is null)
        {
            if ((uint)slot < (uint)clientFds.Length)
            {
                CloseFdEpollSlabClient(epollFd, slot, clientFds, usedBytes, handledRequests, freeClients, ref freeClientCount);
            }

            return;
        }

        var fd = clientFds[slot];
        var buffer = new Span<byte>((byte*)bufferBase + (slot * Constants.MaxRequestBytes), Constants.MaxRequestBytes);

        while (true)
        {
            var used = usedBytes[slot];
            if (RequestComplete(buffer[..used], out var headerEnd, out var contentLength))
            {
                Send(fd, SelectResponse(buffer[..used], headerEnd, contentLength, index, searchParams).Span);
                handledRequests[slot]++;
                if (keepAliveRequests > 0 && handledRequests[slot] >= keepAliveRequests)
                {
                    CloseFdEpollSlabClient(epollFd, slot, clientFds, usedBytes, handledRequests, freeClients, ref freeClientCount);
                    return;
                }

                usedBytes[slot] = 0;
                continue;
            }

            if (used >= buffer.Length)
            {
                Send(fd, DefaultResponse);
                CloseFdEpollSlabClient(epollFd, slot, clientFds, usedBytes, handledRequests, freeClients, ref freeClientCount);
                return;
            }

            var read = ReceiveDontWait(fd, buffer[used..], out var wouldBlock);
            if (read > 0)
            {
                usedBytes[slot] = used + read;
                continue;
            }

            if (wouldBlock)
            {
                return;
            }

            CloseFdEpollSlabClient(epollFd, slot, clientFds, usedBytes, handledRequests, freeClients, ref freeClientCount);
            return;
        }
    }

    private static void CloseFdEpollSlabControl(
        int epollFd,
        int slot,
        int[] controlFds,
        int[] freeControls,
        ref int freeControlCount)
    {
        if ((uint)slot >= (uint)controlFds.Length || controlFds[slot] < 0)
        {
            return;
        }

        var ev = default(EpollEvent);
        _ = epoll_ctl(epollFd, EpollCtlDel, controlFds[slot], ref ev);
        CloseFd(controlFds[slot]);
        controlFds[slot] = -1;
        ReturnSlot(freeControls, ref freeControlCount, slot);
    }

    private static void CloseFdEpollSlabClient(
        int epollFd,
        int slot,
        int[] clientFds,
        int[] usedBytes,
        int[] handledRequests,
        int[] freeClients,
        ref int freeClientCount)
    {
        if ((uint)slot >= (uint)clientFds.Length || clientFds[slot] < 0)
        {
            return;
        }

        var ev = default(EpollEvent);
        _ = epoll_ctl(epollFd, EpollCtlDel, clientFds[slot], ref ev);
        CloseFd(clientFds[slot]);
        clientFds[slot] = -1;
        usedBytes[slot] = 0;
        handledRequests[slot] = 0;
        ReturnSlot(freeClients, ref freeClientCount, slot);
    }

    private static void RunFdDedicatedConnection(object? state)
    {
        if (state is not FdConnectionWork work)
        {
            return;
        }

        try
        {
            HandleConnection(work.Fd, work.Index, work.SearchParams, work.KeepAliveRequests, work.InitialBuffer, work.InitialLength);
        }
        catch
        {
        }
    }

    private static void QueueFdToThreadPool(FdConnectionWork work)
    {
        ThreadPool.UnsafeQueueUserWorkItem(
            static (FdConnectionWork queued) =>
            {
                HandleConnection(queued.Fd, queued.Index, queued.SearchParams, queued.KeepAliveRequests, queued.InitialBuffer, queued.InitialLength);
            },
            work,
            preferLocal: ThreadPoolPreferLocal);
    }

    private static void AcceptFdEpollControls(int epollFd, int listenerFd)
    {
        while (true)
        {
            var controlFd = accept4(listenerFd, 0, 0, SockCloexec);
            if (controlFd < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == Eintr)
                {
                    continue;
                }

                return;
            }

            var state = new FdEpollState(FdEpollStateKind.Control, controlFd, null);
            state.Handle = GCHandle.Alloc(state);
            if (EpollAdd(epollFd, state) < 0)
            {
                CloseFd(controlFd);
                state.Handle.Free();
            }
        }
    }

    private static void ReceiveFdEpollClients(
        int epollFd,
        FdEpollState controlState,
        BinaryIndex index,
        SearchParams searchParams,
        int keepAliveRequests,
        bool prebuffer)
    {
        while (true)
        {
            var initialBuffer = prebuffer
                ? ArrayPool<byte>.Shared.Rent(Constants.MaxRequestBytes)
                : null;
            var clientFd = ReceiveSocketFdRaw(
                controlState.Fd,
                initialBuffer,
                out var initialLength,
                MsgDontWait,
                out var wouldBlock);
            if (clientFd < 0)
            {
                if (initialBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(initialBuffer);
                }

                if (wouldBlock)
                {
                    return;
                }

                CloseFdEpollState(epollFd, controlState);
                return;
            }

            var buffer = initialBuffer ?? ArrayPool<byte>.Shared.Rent(Constants.MaxRequestBytes);
            var state = new FdEpollState(FdEpollStateKind.Client, clientFd, buffer)
            {
                Used = Math.Clamp(initialLength, 0, buffer.Length)
            };
            state.Handle = GCHandle.Alloc(state);
            if (EpollAdd(epollFd, state) < 0)
            {
                CloseFd(clientFd);
                ArrayPool<byte>.Shared.Return(buffer);
                state.Handle.Free();
                continue;
            }

            if (state.Used > 0)
            {
                HandleFdEpollClient(epollFd, state, index, searchParams, keepAliveRequests);
            }
        }
    }

    private static void HandleFdEpollClient(
        int epollFd,
        FdEpollState state,
        BinaryIndex? index,
        SearchParams searchParams,
        int keepAliveRequests)
    {
        var buffer = state.Buffer;
        if (buffer is null || index is null)
        {
            CloseFdEpollState(epollFd, state);
            return;
        }

        while (true)
        {
            if (RequestComplete(buffer.AsSpan(0, state.Used), out var headerEnd, out var contentLength))
            {
                Send(state.Fd, SelectResponse(buffer, state.Used, headerEnd, contentLength, index, searchParams).Span);
                state.Handled++;
                if (keepAliveRequests > 0 && state.Handled >= keepAliveRequests)
                {
                    CloseFdEpollState(epollFd, state);
                    return;
                }

                state.Used = 0;
                continue;
            }

            if (state.Used >= buffer.Length)
            {
                Send(state.Fd, DefaultResponse);
                CloseFdEpollState(epollFd, state);
                return;
            }

            var read = ReceiveDontWait(state.Fd, buffer.AsSpan(state.Used, buffer.Length - state.Used), out var wouldBlock);
            if (read > 0)
            {
                state.Used += read;
                continue;
            }

            if (wouldBlock)
            {
                return;
            }

            CloseFdEpollState(epollFd, state);
            return;
        }
    }

    private static int EpollAdd(int epollFd, FdEpollState state)
    {
        var ev = new EpollEvent
        {
            Events = EpollIn | EpollErr | EpollHup | EpollRdHup,
            Data = GCHandle.ToIntPtr(state.Handle)
        };
        return epoll_ctl(epollFd, EpollCtlAdd, state.Fd, ref ev);
    }

    private static int EpollAddRaw(int epollFd, int fd, ulong token)
    {
        var ev = new EpollEvent
        {
            Events = EpollIn | EpollErr | EpollHup | EpollRdHup,
            Data = (nint)token
        };
        return epoll_ctl(epollFd, EpollCtlAdd, fd, ref ev);
    }

    private static unsafe int EpollWait(int epollFd, EpollEvent* events, int maxEvents)
    {
        if (FdEpollSpinTicks > 0)
        {
            var deadline = Stopwatch.GetTimestamp() + FdEpollSpinTicks;
            while (true)
            {
                var ready = epoll_wait(epollFd, events, maxEvents, 0);
                if (ready != 0)
                {
                    return ready;
                }

                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    break;
                }

                Thread.SpinWait(1);
            }
        }

        if (FdEpollTimeoutUs > 0 && Volatile.Read(ref epollPwait2Disabled) == 0)
        {
            var timeout = new Timespec
            {
                Seconds = FdEpollTimeoutUs / 1_000_000,
                Nanoseconds = (FdEpollTimeoutUs % 1_000_000) * 1_000
            };

            try
            {
                var ready = epoll_pwait2(epollFd, events, maxEvents, &timeout, 0);
                if (ready >= 0)
                {
                    return ready;
                }

                if (Marshal.GetLastPInvokeError() != Enosys)
                {
                    return ready;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }

            Volatile.Write(ref epollPwait2Disabled, 1);
        }

        return epoll_wait(epollFd, events, maxEvents, FdEpollTimeoutMs);
    }

    private static void CloseFdEpollState(int epollFd, FdEpollState state)
    {
        if (state.Kind != FdEpollStateKind.Listener)
        {
            var ev = default(EpollEvent);
            _ = epoll_ctl(epollFd, EpollCtlDel, state.Fd, ref ev);
            CloseFd(state.Fd);
        }

        if (state.Buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(state.Buffer);
            state.Buffer = null;
        }

        if (state.Handle.IsAllocated)
        {
            state.Handle.Free();
        }
    }

    private sealed class FdEpollState
    {
        public FdEpollState(FdEpollStateKind kind, int fd, byte[]? buffer)
        {
            Kind = kind;
            Fd = fd;
            Buffer = buffer;
        }

        public FdEpollStateKind Kind { get; }

        public int Fd { get; }

        public byte[]? Buffer { get; set; }

        public int Used { get; set; }

        public int Handled { get; set; }

        public GCHandle Handle { get; set; }
    }

    private enum FdEpollStateKind
    {
        Listener,
        Control,
        Client
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
        public FdConnectionWork(int fd, BinaryIndex index, SearchParams searchParams, int keepAliveRequests, byte[]? initialBuffer, int initialLength)
        {
            Fd = fd;
            Index = index;
            SearchParams = searchParams;
            KeepAliveRequests = keepAliveRequests;
            InitialBuffer = initialBuffer;
            InitialLength = initialLength;
        }

        public int Fd { get; }

        public BinaryIndex Index { get; }

        public SearchParams SearchParams { get; }

        public int KeepAliveRequests { get; }

        public byte[]? InitialBuffer { get; }

        public int InitialLength { get; }
    }

    [SkipLocalsInit]
    private static bool TryClassifyFraudCountJsonGuarded(
        BinaryIndex index,
        ReadOnlySpan<byte> body,
        in SearchParams searchParams,
        out int fraudCount)
    {
        var activeParams = ActiveSearchParams(searchParams);
        if (!index.TryClassifyFraudCountJson(body, activeParams, out fraudCount))
        {
            return false;
        }

        if (!ShouldRunFastPathCanary(activeParams))
        {
            return true;
        }

        Span<short> query = stackalloc short[Constants.PaddedDim];
        if (!QueryBuilder.TryBuildQuery(body, query))
        {
            return true;
        }

        fraudCount = CompareCanary(index, query, activeParams, fraudCount);
        return true;
    }

    [SkipLocalsInit]
    private static int ClassifyFraudCountGuarded(
        BinaryIndex index,
        ReadOnlySpan<short> query,
        in SearchParams searchParams)
    {
        var activeParams = ActiveSearchParams(searchParams);
        var fraudCount = index.ClassifyFraudCount(query, activeParams);
        return ShouldRunFastPathCanary(activeParams)
            ? CompareCanary(index, query, activeParams, fraudCount)
            : fraudCount;
    }

    private static SearchParams ActiveSearchParams(in SearchParams searchParams)
    {
        return Volatile.Read(ref fastPathsDisabled) == 0 || !searchParams.UsesFastPaths
            ? searchParams
            : searchParams.WithoutFastPaths();
    }

    private static bool ShouldRunFastPathCanary(in SearchParams searchParams)
    {
        if (!searchParams.UsesFastPaths ||
            Volatile.Read(ref fastPathsDisabled) != 0 ||
            (FastPathCanaryRequests == 0 && FastPathCanaryInterval == 0))
        {
            return false;
        }

        var seen = Interlocked.Increment(ref fastPathCanarySeen);
        return seen <= FastPathCanaryRequests ||
               (FastPathCanaryInterval > 0 && seen % FastPathCanaryInterval == 0);
    }

    private static int CompareCanary(
        BinaryIndex index,
        ReadOnlySpan<short> query,
        in SearchParams activeParams,
        int fastFraudCount)
    {
        var safeFraudCount = index.ClassifyFraudCount(query, activeParams.WithoutFastPaths());
        if (safeFraudCount == fastFraudCount)
        {
            return fastFraudCount;
        }

        if (Interlocked.Exchange(ref fastPathsDisabled, 1) == 0)
        {
            Console.Error.WriteLine(
                $"fast path disabled by canary: fast={fastFraudCount}, safe={safeFraudCount}, checked={Volatile.Read(ref fastPathCanarySeen)}");
        }

        return safeFraudCount;
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
            if (TryClassifyFraudCountJsonGuarded(index, body, searchParams, out var nativeFraudCount))
            {
                SendDecision(socket, nativeFraudCount);
                return;
            }

            Span<short> query = stackalloc short[Constants.PaddedDim];
            if (!QueryBuilder.TryBuildQuery(body, query))
            {
                Send(socket, DefaultResponse);
                return;
            }

            var fraudCount = ClassifyFraudCountGuarded(index, query, searchParams);
            SendDecision(socket, fraudCount);
        }
        catch
        {
            Send(socket, DefaultResponse);
        }
    }


    private static bool RequestComplete(ReadOnlySpan<byte> bytes, out int headerEnd, out int contentLength)
    {
        if (AssumeJsonBodyStart &&
            bytes.Length >= 18 &&
            bytes.StartsWith("POST /fraud-score"u8) &&
            (bytes[17] is (byte)' ' or (byte)'?'))
        {
            var bodyStart = bytes.IndexOf((byte)'{');
            if (bodyStart >= 0)
            {
                headerEnd = bodyStart - 4;
                contentLength = bytes.Length - bodyStart;
                return headerEnd >= 0;
            }
        }

        headerEnd = IndexOf(bytes, "\r\n\r\n"u8);
        contentLength = 0;
        if (headerEnd < 0)
        {
            return false;
        }

        if (AssumeBodyComplete &&
            bytes.StartsWith("POST /fraud-score"u8) &&
            bytes.Length >= 18 &&
            bytes[17] is (byte)' ' or (byte)'?')
        {
            contentLength = bytes.Length - headerEnd - 4;
            return contentLength >= 0;
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
        var contentLength = FastContentLength(headers);
        if (contentLength >= 0)
        {
            return contentLength;
        }

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

    private static int FastContentLength(ReadOnlySpan<byte> headers)
    {
        var pos = headers.IndexOf("\r\nContent-Length:"u8);
        if (pos >= 0)
        {
            return ParsePositiveInt(headers[(pos + 17)..]);
        }

        pos = headers.IndexOf("\r\ncontent-length:"u8);
        if (pos >= 0)
        {
            return ParsePositiveInt(headers[(pos + 17)..]);
        }

        if (headers.StartsWith("Content-Length:"u8))
        {
            return ParsePositiveInt(headers[15..]);
        }

        if (headers.StartsWith("content-length:"u8))
        {
            return ParsePositiveInt(headers[15..]);
        }

        return -1;
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
        return SelectResponse(buffer.AsSpan(0, used), headerEnd, contentLength, index, searchParams);
    }

    [SkipLocalsInit]
    private static ReadOnlyMemory<byte> SelectResponse(
        ReadOnlySpan<byte> request,
        int headerEnd,
        int contentLength,
        BinaryIndex index,
        SearchParams searchParams)
    {
        if (!AssumeFraudScorePath &&
            !request.StartsWith("POST /fraud-score "u8) &&
            !request.StartsWith("POST /fraud-score?"u8))
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
            if (TryClassifyFraudCountJsonGuarded(index, body, searchParams, out var nativeFraudCount))
            {
                return nativeFraudCount switch
                {
                    <= 0 => Approved00ResponseBytes,
                    1 => Approved02ResponseBytes,
                    2 => Approved04ResponseBytes,
                    3 => Denied06ResponseBytes,
                    4 => Denied08ResponseBytes,
                    _ => Denied10ResponseBytes
                };
            }

            Span<short> query = stackalloc short[Constants.PaddedDim];
            if (!QueryBuilder.TryBuildQuery(body, query))
            {
                return DefaultResponseBytes;
            }

            var fraudCount = ClassifyFraudCountGuarded(index, query, searchParams);
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

    private static unsafe int ReceiveDontWait(int fd, Span<byte> buffer, out bool wouldBlock)
    {
        wouldBlock = false;
        fixed (byte* ptr = buffer)
        {
            while (true)
            {
                var received = recv(fd, ptr, (nuint)buffer.Length, MsgDontWait);
                if (received >= 0)
                {
                    return (int)received;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error == Eintr)
                {
                    continue;
                }

                if (error == Eagain)
                {
                    wouldBlock = true;
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
        var socketType = EnvBool("FD_CONTROL_SEQPACKET", false) ? SocketType.Seqpacket : SocketType.Stream;
        var listener = new Socket(AddressFamily.Unix, socketType, ProtocolType.Unspecified);
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

    private static unsafe int ReceiveSocketFd(Socket control, byte[]? initialBuffer, out int initialLength)
    {
        var sockfd = (int)control.SafeHandle.DangerousGetHandle();
        return ReceiveSocketFdRaw(sockfd, initialBuffer, out initialLength, 0, out _);
    }

    private static unsafe int ReceiveSocketFdRaw(
        int sockfd,
        byte[]? initialBuffer,
        out int initialLength,
        int flags,
        out bool wouldBlock)
    {
        initialLength = 0;
        wouldBlock = false;
        byte data = 0;
        Span<byte> controlBuffer = stackalloc byte[24];

        fixed (byte* controlPtr = controlBuffer)
        fixed (byte* initialPtr = initialBuffer)
        {
            var iov = new IOVec
            {
                Base = initialBuffer is null ? &data : initialPtr,
                Len = initialBuffer is null ? 1 : (nuint)initialBuffer.Length
            };
            var msg = new MsgHdr
            {
                Iov = &iov,
                IovLen = 1,
                Control = controlPtr,
                ControlLen = (nuint)controlBuffer.Length
            };

            nint received;
            while (true)
            {
                received = recvmsg(sockfd, &msg, flags);
                if (received >= 0 || Marshal.GetLastPInvokeError() != Eintr)
                {
                    break;
                }
            }

            if (received <= 0)
            {
                if (received < 0 && Marshal.GetLastPInvokeError() == Eagain)
                {
                    wouldBlock = true;
                }

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

            if (initialBuffer is not null && !(received == 1 && initialBuffer[0] == 0))
            {
                initialLength = Math.Min((int)received, initialBuffer.Length);
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
    private const int Eagain = 11;
    private const int Enosys = 38;
    private const int FGetFl = 3;
    private const int FSetFl = 4;
    private const int MclCurrent = 1;
    private const int ONonBlock = 0x800;
    private const int SockCloexec = 0x80000;
    private const int EpollCloexec = 0x80000;
    private const int EpollCtlAdd = 1;
    private const int EpollCtlDel = 2;
    private const uint EpollIn = 0x001;
    private const uint EpollErr = 0x008;
    private const uint EpollHup = 0x010;
    private const uint EpollRdHup = 0x2000;
    private const int MsgDontWait = 0x40;
    private const int MsgNoSignal = 0x4000;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct EpollEvent
    {
        public uint Events;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

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

    [DllImport("libc", SetLastError = true)]
    private static extern int accept4(int sockfd, nint addr, nint addrlen, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int epoll_create1(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int epoll_ctl(int epfd, int op, int fd, ref EpollEvent ev);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int epoll_wait(int epfd, EpollEvent* events, int maxevents, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int epoll_pwait2(int epfd, EpollEvent* events, int maxevents, Timespec* timeout, nint sigmask);

    [DllImport("libc", SetLastError = true)]
    private static extern int mlockall(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    private static void SetNonBlocking(int fd)
    {
        var flags = fcntl(fd, FGetFl, 0);
        if (flags >= 0)
        {
            _ = fcntl(fd, FSetFl, flags | ONonBlock);
        }
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
