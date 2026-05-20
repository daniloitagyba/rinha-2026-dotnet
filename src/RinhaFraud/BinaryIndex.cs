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
    private static ReadOnlySpan<byte> ExtensionMagic => "R26XDIR1"u8;
    private const int MadvHugePage = 14;
    private const int HeaderLength = 80;
    private const int ProfileKeyCount = 1 << 22;
    private const int RiskyVectorStride = 16;
    private const int RiskyFineExtraBits = 3;
    private const int RiskyFineBucketsPerCoarse = 1 << RiskyFineExtraBits;
    private const int RiskyFineBucketCount = Constants.BucketCount << RiskyFineExtraBits;
    private const int BlockLaneCount = 8;
    private const int BlockVectorStride = Constants.Dim * BlockLaneCount;
    private const byte LegitMask = 1;
    private const byte FraudMask = 2;
    private const uint SectionProfileCounts = 1;
    private const uint SectionProfileMasks = 2;
    private const uint SectionNeighborOrders = 3;
    private const uint SectionRiskyMeta = 4;
    private const uint SectionRiskyVectors = 5;
    private const uint SectionRiskyLabels = 6;
    private const uint SectionRiskyBucketOffsets = 7;
    private const uint SectionRiskyFineBucketOffsets = 8;
    private const uint SectionRiskyCoarseFineOffsets = 9;
    private const uint SectionRiskyFineKeys = 10;
    private const uint SectionRiskySoa = 11;
    private const uint SectionIvfOrders = 12;
    private const uint SectionBlockVectors = 13;
    private const uint SectionProfileFraudCounts = 14;
    private const uint SectionKdMeta = 15;
    private const uint SectionKdPartitions = 16;
    private const uint SectionKdNodes = 17;
    private const uint SectionKdVectors = 18;
    private const uint SectionKdLabels = 19;
    private const uint SectionKdIds = 20;
    private const int KdPartitionCount = 256;
    private const int KdVectorStride = 16;
    private const int KdPartitionRecordSize = 72;
    private const int KdNodeRecordSize = 80;

    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private byte* _ptr;
    private readonly long _length;
    private readonly int _count;
    private readonly long _vectorsOffset;
    private readonly long _labelsOffset;
    private readonly long _bucketOffsetsOffset;
    private readonly long _profileCountsOffset;
    private readonly long _profileLabelMasksOffset;
    private readonly long _profileFraudCountsOffset;
    private readonly ushort[] _profileCounts;
    private readonly ushort[] _profileFraudCounts;
    private readonly byte[] _profileLabelMasks;
    private readonly uint[] _riskyFallbackIds;
    private readonly short[] _riskyFallbackVectors;
    private readonly byte[] _riskyFallbackLabels;
    private readonly int[] _riskyBucketOffsets;
    private readonly int[] _riskyFineBucketOffsets;
    private readonly int[] _riskyCoarseFineOffsets;
    private readonly int[] _riskyFineKeys;
    private readonly long _neighborOrdersOffset;
    private readonly long _ivfOrdersOffset;
    private readonly long _blockVectorsOffset;
    private readonly int _kdNodeCount;
    private readonly int _kdMaxPartitions;
    private readonly long _kdPartitionsOffset;
    private readonly long _kdNodesOffset;
    private readonly long _kdVectorsOffset;
    private readonly long _kdLabelsOffset;
    private readonly long _kdIdsOffset;
    private readonly int _riskyMappedCount;
    private readonly int _riskyMappedFineKeyCount;
    private readonly long _riskyMappedVectorsOffset;
    private readonly long _riskyMappedLabelsOffset;
    private readonly long _riskyMappedBucketOffsetsOffset;
    private readonly long _riskyMappedFineBucketOffsetsOffset;
    private readonly long _riskyMappedCoarseFineOffsetsOffset;
    private readonly long _riskyMappedFineKeysOffset;
    private readonly long _riskyMappedSoaOffset;
    private readonly bool _useRiskyBuckets;
    private readonly bool _useRiskyFineBuckets;
    private readonly bool _useRiskyCompact;
    private readonly bool _useRiskySimd;
    private readonly bool _useRiskySoa;
    private readonly bool _useRiskyNativeFine;
    private readonly bool _useNativeAnn;
    private readonly bool _useNativeAnnDirect;
    private readonly bool _useNativeKd;
    private readonly bool _useIvfOrder;
    private readonly bool _useMappedSimd;
    private readonly bool _useBlockScan;

    public int RiskyFallbackCount => HasMappedRisky ? _riskyMappedCount : _useRiskyCompact ? _riskyFallbackLabels.Length : _riskyFallbackIds.Length;
    private bool HasMappedRisky => _riskyMappedCount > 0;

    private BinaryIndex(
        MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor accessor,
        byte* ptr,
        long length,
        int count,
        long vectorsOffset,
        long labelsOffset,
        long bucketOffsetsOffset,
        long profileCountsOffset,
        long profileLabelMasksOffset,
        long profileFraudCountsOffset,
        ushort[] profileCounts,
        ushort[] profileFraudCounts,
        byte[] profileLabelMasks,
        uint[] riskyFallbackIds,
        short[] riskyFallbackVectors,
        byte[] riskyFallbackLabels,
        int[] riskyBucketOffsets,
        int[] riskyFineBucketOffsets,
        int[] riskyCoarseFineOffsets,
        int[] riskyFineKeys,
        long neighborOrdersOffset,
        long ivfOrdersOffset,
        long blockVectorsOffset,
        int kdNodeCount,
        int kdMaxPartitions,
        long kdPartitionsOffset,
        long kdNodesOffset,
        long kdVectorsOffset,
        long kdLabelsOffset,
        long kdIdsOffset,
        int riskyMappedCount,
        int riskyMappedFineKeyCount,
        long riskyMappedVectorsOffset,
        long riskyMappedLabelsOffset,
        long riskyMappedBucketOffsetsOffset,
        long riskyMappedFineBucketOffsetsOffset,
        long riskyMappedCoarseFineOffsetsOffset,
        long riskyMappedFineKeysOffset,
        long riskyMappedSoaOffset,
        bool useRiskyBuckets,
        bool useRiskyFineBuckets,
        bool useRiskyCompact,
        bool useRiskySimd,
        bool useRiskySoa,
        bool useRiskyNativeFine,
        bool useNativeAnn,
        bool useNativeAnnDirect,
        bool useNativeKd,
        bool useIvfOrder,
        bool useMappedSimd,
        bool useBlockScan)
    {
        _mappedFile = mappedFile;
        _accessor = accessor;
        _ptr = ptr;
        _length = length;
        _count = count;
        _vectorsOffset = vectorsOffset;
        _labelsOffset = labelsOffset;
        _bucketOffsetsOffset = bucketOffsetsOffset;
        _profileCountsOffset = profileCountsOffset;
        _profileLabelMasksOffset = profileLabelMasksOffset;
        _profileFraudCountsOffset = profileFraudCountsOffset;
        _profileCounts = profileCounts;
        _profileFraudCounts = profileFraudCounts;
        _profileLabelMasks = profileLabelMasks;
        _riskyFallbackIds = riskyFallbackIds;
        _riskyFallbackVectors = riskyFallbackVectors;
        _riskyFallbackLabels = riskyFallbackLabels;
        _riskyBucketOffsets = riskyBucketOffsets;
        _riskyFineBucketOffsets = riskyFineBucketOffsets;
        _riskyCoarseFineOffsets = riskyCoarseFineOffsets;
        _riskyFineKeys = riskyFineKeys;
        _neighborOrdersOffset = neighborOrdersOffset;
        _ivfOrdersOffset = ivfOrdersOffset;
        _blockVectorsOffset = blockVectorsOffset;
        _kdNodeCount = kdNodeCount;
        _kdMaxPartitions = kdMaxPartitions;
        _kdPartitionsOffset = kdPartitionsOffset;
        _kdNodesOffset = kdNodesOffset;
        _kdVectorsOffset = kdVectorsOffset;
        _kdLabelsOffset = kdLabelsOffset;
        _kdIdsOffset = kdIdsOffset;
        _riskyMappedCount = riskyMappedCount;
        _riskyMappedFineKeyCount = riskyMappedFineKeyCount;
        _riskyMappedVectorsOffset = riskyMappedVectorsOffset;
        _riskyMappedLabelsOffset = riskyMappedLabelsOffset;
        _riskyMappedBucketOffsetsOffset = riskyMappedBucketOffsetsOffset;
        _riskyMappedFineBucketOffsetsOffset = riskyMappedFineBucketOffsetsOffset;
        _riskyMappedCoarseFineOffsetsOffset = riskyMappedCoarseFineOffsetsOffset;
        _riskyMappedFineKeysOffset = riskyMappedFineKeysOffset;
        _riskyMappedSoaOffset = riskyMappedSoaOffset;
        _useRiskyBuckets = useRiskyBuckets;
        _useRiskyFineBuckets = useRiskyFineBuckets;
        _useRiskyCompact = useRiskyCompact;
        _useRiskySimd = useRiskySimd;
        _useRiskySoa = useRiskySoa;
        _useRiskyNativeFine = useRiskyNativeFine;
        _useNativeAnn = useNativeAnn;
        _useNativeAnnDirect = useNativeAnnDirect;
        _useNativeKd = useNativeKd && kdPartitionsOffset != 0;
        _useIvfOrder = useIvfOrder;
        _useMappedSimd = useMappedSimd;
        _useBlockScan = useBlockScan && blockVectorsOffset != 0;
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
            var extensionDirectoryOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header[72..]));

            if ((version != 1 && version != 2) ||
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

            var riskyFallbackFilter = RiskyFallbackFilter.FromEnvironment();
            var useRiskyCompact = EnvBool("RISKY_COMPACT", true);
            var useRiskyBuckets = EnvBool("RISKY_BUCKETS", true);
            var useRiskyFineBuckets = EnvBool("RISKY_FINE_BUCKETS", true);
            var useRiskySimd = EnvBool("RISKY_SIMD", true);
            var useRiskySoa = EnvBool("RISKY_SOA", false);
            var useRiskyNativeFine = EnvBool("RISKY_NATIVE_FINE", false);
            var useNativeAnn = EnvBool("NATIVE_ANN", false);
            var useNativeAnnDirect = EnvBool("NATIVE_ANN_DIRECT", false);
            var useNativeKd = EnvBool("KDTREE_NATIVE", false);
            var kdMaxPartitions = Math.Clamp(EnvInt("KDTREE_MAX_PARTITIONS", KdPartitionCount), 1, KdPartitionCount);
            var useIvfOrder = EnvBool("IVF_ORDER", false);
            var useMappedSimd = EnvBool("MAPPED_SIMD", true);
            var useBlockScan = EnvBool("BLOCK_SCAN", false);
            var sections = version >= 2 && extensionDirectoryOffset != 0
                ? ReadExtensionSections(ptr, fileLength, extensionDirectoryOffset)
                : default;

            ushort[] profileCounts;
            ushort[] profileFraudCounts;
            byte[] profileLabelMasks;
            var profileCountsOffset = ValidSection(sections.ProfileCountsOffset, sections.ProfileCountsLength, ProfileKeyCount * 2L) ? sections.ProfileCountsOffset : 0;
            var profileLabelMasksOffset = ValidSection(sections.ProfileMasksOffset, sections.ProfileMasksLength, ProfileKeyCount) ? sections.ProfileMasksOffset : 0;
            var profileFraudCountsOffset = ValidSection(sections.ProfileFraudCountsOffset, sections.ProfileFraudCountsLength, ProfileKeyCount * 2L) ? sections.ProfileFraudCountsOffset : 0;
            if (profileCountsOffset != 0 && profileLabelMasksOffset != 0 && profileFraudCountsOffset != 0)
            {
                profileCounts = Array.Empty<ushort>();
                profileFraudCounts = Array.Empty<ushort>();
                profileLabelMasks = Array.Empty<byte>();
            }
            else
            {
                profileCountsOffset = 0;
                profileLabelMasksOffset = 0;
                profileFraudCountsOffset = 0;
                BuildProfileStats(ptr, count, vectorsOffset, labelsOffset, out profileCounts, out profileFraudCounts, out profileLabelMasks);
            }

            uint[] riskyFallbackIds;
            short[] riskyFallbackVectors;
            byte[] riskyFallbackLabels;
            int[] riskyBucketOffsets;
            int[] riskyFineBucketOffsets;
            int[] riskyCoarseFineOffsets;
            int[] riskyFineKeys;
            var riskyMappedCount = 0;
            var riskyMappedFineKeyCount = 0;
            var riskyMappedVectorsOffset = 0L;
            var riskyMappedLabelsOffset = 0L;
            var riskyMappedBucketOffsetsOffset = 0L;
            var riskyMappedFineBucketOffsetsOffset = 0L;
            var riskyMappedCoarseFineOffsetsOffset = 0L;
            var riskyMappedFineKeysOffset = 0L;
            var riskyMappedSoaOffset = 0L;
            if (useRiskyCompact &&
                TryReadRiskyMappedSections(
                    ptr,
                    sections,
                    in riskyFallbackFilter,
                    out riskyMappedCount,
                    out riskyMappedFineKeyCount,
                    out riskyMappedVectorsOffset,
                    out riskyMappedLabelsOffset,
                    out riskyMappedBucketOffsetsOffset,
                    out riskyMappedFineBucketOffsetsOffset,
                    out riskyMappedCoarseFineOffsetsOffset,
                    out riskyMappedFineKeysOffset,
                    out riskyMappedSoaOffset))
            {
                riskyFallbackIds = Array.Empty<uint>();
                riskyFallbackVectors = Array.Empty<short>();
                riskyFallbackLabels = Array.Empty<byte>();
                riskyBucketOffsets = Array.Empty<int>();
                riskyFineBucketOffsets = Array.Empty<int>();
                riskyCoarseFineOffsets = Array.Empty<int>();
                riskyFineKeys = Array.Empty<int>();
            }
            else
            {
                riskyMappedCount = 0;
                riskyMappedFineKeyCount = 0;
                riskyMappedVectorsOffset = 0;
                riskyMappedLabelsOffset = 0;
                riskyMappedBucketOffsetsOffset = 0;
                riskyMappedFineBucketOffsetsOffset = 0;
                riskyMappedCoarseFineOffsetsOffset = 0;
                riskyMappedFineKeysOffset = 0;
                riskyMappedSoaOffset = 0;

                BuildRiskyFallbackIndex(
                    ptr,
                    count,
                    vectorsOffset,
                    labelsOffset,
                    in riskyFallbackFilter,
                    useRiskyCompact,
                    out riskyFallbackIds,
                    out riskyFallbackVectors,
                    out riskyFallbackLabels,
                    out riskyBucketOffsets,
                    out riskyFineBucketOffsets,
                    out riskyCoarseFineOffsets,
                    out riskyFineKeys);
            }

            var neighborOrdersOffset = ValidSection(
                sections.NeighborOrdersOffset,
                sections.NeighborOrdersLength,
                Constants.BucketCount * Constants.BucketCount * 2L)
                ? sections.NeighborOrdersOffset
                : 0;
            var ivfOrdersOffset = ValidSection(
                sections.IvfOrdersOffset,
                sections.IvfOrdersLength,
                Constants.BucketCount * Constants.BucketCount * 2L)
                ? sections.IvfOrdersOffset
                : 0;
            var blockCount = (count + BlockLaneCount - 1) / BlockLaneCount;
            var blockVectorsOffset = ValidSection(
                sections.BlockVectorsOffset,
                sections.BlockVectorsLength,
                blockCount * BlockVectorStride * 2L)
                ? sections.BlockVectorsOffset
                : 0;
            var kdNodeCount = 0;
            var kdPartitionsOffset = 0L;
            var kdNodesOffset = 0L;
            var kdVectorsOffset = 0L;
            var kdLabelsOffset = 0L;
            var kdIdsOffset = 0L;
            if (useNativeKd &&
                !TryReadKdTreeSections(
                    ptr,
                    sections,
                    count,
                    out kdNodeCount,
                    out kdPartitionsOffset,
                    out kdNodesOffset,
                    out kdVectorsOffset,
                    out kdLabelsOffset,
                    out kdIdsOffset))
            {
                useNativeKd = false;
            }

            return new BinaryIndex(
                mappedFile,
                accessor,
                ptr,
                fileLength,
                count,
                vectorsOffset,
                labelsOffset,
                bucketOffsetsOffset,
                profileCountsOffset,
                profileLabelMasksOffset,
                profileFraudCountsOffset,
                profileCounts,
                profileFraudCounts,
                profileLabelMasks,
                riskyFallbackIds,
                riskyFallbackVectors,
                riskyFallbackLabels,
                riskyBucketOffsets,
                riskyFineBucketOffsets,
                riskyCoarseFineOffsets,
                riskyFineKeys,
                neighborOrdersOffset,
                ivfOrdersOffset,
                blockVectorsOffset,
                kdNodeCount,
                kdMaxPartitions,
                kdPartitionsOffset,
                kdNodesOffset,
                kdVectorsOffset,
                kdLabelsOffset,
                kdIdsOffset,
                riskyMappedCount,
                riskyMappedFineKeyCount,
                riskyMappedVectorsOffset,
                riskyMappedLabelsOffset,
                riskyMappedBucketOffsetsOffset,
                riskyMappedFineBucketOffsetsOffset,
                riskyMappedCoarseFineOffsetsOffset,
                riskyMappedFineKeysOffset,
                riskyMappedSoaOffset,
                useRiskyBuckets,
                useRiskyFineBuckets,
                useRiskyCompact,
                useRiskySimd,
                useRiskySoa,
                useRiskyNativeFine,
                useNativeAnn,
                useNativeAnnDirect,
                useNativeKd,
                useIvfOrder,
                useMappedSimd,
                useBlockScan);
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
        if (_useBlockScan && _blockVectorsOffset != 0)
        {
            var blockCount = (_count + BlockLaneCount - 1) / BlockLaneCount;
            checksum ^= PrefaultRange(_blockVectorsOffset, blockCount * BlockVectorStride * 2L);
        }
        else
        {
            checksum ^= PrefaultRange(_vectorsOffset, _count * Constants.Dim * 2L);
        }

        checksum ^= PrefaultRange(_labelsOffset, _count);
        checksum ^= PrefaultRange(_bucketOffsetsOffset, (Constants.BucketCount + 1L) * 4L);
        if (_profileCountsOffset != 0)
        {
            checksum ^= PrefaultRange(_profileCountsOffset, ProfileKeyCount * 2L);
        }

        if (_profileLabelMasksOffset != 0)
        {
            checksum ^= PrefaultRange(_profileLabelMasksOffset, ProfileKeyCount);
        }

        if (_profileFraudCountsOffset != 0)
        {
            checksum ^= PrefaultRange(_profileFraudCountsOffset, ProfileKeyCount * 2L);
        }

        if (_useIvfOrder && _ivfOrdersOffset != 0)
        {
            checksum ^= PrefaultRange(_ivfOrdersOffset, Constants.BucketCount * Constants.BucketCount * 2L);
        }
        else if (_neighborOrdersOffset != 0)
        {
            checksum ^= PrefaultRange(_neighborOrdersOffset, Constants.BucketCount * Constants.BucketCount * 2L);
        }

        if (_useNativeKd)
        {
            checksum ^= PrefaultRange(_kdPartitionsOffset, KdPartitionCount * KdPartitionRecordSize);
            checksum ^= PrefaultRange(_kdNodesOffset, _kdNodeCount * KdNodeRecordSize);
            checksum ^= PrefaultRange(_kdVectorsOffset, _count * KdVectorStride * 2L);
            checksum ^= PrefaultRange(_kdLabelsOffset, _count);
            checksum ^= PrefaultRange(_kdIdsOffset, _count * 4L);
        }

        if (HasMappedRisky)
        {
            checksum ^= PrefaultRange(_riskyMappedVectorsOffset, _riskyMappedCount * RiskyVectorStride * 2L);
            checksum ^= PrefaultRange(_riskyMappedLabelsOffset, _riskyMappedCount);
            checksum ^= PrefaultRange(_riskyMappedBucketOffsetsOffset, (Constants.BucketCount + 1L) * 4L);
            checksum ^= PrefaultRange(_riskyMappedFineBucketOffsetsOffset, (RiskyFineBucketCount + 1L) * 4L);
            checksum ^= PrefaultRange(_riskyMappedCoarseFineOffsetsOffset, (Constants.BucketCount + 1L) * 4L);
            checksum ^= PrefaultRange(_riskyMappedFineKeysOffset, _riskyMappedFineKeyCount * 4L);
            if (_useRiskySoa && _riskyMappedSoaOffset != 0)
            {
                checksum ^= PrefaultRange(_riskyMappedSoaOffset, _riskyMappedCount * Constants.Dim * 2L);
            }
        }

        return checksum;
    }

    public long AdviseHugePages()
    {
        if (!OperatingSystem.IsLinux() || _ptr == null || _length <= 0)
        {
            return 0;
        }

        try
        {
            return madvise((nint)_ptr, (nuint)_length, MadvHugePage) == 0 ? _length : 0;
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private int PrefaultRange(long offset, long length)
    {
        if (offset <= 0 || length <= 0)
        {
            return 0;
        }

        var checksum = 0;
        var end = offset + length;
        for (var pos = offset; pos < end; pos += 4096)
        {
            checksum ^= VolatileRead(_ptr + pos);
        }

        checksum ^= VolatileRead(_ptr + end - 1);
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

        if (_useNativeKd)
        {
            return ClassifyNativeKd(query);
        }

        if (_useNativeAnnDirect && _useNativeAnn && _useMappedSimd && Avx2.IsSupported)
        {
            return ClassifyFraudCountNativeAnnDirect(query, searchParams);
        }

        Span<long> topDist = stackalloc long[Constants.K];
        Span<byte> topLabel = stackalloc byte[Constants.K];
        topDist.Fill(long.MaxValue);

        var candidates = ConsiderCandidateSearch(query, searchParams, topDist, topLabel);
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
    private int ClassifyFraudCountNativeAnnDirect(ReadOnlySpan<short> query, in SearchParams searchParams)
    {
        var packed = ClassifyNativeAnn(query, searchParams);
        var candidates = packed >> 3;
        var frauds = packed & 7;
        if (candidates < Constants.K)
        {
            return ClassifyFlat(query);
        }

        if (!ShouldUseExactFallback(query, frauds, searchParams))
        {
            return frauds;
        }

        return searchParams.ExactFallback == SearchParams.ExactFallbackRisky
            ? ClassifyRiskyFlat(query, allowFullTiebreak: true)
            : ClassifyFlat(query);
    }

    [SkipLocalsInit]
    private int ClassifyNativeKd(ReadOnlySpan<short> query)
    {
        if (query.Length >= KdVectorStride)
        {
            fixed (short* queryPtr = query)
            {
                return NativeClassifyKdTreeAvx2(
                    _ptr + _kdPartitionsOffset,
                    _ptr + _kdNodesOffset,
                    (short*)(_ptr + _kdVectorsOffset),
                    _ptr + _kdLabelsOffset,
                    (int*)(_ptr + _kdIdsOffset),
                    queryPtr,
                    _kdNodeCount,
                    _kdMaxPartitions);
            }
        }

        Span<short> paddedQuery = stackalloc short[KdVectorStride];
        paddedQuery.Clear();
        query[..Math.Min(query.Length, Constants.Dim)].CopyTo(paddedQuery);

        fixed (short* queryPtr = paddedQuery)
        {
            return NativeClassifyKdTreeAvx2(
                _ptr + _kdPartitionsOffset,
                _ptr + _kdNodesOffset,
                (short*)(_ptr + _kdVectorsOffset),
                _ptr + _kdLabelsOffset,
                (int*)(_ptr + _kdIdsOffset),
                queryPtr,
                _kdNodeCount,
                _kdMaxPartitions);
        }
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

        if (_useNativeKd)
        {
            return Diagnostic(
                ClassifyNativeKd(query),
                ClassificationPath.NativeKdTree,
                profileKey,
                primaryBucket,
                candidates: 0,
                fallbackCandidates: 0,
                started);
        }

        Span<long> topDist = stackalloc long[Constants.K];
        Span<byte> topLabel = stackalloc byte[Constants.K];
        topDist.Fill(long.MaxValue);

        var candidates = ConsiderCandidateSearch(query, searchParams, topDist, topLabel);
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
    private int ConsiderCandidateSearch(
        ReadOnlySpan<short> query,
        in SearchParams searchParams,
        Span<long> topDist,
        Span<byte> topLabel)
    {
        if (_useNativeAnn && _useMappedSimd && Avx2.IsSupported)
        {
            return ConsiderCandidateSearchNative(query, searchParams, topDist, topLabel);
        }

        return ConsiderCandidateSearchManaged(query, searchParams, topDist, topLabel);
    }

    private int ConsiderCandidateSearchManaged(
        ReadOnlySpan<short> query,
        in SearchParams searchParams,
        Span<long> topDist,
        Span<byte> topLabel)
    {
        var candidates = 0;
        var neighborKeys = NeighborKeyOrderFor(query);
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
                return candidates;
            }

            if (candidates >= searchParams.EarlyCandidates && StrongDecision(topLabel, searchParams.EarlyEdgeFallback))
            {
                return candidates;
            }

            if (candidates >= searchParams.MinCandidates)
            {
                return candidates;
            }
        }

        return candidates;
    }

    [SkipLocalsInit]
    private int ConsiderCandidateSearchNative(
        ReadOnlySpan<short> query,
        in SearchParams searchParams,
        Span<long> topDist,
        Span<byte> topLabel)
    {
        Span<short> paddedQuery = stackalloc short[RiskyVectorStride];
        paddedQuery.Clear();
        query.CopyTo(paddedQuery);

        var neighborKeys = NeighborKeyOrderFor(query);
        fixed (short* queryPtr = paddedQuery)
        fixed (ushort* neighborKeysPtr = neighborKeys)
        fixed (long* topDistPtr = topDist)
        fixed (byte* topLabelPtr = topLabel)
        {
            return NativeConsiderAnnAvx2(
                (short*)(_ptr + _vectorsOffset),
                _ptr + _labelsOffset,
                (int*)(_ptr + _bucketOffsetsOffset),
                neighborKeysPtr,
                queryPtr,
                searchParams.EarlyCandidates,
                searchParams.MinCandidates,
                searchParams.MaxCandidates,
                searchParams.EarlyEdgeFallback ? 1 : 0,
                topDistPtr,
                topLabelPtr);
        }
    }

    [SkipLocalsInit]
    private int ClassifyNativeAnn(ReadOnlySpan<short> query, in SearchParams searchParams)
    {
        Span<short> paddedQuery = stackalloc short[RiskyVectorStride];
        paddedQuery.Clear();
        query.CopyTo(paddedQuery);

        var neighborKeys = NeighborKeyOrderFor(query);
        fixed (short* queryPtr = paddedQuery)
        fixed (ushort* neighborKeysPtr = neighborKeys)
        {
            return NativeClassifyAnnAvx2(
                (short*)(_ptr + _vectorsOffset),
                _ptr + _labelsOffset,
                (int*)(_ptr + _bucketOffsetsOffset),
                neighborKeysPtr,
                queryPtr,
                searchParams.EarlyCandidates,
                searchParams.MinCandidates,
                searchParams.MaxCandidates,
                searchParams.EarlyEdgeFallback ? 1 : 0);
        }
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

        var neighborKeys = NeighborKeyOrderFor(query);
        for (var neighborIndex = 0; neighborIndex < neighborKeys.Length; neighborIndex++)
        {
            var key = neighborKeys[neighborIndex];
            var start = RiskyBucketOffset(key);
            var end = RiskyBucketOffset(key + 1);
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

        if (_useRiskyNativeFine && HasMappedRisky && Avx2.IsSupported)
        {
            fallbackCandidates = ConsiderRiskyFineBucketedNative(query, topDist, topLabel);
            var nativeFrauds = CountFrauds(topLabel);
            if (allowFullTiebreak && NeedsFullRiskyTiebreak(query, nativeFrauds))
            {
                usedFullFlat = true;
                var flatFrauds = ClassifyFlat(query, topDist[Constants.K - 1], out var flatCandidates);
                fallbackCandidates += flatCandidates;
                return flatFrauds;
            }

            return nativeFrauds;
        }

        Span<int> orderedFineKeys = stackalloc int[RiskyFineBucketsPerCoarse];
        Span<long> orderedFineBounds = stackalloc long[RiskyFineBucketsPerCoarse];
        Span<long> amountBounds = stackalloc long[8];
        Span<long> ratioBounds = stackalloc long[8];
        Span<long> kmHomeBounds = stackalloc long[8];
        Span<long> hourBounds = stackalloc long[4];
        Span<long> lastBounds = stackalloc long[2];
        for (var bucket = 0; bucket < 8; bucket++)
        {
            amountBounds[bucket] = BucketDistanceSquared(query[0], bucket, 8);
            ratioBounds[bucket] = BucketDistanceSquared(query[2], bucket, 8);
            kmHomeBounds[bucket] = BucketDistanceSquared(query[7], bucket, 8);
        }

        for (var bucket = 0; bucket < 4; bucket++)
        {
            hourBounds[bucket] = BucketDistanceSquared(query[3], bucket, 4);
        }

        lastBounds[0] = RangeDistanceSquared(query[5], 0, Constants.Scale);
        lastBounds[1] = RangeDistanceSquared(query[5], -Constants.Scale, -Constants.Scale);

        Span<long> fineExtraBounds = stackalloc long[RiskyFineBucketsPerCoarse];
        for (var extra = 0; extra < RiskyFineBucketsPerCoarse; extra++)
        {
            fineExtraBounds[extra] =
                BinaryDistanceSquared(query[9], extra & 1) +
                BinaryDistanceSquared(query[10], (extra >> 1) & 1) +
                BinaryDistanceSquared(query[11], (extra >> 2) & 1);
        }

        var neighborKeys = NeighborKeyOrderFor(query);
        for (var neighborIndex = 0; neighborIndex < neighborKeys.Length; neighborIndex++)
        {
            var coarseKey = neighborKeys[neighborIndex];
            var fineStart = RiskyCoarseFineOffset(coarseKey);
            var fineEnd = RiskyCoarseFineOffset(coarseKey + 1);
            if (fineStart == fineEnd)
            {
                continue;
            }

            var coarseLowerBound =
                amountBounds[coarseKey & 7] +
                ratioBounds[(coarseKey >> 3) & 7] +
                kmHomeBounds[(coarseKey >> 6) & 7] +
                hourBounds[(coarseKey >> 9) & 3] +
                lastBounds[(coarseKey >> 11) & 1];
            if (coarseLowerBound >= topDist[Constants.K - 1])
            {
                continue;
            }

            var orderedFineCount = 0;
            for (var finePos = fineStart; finePos < fineEnd; finePos++)
            {
                var fineKey = RiskyFineKey(finePos);
                var lowerBound = coarseLowerBound + fineExtraBounds[fineKey & (RiskyFineBucketsPerCoarse - 1)];
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
                var start = RiskyFineBucketOffset(fineKey);
                var end = RiskyFineBucketOffset(fineKey + 1);
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
    private int ConsiderRiskyFineBucketedNative(ReadOnlySpan<short> query, Span<long> topDist, Span<byte> topLabel)
    {
        Span<short> paddedQuery = stackalloc short[RiskyVectorStride];
        paddedQuery.Clear();
        query.CopyTo(paddedQuery);

        var neighborKeys = NeighborKeyOrderFor(query);
        fixed (short* queryPtr = paddedQuery)
        fixed (long* topDistPtr = topDist)
        fixed (byte* topLabelPtr = topLabel)
        fixed (ushort* neighborKeysPtr = neighborKeys)
        {
            return NativeConsiderRiskyFineAvx2(
                (short*)(_ptr + _riskyMappedVectorsOffset),
                _ptr + _riskyMappedLabelsOffset,
                (int*)(_ptr + _riskyMappedFineBucketOffsetsOffset),
                (int*)(_ptr + _riskyMappedCoarseFineOffsetsOffset),
                (int*)(_ptr + _riskyMappedFineKeysOffset),
                neighborKeysPtr,
                queryPtr,
                topDistPtr,
                topLabelPtr);
        }
    }

    [SkipLocalsInit]
    private void ConsiderRiskyCompactRange(ReadOnlySpan<short> query, int start, int end, Span<long> topDist, Span<byte> topLabel)
    {
        if (_useRiskySoa && HasMappedRisky && _riskyMappedSoaOffset != 0 && Avx2.IsSupported && end - start >= 32)
        {
            ConsiderRiskySoaRangeAvx2(query, start, end, topDist, topLabel);
            return;
        }

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
        {
            if (HasMappedRisky)
            {
                var vectorBase = (short*)(_ptr + _riskyMappedVectorsOffset);
                var labelBase = _ptr + _riskyMappedLabelsOffset;
                for (var pos = start; pos < end; pos++)
                {
                    var dist = DistanceSquaredRiskyAvx2(vectorBase + pos * RiskyVectorStride, queryPtr);
                    if (dist >= topDist[4])
                    {
                        continue;
                    }

                    InsertRiskyCandidate(dist, labelBase[pos], topDist, topLabel);
                }

                return;
            }

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
    }

    [SkipLocalsInit]
    private void ConsiderRiskyCompactRangeSse2(ReadOnlySpan<short> query, int start, int end, Span<long> topDist, Span<byte> topLabel)
    {
        Span<short> paddedQuery = stackalloc short[RiskyVectorStride];
        paddedQuery.Clear();
        query.CopyTo(paddedQuery);

        fixed (short* queryPtr = paddedQuery)
        {
            if (HasMappedRisky)
            {
                var vectorBase = (short*)(_ptr + _riskyMappedVectorsOffset);
                var labelBase = _ptr + _riskyMappedLabelsOffset;
                for (var pos = start; pos < end; pos++)
                {
                    var dist = DistanceSquaredRiskySse2(vectorBase + pos * RiskyVectorStride, queryPtr);
                    if (dist >= topDist[4])
                    {
                        continue;
                    }

                    InsertRiskyCandidate(dist, labelBase[pos], topDist, topLabel);
                }

                return;
            }

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
    }

    private void ConsiderRiskyCompactRangeScalar(ReadOnlySpan<short> query, int start, int end, Span<long> topDist, Span<byte> topLabel)
    {
        if (HasMappedRisky)
        {
            var vectorBase = (short*)(_ptr + _riskyMappedVectorsOffset);
            var labelBase = _ptr + _riskyMappedLabelsOffset;
            for (var pos = start; pos < end; pos++)
            {
                var dist = DistanceSquaredRiskyScalar(vectorBase + pos * RiskyVectorStride, query, topDist[4]);
                if (dist >= topDist[4])
                {
                    continue;
                }

                InsertRiskyCandidate(dist, labelBase[pos], topDist, topLabel);
            }

            return;
        }

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

    [SkipLocalsInit]
    private void ConsiderRiskySoaRangeAvx2(ReadOnlySpan<short> query, int start, int end, Span<long> topDist, Span<byte> topLabel)
    {
        var soaBase = (short*)(_ptr + _riskyMappedSoaOffset);
        var vectorBase = (short*)(_ptr + _riskyMappedVectorsOffset);
        var labelBase = _ptr + _riskyMappedLabelsOffset;
        var pos = start;

        fixed (short* queryPtr = query)
        {
            for (; pos + 8 <= end; pos += 8)
            {
                var acc0 = Vector256<long>.Zero;
                var acc1 = Vector256<long>.Zero;
                for (var dim = 0; dim < Constants.Dim; dim++)
                {
                    var refs16 = Sse2.LoadVector128(soaBase + dim * _riskyMappedCount + pos);
                    var refs32 = Avx2.ConvertToVector256Int32(refs16);
                    var diff = Avx2.Subtract(refs32, Vector256.Create((int)queryPtr[dim]));
                    var squares = Avx2.MultiplyLow(diff, diff);
                    acc0 = Avx2.Add(acc0, Avx2.ConvertToVector256Int64(squares.GetLower()));
                    acc1 = Avx2.Add(acc1, Avx2.ConvertToVector256Int64(squares.GetUpper()));
                }

                var d0 = acc0.GetElement(0);
                if (d0 < topDist[4]) InsertRiskyCandidate(d0, labelBase[pos], topDist, topLabel);
                var d1 = acc0.GetElement(1);
                if (d1 < topDist[4]) InsertRiskyCandidate(d1, labelBase[pos + 1], topDist, topLabel);
                var d2 = acc0.GetElement(2);
                if (d2 < topDist[4]) InsertRiskyCandidate(d2, labelBase[pos + 2], topDist, topLabel);
                var d3 = acc0.GetElement(3);
                if (d3 < topDist[4]) InsertRiskyCandidate(d3, labelBase[pos + 3], topDist, topLabel);
                var d4 = acc1.GetElement(0);
                if (d4 < topDist[4]) InsertRiskyCandidate(d4, labelBase[pos + 4], topDist, topLabel);
                var d5 = acc1.GetElement(1);
                if (d5 < topDist[4]) InsertRiskyCandidate(d5, labelBase[pos + 5], topDist, topLabel);
                var d6 = acc1.GetElement(2);
                if (d6 < topDist[4]) InsertRiskyCandidate(d6, labelBase[pos + 6], topDist, topLabel);
                var d7 = acc1.GetElement(3);
                if (d7 < topDist[4]) InsertRiskyCandidate(d7, labelBase[pos + 7], topDist, topLabel);
            }
        }

        for (; pos < end; pos++)
        {
            var dist = DistanceSquaredRiskyScalar(vectorBase + pos * RiskyVectorStride, query, topDist[4]);
            if (dist >= topDist[4])
            {
                continue;
            }

            InsertRiskyCandidate(dist, labelBase[pos], topDist, topLabel);
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
    private static bool StrongDecision(ReadOnlySpan<byte> topLabel, bool includeEdges)
    {
        var frauds = 0;
        for (var i = 0; i < Constants.K; i++)
        {
            frauds += topLabel[i];
        }

        return includeEdges ? frauds <= 1 || frauds >= Constants.K - 1 : frauds == 0 || frauds == Constants.K;
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
        var mask = ProfileLabelMask(key);
        var profileCount = ProfileCount(key);
        if (mask == LegitMask)
        {
            if (profileCount < searchParams.ProfileLegitMinCount)
            {
                return false;
            }

            fraudCount = 0;
            return true;
        }

        if (mask == FraudMask)
        {
            if (profileCount < searchParams.ProfileFraudMinCount)
            {
                return false;
            }

            fraudCount = Constants.K;
            return true;
        }

        if (searchParams.ProfileDominantFastPath)
        {
            var profileFrauds = ProfileFraudCount(key);
            var profileLegits = Math.Max(0, profileCount - profileFrauds);
            if (profileFrauds >= searchParams.ProfileDominantMinCount &&
                profileLegits <= searchParams.ProfileDominantMaxOpposite)
            {
                fraudCount = Constants.K;
                return true;
            }

            if (profileLegits >= searchParams.ProfileDominantMinCount &&
                profileFrauds <= searchParams.ProfileDominantMaxOpposite)
            {
                fraudCount = 0;
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ConsiderCandidateRange(ReadOnlySpan<short> query, uint start, uint end, Span<long> topDist, Span<byte> topLabel)
    {
        if (_useBlockScan && Avx2.IsSupported)
        {
            ConsiderCandidateRangeBlockAvx2(query, start, end, topDist, topLabel);
            return;
        }

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

    [SkipLocalsInit]
    private void ConsiderCandidateRangeBlockAvx2(ReadOnlySpan<short> query, uint start, uint end, Span<long> topDist, Span<byte> topLabel)
    {
        Span<short> paddedQuery = stackalloc short[RiskyVectorStride];
        paddedQuery.Clear();
        query.CopyTo(paddedQuery);

        fixed (short* queryPtr = paddedQuery)
        {
            var vectorBase = (short*)(_ptr + _vectorsOffset);
            var blockBase = (short*)(_ptr + _blockVectorsOffset);
            var labelBase = _ptr + _labelsOffset;
            var id = start;

            while (id < end && (id & (BlockLaneCount - 1)) != 0)
            {
                var dist = DistanceSquaredMappedAvx2(vectorBase + id * Constants.Dim, queryPtr);
                if (dist < topDist[4])
                {
                    InsertRiskyCandidate(dist, labelBase[id], topDist, topLabel);
                }

                id++;
            }

            while (id + BlockLaneCount <= end)
            {
                ConsiderCandidateBlockAvx2(
                    blockBase + (id / BlockLaneCount) * BlockVectorStride,
                    labelBase + id,
                    queryPtr,
                    topDist,
                    topLabel);
                id += BlockLaneCount;
            }

            while (id < end)
            {
                var dist = DistanceSquaredMappedAvx2(vectorBase + id * Constants.Dim, queryPtr);
                if (dist < topDist[4])
                {
                    InsertRiskyCandidate(dist, labelBase[id], topDist, topLabel);
                }

                id++;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConsiderCandidateBlockAvx2(short* block, byte* labels, short* query, Span<long> topDist, Span<byte> topLabel)
    {
        var acc0 = Vector256<long>.Zero;
        var acc1 = Vector256<long>.Zero;

        for (var dim = 0; dim < Constants.Dim; dim++)
        {
            var refs16 = Sse2.LoadVector128(block + dim * BlockLaneCount);
            var refs32 = Avx2.ConvertToVector256Int32(refs16);
            var diff = Avx2.Subtract(refs32, Vector256.Create((int)query[dim]));
            var squares = Avx2.MultiplyLow(diff, diff);
            acc0 = Avx2.Add(acc0, Avx2.ConvertToVector256Int64(squares.GetLower()));
            acc1 = Avx2.Add(acc1, Avx2.ConvertToVector256Int64(squares.GetUpper()));

            if (dim == 7 && AllDistancesAtLeast(acc0, acc1, topDist[4]))
            {
                return;
            }
        }

        var d0 = acc0.GetElement(0);
        if (d0 < topDist[4]) InsertRiskyCandidate(d0, labels[0], topDist, topLabel);
        var d1 = acc0.GetElement(1);
        if (d1 < topDist[4]) InsertRiskyCandidate(d1, labels[1], topDist, topLabel);
        var d2 = acc0.GetElement(2);
        if (d2 < topDist[4]) InsertRiskyCandidate(d2, labels[2], topDist, topLabel);
        var d3 = acc0.GetElement(3);
        if (d3 < topDist[4]) InsertRiskyCandidate(d3, labels[3], topDist, topLabel);
        var d4 = acc1.GetElement(0);
        if (d4 < topDist[4]) InsertRiskyCandidate(d4, labels[4], topDist, topLabel);
        var d5 = acc1.GetElement(1);
        if (d5 < topDist[4]) InsertRiskyCandidate(d5, labels[5], topDist, topLabel);
        var d6 = acc1.GetElement(2);
        if (d6 < topDist[4]) InsertRiskyCandidate(d6, labels[6], topDist, topLabel);
        var d7 = acc1.GetElement(3);
        if (d7 < topDist[4]) InsertRiskyCandidate(d7, labels[7], topDist, topLabel);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AllDistancesAtLeast(Vector256<long> acc0, Vector256<long> acc1, long cutoff)
    {
        return acc0.GetElement(0) >= cutoff &&
               acc0.GetElement(1) >= cutoff &&
               acc0.GetElement(2) >= cutoff &&
               acc0.GetElement(3) >= cutoff &&
               acc1.GetElement(0) >= cutoff &&
               acc1.GetElement(1) >= cutoff &&
               acc1.GetElement(2) >= cutoff &&
               acc1.GetElement(3) >= cutoff;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ProfileCount(int key)
    {
        return _profileCountsOffset != 0
            ? Unsafe.ReadUnaligned<ushort>(_ptr + _profileCountsOffset + key * 2L)
            : _profileCounts[key];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ProfileFraudCount(int key)
    {
        return _profileFraudCountsOffset != 0
            ? Unsafe.ReadUnaligned<ushort>(_ptr + _profileFraudCountsOffset + key * 2L)
            : _profileFraudCounts[key];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ProfileLabelMask(int key)
    {
        return _profileLabelMasksOffset != 0
            ? *(_ptr + _profileLabelMasksOffset + key)
            : _profileLabelMasks[key];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RiskyBucketOffset(int key)
    {
        return HasMappedRisky
            ? Unsafe.ReadUnaligned<int>(_ptr + _riskyMappedBucketOffsetsOffset + key * 4L)
            : _riskyBucketOffsets[key];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RiskyFineBucketOffset(int key)
    {
        return HasMappedRisky
            ? Unsafe.ReadUnaligned<int>(_ptr + _riskyMappedFineBucketOffsetsOffset + key * 4L)
            : _riskyFineBucketOffsets[key];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RiskyCoarseFineOffset(int key)
    {
        return HasMappedRisky
            ? Unsafe.ReadUnaligned<int>(_ptr + _riskyMappedCoarseFineOffsetsOffset + key * 4L)
            : _riskyCoarseFineOffsets[key];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RiskyFineKey(int pos)
    {
        return HasMappedRisky
            ? Unsafe.ReadUnaligned<int>(_ptr + _riskyMappedFineKeysOffset + pos * 4L)
            : _riskyFineKeys[pos];
    }

    private ReadOnlySpan<ushort> NeighborKeyOrderFor(ReadOnlySpan<short> query)
    {
        var key = Vectorizer.BucketKey(query);
        if (_useIvfOrder && _ivfOrdersOffset != 0)
        {
            return new ReadOnlySpan<ushort>(_ptr + _ivfOrdersOffset + key * Constants.BucketCount * 2L, Constants.BucketCount);
        }

        if (_neighborOrdersOffset != 0)
        {
            return new ReadOnlySpan<ushort>(_ptr + _neighborOrdersOffset + key * Constants.BucketCount * 2L, Constants.BucketCount);
        }

        return Vectorizer.NeighborKeyOrderForBucketKey(key);
    }

    private static bool ValidSection(long offset, long length, long expectedLength)
    {
        return offset > 0 && length == expectedLength;
    }

    private static SectionDirectory ReadExtensionSections(byte* ptr, long fileLength, long directoryOffset)
    {
        if (directoryOffset < HeaderLength || directoryOffset + 16 > fileLength)
        {
            throw new InvalidOperationException("bad index extension directory offset");
        }

        var header = new ReadOnlySpan<byte>(ptr + directoryOffset, 16);
        if (!header[..8].SequenceEqual(ExtensionMagic))
        {
            throw new InvalidOperationException("bad index extension directory magic");
        }

        var sectionCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]));
        if ((uint)sectionCount > 64)
        {
            throw new InvalidOperationException("too many index extension sections");
        }

        var entriesOffset = directoryOffset + 16;
        if (entriesOffset + sectionCount * 24L > fileLength)
        {
            throw new InvalidOperationException("index extension directory out of bounds");
        }

        var sections = default(SectionDirectory);
        for (var i = 0; i < sectionCount; i++)
        {
            var entry = ptr + entriesOffset + i * 24L;
            var type = Unsafe.ReadUnaligned<uint>(entry);
            var offset = checked((long)Unsafe.ReadUnaligned<ulong>(entry + 8));
            var length = checked((long)Unsafe.ReadUnaligned<ulong>(entry + 16));
            if (offset < HeaderLength || length < 0 || offset + length > fileLength)
            {
                throw new InvalidOperationException("index extension section out of bounds");
            }

            switch (type)
            {
                case SectionProfileCounts:
                    sections.ProfileCountsOffset = offset;
                    sections.ProfileCountsLength = length;
                    break;
                case SectionProfileMasks:
                    sections.ProfileMasksOffset = offset;
                    sections.ProfileMasksLength = length;
                    break;
                case SectionNeighborOrders:
                    sections.NeighborOrdersOffset = offset;
                    sections.NeighborOrdersLength = length;
                    break;
                case SectionRiskyMeta:
                    sections.RiskyMetaOffset = offset;
                    sections.RiskyMetaLength = length;
                    break;
                case SectionRiskyVectors:
                    sections.RiskyVectorsOffset = offset;
                    sections.RiskyVectorsLength = length;
                    break;
                case SectionRiskyLabels:
                    sections.RiskyLabelsOffset = offset;
                    sections.RiskyLabelsLength = length;
                    break;
                case SectionRiskyBucketOffsets:
                    sections.RiskyBucketOffsetsOffset = offset;
                    sections.RiskyBucketOffsetsLength = length;
                    break;
                case SectionRiskyFineBucketOffsets:
                    sections.RiskyFineBucketOffsetsOffset = offset;
                    sections.RiskyFineBucketOffsetsLength = length;
                    break;
                case SectionRiskyCoarseFineOffsets:
                    sections.RiskyCoarseFineOffsetsOffset = offset;
                    sections.RiskyCoarseFineOffsetsLength = length;
                    break;
                case SectionRiskyFineKeys:
                    sections.RiskyFineKeysOffset = offset;
                    sections.RiskyFineKeysLength = length;
                    break;
                case SectionRiskySoa:
                    sections.RiskySoaOffset = offset;
                    sections.RiskySoaLength = length;
                    break;
                case SectionIvfOrders:
                    sections.IvfOrdersOffset = offset;
                    sections.IvfOrdersLength = length;
                    break;
                case SectionBlockVectors:
                    sections.BlockVectorsOffset = offset;
                    sections.BlockVectorsLength = length;
                    break;
                case SectionProfileFraudCounts:
                    sections.ProfileFraudCountsOffset = offset;
                    sections.ProfileFraudCountsLength = length;
                    break;
                case SectionKdMeta:
                    sections.KdMetaOffset = offset;
                    sections.KdMetaLength = length;
                    break;
                case SectionKdPartitions:
                    sections.KdPartitionsOffset = offset;
                    sections.KdPartitionsLength = length;
                    break;
                case SectionKdNodes:
                    sections.KdNodesOffset = offset;
                    sections.KdNodesLength = length;
                    break;
                case SectionKdVectors:
                    sections.KdVectorsOffset = offset;
                    sections.KdVectorsLength = length;
                    break;
                case SectionKdLabels:
                    sections.KdLabelsOffset = offset;
                    sections.KdLabelsLength = length;
                    break;
                case SectionKdIds:
                    sections.KdIdsOffset = offset;
                    sections.KdIdsLength = length;
                    break;
            }
        }

        return sections;
    }

    private static bool TryReadKdTreeSections(
        byte* ptr,
        in SectionDirectory sections,
        int expectedVectorCount,
        out int nodeCount,
        out long partitionsOffset,
        out long nodesOffset,
        out long vectorsOffset,
        out long labelsOffset,
        out long idsOffset)
    {
        nodeCount = 0;
        partitionsOffset = 0;
        nodesOffset = 0;
        vectorsOffset = 0;
        labelsOffset = 0;
        idsOffset = 0;

        if (sections.KdMetaOffset == 0 || sections.KdMetaLength < 64)
        {
            return false;
        }

        var meta = new ReadOnlySpan<byte>(ptr + sections.KdMetaOffset, (int)sections.KdMetaLength);
        if (!meta[..4].SequenceEqual("KDT1"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(meta[4..]) != 1)
        {
            return false;
        }

        var partitionCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(meta[8..]));
        nodeCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(meta[12..]));
        var vectorCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(meta[16..]));
        var partitionRecordSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(meta[24..]));
        var nodeRecordSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(meta[28..]));
        var vectorStride = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(meta[32..]));
        if (partitionCount != KdPartitionCount ||
            nodeCount <= 0 ||
            vectorCount != expectedVectorCount ||
            partitionRecordSize != KdPartitionRecordSize ||
            nodeRecordSize != KdNodeRecordSize ||
            vectorStride != KdVectorStride)
        {
            return false;
        }

        if (!ValidSection(sections.KdPartitionsOffset, sections.KdPartitionsLength, KdPartitionCount * KdPartitionRecordSize) ||
            !ValidSection(sections.KdNodesOffset, sections.KdNodesLength, nodeCount * KdNodeRecordSize) ||
            !ValidSection(sections.KdVectorsOffset, sections.KdVectorsLength, vectorCount * KdVectorStride * 2L) ||
            !ValidSection(sections.KdLabelsOffset, sections.KdLabelsLength, vectorCount) ||
            !ValidSection(sections.KdIdsOffset, sections.KdIdsLength, vectorCount * 4L))
        {
            return false;
        }

        partitionsOffset = sections.KdPartitionsOffset;
        nodesOffset = sections.KdNodesOffset;
        vectorsOffset = sections.KdVectorsOffset;
        labelsOffset = sections.KdLabelsOffset;
        idsOffset = sections.KdIdsOffset;
        return true;
    }

    private static bool TryReadRiskyMappedSections(
        byte* ptr,
        in SectionDirectory sections,
        in RiskyFallbackFilter expectedFilter,
        out int count,
        out int fineKeyCount,
        out long vectorsOffset,
        out long labelsOffset,
        out long bucketOffsetsOffset,
        out long fineBucketOffsetsOffset,
        out long coarseFineOffsetsOffset,
        out long fineKeysOffset,
        out long soaOffset)
    {
        count = 0;
        fineKeyCount = 0;
        vectorsOffset = 0;
        labelsOffset = 0;
        bucketOffsetsOffset = 0;
        fineBucketOffsetsOffset = 0;
        coarseFineOffsetsOffset = 0;
        fineKeysOffset = 0;
        soaOffset = 0;

        if (sections.RiskyMetaOffset == 0 || sections.RiskyMetaLength < 64)
        {
            return false;
        }

        var meta = new ReadOnlySpan<byte>(ptr + sections.RiskyMetaOffset, (int)sections.RiskyMetaLength);
        if (!meta[..4].SequenceEqual("RSKY"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(meta[4..]) != 1)
        {
            return false;
        }

        count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(meta[8..]));
        var stride = BinaryPrimitives.ReadUInt32LittleEndian(meta[12..]);
        fineKeyCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(meta[16..]));
        if (count <= 0 || stride != RiskyVectorStride || fineKeyCount < 0)
        {
            return false;
        }

        var filter = new RiskyFallbackFilter(
            BinaryPrimitives.ReadInt32LittleEndian(meta[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(meta[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(meta[28..]),
            BinaryPrimitives.ReadInt32LittleEndian(meta[32..]),
            BinaryPrimitives.ReadInt32LittleEndian(meta[36..]),
            BinaryPrimitives.ReadInt32LittleEndian(meta[40..]),
            BinaryPrimitives.ReadInt32LittleEndian(meta[44..]),
            BinaryPrimitives.ReadInt32LittleEndian(meta[48..]),
            BinaryPrimitives.ReadInt32LittleEndian(meta[52..]),
            BinaryPrimitives.ReadInt32LittleEndian(meta[56..]),
            BinaryPrimitives.ReadInt32LittleEndian(meta[60..]));
        if (!filter.Equals(expectedFilter))
        {
            return false;
        }

        if (!ValidSection(sections.RiskyVectorsOffset, sections.RiskyVectorsLength, count * RiskyVectorStride * 2L) ||
            !ValidSection(sections.RiskyLabelsOffset, sections.RiskyLabelsLength, count) ||
            !ValidSection(sections.RiskyBucketOffsetsOffset, sections.RiskyBucketOffsetsLength, (Constants.BucketCount + 1L) * 4L) ||
            !ValidSection(sections.RiskyFineBucketOffsetsOffset, sections.RiskyFineBucketOffsetsLength, (RiskyFineBucketCount + 1L) * 4L) ||
            !ValidSection(sections.RiskyCoarseFineOffsetsOffset, sections.RiskyCoarseFineOffsetsLength, (Constants.BucketCount + 1L) * 4L) ||
            !ValidSection(sections.RiskyFineKeysOffset, sections.RiskyFineKeysLength, fineKeyCount * 4L) ||
            !ValidSection(sections.RiskySoaOffset, sections.RiskySoaLength, count * Constants.Dim * 2L))
        {
            return false;
        }

        vectorsOffset = sections.RiskyVectorsOffset;
        labelsOffset = sections.RiskyLabelsOffset;
        bucketOffsetsOffset = sections.RiskyBucketOffsetsOffset;
        fineBucketOffsetsOffset = sections.RiskyFineBucketOffsetsOffset;
        coarseFineOffsetsOffset = sections.RiskyCoarseFineOffsetsOffset;
        fineKeysOffset = sections.RiskyFineKeysOffset;
        soaOffset = sections.RiskySoaOffset;
        return true;
    }

    private static void BuildProfileStats(
        byte* ptr,
        int count,
        long vectorsOffset,
        long labelsOffset,
        out ushort[] profileCounts,
        out ushort[] profileFraudCounts,
        out byte[] profileLabelMasks)
    {
        profileCounts = new ushort[ProfileKeyCount];
        profileFraudCounts = new ushort[ProfileKeyCount];
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
            if (label == 1)
            {
                if (profileFraudCounts[key] < ushort.MaxValue)
                {
                    profileFraudCounts[key]++;
                }

                profileLabelMasks[key] |= FraudMask;
            }
            else
            {
                profileLabelMasks[key] |= LegitMask;
            }
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

    private struct SectionDirectory
    {
        public long ProfileCountsOffset;
        public long ProfileCountsLength;
        public long ProfileMasksOffset;
        public long ProfileMasksLength;
        public long ProfileFraudCountsOffset;
        public long ProfileFraudCountsLength;
        public long NeighborOrdersOffset;
        public long NeighborOrdersLength;
        public long RiskyMetaOffset;
        public long RiskyMetaLength;
        public long RiskyVectorsOffset;
        public long RiskyVectorsLength;
        public long RiskyLabelsOffset;
        public long RiskyLabelsLength;
        public long RiskyBucketOffsetsOffset;
        public long RiskyBucketOffsetsLength;
        public long RiskyFineBucketOffsetsOffset;
        public long RiskyFineBucketOffsetsLength;
        public long RiskyCoarseFineOffsetsOffset;
        public long RiskyCoarseFineOffsetsLength;
        public long RiskyFineKeysOffset;
        public long RiskyFineKeysLength;
        public long RiskySoaOffset;
        public long RiskySoaLength;
        public long IvfOrdersOffset;
        public long IvfOrdersLength;
        public long BlockVectorsOffset;
        public long BlockVectorsLength;
        public long KdMetaOffset;
        public long KdMetaLength;
        public long KdPartitionsOffset;
        public long KdPartitionsLength;
        public long KdNodesOffset;
        public long KdNodesLength;
        public long KdVectorsOffset;
        public long KdVectorsLength;
        public long KdLabelsOffset;
        public long KdLabelsLength;
        public long KdIdsOffset;
        public long KdIdsLength;
    }

    private static int EnvInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool EnvBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is null ? fallback : value is "1" or "true" or "TRUE" or "yes" or "YES";
    }

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int madvise(nint address, nuint length, int advice);

    [DllImport("rinha_native", EntryPoint = "rinha_classify_kdtree_avx2")]
    private static extern int NativeClassifyKdTreeAvx2(
        byte* partitions,
        byte* nodes,
        short* vectors,
        byte* labels,
        int* ids,
        short* query,
        int nodeCount,
        int maxPartitions);

    [DllImport("rinha_native", EntryPoint = "rinha_consider_ann_avx2")]
    private static extern int NativeConsiderAnnAvx2(
        short* vectors,
        byte* labels,
        int* bucketOffsets,
        ushort* neighborKeys,
        short* query,
        int earlyCandidates,
        int minCandidates,
        int maxCandidates,
        int earlyEdgeFallback,
        long* topDist,
        byte* topLabel);

    [DllImport("rinha_native", EntryPoint = "rinha_classify_ann_avx2")]
    private static extern int NativeClassifyAnnAvx2(
        short* vectors,
        byte* labels,
        int* bucketOffsets,
        ushort* neighborKeys,
        short* query,
        int earlyCandidates,
        int minCandidates,
        int maxCandidates,
        int earlyEdgeFallback);

    [DllImport("rinha_native", EntryPoint = "rinha_consider_risky_fine_avx2")]
    private static extern int NativeConsiderRiskyFineAvx2(
        short* vectors,
        byte* labels,
        int* fineBucketOffsets,
        int* coarseFineOffsets,
        int* fineKeys,
        ushort* neighborKeys,
        short* query,
        long* topDist,
        byte* topLabel);

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
