namespace RinhaFraud;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal unsafe sealed class BinaryIndex : IDisposable
{
    private static ReadOnlySpan<byte> Magic => "RINHA26I"u8;
    private const int HeaderLength = 80;
    private const int ProfileKeyCount = 1 << 22;
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

    public int RiskyFallbackCount => _riskyFallbackIds.Length;

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
        uint[] riskyFallbackIds)
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
            var riskyFallbackIds = BuildRiskyFallbackIds(ptr, count, vectorsOffset, in riskyFallbackFilter);

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
                riskyFallbackIds);
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
            for (var itemPos = start; itemPos < end; itemPos++)
            {
                Consider(itemPos, query, topDist, topLabel);
                candidates++;
                if (candidates >= searchParams.MaxCandidates)
                {
                    goto CandidateSearchDone;
                }
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
            var flatFrauds = ClassifyFlat(query);
            return Diagnostic(
                flatFrauds,
                ClassificationPath.FullFlatFallback,
                profileKey,
                primaryBucket,
                candidates: 0,
                fallbackCandidates: _count,
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
            for (var itemPos = start; itemPos < end; itemPos++)
            {
                Consider(itemPos, query, topDist, topLabel);
                candidates++;
                if (candidates >= searchParams.MaxCandidates)
                {
                    goto CandidateSearchDone;
                }
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
            var flatFrauds = ClassifyFlat(query);
            return Diagnostic(
                flatFrauds,
                ClassificationPath.FullFlatFallback,
                profileKey,
                primaryBucket,
                candidates,
                _count,
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

        var fallbackFrauds = ClassifyFlat(query);
        return Diagnostic(
            fallbackFrauds,
            ClassificationPath.FullFlatFallback,
            profileKey,
            primaryBucket,
            candidates,
            _count,
            started);
    }

    [SkipLocalsInit]
    private int ClassifyFlat(ReadOnlySpan<short> query)
    {
        Span<long> topDist = stackalloc long[Constants.K];
        Span<byte> topLabel = stackalloc byte[Constants.K];
        topDist.Fill(long.MaxValue);

        for (uint id = 0; id < _count; id++)
        {
            Consider(id, query, topDist, topLabel);
        }

        return CountFrauds(topLabel);
    }

    [SkipLocalsInit]
    private int ClassifyRiskyFlat(ReadOnlySpan<short> query, bool allowFullTiebreak)
    {
        if (_riskyFallbackIds.Length < Constants.K)
        {
            return ClassifyFlat(query);
        }

        Span<long> topDist = stackalloc long[Constants.K];
        Span<byte> topLabel = stackalloc byte[Constants.K];
        topDist.Fill(long.MaxValue);

        foreach (var id in _riskyFallbackIds)
        {
            Consider(id, query, topDist, topLabel);
        }

        var frauds = CountFrauds(topLabel);
        return allowFullTiebreak && NeedsFullRiskyTiebreak(query, frauds) ? ClassifyFlat(query) : frauds;
    }

    [SkipLocalsInit]
    private int ClassifyRiskyFlatForDiagnostics(ReadOnlySpan<short> query, bool allowFullTiebreak, out bool usedFullFlat, out int fallbackCandidates)
    {
        usedFullFlat = false;
        if (_riskyFallbackIds.Length < Constants.K)
        {
            usedFullFlat = true;
            fallbackCandidates = _count;
            return ClassifyFlat(query);
        }

        Span<long> topDist = stackalloc long[Constants.K];
        Span<byte> topLabel = stackalloc byte[Constants.K];
        topDist.Fill(long.MaxValue);

        foreach (var id in _riskyFallbackIds)
        {
            Consider(id, query, topDist, topLabel);
        }

        fallbackCandidates = _riskyFallbackIds.Length;
        var frauds = CountFrauds(topLabel);
        if (allowFullTiebreak && NeedsFullRiskyTiebreak(query, frauds))
        {
            usedFullFlat = true;
            fallbackCandidates += _count;
            return ClassifyFlat(query);
        }

        return frauds;
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

    private static uint[] BuildRiskyFallbackIds(byte* ptr, int count, long vectorsOffset, in RiskyFallbackFilter filter)
    {
        var ids = new List<uint>(128_000);
        for (uint id = 0; id < count; id++)
        {
            var vector = ptr + vectorsOffset + id * Constants.Dim * 2L;
            if (IsRiskyFallbackReference(vector, in filter))
            {
                ids.Add(id);
            }
        }

        return ids.ToArray();
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
