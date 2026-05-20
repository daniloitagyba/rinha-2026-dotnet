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
    private const int BlockLaneCount = 8;
    private const int BlockVectorStride = Constants.Dim * BlockLaneCount;
    private const int KdPartitionCount = 256;
    private const int KdPartitionRecordSize = 72;
    private const int KdNodeRecordSize = 80;
    private const int KdVectorStride = 16;
    private const int KdDefaultLeafSize = 128;

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

        var nativeOnly = EnvBool("BUILD_NATIVE_ONLY_INDEX", false);
        var bucketItemsOffset = nativeOnly ? 0 : output.Position;
        for (uint id = 0; !nativeOnly && id < labels.Count; id++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fourBytes, id);
            output.Write(fourBytes);
        }

        var extensionDirectoryOffset = WriteExtensionSections(output, vectorSpan, labelSpan, items, nativeOnly);
        var fileLength = output.Position;
        output.Position = 0;
        WriteHeader(output, labels.Count, vectorsOffset, labelsOffset, bucketOffsetsOffset, bucketItemsOffset, fileLength, extensionDirectoryOffset);
        Console.Error.WriteLine($"indexed {labels.Count} vectors into {outputPath} ({Constants.BucketCount} buckets, {fileLength} bytes)");
    }

    private static long WriteExtensionSections(
        FileStream output,
        ReadOnlySpan<short> vectors,
        ReadOnlySpan<byte> labels,
        ReadOnlySpan<uint> orderedOriginalIds,
        bool nativeOnly)
    {
        var sections = new List<SectionEntry>(16);
        WriteProfileSections(output, vectors, labels, orderedOriginalIds, sections);
        WriteNeighborOrdersSection(output, sections);
        if (!nativeOnly)
        {
            WriteIvfOrdersSection(output, vectors, orderedOriginalIds, sections);
        }

        if (EnvBool("BUILD_BLOCK_INDEX", false))
        {
            WriteBlockVectorsSection(output, vectors, orderedOriginalIds, sections);
        }

        WriteRiskySections(output, vectors, labels, orderedOriginalIds, sections, nativeOnly);
        if (EnvBool("BUILD_KDTREE_INDEX", false))
        {
            WriteKdTreeSections(output, vectors, labels, orderedOriginalIds, sections);
        }

        return WriteExtensionDirectory(output, sections);
    }

    private static void WriteBlockVectorsSection(
        FileStream output,
        ReadOnlySpan<short> vectors,
        ReadOnlySpan<uint> orderedOriginalIds,
        List<SectionEntry> sections)
    {
        var blockCount = (orderedOriginalIds.Length + BlockLaneCount - 1) / BlockLaneCount;
        var blockVectors = new short[blockCount * BlockVectorStride];
        Array.Fill(blockVectors, short.MaxValue);

        for (var mappedId = 0; mappedId < orderedOriginalIds.Length; mappedId++)
        {
            var originalId = (int)orderedOriginalIds[mappedId];
            var lane = mappedId & (BlockLaneCount - 1);
            var blockBase = (mappedId / BlockLaneCount) * BlockVectorStride;
            var source = vectors.Slice(originalId * Constants.Dim, Constants.Dim);
            for (var dim = 0; dim < Constants.Dim; dim++)
            {
                blockVectors[blockBase + dim * BlockLaneCount + lane] = source[dim];
            }
        }

        WriteSection(output, SectionBlockVectors, MemoryMarshal.AsBytes(blockVectors.AsSpan()), sections);
    }

    private static void WriteProfileSections(
        FileStream output,
        ReadOnlySpan<short> vectors,
        ReadOnlySpan<byte> labels,
        ReadOnlySpan<uint> orderedOriginalIds,
        List<SectionEntry> sections)
    {
        var profileCounts = new ushort[ProfileKeyCount];
        var profileFraudCounts = new ushort[ProfileKeyCount];
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

            if (labels[originalId] == 1)
            {
                if (profileFraudCounts[key] < ushort.MaxValue)
                {
                    profileFraudCounts[key]++;
                }

                profileMasks[key] |= 2;
            }
            else
            {
                profileMasks[key] |= 1;
            }
        }

        WriteSection(output, SectionProfileCounts, MemoryMarshal.AsBytes(profileCounts.AsSpan()), sections);
        WriteSection(output, SectionProfileMasks, profileMasks, sections);
        WriteSection(output, SectionProfileFraudCounts, MemoryMarshal.AsBytes(profileFraudCounts.AsSpan()), sections);
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
        List<SectionEntry> sections,
        bool nativeOnly)
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
        var soaVectors = nativeOnly ? Array.Empty<short>() : new short[originalIds.Count * Constants.Dim];
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
                if (!nativeOnly)
                {
                    soaVectors[dim * originalIds.Count + writePosition] = value;
                }
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
        if (!nativeOnly)
        {
            WriteSection(output, SectionRiskyBucketOffsets, MemoryMarshal.AsBytes(bucketOffsets.AsSpan()), sections);
        }

        WriteSection(output, SectionRiskyFineBucketOffsets, MemoryMarshal.AsBytes(fineBucketOffsets.AsSpan()), sections);
        WriteSection(output, SectionRiskyCoarseFineOffsets, MemoryMarshal.AsBytes(coarseFineOffsets.AsSpan()), sections);
        WriteSection(output, SectionRiskyFineKeys, MemoryMarshal.AsBytes(fineKeys.AsSpan()), sections);
        if (!nativeOnly)
        {
            WriteSection(output, SectionRiskySoa, MemoryMarshal.AsBytes(soaVectors.AsSpan()), sections);
        }
    }

    private static void WriteKdTreeSections(
        FileStream output,
        ReadOnlySpan<short> vectors,
        ReadOnlySpan<byte> labels,
        ReadOnlySpan<uint> orderedOriginalIds,
        List<SectionEntry> sections)
    {
        var leafSize = Math.Max(16, EnvInt("KDTREE_LEAF_SIZE", KdDefaultLeafSize));
        var vectorArray = vectors.ToArray();
        var labelArray = labels.ToArray();
        var orderedIdArray = orderedOriginalIds.ToArray();
        var partitions = new List<int>[KdPartitionCount];
        for (var i = 0; i < partitions.Length; i++)
        {
            partitions[i] = new List<int>(16_384);
        }

        for (var mappedId = 0; mappedId < orderedIdArray.Length; mappedId++)
        {
            var originalId = (int)orderedIdArray[mappedId];
            var vector = vectorArray.AsSpan(originalId * Constants.Dim, Constants.Dim);
            partitions[KdPartitionKey(vector)].Add(mappedId);
        }

        var nodes = new List<KdNodeRecord>(orderedIdArray.Length / leafSize * 2);
        var partitionRecords = new KdPartitionRecord[KdPartitionCount];
        var kdVectors = new List<short>(checked(orderedIdArray.Length * KdVectorStride));
        var kdLabels = new List<byte>(orderedIdArray.Length);
        var kdIds = new List<int>(orderedIdArray.Length);

        for (var partitionId = 0; partitionId < partitions.Length; partitionId++)
        {
            var ids = partitions[partitionId].ToArray();
            if (ids.Length == 0)
            {
                partitionRecords[partitionId] = KdPartitionRecord.Empty;
                continue;
            }

            var root = BuildKdNode(
                ids,
                0,
                ids.Length,
                leafSize,
                vectorArray,
                labelArray,
                orderedIdArray,
                nodes,
                kdVectors,
                kdLabels,
                kdIds,
                out var min,
                out var max);

            partitionRecords[partitionId] = new KdPartitionRecord(root, ids.Length, min, max);
        }

        Span<byte> meta = stackalloc byte[64];
        "KDT1"u8.CopyTo(meta);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[8..], KdPartitionCount);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[12..], (uint)nodes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[16..], (uint)kdLabels.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[20..], (uint)leafSize);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[24..], KdPartitionRecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[28..], KdNodeRecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[32..], KdVectorStride);

        var partitionBytes = new byte[KdPartitionCount * KdPartitionRecordSize];
        for (var i = 0; i < partitionRecords.Length; i++)
        {
            WriteKdPartitionRecord(partitionBytes.AsSpan(i * KdPartitionRecordSize, KdPartitionRecordSize), partitionRecords[i]);
        }

        var nodeBytes = new byte[nodes.Count * KdNodeRecordSize];
        for (var i = 0; i < nodes.Count; i++)
        {
            WriteKdNodeRecord(nodeBytes.AsSpan(i * KdNodeRecordSize, KdNodeRecordSize), nodes[i]);
        }

        WriteSection(output, SectionKdMeta, meta, sections);
        WriteSection(output, SectionKdPartitions, partitionBytes, sections);
        WriteSection(output, SectionKdNodes, nodeBytes, sections);
        WriteSection(output, SectionKdVectors, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(kdVectors)), sections);
        WriteSection(output, SectionKdLabels, CollectionsMarshal.AsSpan(kdLabels), sections);
        WriteSection(output, SectionKdIds, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(kdIds)), sections);
    }

    private static int BuildKdNode(
        int[] mappedIds,
        int start,
        int count,
        int leafSize,
        short[] vectors,
        byte[] labels,
        uint[] orderedOriginalIds,
        List<KdNodeRecord> nodes,
        List<short> kdVectors,
        List<byte> kdLabels,
        List<int> kdIds,
        out short[] min,
        out short[] max)
    {
        min = new short[KdVectorStride];
        max = new short[KdVectorStride];
        Array.Fill(min, short.MaxValue);
        Array.Fill(max, short.MinValue);

        for (var i = start; i < start + count; i++)
        {
            var originalId = (int)orderedOriginalIds[mappedIds[i]];
            var source = vectors.AsSpan(originalId * Constants.Dim, Constants.Dim);
            for (var dim = 0; dim < Constants.Dim; dim++)
            {
                var value = source[dim];
                if (value < min[dim])
                {
                    min[dim] = value;
                }

                if (value > max[dim])
                {
                    max[dim] = value;
                }
            }
        }

        var nodeIndex = nodes.Count;
        nodes.Add(default);

        if (count <= leafSize)
        {
            var vectorStart = kdLabels.Count;
            for (var i = start; i < start + count; i++)
            {
                var mappedId = mappedIds[i];
                var originalId = (int)orderedOriginalIds[mappedId];
                var source = vectors.AsSpan(originalId * Constants.Dim, Constants.Dim);
                for (var dim = 0; dim < Constants.Dim; dim++)
                {
                    kdVectors.Add(source[dim]);
                }

                for (var dim = Constants.Dim; dim < KdVectorStride; dim++)
                {
                    kdVectors.Add(0);
                }

                kdLabels.Add(labels[originalId]);
                kdIds.Add(mappedId);
            }

            nodes[nodeIndex] = new KdNodeRecord(-1, -1, vectorStart, count, min, max);
            return nodeIndex;
        }

        var axis = 0;
        var range = -1;
        for (var dim = 0; dim < Constants.Dim; dim++)
        {
            var dimRange = max[dim] - min[dim];
            if (dimRange > range)
            {
                range = dimRange;
                axis = dim;
            }
        }

        Array.Sort(mappedIds, start, count, new KdMappedIdComparer(vectors, orderedOriginalIds, axis));
        var leftCount = count / 2;
        var rightCount = count - leftCount;
        var left = BuildKdNode(
            mappedIds,
            start,
            leftCount,
            leafSize,
            vectors,
            labels,
            orderedOriginalIds,
            nodes,
            kdVectors,
            kdLabels,
            kdIds,
            out _,
            out _);
        var right = BuildKdNode(
            mappedIds,
            start + leftCount,
            rightCount,
            leafSize,
            vectors,
            labels,
            orderedOriginalIds,
            nodes,
            kdVectors,
            kdLabels,
            kdIds,
            out _,
            out _);

        nodes[nodeIndex] = new KdNodeRecord(left, right, 0, 0, min, max);
        return nodeIndex;
    }

    private static void WriteKdPartitionRecord(Span<byte> destination, in KdPartitionRecord record)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, record.Root);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], record.Count);
        WriteKdBounds(destination[8..], record.Min);
        WriteKdBounds(destination[40..], record.Max);
    }

    private static void WriteKdNodeRecord(Span<byte> destination, in KdNodeRecord record)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, record.Left);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], record.Right);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], record.Start);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], record.Count);
        WriteKdBounds(destination[16..], record.Min);
        WriteKdBounds(destination[48..], record.Max);
    }

    private static void WriteKdBounds(Span<byte> destination, short[] values)
    {
        for (var dim = 0; dim < KdVectorStride; dim++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(destination[(dim * 2)..], values[dim]);
        }
    }

    private static int KdPartitionKey(ReadOnlySpan<short> vector)
    {
        var key = vector[5] < 0 ? 1 : 0;
        key |= (vector[9] > 0 ? 1 : 0) << 1;
        key |= (vector[10] > 0 ? 1 : 0) << 2;
        key |= (vector[11] > 0 ? 1 : 0) << 3;
        key |= Vectorizer.Bucket4(vector[12]) << 4;
        key |= (vector[2] >= 8500 ? 1 : 0) << 6;
        key |= (vector[8] >= 4000 ? 1 : 0) << 7;
        return key;
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

    private static bool EnvBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value == "1" ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int EnvInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
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

    private readonly struct KdPartitionRecord
    {
        public static readonly KdPartitionRecord Empty = new(-1, 0, new short[KdVectorStride], new short[KdVectorStride]);

        public readonly int Root;
        public readonly int Count;
        public readonly short[] Min;
        public readonly short[] Max;

        public KdPartitionRecord(int root, int count, short[] min, short[] max)
        {
            Root = root;
            Count = count;
            Min = min;
            Max = max;
        }
    }

    private readonly struct KdNodeRecord
    {
        public readonly int Left;
        public readonly int Right;
        public readonly int Start;
        public readonly int Count;
        public readonly short[] Min;
        public readonly short[] Max;

        public KdNodeRecord(int left, int right, int start, int count, short[] min, short[] max)
        {
            Left = left;
            Right = right;
            Start = start;
            Count = count;
            Min = min;
            Max = max;
        }
    }

    private sealed class KdMappedIdComparer : IComparer<int>
    {
        private readonly short[] _vectors;
        private readonly uint[] _orderedOriginalIds;
        private readonly int _axis;

        public KdMappedIdComparer(short[] vectors, uint[] orderedOriginalIds, int axis)
        {
            _vectors = vectors;
            _orderedOriginalIds = orderedOriginalIds;
            _axis = axis;
        }

        public int Compare(int left, int right)
        {
            var leftOriginal = (int)_orderedOriginalIds[left];
            var rightOriginal = (int)_orderedOriginalIds[right];
            var leftValue = _vectors[(leftOriginal * Constants.Dim) + _axis];
            var rightValue = _vectors[(rightOriginal * Constants.Dim) + _axis];
            var valueComparison = leftValue.CompareTo(rightValue);
            return valueComparison != 0 ? valueComparison : left.CompareTo(right);
        }
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
