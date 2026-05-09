namespace RinhaFraud;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal unsafe sealed class BinaryIndex : IDisposable
{
    private static ReadOnlySpan<byte> Magic => "RINHA26I"u8;
    private const int HeaderLength = 80;
    private const int ProfileKeyCount = 1 << 22;
    private const int RiskyVectorStride = 16;
    private const int RiskyFineExtraBits = 3;
    private const int RiskyFineBucketsPerCoarse = 1 << RiskyFineExtraBits;
    private const int RiskyFineBucketCount = Constants.BucketCount << RiskyFineExtraBits;
    private const byte LegitMask = 1;
    private const byte FraudMask = 2;

    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private byte* _ptr;
    private readonly long _length;
    private readonly int _count;
    private readonly long _vectorsOffset;
    private readonly long _labelsOffset;
    private readonly long _bucketOffsetsOffset;
    private readonly ushort[] _profileCounts;
    private readonly byte[] _profileLabelMasks;
    private readonly uint[] _riskyFallbackIds;
    private readonly short[] _riskyFallbackVectors;
    private readonly byte[] _riskyFallbackLabels;
    private readonly int[] _riskyBucketOffsets;
    private readonly int[] _riskyFineBucketOffsets;
    private readonly int[] _riskyCoarseFineOffsets;
    private readonly int[] _riskyFineKeys;
    private readonly bool _useRiskyBuckets;
    private readonly bool _useRiskyFineBuckets;
    private readonly bool _useRiskyCompact;
    private readonly bool _useRiskySimd;
    private readonly bool _useMappedSimd;

    public int RiskyFallbackCount => _useRiskyCompact ? _riskyFallbackLabels.Length : _riskyFallbackIds.Length;

    private BinaryIndex(
        MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor accessor,
        byte* ptr,
        long length,
        int count,
        long vectorsOffset,
        long labelsOffset,
        long bucketOffsetsOffset,
        ushort[] profileCounts,
        byte[] profileLabelMasks,
        uint[] riskyFallbackIds,
        short[] riskyFallbackVectors,
        byte[] riskyFallbackLabels,
        int[] riskyBucketOffsets,
        int[] riskyFineBucketOffsets,
        int[] riskyCoarseFineOffsets,
        int[] riskyFineKeys,
        bool useRiskyBuckets,
        bool useRiskyFineBuckets,
        bool useRiskyCompact,
        bool useRiskySimd,
        bool useMappedSimd)
    {
        _mappedFile = mappedFile;
        _accessor = accessor;
        _ptr = ptr;
        _length = length;
        _count = count;
        _vectorsOffset = vectorsOffset;
        _labelsOffset = labelsOffset;
        _bucketOffsetsOffset = bucketOffsetsOffset;
        _profileCounts = profileCounts;
        _profileLabelMasks = profileLabelMasks;
        _riskyFallbackIds = riskyFallbackIds;
        _riskyFallbackVectors = riskyFallbackVectors;
        _riskyFallbackLabels = riskyFallbackLabels;
        _riskyBucketOffsets = riskyBucketOffsets;
        _riskyFineBucketOffsets = riskyFineBucketOffsets;
        _riskyCoarseFineOffsets = riskyCoarseFineOffsets;
        _riskyFineKeys = riskyFineKeys;
        _useRiskyBuckets = useRiskyBuckets;
        _useRiskyFineBuckets = useRiskyFineBuckets;
        _useRiskyCompact = useRiskyCompact;
        _useRiskySimd = useRiskySimd;
        _useMappedSimd = useMappedSimd;
    }

    public static BinaryIndex Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"index not found: {path}", path);
        }

        var info = new FileInfo(path);
        if (info.Length < HeaderLength)
        {
            throw new InvalidOperationException("index too small");
        }

        var mappedFile = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var accessor = mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

        try
        {
            var header = new ReadOnlySpan<byte>(ptr, HeaderLength);
            if (!header[..8].SequenceEqual(Magic))
            {
                throw new InvalidOperationException("bad index magic");
            }

            var version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            var dim = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[16..]));
            var scale = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var bucketCount = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var vectorsOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header[32..]));
            var labelsOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header[40..]));
            var bucketOffsetsOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header[48..]));
            var bucketItemsOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header[56..]));
            var fileLength = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header[64..]));

            if (version != 1 ||
                dim != Constants.Dim ||
                scale != Constants.Scale ||
                bucketCount != Constants.BucketCount)
            {
                throw new InvalidOperationException("unsupported index version or shape");
            }

            if (fileLength != info.Length)
            {
                throw new InvalidOperationException("index file length mismatch");
            }

            var vectorsEnd = vectorsOffset + count * Constants.Dim * 2L;
            var labelsEnd = labelsOffset + count;
            var bucketOffsetsEnd = bucketOffsetsOffset + (Constants.BucketCount + 1L) * 4L;
            var bucketItemsEnd = bucketItemsOffset + count * 4L;
            if (vectorsEnd > fileLength || labelsEnd > fileLength || bucketOffsetsEnd > fileLength || bucketItemsEnd > fileLength)
            {
                throw new InvalidOperationException("index offsets out of bounds");
            }

            BuildProfileStats(ptr, count, vectorsOffset, labelsOffset, out var profileCounts, out var profileLabelMasks);
            var riskyFallbackFilter = RiskyFallbackFilter.FromEnvironment();
            var useRiskyCompact = EnvBool("RISKY_COMPACT", true);
            BuildRiskyFallbackIndex(
                ptr,
                count,
                vectorsOffset,
                labelsOffset,
                in riskyFallbackFilter,
                useRiskyCompact,
                out var riskyFallbackIds,
                out var riskyFallbackVectors,
                out var riskyFallbackLabels,
                out var riskyBucketOffsets,
                out var riskyFineBucketOffsets,
                out var riskyCoarseFineOffsets,
                out var riskyFineKeys);
            var useRiskyBuckets = EnvBool("RISKY_BUCKETS", true);
            var useRiskyFineBuckets = EnvBool("RISKY_FINE_BUCKETS", true);
            var useRiskySimd = EnvBool("RISKY_SIMD", true);
            var useMappedSimd = EnvBool("MAPPED_SIMD", true);

            return new BinaryIndex(
                mappedFile,
                accessor,
                ptr,
                fileLength,
                count,
                vectorsOffset,
                labelsOffset,
                bucketOffsetsOffset,
                profileCounts,
                profileLabelMasks,
                riskyFallbackIds,
                riskyFallbackVectors,
                riskyFallbackLabels,
                riskyBucketOffsets,
                riskyFineBucketOffsets,
                riskyCoarseFineOffsets,
                riskyFineKeys,
                useRiskyBuckets,
                useRiskyFineBuckets,
                useRiskyCompact,
                useRiskySimd,
                useMappedSimd);
        }
        catch
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor.Dispose();
            mappedFile.Dispose();
            throw;
        }
    }

    public int Prefault()
    {
        var checksum = 0;
        for (long pos = 0; pos < _length; pos += 4096)
        {
            checksum ^= VolatileRead(_ptr + pos);
        }

        checksum ^= VolatileRead(_ptr + _length - 1);
        return checksum;
    }

    [SkipLocalsInit]
    public int ClassifyFraudCount(ReadOnlySpan<short> query, in SearchParams searchParams)
    {
        if (TryProfileFastDecision(query, searchParams, out var fastFraudCount))
        {
            return fastFraudCount;
        }

        if (searchParams.Flat || searchParams.ExactFallback == SearchParams.ExactFallbackProfileMiss)
        {
            return ClassifyFlat(query);
        }

        Span<long> topDist = stackalloc long[Constants.K];
        Span<byte> topLabel = stackalloc byte[Constants.K];
        topDist.Fill(long.MaxValue);

        var candidates = 0;
        var neighborKeys = Vectorizer.NeighborKeyOrderFor(query);
        for (var neighborIndex = 0; neighborIndex < neighborKeys.Length; neighborIndex++)
        {
            var key = neighborKeys[neighborIndex];
            var start = BucketOffset(key);
            var end = BucketOffset(key + 1);
            var scanEnd = end;
            var remaining = searchParams.MaxCandidates - candidates;
            if (end - start > remaining)
            {
                scanEnd = start + (uint)remaining;
            }

            ConsiderCandidateRange(query, start, scanEnd, topDist, topLabel);
            candidates += (int)(scanEnd - start);
            if (candidates >= searchParams.MaxCandidates)
            {
                goto CandidateSearchDone;
            }

            if (candidates >= searchParams.EarlyCandidates && StrongDecision(topLabel))
            {
                goto CandidateSearchDone;
            }

            if (candidates >= searchParams.MinCandidates)
            {
                goto CandidateSearchDone;
            }
        }

CandidateSearchDone:
        if (candidates < Constants.K)
        {
            return ClassifyFlat(query);
        }

        var frauds = CountFrauds(topLabel);
        if (!ShouldUseExactFallback(query, frauds, searchParams))
        {
            return frauds;
        }

        return searchParams.ExactFallback == SearchParams.ExactFallbackRisky
            ? ClassifyRiskyFlat(query, allowFullTiebreak: true)
            : ClassifyFlat(query);
    }

    [SkipLocalsInit]
    public ClassificationDiagnostics ClassifyFraudCountWithDiagnostics(ReadOnlySpan<short> query, in SearchParams searchParams)
    {
        var started = Stopwatch.GetTimestamp();
        var profileKey = ProfileKey(query);
        var primaryBucket = Vectorizer.BucketKey(query);

        if (TryProfileFastDecision(query, searchParams, out var fastFraudCount))
        {
            return Diagnostic(
                fastFraudCount,
                ClassificationPath.ProfileFastPath,
                profileKey,
                primaryBucket,
                candidates: 0,
                fallbackCandidates: 0,
                started);
        }

        if (searchParams.Flat || searchParams.ExactFallback == SearchParams.ExactFallbackProfileMiss)
        {
            var flatFrauds = ClassifyFlat(query, out var flatCandidates);
            return Diagnostic(
                flatFrauds,
                ClassificationPath.FullFlatFallback,
                profileKey,
                primaryBucket,
                candidates: 0,
                fallbackCandidates: flatCandidates,
                started);
        }

        Span<long> topDist = stackalloc long[Constants.K];
        Span<byte> topLabel = stackalloc byte[Constants.K];
        topDist.Fill(long.MaxValue);

        var candidates = 0;
        var neighborKeys = Vectorizer.NeighborKeyOrderFor(query);
        for (var neighborIndex = 0; neighborIndex < neighborKeys.Length; neighborIndex++)
        {
            var key = neighborKeys[neighborIndex];
            var start = BucketOffset(key);
            var end = BucketOffset(key + 1);
            var scanEnd = end;
            var remaining = searchParams.MaxCandidates - candidates;
            if (end - start > remaining)
            {
                scanEnd = start + (uint)remaining;
            }

            ConsiderCandidateRange(query, start, scanEnd, topDist, topLabel);
            candidates += (int)(scanEnd - start);
            if (candidates >= searchParams.MaxCandidates)
            {
                goto CandidateSearchDone;
            }

            if (candidates >= searchParams.EarlyCandidates && StrongDecision(topLabel))
            {
                goto CandidateSearchDone;
            }

            if (candidates >= searchParams.MinCandidates)
            {
                goto CandidateSearchDone;
            }
        }

CandidateSearchDone:
        if (candidates < Constants.K)
        {
            var flatFrauds = ClassifyFlat(query, out var flatCandidates);
            return Diagnostic(
                flatFrauds,
                ClassificationPath.FullFlatFallback,
                profileKey,
                primaryBucket,
                candidates,
                flatCandidates,
                started);
        }

        var frauds = CountFrauds(topLabel);
        if (!ShouldUseExactFallback(query, frauds, searchParams))
        {
            return Diagnostic(
                frauds,
                ClassificationPath.AnnBuckets,
                profileKey,
                primaryBucket,
                candidates,
                fallbackCandidates: 0,
                started);
        }

        if (searchParams.ExactFallback == SearchParams.ExactFallbackRisky)
        {
            var riskyFrauds = ClassifyRiskyFlatForDiagnostics(query, allowFullTiebreak: true, out var usedFullFlat, out var fallbackCandidates);
            return Diagnostic(
                riskyFrauds,
                usedFullFlat ? ClassificationPath.FullFlatFallback : ClassificationPath.RiskyFlatFallback,
                profileKey,
                primaryBucket,
                candidates,
                fallbackCandidates,
                started);
        }

        var fallbackFrauds = ClassifyFlat(query, out var flatFallbackCandidates);
        return Diagnostic(
            fallbackFrauds,
            ClassificationPath.FullFlatFallback,
            profileKey,
            primaryBucket,
            candidates,
            flatFallbackCandidates,
            started);
    }

    [SkipLocalsInit]
    private int ClassifyFlat(ReadOnlySpan<short> query)
        => ClassifyFlat(query, long.MaxValue, out _);

    [SkipLocalsInit]
    private int ClassifyFlat(ReadOnlySpan<short> query, long seedCutoff)
        => ClassifyFlat(query, seedCutoff, out _);

    [SkipLocalsInit]
    private int ClassifyFlat(ReadOnlySpan<short> query, out int scannedCandidates)
        => ClassifyFlat(query, long.MaxValue, out scannedCandidates);

    [SkipLocalsInit]
    private int ClassifyFlat(ReadOnlySpan<short> query, long seedCutoff, out int scannedCandidates)
    {
        Span<long> topDist = stackalloc long[Constants.K];
        Span<byte> topLabel = stackalloc byte[Constants.K];
        topDist.Fill(long.MaxValue);
        scannedCandidates = 0;

        var pruneCutoff = seedCutoff;
        for (var key = 0; key < Constants.BucketCount; key++)
        {
            if (pruneCutoff != long.MaxValue && RiskyBucketLowerBound(key, query) > pruneCutoff)
            {
                continue;
            }

            var start = BucketOffset(key);
            var end = BucketOffset(key + 1);
            if (start == end)
            {
                continue;
            }

            scannedCandidates += (int)(end - start);
            ConsiderCandidateRange(query, start, end, topDist, topLabel);
            if (topDist[Constants.K - 1] < pruneCutoff)
            {
                pruneCutoff = topDist[Constants.K - 1];
            }
        }

        return CountFrauds(topLabel);
    }

    [SkipLocalsInit]
    private int ClassifyRiskyFlat(ReadOnlySpan<short> query, bool allowFullTiebreak)
    {
        if (RiskyFallbackCount < Constants.K)
        {
            return ClassifyFlat(query);
        }

        Span<long> topDist = stackalloc long[Constants.K];
        Span<byte> topLabel = stackalloc byte[Constants.K];
        topDist.Fill(long.MaxValue);

        if (_useRiskyBuckets)
        {
            return ClassifyRiskyBucketedFlat(query, topDist, topLabel, allowFullTiebreak, out _, out _);
        }

        if (_useRiskyCompact)
        {
            ConsiderRiskyCompactRange(query, 0, _riskyFallbackLabels.Length, topDist, topLabel);
            var compactFrauds = CountFrauds(topLabel);
            return allowFullTiebreak && NeedsFullRiskyTiebreak(query, compactFrauds) ? ClassifyFlat(query, topDist[Constants.K - 1]) : compactFrauds;
        }

        foreach (var id in _riskyFallbackIds)
        {
            Consider(id, query, topDist, topLabel);
        }

        var frauds = CountFrauds(topLabel);
        return allowFullTiebreak && NeedsFullRiskyTiebreak(query, frauds) ? ClassifyFlat(query, topDist[Constants.K - 1]) : frauds;
    }

    [SkipLocalsInit]
    private int ClassifyRiskyFlatForDiagnostics(ReadOnlySpan<short> query, bool allowFullTiebreak, out bool usedFullFlat, out int fallbackCandidates)
    {
        usedFullFlat = false;
        if (RiskyFallbackCount < Constants.K)
        {
            usedFullFlat = true;
            return ClassifyFlat(query, out fallbackCandidates);
        }

        Span<long> topDist = stackalloc long[Constants.K];
        Span<byte> topLabel = stackalloc byte[Constants.K];
        topDist.Fill(long.MaxValue);

        if (_useRiskyBuckets)
        {
            return ClassifyRiskyBucketedFlat(query, topDist, topLabel, allowFullTiebreak, out usedFullFlat, out fallbackCandidates);
        }

        if (_useRiskyCompact)
        {
            ConsiderRiskyCompactRange(query, 0, _riskyFallbackLabels.Length, topDist, topLabel);
            fallbackCandidates = _riskyFallbackLabels.Length;
            var compactFrauds = CountFrauds(topLabel);
            if (allowFullTiebreak && NeedsFullRiskyTiebreak(query, compactFrauds))
            {
                usedFullFlat = true;
                var flatFrauds = ClassifyFlat(query, topDist[Constants.K - 1], out var flatCandidates);
                fallbackCandidates += flatCandidates;
                return flatFrauds;
            }

            return compactFrauds;
        }

        foreach (var id in _riskyFallbackIds)
        {
            Consider(id, query, topDist, topLabel);
        }

        fallbackCandidates = _riskyFallbackIds.Length;
        var frauds = CountFrauds(topLabel);
        if (allowFullTiebreak && NeedsFullRiskyTiebreak(query, frauds))
        {
            usedFullFlat = true;
            var flatFrauds = ClassifyFlat(query, topDist[Constants.K - 1], out var flatCandidates);
            fallbackCandidates += flatCandidates;
            return flatFrauds;
        }

        return frauds;
    }

    private int ClassifyRiskyBucketedFlat(
        ReadOnlySpan<short> query,
        Span<long> topDist,
        Span<byte> topLabel,
        bool allowFullTiebreak,
        out bool usedFullFlat,
        out int fallbackCandidates)
    {
        usedFullFlat = false;
        fallbackCandidates = 0;

        if (_useRiskyFineBuckets)
        {
            return ClassifyRiskyFineBucketedFlat(
                query,
                topDist,
                topLabel,
                allowFullTiebreak,
                out usedFullFlat,
                out fallbackCandidates);
        }

        var neighborKeys = Vectorizer.NeighborKeyOrderFor(query);
        for (var neighborIndex = 0; neighborIndex < neighborKeys.Length; neighborIndex++)
        {
            var key = neighborKeys[neighborIndex];
            var start = _riskyBucketOffsets[key];
            var end = _riskyBucketOffsets[key + 1];
            if (start == end)
            {
                continue;
            }

            if (RiskyBucketLowerBound(key, query) >= topDist[Constants.K - 1])
            {
                continue;
            }

            fallbackCandidates += end - start;
            if (_useRiskyCompact)
            {
                ConsiderRiskyCompactRange(query, start, end, topDist, topLabel);
            }
            else
            {
                for (var pos = start; pos < end; pos++)
                {
                    Consider(_riskyFallbackIds[pos], query, topDist, topLabel);
                }
            }
        }

        var frauds = CountFrauds(topLabel);
        if (allowFullTiebreak && NeedsFullRiskyTiebreak(query, frauds))
        {
            usedFullFlat = true;
            var flatFrauds = ClassifyFlat(query, topDist[Constants.K - 1], out var flatCandidates);
            fallbackCandidates += flatCandidates;
            return flatFrauds;
        }

        return frauds;
    }

    private int ClassifyRiskyFineBucketedFlat(
        ReadOnlySpan<short> query,
        Span<long> topDist,
        Span<byte> topLabel,
        bool allowFullTiebreak,
        out bool usedFullFlat,
        out int fallbackCandidates)
    {
        usedFullFlat = false;
        fallbackCandidates = 0;

        Span<int> orderedFineKeys = stackalloc int[RiskyFineBucketsPerCoarse];
        Span<long> orderedFineBounds = stackalloc long[RiskyFineBucketsPerCoarse];
        var neighborKeys = Vectorizer.NeighborKeyOrderFor(query);
        for (var neighborIndex = 0; neighborIndex < neighborKeys.Length; neighborIndex++)
        {
            var coarseKey = neighborKeys[neighborIndex];
            var fineStart = _riskyCoarseFineOffsets[coarseKey];
            var fineEnd = _riskyCoarseFineOffsets[coarseKey + 1];
            if (fineStart == fineEnd)
            {
                continue;
            }

            var coarseLowerBound = RiskyBucketLowerBound(coarseKey, query);
            if (coarseLowerBound >= topDist[Constants.K - 1])
            {
                continue;
            }

            var orderedFineCount = 0;
            for (var finePos = fineStart; finePos < fineEnd; finePos++)
            {
                var fineKey = _riskyFineKeys[finePos];
                var lowerBound = RiskyFineBucketLowerBound(fineKey, query, coarseLowerBound);
                if (lowerBound >= topDist[Constants.K - 1])
                {
                    continue;
                }

                var insertAt = orderedFineCount;
                while (insertAt > 0 && lowerBound < orderedFineBounds[insertAt - 1])
                {
                    orderedFineKeys[insertAt] = orderedFineKeys[insertAt - 1];
                    orderedFineBounds[insertAt] = orderedFineBounds[insertAt - 1];
                    insertAt--;
                }

                orderedFineKeys[insertAt] = fineKey;
                orderedFineBounds[insertAt] = lowerBound;
                orderedFineCount++;
            }

            for (var orderedPos = 0; orderedPos < orderedFineCount; orderedPos++)
            {
                if (orderedFineBounds[orderedPos] >= topDist[Constants.K - 1])
                {
                    break;
                }

                var fineKey = orderedFineKeys[orderedPos];
                var start = _riskyFineBucketOffsets[fineKey];
                var end = _riskyFineBucketOffsets[fineKey + 1];
                fallbackCandidates += end - start;
                if (_useRiskyCompact)
                {
                    ConsiderRiskyCompactRange(query, start, end, topDist, topLabel);
                }
                else
                {
                    for (var pos = start; pos < end; pos++)
                    {
                        Consider(_riskyFallbackIds[pos], query, topDist, topLabel);
                    }
                }
            }
        }

        var frauds = CountFrauds(topLabel);
        if (allowFullTiebreak && NeedsFullRiskyTiebreak(query, frauds))
        {
            usedFullFlat = true;
            var flatFrauds = ClassifyFlat(query, topDist[Constants.K - 1], out var flatCandidates);
            fallbackCandidates += flatCandidates;
            return flatFrauds;
        }

        return frauds;
    }

    [SkipLocalsInit]
    private void ConsiderRiskyCompactRange(ReadOnlySpan<short> query, int start, int end, Span<long> topDist, Span<byte> topLabel)
    {
        if (_useRiskySimd && Avx2.IsSupported)
        {
            ConsiderRiskyCompactRangeAvx2(query, start, end, topDist, topLabel);
            return;
        }

        if (_useRiskySimd && Sse2.IsSupported)
        {
            ConsiderRiskyCompactRangeSse2(query, start, end, topDist, topLabel);
            return;
        }

        ConsiderRiskyCompactRangeScalar(query, start, end, topDist, topLabel);
    }

    [SkipLocalsInit]
    private void ConsiderRiskyCompactRangeAvx2(ReadOnlySpan<short> query, int start, int end, Span<long> topDist, Span<byte> topLabel)
    {
        Span<short> paddedQuery = stackalloc short[RiskyVectorStride];
        paddedQuery.Clear();
        query.CopyTo(paddedQuery);

        fixed (short* queryPtr = paddedQuery)
        fixed (short* vectorBase = _riskyFallbackVectors)
        fixed (byte* labelBase = _riskyFallbackLabels)
        {
            for (var pos = start; pos < end; pos++)
            {
                var dist = DistanceSquaredRiskyAvx2(vectorBase + pos * RiskyVectorStride, queryPtr);
                if (dist >= topDist[4])
                {
                    continue;
                }

                InsertRiskyCandidate(dist, labelBase[pos], topDist, topLabel);
            }
        }
    }

    [SkipLocalsInit]
    private void ConsiderRiskyCompactRangeSse2(ReadOnlySpan<short> query, int start, int end, Span<long> topDist, Span<byte> topLabel)
    {
        Span<short> paddedQuery = stackalloc short[RiskyVectorStride];
        paddedQuery.Clear();
        query.CopyTo(paddedQuery);

        fixed (short* queryPtr = paddedQuery)
        fixed (short* vectorBase = _riskyFallbackVectors)
        fixed (byte* labelBase = _riskyFallbackLabels)
        {
            for (var pos = start; pos < end; pos++)
            {
                var dist = DistanceSquaredRiskySse2(vectorBase + pos * RiskyVectorStride, queryPtr);
                if (dist >= topDist[4])
                {
                    continue;
                }

                InsertRiskyCandidate(dist, labelBase[pos], topDist, topLabel);
            }
        }
    }

    private void ConsiderRiskyCompactRangeScalar(ReadOnlySpan<short> query, int start, int end, Span<long> topDist, Span<byte> topLabel)
    {
        fixed (short* vectorBase = _riskyFallbackVectors)
        fixed (byte* labelBase = _riskyFallbackLabels)
        {
            for (var pos = start; pos < end; pos++)
            {
                var dist = DistanceSquaredRiskyScalar(vectorBase + pos * RiskyVectorStride, query, topDist[4]);
                if (dist >= topDist[4])
                {
                    continue;
                }

                InsertRiskyCandidate(dist, labelBase[pos], topDist, topLabel);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertRiskyCandidate(long dist, byte label, Span<long> topDist, Span<byte> topLabel)
    {
        if (dist < topDist[0])
        {
            topDist[4] = topDist[3];
            topDist[3] = topDist[2];
            topDist[2] = topDist[1];
            topDist[1] = topDist[0];
            topDist[0] = dist;
            topLabel[4] = topLabel[3];
            topLabel[3] = topLabel[2];
            topLabel[2] = topLabel[1];
            topLabel[1] = topLabel[0];
            topLabel[0] = label;
        }
        else if (dist < topDist[1])
        {
            topDist[4] = topDist[3];
            topDist[3] = topDist[2];
            topDist[2] = topDist[1];
            topDist[1] = dist;
            topLabel[4] = topLabel[3];
            topLabel[3] = topLabel[2];
            topLabel[2] = topLabel[1];
            topLabel[1] = label;
        }
        else if (dist < topDist[2])
        {
            topDist[4] = topDist[3];
            topDist[3] = topDist[2];
            topDist[2] = dist;
            topLabel[4] = topLabel[3];
            topLabel[3] = topLabel[2];
            topLabel[2] = label;
        }
        else if (dist < topDist[3])
        {
            topDist[4] = topDist[3];
            topDist[3] = dist;
            topLabel[4] = topLabel[3];
            topLabel[3] = label;
        }
        else
        {
            topDist[4] = dist;
            topLabel[4] = label;
        }
    }

    private static ClassificationDiagnostics Diagnostic(
        int fraudCount,
        ClassificationPath path,
        int profileKey,
        int primaryBucket,
        int candidates,
        int fallbackCandidates,
        long started)
    {
        return new ClassificationDiagnostics(
            fraudCount,
            path,
            profileKey,
            primaryBucket,
            candidates,
            fallbackCandidates,
            Stopwatch.GetTimestamp() - started);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountFrauds(ReadOnlySpan<byte> topLabel)
    {
        var frauds = 0;
        for (var i = 0; i < Constants.K; i++)
        {
            if (topLabel[i] == 1)
            {
                frauds++;
            }
        }

        return frauds;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldUseExactFallback(ReadOnlySpan<short> query, int frauds, in SearchParams searchParams)
    {
        if (frauds > 0 && frauds < Constants.K)
        {
            return searchParams.ExactFallback is SearchParams.ExactFallbackUncertain or SearchParams.ExactFallbackRisky;
        }

        return searchParams.ExactFallback == SearchParams.ExactFallbackRisky && IsStrongFallbackRisk(query, frauds);
    }

    private static bool IsStrongFallbackRisk(ReadOnlySpan<short> query, int frauds)
    {
        if (frauds != 0 && frauds != Constants.K)
        {
            return false;
        }

        if (frauds == 0 && IsHighRiskOnlineFallback(query))
        {
            return true;
        }

        if (IsStrongProfileTiebreak(query, frauds))
        {
            return true;
        }

        if (frauds == Constants.K &&
            query[5] >= 600 && query[5] <= 850 &&
            query[9] == 0 &&
            query[10] == 0 &&
            query[11] == 0 &&
            query[12] <= 2000 &&
            query[0] >= 1100 && query[0] <= 1300 &&
            query[2] >= 4000 && query[2] <= 4600 &&
            query[7] >= 550 && query[7] <= 750 &&
            query[8] >= 2000 && query[8] <= 3000 &&
            query[13] >= 220 && query[13] <= 320)
        {
            return true;
        }

        return query[5] >= 0 &&
               query[10] == 0 &&
               query[0] >= 450 && query[0] <= 1100 &&
               query[2] >= 900 && query[2] <= 2500 &&
               query[7] >= 500 && query[7] <= 2000 &&
               query[8] >= 2000 && query[8] <= 4500;
    }

    private static bool IsStrongProfileTiebreak(ReadOnlySpan<short> query, int frauds)
    {
        if (query[5] < 0 || query[13] > 220)
        {
            return false;
        }

        if (frauds == 0)
        {
            return
                (query[9] == 0 &&
                query[10] > 0 &&
                query[12] >= 7500 &&
                query[0] >= 450 && query[0] <= 600 &&
                query[2] >= 1000 && query[2] <= 1200 &&
                query[7] >= 400 && query[7] <= 600 &&
                query[8] >= 4000 && query[8] <= 5000) ||
                (query[9] > 0 &&
                query[10] == 0 &&
                query[12] <= 2500 &&
                query[0] >= 2100 && query[0] <= 2300 &&
                query[2] >= 4400 && query[2] <= 4900 &&
                query[7] >= 700 && query[7] <= 950 &&
                query[8] >= 2000 && query[8] <= 3000) ||
                (query[9] > 0 &&
                query[10] == 0 &&
                query[11] > 0 &&
                query[12] >= 4000 && query[12] <= 5000 &&
                query[0] >= 1200 && query[0] <= 1500 &&
                query[2] >= 3300 && query[2] <= 3800 &&
                query[7] >= 3300 && query[7] <= 3900 &&
                query[8] >= 2000 && query[8] <= 3000);
        }

        return query[9] == 0 &&
               query[10] > 0 &&
               query[12] <= 2500 &&
               query[0] >= 2700 && query[0] <= 3000 &&
               query[2] >= 9000 &&
               query[7] >= 3500 && query[7] <= 4000 &&
               query[8] >= 2500 && query[8] <= 3500;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StrongDecision(ReadOnlySpan<byte> topLabel)
    {
        var frauds = 0;
        for (var i = 0; i < Constants.K; i++)
        {
            frauds += topLabel[i];
        }

        return frauds == 0 || frauds == Constants.K;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryProfileFastDecision(ReadOnlySpan<short> query, in SearchParams searchParams, out int fraudCount)
    {
        fraudCount = 0;
        if (!searchParams.ProfileFastPath)
        {
            return false;
        }

        var key = ProfileKey(query);
        var mask = _profileLabelMasks[key];
        if (mask == LegitMask)
        {
            if (_profileCounts[key] < searchParams.ProfileLegitMinCount)
            {
                return false;
            }

            fraudCount = 0;
            return true;
        }

        if (mask == FraudMask)
        {
            if (_profileCounts[key] < searchParams.ProfileFraudMinCount)
            {
                return false;
            }

            fraudCount = Constants.K;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ConsiderCandidateRange(ReadOnlySpan<short> query, uint start, uint end, Span<long> topDist, Span<byte> topLabel)
    {
        if (_useMappedSimd && Avx2.IsSupported)
        {
            ConsiderCandidateRangeAvx2(query, start, end, topDist, topLabel);
            return;
        }

        for (var id = start; id < end; id++)
        {
            Consider(id, query, topDist, topLabel);
        }
    }

    [SkipLocalsInit]
    private void ConsiderCandidateRangeAvx2(ReadOnlySpan<short> query, uint start, uint end, Span<long> topDist, Span<byte> topLabel)
    {
        Span<short> paddedQuery = stackalloc short[RiskyVectorStride];
        paddedQuery.Clear();
        query.CopyTo(paddedQuery);

        fixed (short* queryPtr = paddedQuery)
        {
            var vectorBase = (short*)(_ptr + _vectorsOffset);
            var labelBase = _ptr + _labelsOffset;
            for (var id = start; id < end; id++)
            {
                var dist = DistanceSquaredMappedAvx2(vectorBase + id * Constants.Dim, queryPtr);
                if (dist >= topDist[4])
                {
                    continue;
                }

                InsertRiskyCandidate(dist, labelBase[id], topDist, topLabel);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Consider(uint id, ReadOnlySpan<short> query, Span<long> topDist, Span<byte> topLabel)
    {
        var dist = DistanceSquared(id, query, topDist[Constants.K - 1]);
        if (dist >= topDist[4])
        {
            return;
        }

        var label = Label(id);
        if (dist < topDist[0])
        {
            topDist[4] = topDist[3];
            topDist[3] = topDist[2];
            topDist[2] = topDist[1];
            topDist[1] = topDist[0];
            topDist[0] = dist;
            topLabel[4] = topLabel[3];
            topLabel[3] = topLabel[2];
            topLabel[2] = topLabel[1];
            topLabel[1] = topLabel[0];
            topLabel[0] = label;
        }
        else if (dist < topDist[1])
        {
            topDist[4] = topDist[3];
            topDist[3] = topDist[2];
            topDist[2] = topDist[1];
            topDist[1] = dist;
            topLabel[4] = topLabel[3];
            topLabel[3] = topLabel[2];
            topLabel[2] = topLabel[1];
            topLabel[1] = label;
        }
        else if (dist < topDist[2])
        {
            topDist[4] = topDist[3];
            topDist[3] = topDist[2];
            topDist[2] = dist;
            topLabel[4] = topLabel[3];
            topLabel[3] = topLabel[2];
            topLabel[2] = label;
        }
        else if (dist < topDist[3])
        {
            topDist[4] = topDist[3];
            topDist[3] = dist;
            topLabel[4] = topLabel[3];
            topLabel[3] = label;
        }
        else
        {
            topDist[4] = dist;
            topLabel[4] = label;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long DistanceSquared(uint id, ReadOnlySpan<short> query, long cutoff)
    {
        var vector = _ptr + _vectorsOffset + id * Constants.Dim * 2L;
        ref var q = ref MemoryMarshal.GetReference(query);
        long sum = 0;

        var d = (long)Unsafe.Add(ref q, 6) - Unsafe.ReadUnaligned<short>(vector + 12);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 10) - Unsafe.ReadUnaligned<short>(vector + 20);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 9) - Unsafe.ReadUnaligned<short>(vector + 18);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 5) - Unsafe.ReadUnaligned<short>(vector + 10);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 11) - Unsafe.ReadUnaligned<short>(vector + 22);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 2) - Unsafe.ReadUnaligned<short>(vector + 4);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 4) - Unsafe.ReadUnaligned<short>(vector + 8);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 7) - Unsafe.ReadUnaligned<short>(vector + 14);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = q - Unsafe.ReadUnaligned<short>(vector);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 1) - Unsafe.ReadUnaligned<short>(vector + 2);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 8) - Unsafe.ReadUnaligned<short>(vector + 16);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 12) - Unsafe.ReadUnaligned<short>(vector + 24);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 3) - Unsafe.ReadUnaligned<short>(vector + 6);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 13) - Unsafe.ReadUnaligned<short>(vector + 26);
        sum += d * d;

        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DistanceSquaredMappedAvx2(short* vector, short* query)
    {
        var diff = Avx2.Subtract(Avx.LoadVector256(query), Avx.LoadVector256(vector));
        var mask = Vector256.Create(
            (short)-1,
            (short)-1,
            (short)-1,
            (short)-1,
            (short)-1,
            (short)-1,
            (short)-1,
            (short)-1,
            (short)-1,
            (short)-1,
            (short)-1,
            (short)-1,
            (short)-1,
            (short)-1,
            (short)0,
            (short)0);
        diff = Avx2.And(diff, mask);
        var pairs = Avx2.MultiplyAddAdjacent(diff, diff);
        return (long)pairs.GetElement(0) +
               pairs.GetElement(1) +
               pairs.GetElement(2) +
               pairs.GetElement(3) +
               pairs.GetElement(4) +
               pairs.GetElement(5) +
               pairs.GetElement(6) +
               pairs.GetElement(7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DistanceSquaredRiskyAvx2(short* vector, short* query)
    {
        var diff = Avx2.Subtract(Avx.LoadVector256(query), Avx.LoadVector256(vector));
        var pairs = Avx2.MultiplyAddAdjacent(diff, diff);
        return (long)pairs.GetElement(0) +
               pairs.GetElement(1) +
               pairs.GetElement(2) +
               pairs.GetElement(3) +
               pairs.GetElement(4) +
               pairs.GetElement(5) +
               pairs.GetElement(6) +
               pairs.GetElement(7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DistanceSquaredRiskySse2(short* vector, short* query)
    {
        var diff0 = Sse2.Subtract(Sse2.LoadVector128(query), Sse2.LoadVector128(vector));
        var diff1 = Sse2.Subtract(Sse2.LoadVector128(query + 8), Sse2.LoadVector128(vector + 8));
        var pairs = Sse2.Add(Sse2.MultiplyAddAdjacent(diff0, diff0), Sse2.MultiplyAddAdjacent(diff1, diff1));
        return (long)pairs.GetElement(0) +
               pairs.GetElement(1) +
               pairs.GetElement(2) +
               pairs.GetElement(3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DistanceSquaredRiskyScalar(short* vector, ReadOnlySpan<short> query, long cutoff)
    {
        ref var q = ref MemoryMarshal.GetReference(query);
        long sum = 0;

        var d = (long)Unsafe.Add(ref q, 6) - vector[6];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 10) - vector[10];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 9) - vector[9];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 5) - vector[5];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 11) - vector[11];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 2) - vector[2];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 4) - vector[4];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 7) - vector[7];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = q - vector[0];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 1) - vector[1];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 8) - vector[8];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 12) - vector[12];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 3) - vector[3];
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)Unsafe.Add(ref q, 13) - vector[13];
        sum += d * d;

        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Label(uint id)
    {
        return *(_ptr + _labelsOffset + id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint BucketOffset(int key)
    {
        return Unsafe.ReadUnaligned<uint>(_ptr + _bucketOffsetsOffset + key * 4L);
    }

    private static void BuildProfileStats(
        byte* ptr,
        int count,
        long vectorsOffset,
        long labelsOffset,
        out ushort[] profileCounts,
        out byte[] profileLabelMasks)
    {
        profileCounts = new ushort[ProfileKeyCount];
        profileLabelMasks = new byte[ProfileKeyCount];

        for (uint id = 0; id < count; id++)
        {
            var vector = ptr + vectorsOffset + id * Constants.Dim * 2L;
            var key = ProfileKey(vector);
            if (profileCounts[key] < ushort.MaxValue)
            {
                profileCounts[key]++;
            }

            var label = *(ptr + labelsOffset + id);
            profileLabelMasks[key] |= label == 1 ? FraudMask : LegitMask;
        }
    }

    private static void BuildRiskyFallbackIndex(
        byte* ptr,
        int count,
        long vectorsOffset,
        long labelsOffset,
        in RiskyFallbackFilter filter,
        bool buildCompact,
        out uint[] ids,
        out short[] vectors,
        out byte[] labels,
        out int[] bucketOffsets,
        out int[] fineBucketOffsets,
        out int[] coarseFineOffsets,
        out int[] fineKeys)
    {
        var idList = new List<uint>(128_000);
        var fineKeyList = new List<int>(128_000);
        Span<int> counts = stackalloc int[Constants.BucketCount];
        var fineCounts = new int[RiskyFineBucketCount];

        for (uint id = 0; id < count; id++)
        {
            var vector = ptr + vectorsOffset + id * Constants.Dim * 2L;
            if (IsRiskyFallbackReference(vector, in filter))
            {
                var key = BucketKey(vector);
                var fineKey = RiskyFineBucketKey(vector, key);
                idList.Add(id);
                fineKeyList.Add(fineKey);
                counts[key]++;
                fineCounts[fineKey]++;
            }
        }

        bucketOffsets = new int[Constants.BucketCount + 1];
        for (var i = 0; i < Constants.BucketCount; i++)
        {
            bucketOffsets[i + 1] = bucketOffsets[i] + counts[i];
        }

        fineBucketOffsets = new int[RiskyFineBucketCount + 1];
        coarseFineOffsets = new int[Constants.BucketCount + 1];
        for (var i = 0; i < RiskyFineBucketCount; i++)
        {
            fineBucketOffsets[i + 1] = fineBucketOffsets[i] + fineCounts[i];
            if (fineCounts[i] != 0)
            {
                coarseFineOffsets[(i >> RiskyFineExtraBits) + 1]++;
            }
        }

        for (var i = 0; i < Constants.BucketCount; i++)
        {
            coarseFineOffsets[i + 1] += coarseFineOffsets[i];
        }

        fineKeys = new int[coarseFineOffsets[Constants.BucketCount]];
        var fineKeyPositions = new int[Constants.BucketCount];
        coarseFineOffsets.AsSpan(0, Constants.BucketCount).CopyTo(fineKeyPositions);
        for (var i = 0; i < RiskyFineBucketCount; i++)
        {
            if (fineCounts[i] != 0)
            {
                var coarse = i >> RiskyFineExtraBits;
                fineKeys[fineKeyPositions[coarse]++] = i;
            }
        }

        var writePositions = new int[RiskyFineBucketCount];
        fineBucketOffsets.AsSpan(0, RiskyFineBucketCount).CopyTo(writePositions);

        if (buildCompact)
        {
            ids = Array.Empty<uint>();
            vectors = new short[idList.Count * RiskyVectorStride];
            labels = new byte[idList.Count];

            for (var i = 0; i < idList.Count; i++)
            {
                var fineKey = fineKeyList[i];
                var writePosition = writePositions[fineKey]++;
                var vector = ptr + vectorsOffset + idList[i] * Constants.Dim * 2L;
                var vectorStart = writePosition * RiskyVectorStride;
                for (var dim = 0; dim < Constants.Dim; dim++)
                {
                    vectors[vectorStart + dim] = Unsafe.ReadUnaligned<short>(vector + dim * 2);
                }

                labels[writePosition] = *(ptr + labelsOffset + idList[i]);
            }

            return;
        }

        ids = new uint[idList.Count];
        vectors = Array.Empty<short>();
        labels = Array.Empty<byte>();

        for (var i = 0; i < idList.Count; i++)
        {
            var fineKey = fineKeyList[i];
            ids[writePositions[fineKey]++] = idList[i];
        }
    }

    private static bool IsRiskyFallbackReference(byte* vector, in RiskyFallbackFilter filter)
    {
        var amount = Unsafe.ReadUnaligned<short>(vector);
        if (amount < filter.AmountMin || amount > filter.AmountMax)
        {
            return false;
        }

        var installments = Unsafe.ReadUnaligned<short>(vector + 2);
        if (installments < filter.InstallmentsMin || installments > filter.InstallmentsMax)
        {
            return false;
        }

        if (Unsafe.ReadUnaligned<short>(vector + 4) < filter.RatioMin)
        {
            return false;
        }

        var kmHome = Unsafe.ReadUnaligned<short>(vector + 14);
        if (kmHome < filter.KmHomeMin || kmHome > filter.KmHomeMax)
        {
            return false;
        }

        var tx24h = Unsafe.ReadUnaligned<short>(vector + 16);
        if (tx24h < filter.Tx24hMin || tx24h > filter.Tx24hMax)
        {
            return false;
        }

        var merchantAverage = Unsafe.ReadUnaligned<short>(vector + 26);
        return merchantAverage >= filter.MerchantAverageMin && merchantAverage <= filter.MerchantAverageMax;
    }

    private static ushort BucketKey(byte* vector)
    {
        var amount = Vectorizer.Bucket8(Unsafe.ReadUnaligned<short>(vector));
        var ratio = Vectorizer.Bucket8(Unsafe.ReadUnaligned<short>(vector + 4));
        var kmHome = Vectorizer.Bucket8(Unsafe.ReadUnaligned<short>(vector + 14));
        var hour = Vectorizer.Bucket4(Unsafe.ReadUnaligned<short>(vector + 6));
        var noLast = Unsafe.ReadUnaligned<short>(vector + 10) < 0 ? 1 : 0;
        return (ushort)(amount | (ratio << 3) | (kmHome << 6) | (hour << 9) | (noLast << 11));
    }

    private static int RiskyFineBucketKey(byte* vector, int coarseKey)
    {
        var extra = Unsafe.ReadUnaligned<short>(vector + 18) > 0 ? 1 : 0;
        extra |= (Unsafe.ReadUnaligned<short>(vector + 20) > 0 ? 1 : 0) << 1;
        extra |= (Unsafe.ReadUnaligned<short>(vector + 22) > 0 ? 1 : 0) << 2;
        return (coarseKey << RiskyFineExtraBits) | extra;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RiskyBucketLowerBound(int key, ReadOnlySpan<short> query)
    {
        var amount = key & 7;
        var ratio = (key >> 3) & 7;
        var kmHome = (key >> 6) & 7;
        var hour = (key >> 9) & 3;
        var noLast = (key >> 11) & 1;

        long sum = 0;
        sum += BucketDistanceSquared(query[0], amount, 8);
        sum += BucketDistanceSquared(query[2], ratio, 8);
        sum += BucketDistanceSquared(query[7], kmHome, 8);
        sum += BucketDistanceSquared(query[3], hour, 4);
        sum += noLast == 0
            ? RangeDistanceSquared(query[5], 0, Constants.Scale)
            : RangeDistanceSquared(query[5], -Constants.Scale, -Constants.Scale);
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RiskyFineBucketLowerBound(int fineKey, ReadOnlySpan<short> query, long coarseLowerBound)
    {
        var extra = fineKey & ((1 << RiskyFineExtraBits) - 1);

        var sum = coarseLowerBound;
        sum += BinaryDistanceSquared(query[9], extra & 1);
        sum += BinaryDistanceSquared(query[10], (extra >> 1) & 1);
        sum += BinaryDistanceSquared(query[11], (extra >> 2) & 1);
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long BinaryDistanceSquared(short value, int bit)
    {
        var exact = bit == 0 ? 0 : Constants.Scale;
        return RangeDistanceSquared(value, exact, exact);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long BucketDistanceSquared(short value, int bucket, int divisions)
    {
        var min = bucket == 0 ? 0 : (bucket * (Constants.Scale + 1) + divisions - 1) / divisions;
        var max = bucket == divisions - 1 ? Constants.Scale : (((bucket + 1) * (Constants.Scale + 1)) - 1) / divisions;
        return RangeDistanceSquared(value, min, max);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RangeDistanceSquared(short value, int min, int max)
    {
        if (value < min)
        {
            var d = (long)min - value;
            return d * d;
        }

        if (value > max)
        {
            var d = (long)value - max;
            return d * d;
        }

        return 0;
    }

    private readonly struct RiskyFallbackFilter
    {
        public readonly int AmountMin;
        public readonly int AmountMax;
        public readonly int InstallmentsMin;
        public readonly int InstallmentsMax;
        public readonly int RatioMin;
        public readonly int KmHomeMin;
        public readonly int KmHomeMax;
        public readonly int Tx24hMin;
        public readonly int Tx24hMax;
        public readonly int MerchantAverageMin;
        public readonly int MerchantAverageMax;

        private RiskyFallbackFilter(
            int amountMin,
            int amountMax,
            int installmentsMin,
            int installmentsMax,
            int ratioMin,
            int kmHomeMin,
            int kmHomeMax,
            int tx24hMin,
            int tx24hMax,
            int merchantAverageMin,
            int merchantAverageMax)
        {
            AmountMin = amountMin;
            AmountMax = amountMax;
            InstallmentsMin = installmentsMin;
            InstallmentsMax = installmentsMax;
            RatioMin = ratioMin;
            KmHomeMin = kmHomeMin;
            KmHomeMax = kmHomeMax;
            Tx24hMin = tx24hMin;
            Tx24hMax = tx24hMax;
            MerchantAverageMin = merchantAverageMin;
            MerchantAverageMax = merchantAverageMax;
        }

        public static RiskyFallbackFilter FromEnvironment()
        {
            return new RiskyFallbackFilter(
                EnvInt("RISKY_AMOUNT_MIN", 350),
                EnvInt("RISKY_AMOUNT_MAX", 3200),
                EnvInt("RISKY_INSTALLMENTS_MIN", 2000),
                EnvInt("RISKY_INSTALLMENTS_MAX", 6500),
                EnvInt("RISKY_RATIO_MIN", 750),
                EnvInt("RISKY_KM_HOME_MIN", 200),
                EnvInt("RISKY_KM_HOME_MAX", 4300),
                EnvInt("RISKY_TX24H_MIN", 1500),
                EnvInt("RISKY_TX24H_MAX", 6000),
                EnvInt("RISKY_MERCHANT_AVG_MIN", 0),
                EnvInt("RISKY_MERCHANT_AVG_MAX", 450));
        }

        private static int EnvInt(string name, int fallback)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
        }
    }

    private static bool EnvBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is null ? fallback : value is "1" or "true" or "TRUE" or "yes" or "YES";
    }

    private static bool NeedsFullRiskyTiebreak(ReadOnlySpan<short> query, int frauds)
    {
        if (query[5] < 0 || query[9] <= 0 || query[10] != 0)
        {
            return false;
        }

        if (frauds >= 3)
        {
            return query[11] == 0 &&
                   query[12] <= 1700 &&
                   query[0] >= 500 && query[0] <= 900 &&
                   query[2] >= 1000 && query[2] <= 2200 &&
                   query[7] >= 350 && query[7] <= 900 &&
                   query[8] >= 1800 && query[8] <= 3000;
        }

        return IsHighRiskOnlineFallback(query);
    }

    private static bool IsHighRiskOnlineFallback(ReadOnlySpan<short> query)
    {
        return query[12] >= 8000 &&
               query[1] >= 5500 &&
               query[6] >= 1000 && query[6] <= 1700 &&
               query[7] >= 300 && query[7] <= 4200 &&
               query[8] >= 3800 && query[8] <= 6000 &&
               ((query[0] >= 450 && query[0] <= 600 && query[2] <= 1200) ||
                (query[0] >= 2500 && query[0] <= 3100 && query[2] >= 9000));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ProfileKey(ReadOnlySpan<short> vector)
    {
        var key = 0;
        key |= Vectorizer.Bucket16(vector[2]);
        key |= Vectorizer.Bucket8(vector[7]) << 4;
        key |= Vectorizer.Bucket4(vector[8]) << 7;
        key |= Vectorizer.Bucket4(vector[12]) << 9;
        key |= Vectorizer.Bucket4(vector[0]) << 11;
        key |= (vector[5] < 0 ? 1 : 0) << 13;
        key |= (vector[9] > 0 ? 1 : 0) << 14;
        key |= (vector[10] > 0 ? 1 : 0) << 15;
        key |= (vector[11] > 0 ? 1 : 0) << 16;
        key |= Vectorizer.Bucket4(vector[6]) << 17;
        key |= (vector[1] > 1000 ? 1 : 0) << 19;
        key |= Vectorizer.Bucket4(vector[13]) << 20;
        return key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ProfileKey(byte* vector)
    {
        var key = 0;
        key |= Vectorizer.Bucket16(Unsafe.ReadUnaligned<short>(vector + 4));
        key |= Vectorizer.Bucket8(Unsafe.ReadUnaligned<short>(vector + 14)) << 4;
        key |= Vectorizer.Bucket4(Unsafe.ReadUnaligned<short>(vector + 16)) << 7;
        key |= Vectorizer.Bucket4(Unsafe.ReadUnaligned<short>(vector + 24)) << 9;
        key |= Vectorizer.Bucket4(Unsafe.ReadUnaligned<short>(vector)) << 11;
        key |= (Unsafe.ReadUnaligned<short>(vector + 10) < 0 ? 1 : 0) << 13;
        key |= (Unsafe.ReadUnaligned<short>(vector + 18) > 0 ? 1 : 0) << 14;
        key |= (Unsafe.ReadUnaligned<short>(vector + 20) > 0 ? 1 : 0) << 15;
        key |= (Unsafe.ReadUnaligned<short>(vector + 22) > 0 ? 1 : 0) << 16;
        key |= Vectorizer.Bucket4(Unsafe.ReadUnaligned<short>(vector + 12)) << 17;
        key |= (Unsafe.ReadUnaligned<short>(vector + 2) > 1000 ? 1 : 0) << 19;
        key |= Vectorizer.Bucket4(Unsafe.ReadUnaligned<short>(vector + 26)) << 20;
        return key;
    }

    private static byte VolatileRead(byte* ptr)
    {
        return System.Threading.Volatile.Read(ref *ptr);
    }

    public void Dispose()
    {
        if (_ptr != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptr = null;
        }

        _accessor.Dispose();
        _mappedFile.Dispose();
    }
}
