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
        var dumpPath = Environment.GetEnvironmentVariable("EVAL_DUMP_PATH");
        var reportPath = Environment.GetEnvironmentVariable("EVAL_REPORT_PATH");
        var evalNativeJson = EnvBool("EVAL_NATIVE_JSON", false);
        var evalNativeJsonOnly = EnvBool("EVAL_NATIVE_JSON_ONLY", false);
        var diagnosticsEnabled = EnvBool("EVAL_DIAGNOSTICS", false) || !string.IsNullOrWhiteSpace(reportPath);
        var data = File.ReadAllBytes(inputPath);
        using var index = BinaryIndex.Open(indexPath);
        using var errorWriter = string.IsNullOrWhiteSpace(errorsPath)
            ? null
            : new StreamWriter(errorsPath, false, Encoding.UTF8, 1 << 16);
        using var dumpWriter = string.IsNullOrWhiteSpace(dumpPath)
            ? null
            : new StreamWriter(dumpPath, false, Encoding.UTF8, 1 << 16);
        var pathStats = diagnosticsEnabled ? new Dictionary<ClassificationPath, PathAggregate>() : null;
        var errorGroups = diagnosticsEnabled ? new Dictionary<string, ErrorGroup>() : null;

        var cursor = 0;
        var total = 0;
        var correct = 0;
        var fp = 0;
        var fn = 0;
        var parseErrors = 0;
        Span<int> fraudCountBuckets = stackalloc int[Constants.K + 1];
        var latencies = new List<long>(Math.Min(100_000, Math.Max(1024, limit)));
        var queryBuffer = new short[Constants.PaddedDim];
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
            var parsed = false;
            var hasDiagnostics = false;
            ClassificationDiagnostics diagnostics = default;
            var request = data.AsSpan(requestStart, requestEnd - requestStart + 1);
            var parseStarted = Stopwatch.GetTimestamp();
            if (evalNativeJson && index.TryClassifyFraudCountJson(request, searchParams, out fraudCount))
            {
                parsed = true;
                approved = fraudCount < 3;
            }
            else if (evalNativeJsonOnly)
            {
                parseErrors++;
            }
            else if (QueryBuilder.TryBuildQuery(request, queryBuffer))
            {
                parsed = true;
                if (diagnosticsEnabled)
                {
                    diagnostics = index.ClassifyFraudCountWithDiagnostics(queryBuffer, searchParams);
                    hasDiagnostics = true;
                    fraudCount = diagnostics.FraudCount;
                    AddPathStats(pathStats!, diagnostics);
                }
                else
                {
                    fraudCount = index.ClassifyFraudCount(queryBuffer, searchParams);
                }

                approved = fraudCount < 3;
            }
            else
            {
                parseErrors++;
                if (diagnosticsEnabled)
                {
                    AddPathStats(pathStats!, ClassificationPath.ParseError, Stopwatch.GetTimestamp() - parseStarted, 0, 0);
                }
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

            if (dumpWriter is not null && parsed)
            {
                WriteEvalRow(dumpWriter, expectedApproved, approved, fraudCount, queryBuffer, hasDiagnostics, diagnostics);
                dumpWriter.WriteLine("}");
            }

            if (approved != expectedApproved && errorWriter is not null)
            {
                WriteEvalRow(errorWriter, expectedApproved, approved, fraudCount, parsed ? queryBuffer : null, hasDiagnostics, diagnostics);
                errorWriter.Write(",\"request\":");
                errorWriter.Write(Encoding.UTF8.GetString(request));
                errorWriter.WriteLine("}");
            }

            if (approved != expectedApproved && diagnosticsEnabled && hasDiagnostics)
            {
                AddErrorGroup(errorGroups!, expectedApproved, approved, diagnostics, queryBuffer);
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
        Console.WriteLine($"risky_fallback_refs={index.RiskyFallbackCount}");
        Console.WriteLine(
            $"params early_candidates={searchParams.EarlyCandidates} min_candidates={searchParams.MinCandidates} max_candidates={searchParams.MaxCandidates} flat={searchParams.Flat} profile_fastpath={searchParams.ProfileFastPath} profile_min_count={searchParams.ProfileMinCount} profile_legit_min_count={searchParams.ProfileLegitMinCount} profile_fraud_min_count={searchParams.ProfileFraudMinCount} profile_fraud_amount_min={searchParams.ProfileFraudAmountMin} profile_fraud_low_amount_fastpath={searchParams.ProfileFraudLowAmountFastPath} profile_fraud_mid_amount_no_last_fastpath={searchParams.ProfileFraudMidAmountNoLastFastPath} profile_fraud_mid_amount_min={searchParams.ProfileFraudMidAmountMin} profile_dominant_fastpath={searchParams.ProfileDominantFastPath} profile_dominant_min_count={searchParams.ProfileDominantMinCount} profile_dominant_max_opposite={searchParams.ProfileDominantMaxOpposite} exact_fallback={searchParams.ExactFallback}");
        Console.WriteLine($"total={total} measured={measured} correct={correct} accuracy={accuracy:F6}");
        Console.WriteLine($"fp={fp} fn={fn} parse_errors={parseErrors} weighted_errors={weightedErrors} failure_rate={failureRate:F6} score_det={detectionScore:F2}");
        Console.WriteLine($"elapsed_ms={started.ElapsedMilliseconds} throughput_per_s={throughput:F1}");
        Console.WriteLine($"classify_latency_ns p50={TicksToNs(p50)} p95={TicksToNs(p95)} p99={TicksToNs(p99)}");
        Console.WriteLine(
            $"fraud_count_buckets 0={fraudCountBuckets[0]} 1={fraudCountBuckets[1]} 2={fraudCountBuckets[2]} 3={fraudCountBuckets[3]} 4={fraudCountBuckets[4]} 5={fraudCountBuckets[5]}");
        if (diagnosticsEnabled)
        {
            PrintDiagnostics(pathStats!, errorGroups!, total);
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                WriteDiagnosticsReport(reportPath, pathStats!, errorGroups!, total);
                Console.WriteLine($"diagnostics_report={reportPath}");
            }
        }
    }

    private static void WriteEvalRow(
        StreamWriter writer,
        bool expectedApproved,
        bool approved,
        int fraudCount,
        short[]? vector,
        bool hasDiagnostics = false,
        ClassificationDiagnostics diagnostics = default)
    {
        writer.Write("{\"expected_approved\":");
        writer.Write(expectedApproved ? "true" : "false");
        writer.Write(",\"approved\":");
        writer.Write(approved ? "true" : "false");
        writer.Write(",\"fraud_count\":");
        writer.Write(fraudCount);
        if (vector is not null)
        {
            writer.Write(",\"vector\":[");
            for (var i = 0; i < Constants.Dim; i++)
            {
                if (i > 0)
                {
                    writer.Write(',');
                }

                writer.Write(vector[i]);
            }

            writer.Write(']');
        }

        if (hasDiagnostics)
        {
            writer.Write(",\"profile_key\":");
            writer.Write(diagnostics.ProfileKey);
            writer.Write(",\"primary_bucket\":");
            writer.Write(diagnostics.PrimaryBucket);
            writer.Write(",\"path\":\"");
            writer.Write(ClassificationPathName(diagnostics.Path));
            writer.Write("\",\"candidates\":");
            writer.Write(diagnostics.Candidates);
            writer.Write(",\"fallback_candidates\":");
            writer.Write(diagnostics.FallbackCandidates);
            writer.Write(",\"classify_latency_ns\":");
            writer.Write(TicksToNs(diagnostics.ElapsedTicks));
            if (diagnostics.KdPrimaryPartition >= 0)
            {
                writer.Write(",\"kd_primary_partition\":");
                writer.Write(diagnostics.KdPrimaryPartition);
                writer.Write(",\"kd_searched_partitions\":");
                writer.Write(diagnostics.KdSearchedPartitions);
                writer.Write(",\"kd_candidate_partitions\":");
                writer.Write(diagnostics.KdCandidatePartitions);
                writer.Write(",\"kd_visited_nodes\":");
                writer.Write(diagnostics.KdVisitedNodes);
                writer.Write(",\"kd_pruned_nodes\":");
                writer.Write(diagnostics.KdPrunedNodes);
                writer.Write(",\"kd_scanned_leaves\":");
                writer.Write(diagnostics.KdScannedLeaves);
                writer.Write(",\"kd_scanned_vectors\":");
                writer.Write(diagnostics.KdScannedVectors);
                writer.Write(",\"kd_max_stack_depth\":");
                writer.Write(diagnostics.KdMaxStackDepth);
            }
        }
    }

    private static void AddPathStats(
        Dictionary<ClassificationPath, PathAggregate> stats,
        ClassificationDiagnostics diagnostics)
    {
        if (!stats.TryGetValue(diagnostics.Path, out var aggregate))
        {
            aggregate = new PathAggregate();
            stats.Add(diagnostics.Path, aggregate);
        }

        aggregate.Add(diagnostics);
    }

    private static void AddPathStats(
        Dictionary<ClassificationPath, PathAggregate> stats,
        ClassificationPath path,
        long ticks,
        int candidates,
        int fallbackCandidates)
    {
        if (!stats.TryGetValue(path, out var aggregate))
        {
            aggregate = new PathAggregate();
            stats.Add(path, aggregate);
        }

        aggregate.Add(ticks, candidates, fallbackCandidates);
    }

    private static void AddErrorGroup(
        Dictionary<string, ErrorGroup> groups,
        bool expectedApproved,
        bool approved,
        ClassificationDiagnostics diagnostics,
        short[] vector)
    {
        var vectorKey = VectorKey(vector);
        var path = ClassificationPathName(diagnostics.Path);
        var key = $"{diagnostics.ProfileKey}|{diagnostics.FraudCount}|{diagnostics.PrimaryBucket}|{path}|{vectorKey}";
        if (!groups.TryGetValue(key, out var group))
        {
            group = new ErrorGroup(
                diagnostics.ProfileKey,
                diagnostics.FraudCount,
                diagnostics.PrimaryBucket,
                path,
                vectorKey);
            groups.Add(key, group);
        }

        if (expectedApproved && !approved)
        {
            group.Fp++;
        }
        else if (!expectedApproved && approved)
        {
            group.Fn++;
        }
    }

    private static void PrintDiagnostics(
        Dictionary<ClassificationPath, PathAggregate> pathStats,
        Dictionary<string, ErrorGroup> errorGroups,
        int total)
    {
        Console.WriteLine("diagnostics_path_stats:");
        var pathItems = new List<KeyValuePair<ClassificationPath, PathAggregate>>(pathStats);
        pathItems.Sort((left, right) => right.Value.Count.CompareTo(left.Value.Count));
        foreach (var item in pathItems)
        {
            var aggregate = item.Value;
            aggregate.Latencies.Sort();
            aggregate.SortKdSamples();
            var p50 = TicksToNs(Percentile(aggregate.Latencies, 0.50));
            var p95 = TicksToNs(Percentile(aggregate.Latencies, 0.95));
            var p99 = TicksToNs(Percentile(aggregate.Latencies, 0.99));
            var percentage = total == 0 ? 0.0 : aggregate.Count * 100.0 / total;
            var line =
                $"path={ClassificationPathName(item.Key)} count={aggregate.Count} pct={percentage:F2} p50_ns={p50} p95_ns={p95} p99_ns={p99} avg_candidates={aggregate.AverageCandidates():F1} avg_fallback_candidates={aggregate.AverageFallbackCandidates():F1}";
            if (aggregate.KdSamples > 0)
            {
                line +=
                    $" kd_samples={aggregate.KdSamples} avg_kd_searched_partitions={aggregate.AverageKdSearchedPartitions():F1} p99_kd_searched_partitions={Percentile(aggregate.KdSearchedPartitions, 0.99)} avg_kd_candidate_partitions={aggregate.AverageKdCandidatePartitions():F1} avg_kd_visited_nodes={aggregate.AverageKdVisitedNodes():F1} p99_kd_visited_nodes={Percentile(aggregate.KdVisitedNodes, 0.99)} avg_kd_pruned_nodes={aggregate.AverageKdPrunedNodes():F1} avg_kd_scanned_leaves={aggregate.AverageKdScannedLeaves():F1} avg_kd_scanned_vectors={aggregate.AverageKdScannedVectors():F1} p99_kd_scanned_vectors={Percentile(aggregate.KdScannedVectors, 0.99)} max_kd_stack_depth={aggregate.MaxKdStackDepth}";
            }

            Console.WriteLine(line);
        }

        Console.WriteLine($"diagnostics_error_groups={errorGroups.Count}");
        var groups = new List<ErrorGroup>(errorGroups.Values);
        groups.Sort((left, right) =>
        {
            var compare = right.Total.CompareTo(left.Total);
            if (compare != 0)
            {
                return compare;
            }

            compare = right.Fn.CompareTo(left.Fn);
            return compare != 0 ? compare : right.Fp.CompareTo(left.Fp);
        });

        var limit = Math.Min(20, groups.Count);
        for (var i = 0; i < limit; i++)
        {
            var group = groups[i];
            Console.WriteLine(
                $"error_group fp={group.Fp} fn={group.Fn} profile_key={group.ProfileKey} fraud_count={group.FraudCount} primary_bucket={group.PrimaryBucket} path={group.Path} vector={group.Vector}");
        }
    }

    private static void WriteDiagnosticsReport(
        string reportPath,
        Dictionary<ClassificationPath, PathAggregate> pathStats,
        Dictionary<string, ErrorGroup> errorGroups,
        int total)
    {
        using var writer = new StreamWriter(reportPath, false, Encoding.UTF8, 1 << 16);
        writer.WriteLine("{");
        writer.WriteLine("  \"path_stats\": [");

        var pathItems = new List<KeyValuePair<ClassificationPath, PathAggregate>>(pathStats);
        pathItems.Sort((left, right) => right.Value.Count.CompareTo(left.Value.Count));
        for (var i = 0; i < pathItems.Count; i++)
        {
            var item = pathItems[i];
            var aggregate = item.Value;
            aggregate.Latencies.Sort();
            aggregate.SortKdSamples();
            var percentage = total == 0 ? 0.0 : aggregate.Count * 100.0 / total;
            writer.Write("    {");
            writer.Write($"\"path\":\"{ClassificationPathName(item.Key)}\",");
            writer.Write($"\"count\":{aggregate.Count},");
            writer.Write($"\"pct\":{percentage:F4},");
            writer.Write($"\"p50_ns\":{TicksToNs(Percentile(aggregate.Latencies, 0.50))},");
            writer.Write($"\"p95_ns\":{TicksToNs(Percentile(aggregate.Latencies, 0.95))},");
            writer.Write($"\"p99_ns\":{TicksToNs(Percentile(aggregate.Latencies, 0.99))},");
            writer.Write($"\"avg_candidates\":{aggregate.AverageCandidates():F2},");
            writer.Write($"\"avg_fallback_candidates\":{aggregate.AverageFallbackCandidates():F2}");
            if (aggregate.KdSamples > 0)
            {
                writer.Write($",\"kd_samples\":{aggregate.KdSamples}");
                writer.Write($",\"avg_kd_searched_partitions\":{aggregate.AverageKdSearchedPartitions():F2}");
                writer.Write($",\"p99_kd_searched_partitions\":{Percentile(aggregate.KdSearchedPartitions, 0.99)}");
                writer.Write($",\"avg_kd_candidate_partitions\":{aggregate.AverageKdCandidatePartitions():F2}");
                writer.Write($",\"avg_kd_visited_nodes\":{aggregate.AverageKdVisitedNodes():F2}");
                writer.Write($",\"p99_kd_visited_nodes\":{Percentile(aggregate.KdVisitedNodes, 0.99)}");
                writer.Write($",\"avg_kd_pruned_nodes\":{aggregate.AverageKdPrunedNodes():F2}");
                writer.Write($",\"avg_kd_scanned_leaves\":{aggregate.AverageKdScannedLeaves():F2}");
                writer.Write($",\"avg_kd_scanned_vectors\":{aggregate.AverageKdScannedVectors():F2}");
                writer.Write($",\"p99_kd_scanned_vectors\":{Percentile(aggregate.KdScannedVectors, 0.99)}");
                writer.Write($",\"max_kd_stack_depth\":{aggregate.MaxKdStackDepth}");
            }

            writer.Write(i == pathItems.Count - 1 ? "}\n" : "},\n");
        }

        writer.WriteLine("  ],");
        writer.WriteLine("  \"error_groups\": [");
        var groups = new List<ErrorGroup>(errorGroups.Values);
        groups.Sort((left, right) =>
        {
            var compare = right.Total.CompareTo(left.Total);
            if (compare != 0)
            {
                return compare;
            }

            compare = right.Fn.CompareTo(left.Fn);
            return compare != 0 ? compare : right.Fp.CompareTo(left.Fp);
        });

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            writer.Write("    {");
            writer.Write($"\"fp\":{group.Fp},");
            writer.Write($"\"fn\":{group.Fn},");
            writer.Write($"\"profile_key\":{group.ProfileKey},");
            writer.Write($"\"fraud_count\":{group.FraudCount},");
            writer.Write($"\"primary_bucket\":{group.PrimaryBucket},");
            writer.Write($"\"fallback_used\":\"{group.Path}\",");
            writer.Write($"\"vector_quantized\":{group.Vector}");
            writer.Write(i == groups.Count - 1 ? "}\n" : "},\n");
        }

        writer.WriteLine("  ]");
        writer.WriteLine("}");
    }

    private static string VectorKey(short[] vector)
    {
        var builder = new StringBuilder(Constants.Dim * 8);
        builder.Append('[');
        for (var i = 0; i < Constants.Dim; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(vector[i]);
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static string ClassificationPathName(ClassificationPath path)
    {
        return path switch
        {
            ClassificationPath.ParseError => "parse_error",
            ClassificationPath.ProfileFastPath => "profile_fast_path",
            ClassificationPath.AnnBuckets => "ann_buckets",
            ClassificationPath.NativeKdTree => "native_kdtree",
            ClassificationPath.RiskyFlatFallback => "risky_flat_fallback",
            ClassificationPath.FullFlatFallback => "full_flat_fallback",
            _ => "unknown"
        };
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

    private static bool EnvBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is null ? fallback : value is "1" or "true" or "TRUE" or "yes" or "YES";
    }

    private sealed class PathAggregate
    {
        public int Count { get; private set; }

        public long TotalCandidates { get; private set; }

        public long TotalFallbackCandidates { get; private set; }

        public int KdSamples { get; private set; }

        public long TotalKdSearchedPartitions { get; private set; }

        public long TotalKdCandidatePartitions { get; private set; }

        public long TotalKdVisitedNodes { get; private set; }

        public long TotalKdPrunedNodes { get; private set; }

        public long TotalKdScannedLeaves { get; private set; }

        public long TotalKdScannedVectors { get; private set; }

        public int MaxKdStackDepth { get; private set; }

        public List<long> Latencies { get; } = new();

        public List<long> KdSearchedPartitions { get; } = new();

        public List<long> KdVisitedNodes { get; } = new();

        public List<long> KdScannedVectors { get; } = new();

        public void Add(ClassificationDiagnostics diagnostics)
        {
            Add(diagnostics.ElapsedTicks, diagnostics.Candidates, diagnostics.FallbackCandidates);
            if (diagnostics.KdPrimaryPartition < 0)
            {
                return;
            }

            KdSamples++;
            TotalKdSearchedPartitions += diagnostics.KdSearchedPartitions;
            TotalKdCandidatePartitions += diagnostics.KdCandidatePartitions;
            TotalKdVisitedNodes += diagnostics.KdVisitedNodes;
            TotalKdPrunedNodes += diagnostics.KdPrunedNodes;
            TotalKdScannedLeaves += diagnostics.KdScannedLeaves;
            TotalKdScannedVectors += diagnostics.KdScannedVectors;
            if (diagnostics.KdMaxStackDepth > MaxKdStackDepth)
            {
                MaxKdStackDepth = diagnostics.KdMaxStackDepth;
            }

            KdSearchedPartitions.Add(diagnostics.KdSearchedPartitions);
            KdVisitedNodes.Add(diagnostics.KdVisitedNodes);
            KdScannedVectors.Add(diagnostics.KdScannedVectors);
        }

        public void Add(long ticks, int candidates, int fallbackCandidates)
        {
            Count++;
            TotalCandidates += candidates;
            TotalFallbackCandidates += fallbackCandidates;
            Latencies.Add(ticks);
        }

        public void SortKdSamples()
        {
            KdSearchedPartitions.Sort();
            KdVisitedNodes.Sort();
            KdScannedVectors.Sort();
        }

        public double AverageCandidates()
        {
            return Count == 0 ? 0.0 : TotalCandidates / (double)Count;
        }

        public double AverageFallbackCandidates()
        {
            return Count == 0 ? 0.0 : TotalFallbackCandidates / (double)Count;
        }

        public double AverageKdSearchedPartitions()
        {
            return KdSamples == 0 ? 0.0 : TotalKdSearchedPartitions / (double)KdSamples;
        }

        public double AverageKdCandidatePartitions()
        {
            return KdSamples == 0 ? 0.0 : TotalKdCandidatePartitions / (double)KdSamples;
        }

        public double AverageKdVisitedNodes()
        {
            return KdSamples == 0 ? 0.0 : TotalKdVisitedNodes / (double)KdSamples;
        }

        public double AverageKdPrunedNodes()
        {
            return KdSamples == 0 ? 0.0 : TotalKdPrunedNodes / (double)KdSamples;
        }

        public double AverageKdScannedLeaves()
        {
            return KdSamples == 0 ? 0.0 : TotalKdScannedLeaves / (double)KdSamples;
        }

        public double AverageKdScannedVectors()
        {
            return KdSamples == 0 ? 0.0 : TotalKdScannedVectors / (double)KdSamples;
        }
    }

    private sealed class ErrorGroup
    {
        public ErrorGroup(int profileKey, int fraudCount, int primaryBucket, string path, string vector)
        {
            ProfileKey = profileKey;
            FraudCount = fraudCount;
            PrimaryBucket = primaryBucket;
            Path = path;
            Vector = vector;
        }

        public int ProfileKey { get; }

        public int FraudCount { get; }

        public int PrimaryBucket { get; }

        public string Path { get; }

        public string Vector { get; }

        public int Fp { get; set; }

        public int Fn { get; set; }

        public int Total => Fp + Fn;
    }
}
