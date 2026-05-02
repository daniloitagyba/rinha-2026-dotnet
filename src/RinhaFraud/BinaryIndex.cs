namespace RinhaFraud;

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

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
    private readonly long _bucketItemsOffset;
    private readonly ushort[] _profileCounts;
    private readonly byte[] _profileLabelMasks;

    private BinaryIndex(
        MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor accessor,
        byte* ptr,
        long length,
        int count,
        long vectorsOffset,
        long labelsOffset,
        long bucketOffsetsOffset,
        long bucketItemsOffset,
        ushort[] profileCounts,
        byte[] profileLabelMasks)
    {
        _mappedFile = mappedFile;
        _accessor = accessor;
        _ptr = ptr;
        _length = length;
        _count = count;
        _vectorsOffset = vectorsOffset;
        _labelsOffset = labelsOffset;
        _bucketOffsetsOffset = bucketOffsetsOffset;
        _bucketItemsOffset = bucketItemsOffset;
        _profileCounts = profileCounts;
        _profileLabelMasks = profileLabelMasks;
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

            return new BinaryIndex(
                mappedFile,
                accessor,
                ptr,
                fileLength,
                count,
                vectorsOffset,
                labelsOffset,
                bucketOffsetsOffset,
                bucketItemsOffset,
                profileCounts,
                profileLabelMasks);
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

    public int ClassifyFraudCount(ReadOnlySpan<short> query, in SearchParams searchParams)
    {
        if (TryProfileFastDecision(query, searchParams, out var fastFraudCount))
        {
            return fastFraudCount;
        }

        Span<long> topDist = stackalloc long[Constants.K];
        Span<byte> topLabel = stackalloc byte[Constants.K];
        topDist.Fill(long.MaxValue);

        if (searchParams.Flat)
        {
            for (uint id = 0; id < _count; id++)
            {
                Consider(id, query, topDist, topLabel);
            }
        }
        else
        {
            Span<byte> seen = stackalloc byte[Constants.BucketCount];
            seen.Clear();
            var candidates = 0;
            var amount = Vectorizer.Bucket8(query[0]);
            var ratio = Vectorizer.Bucket8(query[2]);
            var kmHome = Vectorizer.Bucket8(query[7]);
            var hour = Vectorizer.Bucket4(query[3]);
            var noLast = query[5] < 0 ? 1 : 0;

            for (var radius = 0; radius < 8; radius++)
            {
                for (var a = Math.Max(amount - radius, 0); a <= Math.Min(amount + radius, 7); a++)
                {
                    for (var r = Math.Max(ratio - radius, 0); r <= Math.Min(ratio + radius, 7); r++)
                    {
                        for (var k = Math.Max(kmHome - radius, 0); k <= Math.Min(kmHome + radius, 7); k++)
                        {
                            for (var h = Math.Max(hour - radius, 0); h <= Math.Min(hour + radius, 3); h++)
                            {
                                var lastStart = radius >= 2 ? 0 : noLast;
                                var lastEnd = radius >= 2 ? 1 : noLast;
                                for (var last = lastStart; last <= lastEnd; last++)
                                {
                                    var key = a | (r << 3) | (k << 6) | (h << 9) | (last << 11);
                                    if (seen[key] != 0)
                                    {
                                        continue;
                                    }

                                    seen[key] = 1;
                                    var start = BucketOffset(key);
                                    var end = BucketOffset(key + 1);
                                    for (var itemPos = start; itemPos < end; itemPos++)
                                    {
                                        var id = BucketItem(itemPos);
                                        Consider(id, query, topDist, topLabel);
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
                            }
                        }
                    }
                }
            }

CandidateSearchDone:
            if (candidates < Constants.K)
            {
                for (uint id = 0; id < _count; id++)
                {
                    Consider(id, query, topDist, topLabel);
                }
            }
        }

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

    private static bool StrongDecision(ReadOnlySpan<byte> topLabel)
    {
        var frauds = 0;
        for (var i = 0; i < Constants.K; i++)
        {
            frauds += topLabel[i];
        }

        return frauds <= 1 || frauds >= 4;
    }

    private bool TryProfileFastDecision(ReadOnlySpan<short> query, in SearchParams searchParams, out int fraudCount)
    {
        fraudCount = 0;
        if (!searchParams.ProfileFastPath)
        {
            return false;
        }

        var key = ProfileKey(query);
        if (_profileCounts[key] < searchParams.ProfileMinCount)
        {
            return false;
        }

        var mask = _profileLabelMasks[key];
        if (mask == LegitMask)
        {
            fraudCount = 0;
            return true;
        }

        if (mask == FraudMask)
        {
            fraudCount = Constants.K;
            return true;
        }

        return false;
    }

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
        long sum = 0;

        var d = (long)query[6] - Unsafe.ReadUnaligned<short>(vector + 12);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[10] - Unsafe.ReadUnaligned<short>(vector + 20);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[9] - Unsafe.ReadUnaligned<short>(vector + 18);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[5] - Unsafe.ReadUnaligned<short>(vector + 10);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[11] - Unsafe.ReadUnaligned<short>(vector + 22);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[2] - Unsafe.ReadUnaligned<short>(vector + 4);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[4] - Unsafe.ReadUnaligned<short>(vector + 8);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[7] - Unsafe.ReadUnaligned<short>(vector + 14);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[0] - Unsafe.ReadUnaligned<short>(vector);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[1] - Unsafe.ReadUnaligned<short>(vector + 2);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[8] - Unsafe.ReadUnaligned<short>(vector + 16);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[12] - Unsafe.ReadUnaligned<short>(vector + 24);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[3] - Unsafe.ReadUnaligned<short>(vector + 6);
        sum += d * d;
        if (sum >= cutoff) return sum;

        d = (long)query[13] - Unsafe.ReadUnaligned<short>(vector + 26);
        sum += d * d;

        return sum;
    }

    private byte Label(uint id)
    {
        return *(_ptr + _labelsOffset + id);
    }

    private uint BucketOffset(int key)
    {
        return Unsafe.ReadUnaligned<uint>(_ptr + _bucketOffsetsOffset + key * 4L);
    }

    private uint BucketItem(uint pos)
    {
        return Unsafe.ReadUnaligned<uint>(_ptr + _bucketItemsOffset + pos * 4L);
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
