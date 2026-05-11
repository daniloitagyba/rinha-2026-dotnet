namespace RinhaFraud;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

internal static class BinaryIndexBuilder
{
    private static ReadOnlySpan<byte> Magic => "RINHA26I"u8;
    private static ReadOnlySpan<byte> ExtensionMagic => "R26XDIR1"u8;
    private const uint Version = 2;
    private const int HeaderLength = 80;
    private const int RiskyVectorStride = 16;
    private const int RiskyFineExtraBits = 3;
    private const int RiskyFineBucketsPerCoarse = 1 << RiskyFineExtraBits;
    private const int RiskyFineBucketCount = Constants.BucketCount << RiskyFineExtraBits;
    private const int ProfileKeyCount = 1 << 22;

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

    public static void Build(string outputPath, Stream input)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var vectors = new List<short>(3_000_000 * Constants.Dim);
        var labels = new List<byte>(3_000_000);
        var keys = new List<ushort>(3_000_000);
        Span<uint> bucketCounts = stackalloc uint[Constants.BucketCount];
        var scanner = new JsonScanner(input);
        Span<short> vector = stackalloc short[Constants.Dim];

        while (scanner.Find("\"vector\""u8))
        {
            scanner.ExpectUntil((byte)'[');
            for (var i = 0; i < Constants.Dim; i++)
            {
                scanner.SkipWhiteSpaceAndCommas();
                vector[i] = Vectorizer.QuantizeReference(scanner.ReadNumber());
            }

            scanner.FindRequired("\"label\""u8);
            scanner.ExpectUntil((byte)':');
            scanner.SkipWhiteSpace();
            var label = scanner.ReadLabel();
            var key = Vectorizer.BucketKey(vector);
            bucketCounts[key]++;

            for (var i = 0; i < Constants.Dim; i++)
            {
                vectors.Add(vector[i]);
            }

            labels.Add(label);
            keys.Add(key);
        }

        if (labels.Count == 0)
        {
            throw new InvalidOperationException("no reference vectors found");
        }

        var offsets = new uint[Constants.BucketCount + 1];
        for (var i = 0; i < Constants.BucketCount; i++)
        {
            offsets[i + 1] = offsets[i] + bucketCounts[i];
        }

        var writePositions = new uint[Constants.BucketCount];
        offsets.AsSpan(0, Constants.BucketCount).CopyTo(writePositions);
        var items = new uint[labels.Count];
        for (var id = 0; id < keys.Count; id++)
        {
            var key = keys[id];
            var itemPos = writePositions[key]++;
            items[(int)itemPos] = (uint)id;
        }

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        Span<byte> header = stackalloc byte[HeaderLength];
        output.Write(header);
        var vectorsOffset = output.Position;
        Span<byte> twoBytes = stackalloc byte[2];
        var vectorSpan = CollectionsMarshal.AsSpan(vectors);
        var labelSpan = CollectionsMarshal.AsSpan(labels);

        foreach (var originalId in items)
        {
            var vectorStart = checked((int)originalId * Constants.Dim);
            for (var dim = 0; dim < Constants.Dim; dim++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(twoBytes, vectorSpan[vectorStart + dim]);
                output.Write(twoBytes);
            }
        }

        var labelsOffset = output.Position;
        foreach (var originalId in items)
        {
            output.WriteByte(labelSpan[(int)originalId]);
        }

        var bucketOffsetsOffset = output.Position;
        Span<byte> fourBytes = stackalloc byte[4];
        foreach (var value in offsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fourBytes, value);
            output.Write(fourBytes);
        }

        var bucketItemsOffset = output.Position;
        for (uint id = 0; id < labels.Count; id++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fourBytes, id);
            output.Write(fourBytes);
        }

        var extensionDirectoryOffset = WriteExtensionSections(output, vectorSpan, labelSpan, items);
        var fileLength = output.Position;
        output.Position = 0;
        WriteHeader(output, labels.Count, vectorsOffset, labelsOffset, bucketOffsetsOffset, bucketItemsOffset, fileLength, extensionDirectoryOffset);
        Console.Error.WriteLine($"indexed {labels.Count} vectors into {outputPath} ({Constants.BucketCount} buckets, {fileLength} bytes)");
    }

    private static long WriteExtensionSections(
        FileStream output,
        ReadOnlySpan<short> vectors,
        ReadOnlySpan<byte> labels,
        ReadOnlySpan<uint> orderedOriginalIds)
    {
        var sections = new List<SectionEntry>(16);
        WriteProfileSections(output, vectors, labels, orderedOriginalIds, sections);
        WriteNeighborOrdersSection(output, sections);
        WriteIvfOrdersSection(output, vectors, orderedOriginalIds, sections);
        WriteRiskySections(output, vectors, labels, orderedOriginalIds, sections);
        return WriteExtensionDirectory(output, sections);
    }

    private static void WriteProfileSections(
        FileStream output,
        ReadOnlySpan<short> vectors,
        ReadOnlySpan<byte> labels,
        ReadOnlySpan<uint> orderedOriginalIds,
        List<SectionEntry> sections)
    {
        var profileCounts = new ushort[ProfileKeyCount];
        var profileMasks = new byte[ProfileKeyCount];

        for (var mappedId = 0; mappedId < orderedOriginalIds.Length; mappedId++)
        {
            var originalId = (int)orderedOriginalIds[mappedId];
            var vector = vectors.Slice(originalId * Constants.Dim, Constants.Dim);
            var key = ProfileKey(vector);
            if (profileCounts[key] < ushort.MaxValue)
            {
                profileCounts[key]++;
            }

            profileMasks[key] |= labels[originalId] == 1 ? (byte)2 : (byte)1;
        }

        WriteSection(output, SectionProfileCounts, MemoryMarshal.AsBytes(profileCounts.AsSpan()), sections);
        WriteSection(output, SectionProfileMasks, profileMasks, sections);
    }

    private static void WriteNeighborOrdersSection(FileStream output, List<SectionEntry> sections)
    {
        var neighborOrders = Vectorizer.BuildNeighborKeyOrders();
        WriteSection(output, SectionNeighborOrders, MemoryMarshal.AsBytes(neighborOrders.AsSpan()), sections);
    }

    private static void WriteIvfOrdersSection(
        FileStream output,
        ReadOnlySpan<short> vectors,
        ReadOnlySpan<uint> orderedOriginalIds,
        List<SectionEntry> sections)
    {
        var sums = new long[Constants.BucketCount * Constants.Dim];
        var counts = new int[Constants.BucketCount];
        for (var mappedId = 0; mappedId < orderedOriginalIds.Length; mappedId++)
        {
            var originalId = (int)orderedOriginalIds[mappedId];
            var vector = vectors.Slice(originalId * Constants.Dim, Constants.Dim);
            var key = Vectorizer.BucketKey(vector);
            counts[key]++;
            var sumStart = key * Constants.Dim;
            for (var dim = 0; dim < Constants.Dim; dim++)
            {
                sums[sumStart + dim] += vector[dim];
            }
        }

        var centroids = new short[Constants.BucketCount * Constants.Dim];
        Span<short> bucketCenter = stackalloc short[Constants.Dim];
        for (var key = 0; key < Constants.BucketCount; key++)
        {
            var centroidStart = key * Constants.Dim;
            if (counts[key] == 0)
            {
                BucketCenter(key, bucketCenter);
                bucketCenter.CopyTo(centroids.AsSpan(centroidStart, Constants.Dim));
                continue;
            }

            var count = counts[key];
            for (var dim = 0; dim < Constants.Dim; dim++)
            {
                centroids[centroidStart + dim] = (short)Math.Clamp(
                    (int)Math.Round((double)sums[centroidStart + dim] / count, MidpointRounding.AwayFromZero),
                    -Constants.Scale,
                    Constants.Scale);
            }
        }

        var orders = new ushort[Constants.BucketCount * Constants.BucketCount];
        var scores = new ulong[Constants.BucketCount];
        for (var sourceKey = 0; sourceKey < Constants.BucketCount; sourceKey++)
        {
            for (var targetKey = 0; targetKey < Constants.BucketCount; targetKey++)
            {
                var distance = CentroidDistanceSquared(centroids, sourceKey, targetKey);
                scores[targetKey] = ((ulong)distance << 12) | (uint)targetKey;
            }

            Array.Sort(scores);
            var rowStart = sourceKey * Constants.BucketCount;
            for (var i = 0; i < Constants.BucketCount; i++)
            {
                orders[rowStart + i] = (ushort)(scores[i] & 0xfff);
            }
        }

        WriteSection(output, SectionIvfOrders, MemoryMarshal.AsBytes(orders.AsSpan()), sections);
    }

    private static void WriteRiskySections(
        FileStream output,
        ReadOnlySpan<short> vectors,
        ReadOnlySpan<byte> labels,
        ReadOnlySpan<uint> orderedOriginalIds,
        List<SectionEntry> sections)
    {
        var filter = RiskyFallbackFilter.PrecomputedDefault();
        var originalIds = new List<int>(128_000);
        var fineKeyList = new List<int>(128_000);
        Span<int> counts = stackalloc int[Constants.BucketCount];
        var fineCounts = new int[RiskyFineBucketCount];

        for (var mappedId = 0; mappedId < orderedOriginalIds.Length; mappedId++)
        {
            var originalId = (int)orderedOriginalIds[mappedId];
            var vector = vectors.Slice(originalId * Constants.Dim, Constants.Dim);
            if (!IsRiskyFallbackReference(vector, in filter))
            {
                continue;
            }

            var key = Vectorizer.BucketKey(vector);
            var fineKey = RiskyFineBucketKey(vector, key);
            originalIds.Add(originalId);
            fineKeyList.Add(fineKey);
            counts[key]++;
            fineCounts[fineKey]++;
        }

        var bucketOffsets = new int[Constants.BucketCount + 1];
        for (var i = 0; i < Constants.BucketCount; i++)
        {
            bucketOffsets[i + 1] = bucketOffsets[i] + counts[i];
        }

        var fineBucketOffsets = new int[RiskyFineBucketCount + 1];
        var coarseFineOffsets = new int[Constants.BucketCount + 1];
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

        var fineKeys = new int[coarseFineOffsets[Constants.BucketCount]];
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

        var orderedVectors = new short[originalIds.Count * RiskyVectorStride];
        var orderedLabels = new byte[originalIds.Count];
        var soaVectors = new short[originalIds.Count * Constants.Dim];
        var writePositions = new int[RiskyFineBucketCount];
        fineBucketOffsets.AsSpan(0, RiskyFineBucketCount).CopyTo(writePositions);

        for (var i = 0; i < originalIds.Count; i++)
        {
            var originalId = originalIds[i];
            var fineKey = fineKeyList[i];
            var writePosition = writePositions[fineKey]++;
            var source = vectors.Slice(originalId * Constants.Dim, Constants.Dim);
            var vectorStart = writePosition * RiskyVectorStride;
            for (var dim = 0; dim < Constants.Dim; dim++)
            {
                var value = source[dim];
                orderedVectors[vectorStart + dim] = value;
                soaVectors[dim * originalIds.Count + writePosition] = value;
            }

            orderedLabels[writePosition] = labels[originalId];
        }

        Span<byte> meta = stackalloc byte[80];
        "RSKY"u8.CopyTo(meta);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[8..], (uint)originalIds.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[12..], RiskyVectorStride);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[16..], (uint)fineKeys.Length);
        BinaryPrimitives.WriteInt32LittleEndian(meta[20..], filter.AmountMin);
        BinaryPrimitives.WriteInt32LittleEndian(meta[24..], filter.AmountMax);
        BinaryPrimitives.WriteInt32LittleEndian(meta[28..], filter.InstallmentsMin);
        BinaryPrimitives.WriteInt32LittleEndian(meta[32..], filter.InstallmentsMax);
        BinaryPrimitives.WriteInt32LittleEndian(meta[36..], filter.RatioMin);
        BinaryPrimitives.WriteInt32LittleEndian(meta[40..], filter.KmHomeMin);
        BinaryPrimitives.WriteInt32LittleEndian(meta[44..], filter.KmHomeMax);
        BinaryPrimitives.WriteInt32LittleEndian(meta[48..], filter.Tx24hMin);
        BinaryPrimitives.WriteInt32LittleEndian(meta[52..], filter.Tx24hMax);
        BinaryPrimitives.WriteInt32LittleEndian(meta[56..], filter.MerchantAverageMin);
        BinaryPrimitives.WriteInt32LittleEndian(meta[60..], filter.MerchantAverageMax);

        WriteSection(output, SectionRiskyMeta, meta, sections);
        WriteSection(output, SectionRiskyVectors, MemoryMarshal.AsBytes(orderedVectors.AsSpan()), sections);
        WriteSection(output, SectionRiskyLabels, orderedLabels, sections);
        WriteSection(output, SectionRiskyBucketOffsets, MemoryMarshal.AsBytes(bucketOffsets.AsSpan()), sections);
        WriteSection(output, SectionRiskyFineBucketOffsets, MemoryMarshal.AsBytes(fineBucketOffsets.AsSpan()), sections);
        WriteSection(output, SectionRiskyCoarseFineOffsets, MemoryMarshal.AsBytes(coarseFineOffsets.AsSpan()), sections);
        WriteSection(output, SectionRiskyFineKeys, MemoryMarshal.AsBytes(fineKeys.AsSpan()), sections);
        WriteSection(output, SectionRiskySoa, MemoryMarshal.AsBytes(soaVectors.AsSpan()), sections);
    }

    private static void WriteSection(FileStream output, uint type, ReadOnlySpan<byte> data, List<SectionEntry> sections)
    {
        var offset = output.Position;
        output.Write(data);
        sections.Add(new SectionEntry(type, offset, data.Length));
    }

    private static long WriteExtensionDirectory(FileStream output, List<SectionEntry> sections)
    {
        var offset = output.Position;
        Span<byte> header = stackalloc byte[16];
        ExtensionMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)sections.Count);
        output.Write(header);

        Span<byte> entry = stackalloc byte[24];
        foreach (var section in sections)
        {
            entry.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(entry, section.Type);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)section.Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], (ulong)section.Length);
            output.Write(entry);
        }

        return offset;
    }

    private static void WriteHeader(
        FileStream output,
        int count,
        long vectorsOffset,
        long labelsOffset,
        long bucketOffsetsOffset,
        long bucketItemsOffset,
        long fileLength,
        long extensionDirectoryOffset)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Constants.Dim);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], Constants.Scale);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], Constants.BucketCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)vectorsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], (ulong)labelsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], (ulong)bucketOffsetsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[56..], (ulong)bucketItemsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[64..], (ulong)fileLength);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], (ulong)extensionDirectoryOffset);
        output.Write(header);
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

    private static bool IsRiskyFallbackReference(ReadOnlySpan<short> vector, in RiskyFallbackFilter filter)
    {
        return vector[0] >= filter.AmountMin && vector[0] <= filter.AmountMax &&
               vector[1] >= filter.InstallmentsMin && vector[1] <= filter.InstallmentsMax &&
               vector[2] >= filter.RatioMin &&
               vector[7] >= filter.KmHomeMin && vector[7] <= filter.KmHomeMax &&
               vector[8] >= filter.Tx24hMin && vector[8] <= filter.Tx24hMax &&
               vector[13] >= filter.MerchantAverageMin && vector[13] <= filter.MerchantAverageMax;
    }

    private static int RiskyFineBucketKey(ReadOnlySpan<short> vector, int coarseKey)
    {
        var extra = vector[9] > 0 ? 1 : 0;
        extra |= (vector[10] > 0 ? 1 : 0) << 1;
        extra |= (vector[11] > 0 ? 1 : 0) << 2;
        return (coarseKey << RiskyFineExtraBits) | extra;
    }

    private static long CentroidDistanceSquared(short[] centroids, int leftKey, int rightKey)
    {
        var leftStart = leftKey * Constants.Dim;
        var rightStart = rightKey * Constants.Dim;
        long sum = 0;
        for (var dim = 0; dim < Constants.Dim; dim++)
        {
            var d = (long)centroids[leftStart + dim] - centroids[rightStart + dim];
            sum += d * d;
        }

        return sum;
    }

    private static void BucketCenter(int key, Span<short> output)
    {
        output.Clear();
        output[0] = BucketCenterValue(key & 7, 8);
        output[2] = BucketCenterValue((key >> 3) & 7, 8);
        output[7] = BucketCenterValue((key >> 6) & 7, 8);
        output[3] = BucketCenterValue((key >> 9) & 3, 4);
        output[5] = ((key >> 11) & 1) == 0 ? (short)(Constants.Scale / 2) : (short)-Constants.Scale;
        output[1] = output[4] = output[6] = output[8] = output[12] = output[13] = (short)(Constants.Scale / 2);
    }

    private static short BucketCenterValue(int bucket, int divisions)
    {
        var min = bucket == 0 ? 0 : (bucket * (Constants.Scale + 1) + divisions - 1) / divisions;
        var max = bucket == divisions - 1 ? Constants.Scale : (((bucket + 1) * (Constants.Scale + 1)) - 1) / divisions;
        return (short)((min + max) / 2);
    }

    private readonly struct SectionEntry
    {
        public readonly uint Type;
        public readonly long Offset;
        public readonly int Length;

        public SectionEntry(uint type, long offset, int length)
        {
            Type = type;
            Offset = offset;
            Length = length;
        }
    }

    private sealed class JsonScanner
    {
        private readonly Stream _input;
        private readonly byte[] _buffer;
        private int _pos;
        private int _len;

        public JsonScanner(Stream input)
        {
            _input = input;
            _buffer = new byte[64 * 1024];
        }

        public bool Find(ReadOnlySpan<byte> needle)
        {
            var matched = 0;
            while (TryReadByte(out var b))
            {
                if (b == needle[matched])
                {
                    matched++;
                    if (matched == needle.Length)
                    {
                        return true;
                    }
                }
                else
                {
                    matched = b == needle[0] ? 1 : 0;
                }
            }

            return false;
        }

        public void FindRequired(ReadOnlySpan<byte> needle)
        {
            if (!Find(needle))
            {
                throw new InvalidOperationException("unexpected EOF while scanning JSON");
            }
        }

        public void ExpectUntil(byte expected)
        {
            while (TryReadByte(out var b))
            {
                if (b == expected)
                {
                    return;
                }
            }

            throw new InvalidOperationException("unexpected EOF while scanning JSON");
        }

        public void SkipWhiteSpace()
        {
            while (TryReadByte(out var b))
            {
                if (IsWhiteSpace(b))
                {
                    continue;
                }

                Unread();
                return;
            }
        }

        public void SkipWhiteSpaceAndCommas()
        {
            while (TryReadByte(out var b))
            {
                if (IsWhiteSpace(b) || b == (byte)',')
                {
                    continue;
                }

                Unread();
                return;
            }

            throw new InvalidOperationException("unexpected EOF while reading vector");
        }

        public double ReadNumber()
        {
            Span<byte> token = stackalloc byte[64];
            var len = 0;
            while (TryReadByte(out var b))
            {
                if (IsDigit(b) || b is (byte)'-' or (byte)'+' or (byte)'.' or (byte)'e' or (byte)'E')
                {
                    if (len >= token.Length)
                    {
                        throw new InvalidOperationException("numeric token too long");
                    }

                    token[len++] = b;
                    continue;
                }

                Unread();
                break;
            }

            if (len == 0 ||
                !System.Buffers.Text.Utf8Parser.TryParse(token[..len], out double value, out var consumed) ||
                consumed != len)
            {
                throw new InvalidOperationException("bad numeric token");
            }

            return value;
        }

        public byte ReadLabel()
        {
            Span<byte> text = stackalloc byte[8];
            var len = ReadString(text);
            var label = text[..len];
            if (label.SequenceEqual("fraud"u8))
            {
                return 1;
            }

            if (label.SequenceEqual("legit"u8))
            {
                return 0;
            }

            throw new InvalidOperationException("unknown label");
        }

        private int ReadString(Span<byte> output)
        {
            if (!TryReadByte(out var first) || first != (byte)'"')
            {
                throw new InvalidOperationException("expected string");
            }

            var len = 0;
            var escaped = false;
            while (TryReadByte(out var b))
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (b == (byte)'\\')
                {
                    escaped = true;
                    continue;
                }
                else if (b == (byte)'"')
                {
                    return len;
                }

                if (len >= output.Length)
                {
                    throw new InvalidOperationException("string token too long");
                }

                output[len++] = b;
            }

            throw new InvalidOperationException("unexpected EOF while reading string");
        }

        private bool TryReadByte(out byte value)
        {
            if (_pos >= _len)
            {
                _len = _input.Read(_buffer, 0, _buffer.Length);
                _pos = 0;
                if (_len == 0)
                {
                    value = 0;
                    return false;
                }
            }

            value = _buffer[_pos++];
            return true;
        }

        private void Unread()
        {
            _pos--;
        }

        private static bool IsWhiteSpace(byte b)
        {
            return b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';
        }

        private static bool IsDigit(byte b)
        {
            return (uint)(b - (byte)'0') <= 9;
        }
    }
}
