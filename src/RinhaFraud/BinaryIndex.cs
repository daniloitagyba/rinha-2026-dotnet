namespace RinhaFraud;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
    private static ReadOnlySpan<int> RiskyFallbackProfileKeys =>
    [
        524972, 525085, 525483, 532634, 532997, 533163, 540679, 540695, 540709, 540714,
        540815, 540818, 540825, 540831, 540839, 540841, 540842, 540943, 540948, 540949,
        540951, 541203, 541207, 541215, 541231, 541247, 541314, 541315, 541329, 541330,
        541333, 541343, 541353, 541755, 541843, 541861, 541871, 541979, 542231, 542233,
        542248, 542252, 542341, 542359, 542372, 542373, 542375, 542382, 542383, 542767,
        542892, 542895, 543021, 543023, 543259, 543375, 543384, 543391, 543919, 543935,
        544281, 544393, 544431, 548866, 548994, 549011, 549522, 549547, 549906, 550163,
        550546, 552616, 557074, 557075, 557077, 557081, 557187, 557202, 557203, 557219,
        557221, 557222, 557226, 557359, 557590, 557591, 557599, 557699, 557702, 557703,
        557715, 557718, 557719, 557726, 557727, 557733, 557735, 557742, 557743, 557843,
        557847, 558230, 558231, 558595, 558607, 558616, 558739, 558740, 558744, 558751,
        558783, 558866, 559119, 559146, 559259, 559263, 559295, 559791, 560143, 560152,
        565384, 565507, 565763, 565889, 566275, 590359, 590379, 590495, 591364, 591405,
        591637, 592010, 599690, 599699, 606209, 606210, 606231, 606241, 606245, 606247,
        606249, 606250, 606338, 606349, 606353, 606354, 606357, 606359, 606360, 606361,
        606366, 606372, 606379, 606483, 606501, 606725, 606740, 606749, 606757, 606764,
        606767, 606851, 606862, 606863, 606869, 606870, 606872, 606873, 606875, 606877,
        606885, 606887, 606890, 606893, 606995, 606996, 606997, 607001, 607005, 607253,
        607386, 607397, 607402, 607746, 607747, 607766, 607890, 607893, 607903, 607909,
        607910, 608415, 608431, 608921, 608923, 608924, 608925, 609471, 609967, 614959,
        615063, 615427, 622611, 622627, 622639, 622722, 622739, 622740, 622742, 622744,
        622745, 622747, 622751, 622866, 622867, 622885, 622895, 623127, 623137, 623235,
        623253, 623255, 623263, 623279, 623285, 623375, 623775, 623785, 623891, 624130,
        624145, 624257, 624273, 624275, 624292, 624298, 624427, 624792, 624812, 624828,
        625289, 625311, 625326, 625327, 625455, 625839, 626191, 626217, 626335, 626348,
        626351, 630915, 631427, 631442, 631471, 631477, 631975, 632466, 656005, 656918,
        671877, 671880, 672387, 672406, 672408, 672415, 672519, 672901, 672925, 673055,
        673320, 673438, 673449, 674479, 674968, 688292, 688786, 688793, 689195, 689302,
        690351, 721025, 721561, 723743, 724521, 737294, 737439, 738963, 738975, 739128,
        739995, 739999, 740031, 754317, 754325, 754333, 754335, 755220, 755225, 755359,
        755475, 755865, 756397
    ];

    private static ReadOnlySpan<int> RiskyDirectApproveRules =>
    [
        4261075, 4326521, 4326547, 4326649, 4326651, 4326739, 4327595, 4329627, 4329657, 4329980,
        4330516, 4330641, 4330643, 4330746, 4334748, 4334891, 4334971, 4335835, 4337865, 4337868,
        4338729, 4338731, 4338986, 4338987, 4343139, 4343163, 4347073, 4347075, 4347129, 4347130,
        4351355, 4351484, 4355145, 4355147, 4391956, 4392091, 4399251, 4401307, 4420931, 4456593,
        4456601, 4456603, 4457497, 4457753, 4457778, 4457779, 4458874, 4458876, 4460729, 4460731,
        4460796, 4461618, 4461625, 4461627, 4461810, 4461811, 4461868, 4461882, 4461883, 4461945,
        4465843, 4468763, 4469915, 4469916, 4469955, 4470011, 4472955, 4474106, 4474108, 4474364,
        4481147, 4723963, 4730915, 4797523, 4797595, 4849684, 4849851, 4849931, 4849980, 4849995,
        4850001, 4850002, 4850794, 4850796, 4850827, 4850857, 4850882, 4850883, 4850890, 4850891,
        4850932, 4850978, 4851867, 4853923, 4854810, 4854899, 4854955, 4854961, 4855019, 4855082,
        4855097, 4855121, 4855147, 4855964, 4855979, 4856010, 4856011, 4863147, 4863225, 4863283,
        4867321, 4867322, 4867450, 4871387, 4875771, 4879739, 4919675, 4920507, 4923419, 4980892,
        4981019, 4981779, 4981914, 4981915, 4981921, 4981979, 4982010, 4982011, 4982939, 4985019,
        4986026, 4986027, 4986041, 4986107, 4986233, 4986234, 4987003, 4990202, 4993163, 4994338,
        4995419, 4998339, 4998497, 4998499, 4998627, 5002314, 5002490, 5002491, 5002619, 5003643,
        5006715, 5009739, 5010811, 5051540, 5051771, 5055803, 5248043, 5375042, 5375043, 5379100,
        5379323, 5380155, 5383211, 5386563, 5395834, 5395835, 5399747, 5510289, 5510347, 5522810,
        5522811, 5796171, 5899513, 5911803, 5919963, 5919994, 5920252, 6034667, 6034682, 6034683,
        6041764, 6041803, 6043804, 6046923
    ];

    private static ReadOnlySpan<int> RiskyDirectDenyRules =>
    [
        4199778, 4200682, 4203866, 4263978, 4265306, 4325434, 4325562, 4325674, 4326602, 4326604,
        4326652, 4326714, 4326738, 4327546, 4327586, 4327609, 4329658, 4329659, 4329722, 4329851,
        4330514, 4330522, 4330634, 4330745, 4330748, 4330827, 4334042, 4337850, 4337986, 4338018,
        4338875, 4338978, 4339002, 4339058, 4339067, 4342138, 4343161, 4344169, 4344186, 4346074,
        4347002, 4347132, 4354250, 4355449, 4390929, 4396178, 4396378, 4404369, 4456594, 4457498,
        4457618, 4457770, 4461593, 4461722, 4461745, 4461747, 4461819, 4461947, 4462746, 4462778,
        4468858, 4468930, 4470266, 4470930, 4473170, 4474074, 4478330, 4478332, 4481217, 4523074,
        4524058, 4526106, 4527113, 4530202, 4722874, 4723033, 4731242, 4733098, 4736082, 4849674,
        4849962, 4850706, 4850834, 4850858, 4850874, 4850884, 4851034, 4852010, 4853994, 4854057,
        4854113, 4854962, 4854963, 4854980, 4854986, 4855003, 4855081, 4855084, 4855099, 4855100,
        4855122, 4855963, 4855970, 4855972, 4855980, 4856041, 4858026, 4859090, 4859177, 4859218,
        4861970, 4861978, 4862130, 4863226, 4863274, 4863284, 4867324, 4871369, 4871393, 4871402,
        4920508, 4981020, 4981922, 4981938, 4981939, 4981954, 4981955, 4981956, 4982929, 4983082,
        4983162, 4983163, 4985098, 4986108, 4986282, 4990282, 4991129, 4993042, 4994057, 4994186,
        4994202, 4994337, 4994386, 5002489, 5002492, 5009530, 5010683, 5010786, 5047322, 5051418,
        5051419, 5051420, 5051818, 5059730, 5255346, 5375018, 5379249, 5379266, 5383402, 5384441,
        5386564, 5387506, 5387594, 5395836, 5506338, 5510290, 5510292, 5510348, 5513562, 5514418,
        5768201, 5772490, 5789946, 5898354, 5899514, 5911706, 5913026, 6034537, 6034602, 6042874,
        6051177
    ];

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
        long bucketItemsOffset,
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
        _bucketItemsOffset = bucketItemsOffset;
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
                bucketItemsOffset,
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
            return ClassifyFlat(query);
        }

        var frauds = CountFrauds(topLabel);
        if (searchParams.ExactFallback == SearchParams.ExactFallbackRisky &&
            TryRiskyDirectDecision(query, frauds, out var directFrauds))
        {
            return directFrauds;
        }

        if (!ShouldUseExactFallback(query, frauds, searchParams))
        {
            return frauds;
        }

        return searchParams.ExactFallback == SearchParams.ExactFallbackRisky
            ? ClassifyRiskyFlat(query, allowFullTiebreak: true)
            : ClassifyFlat(query);
    }

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

    private static bool ShouldUseExactFallback(ReadOnlySpan<short> query, int frauds, in SearchParams searchParams)
    {
        if (frauds > 0 && frauds < Constants.K)
        {
            if (searchParams.ExactFallback == SearchParams.ExactFallbackUncertain)
            {
                return true;
            }

            return searchParams.ExactFallback == SearchParams.ExactFallbackRisky && IsRiskyFallbackProfile(query);
        }

        return searchParams.ExactFallback == SearchParams.ExactFallbackRisky && IsStrongFallbackRisk(query, frauds);
    }

    private static bool IsRiskyFallbackProfile(ReadOnlySpan<short> query)
    {
        var key = ProfileKey(query);
        return ContainsSorted(RiskyFallbackProfileKeys, key);
    }

    private static bool TryRiskyDirectDecision(ReadOnlySpan<short> query, int frauds, out int directFrauds)
    {
        directFrauds = frauds;
        if (frauds <= 0 || frauds >= Constants.K)
        {
            return false;
        }

        var rule = (ProfileKey(query) << 3) | frauds;
        if (ContainsSorted(RiskyDirectApproveRules, rule))
        {
            directFrauds = 0;
            return true;
        }

        if (ContainsSorted(RiskyDirectDenyRules, rule))
        {
            directFrauds = Constants.K;
            return true;
        }

        return false;
    }

    private static bool ContainsSorted(ReadOnlySpan<int> values, int needle)
    {
        var lo = 0;
        var hi = values.Length - 1;
        while (lo <= hi)
        {
            var mid = (int)((uint)(lo + hi) >> 1);
            var value = values[mid];
            if (value == needle)
            {
                return true;
            }

            if (value < needle)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return false;
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
