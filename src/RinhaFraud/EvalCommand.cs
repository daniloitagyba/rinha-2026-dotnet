namespace RinhaFraud;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

internal static class EvalCommand
{
    public static void Run(string inputPath)
    {
        var indexPath = Environment.GetEnvironmentVariable("INDEX_PATH") ?? "data/references.idx";
        var limit = EnvInt("EVAL_LIMIT", int.MaxValue);
        var searchParams = SearchParams.FromEnvironment();
        var errorsPath = Environment.GetEnvironmentVariable("EVAL_ERRORS_PATH");
        var data = File.ReadAllBytes(inputPath);
        using var index = BinaryIndex.Open(indexPath);
        using var errorWriter = string.IsNullOrWhiteSpace(errorsPath)
            ? null
            : new StreamWriter(errorsPath, false, Encoding.UTF8, 1 << 16);

        var cursor = 0;
        var total = 0;
        var correct = 0;
        var fp = 0;
        var fn = 0;
        var parseErrors = 0;
        Span<int> fraudCountBuckets = stackalloc int[Constants.K + 1];
        var latencies = new List<long>(Math.Min(100_000, Math.Max(1024, limit)));
        var queryBuffer = new short[Constants.Dim];
        var started = Stopwatch.StartNew();

        while (total < limit)
        {
            var rest = data.AsSpan(cursor);
            var relativeRequestKey = rest.IndexOf("\"request\""u8);
            if (relativeRequestKey < 0)
            {
                break;
            }

            var requestKey = cursor + relativeRequestKey;
            if (!TryObjectAfterKey(data, requestKey, out var requestStart, out var requestEnd))
            {
                parseErrors++;
                cursor = requestKey + "\"request\""u8.Length;
                continue;
            }

            var expectedSearchStart = requestEnd + 1;
            var relativeExpected = data.AsSpan(expectedSearchStart).IndexOf("\"expected_approved\""u8);
            if (relativeExpected < 0)
            {
                throw new InvalidOperationException("missing expected_approved");
            }

            var expectedKey = expectedSearchStart + relativeExpected;
            if (!TryBoolAfterKey(data, expectedKey, out var expectedApproved))
            {
                throw new InvalidOperationException("bad expected_approved");
            }

            var itemStarted = Stopwatch.GetTimestamp();
            var approved = true;
            var fraudCount = 0;
            var request = data.AsSpan(requestStart, requestEnd - requestStart + 1);
            if (PayloadParser.TryParse(request, out var payload))
            {
                var query = queryBuffer.AsSpan();
                Vectorizer.Vectorize(payload, query);
                fraudCount = index.ClassifyFraudCount(query, searchParams);
                approved = fraudCount < 3;
            }
            else
            {
                parseErrors++;
            }

            fraudCountBuckets[Math.Clamp(fraudCount, 0, Constants.K)]++;
            latencies.Add(Stopwatch.GetTimestamp() - itemStarted);
            if (approved == expectedApproved)
            {
                correct++;
            }
            else if (approved)
            {
                fn++;
            }
            else
            {
                fp++;
            }

            if (approved != expectedApproved && errorWriter is not null)
            {
                errorWriter.Write("{\"expected_approved\":");
                errorWriter.Write(expectedApproved ? "true" : "false");
                errorWriter.Write(",\"approved\":");
                errorWriter.Write(approved ? "true" : "false");
                errorWriter.Write(",\"fraud_count\":");
                errorWriter.Write(fraudCount);
                errorWriter.Write(",\"request\":");
                errorWriter.Write(Encoding.UTF8.GetString(request));
                errorWriter.WriteLine("}");
            }

            total++;
            cursor = expectedKey + "\"expected_approved\""u8.Length;
        }

        started.Stop();
        latencies.Sort();
        var measured = latencies.Count;
        var p50 = Percentile(latencies, 0.50);
        var p95 = Percentile(latencies, 0.95);
        var p99 = Percentile(latencies, 0.99);
        var weightedErrors = fp + 3 * fn;
        var failureRate = total == 0 ? 0.0 : (fp + fn + parseErrors) / (double)total;
        var epsilon = total == 0 ? 0.0 : weightedErrors / (double)total;
        var detectionScore = DetectionScore(weightedErrors, failureRate, epsilon);
        var accuracy = total == 0 ? 0.0 : correct / (double)total;
        var throughput = started.Elapsed.TotalSeconds <= 0 ? 0.0 : total / started.Elapsed.TotalSeconds;

        Console.WriteLine($"index={indexPath}");
        Console.WriteLine(
            $"params early_candidates={searchParams.EarlyCandidates} min_candidates={searchParams.MinCandidates} max_candidates={searchParams.MaxCandidates} flat={searchParams.Flat} profile_fastpath={searchParams.ProfileFastPath} profile_min_count={searchParams.ProfileMinCount} exact_fallback={searchParams.ExactFallback}");
        Console.WriteLine($"total={total} measured={measured} correct={correct} accuracy={accuracy:F6}");
        Console.WriteLine($"fp={fp} fn={fn} parse_errors={parseErrors} weighted_errors={weightedErrors} failure_rate={failureRate:F6} score_det={detectionScore:F2}");
        Console.WriteLine($"elapsed_ms={started.ElapsedMilliseconds} throughput_per_s={throughput:F1}");
        Console.WriteLine($"classify_latency_ns p50={TicksToNs(p50)} p95={TicksToNs(p95)} p99={TicksToNs(p99)}");
        Console.WriteLine(
            $"fraud_count_buckets 0={fraudCountBuckets[0]} 1={fraudCountBuckets[1]} 2={fraudCountBuckets[2]} 3={fraudCountBuckets[3]} 4={fraudCountBuckets[4]} 5={fraudCountBuckets[5]}");
    }

    private static bool TryObjectAfterKey(ReadOnlySpan<byte> data, int keyPos, out int start, out int end)
    {
        start = 0;
        end = 0;
        var colonRelative = data[keyPos..].IndexOf((byte)':');
        if (colonRelative < 0)
        {
            return false;
        }

        var pos = keyPos + colonRelative + 1;
        while (pos < data.Length && IsWhiteSpace(data[pos]))
        {
            pos++;
        }

        if ((uint)pos >= (uint)data.Length || data[pos] != (byte)'{')
        {
            return false;
        }

        start = pos;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (; pos < data.Length; pos++)
        {
            var b = data[pos];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (b == (byte)'\\')
                {
                    escaped = true;
                }
                else if (b == (byte)'"')
                {
                    inString = false;
                }
            }
            else if (b == (byte)'"')
            {
                inString = true;
            }
            else if (b == (byte)'{')
            {
                depth++;
            }
            else if (b == (byte)'}')
            {
                depth--;
                if (depth == 0)
                {
                    end = pos;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryBoolAfterKey(ReadOnlySpan<byte> data, int keyPos, out bool value)
    {
        value = false;
        var colonRelative = data[keyPos..].IndexOf((byte)':');
        if (colonRelative < 0)
        {
            return false;
        }

        var pos = keyPos + colonRelative + 1;
        while (pos < data.Length && IsWhiteSpace(data[pos]))
        {
            pos++;
        }

        if (data[pos..].StartsWith("true"u8))
        {
            value = true;
            return true;
        }

        if (data[pos..].StartsWith("false"u8))
        {
            value = false;
            return true;
        }

        return false;
    }

    private static bool IsWhiteSpace(byte b)
    {
        return b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';
    }

    private static long Percentile(List<long> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Round((sorted.Count - 1) * percentile);
        return sorted[index];
    }

    private static long TicksToNs(long ticks)
    {
        return (long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency));
    }

    private static double DetectionScore(int weightedErrors, double failureRate, double epsilon)
    {
        if (failureRate > 0.15)
        {
            return -3000.0;
        }

        var safeEpsilon = Math.Max(epsilon, 0.001);
        return 1000.0 * Math.Log10(1.0 / safeEpsilon) - 300.0 * Math.Log10(1.0 + weightedErrors);
    }

    private static int EnvInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
    }
}
