namespace RinhaFraud;

using System;
using System.IO;
using System.Text;

internal static class SelfTest
{
    public static void Run()
    {
        TestVectorizationWithoutLastTransaction();
        TestVectorizationFraudShape();
        TestQueryBuilderMatchesLegacyPath();
        TestPrecomputedNeighborKeysMatchDynamic();
        TestBuildIndexAndClassifyExamples();
        Console.WriteLine("self-test ok");
    }

    private static void TestVectorizationWithoutLastTransaction()
    {
        var body = Encoding.UTF8.GetBytes(LegitPayload);
        Assert(PayloadParser.TryParse(body, out var payload), "legit payload should parse");
        Span<short> vector = stackalloc short[Constants.PaddedDim];
        Vectorizer.Vectorize(payload, vector);

        AssertEqual(41, vector[0], "amount");
        AssertEqual(1667, vector[1], "installments");
        AssertEqual(500, vector[2], "amount_vs_avg");
        AssertEqual(7826, vector[3], "hour");
        AssertEqual(3333, vector[4], "day_of_week");
        AssertEqual(-10000, vector[5], "minutes_since_last_tx");
        AssertEqual(-10000, vector[6], "km_from_last_tx");
        AssertEqual(0, vector[9], "is_online");
        AssertEqual(10000, vector[10], "card_present");
        AssertEqual(0, vector[11], "unknown_merchant");
        AssertEqual(1500, vector[12], "mcc_risk");
    }

    private static void TestVectorizationFraudShape()
    {
        var body = Encoding.UTF8.GetBytes(FraudPayload);
        Assert(PayloadParser.TryParse(body, out var payload), "fraud payload should parse");
        Span<short> vector = stackalloc short[Constants.PaddedDim];
        Vectorizer.Vectorize(payload, vector);

        AssertEqual(9506, vector[0], "amount");
        AssertEqual(8333, vector[1], "installments");
        AssertEqual(10000, vector[2], "amount_vs_avg");
        AssertEqual(2174, vector[3], "hour");
        AssertEqual(8333, vector[4], "day_of_week");
        AssertEqual(10000, vector[11], "unknown_merchant");
        AssertEqual(7500, vector[12], "mcc_risk");
    }

    private static void TestBuildIndexAndClassifyExamples()
    {
        var indexPath = Path.Combine(Path.GetTempPath(), $"rinha-dotnet-{Guid.NewGuid():N}.idx");
        try
        {
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(ExampleReferences)))
            {
                BinaryIndexBuilder.Build(indexPath, input);
            }

            using var index = BinaryIndex.Open(indexPath);
            var searchParams = new SearchParams(
                5,
                5,
                100,
                flat: false,
                profileFastPath: false,
                profileMinCount: 20,
                profileLegitMinCount: 20,
                profileFraudMinCount: 20,
                profileFraudAmountMin: 0,
                profileFraudLowAmountFastPath: false,
                profileFraudLowAmountKmHomeMin: 4750,
                profileFraudLowAmountTx24hMin: 4250,
                profileFraudMidAmountNoLastFastPath: false,
                profileFraudMidAmountMin: 3000,
                profileFraudNoLastOnly: false,
                profileDominantFastPath: false,
                profileDominantMinCount: 20,
                profileDominantMaxOpposite: 0,
                profileDominantLegitEnabled: true,
                profileDominantFraudEnabled: true,
                bucketFastPath: false,
                bucketLegitMinCount: 32,
                bucketFraudMinCount: 32,
                bucketFraudNoLastOnly: false,
                exactFallback: SearchParams.ExactFallbackOff,
                earlyEdgeFallback: false);

            Span<short> legitVector = stackalloc short[Constants.PaddedDim];
            var legitBytes = Encoding.UTF8.GetBytes(LegitPayload);
            Assert(PayloadParser.TryParse(legitBytes, out var legit), "legit payload should parse for classify");
            Vectorizer.Vectorize(legit, legitVector);
            Assert(index.ClassifyFraudCount(legitVector, searchParams) < 3, "legit sample should be approved");

            Span<short> fraudVector = stackalloc short[Constants.PaddedDim];
            var fraudBytes = Encoding.UTF8.GetBytes(FraudPayload);
            Assert(PayloadParser.TryParse(fraudBytes, out var fraud), "fraud payload should parse for classify");
            Vectorizer.Vectorize(fraud, fraudVector);
            Assert(index.ClassifyFraudCount(fraudVector, searchParams) >= 3, "fraud sample should be denied");
        }
        finally
        {
            try
            {
                File.Delete(indexPath);
            }
            catch
            {
            }
        }
    }

    private static void TestQueryBuilderMatchesLegacyPath()
    {
        AssertQueryBuilderMatchesLegacy(LegitPayload, "legit payload");
        AssertQueryBuilderMatchesLegacy(FraudPayload, "fraud payload");
    }

    private static void TestPrecomputedNeighborKeysMatchDynamic()
    {
        Span<ushort> dynamic = stackalloc ushort[Constants.BucketCount];
        Span<byte> seen = stackalloc byte[Constants.BucketCount];
        Span<short> query = stackalloc short[Constants.PaddedDim];

        for (var bucketKey = 0; bucketKey < Constants.BucketCount; bucketKey++)
        {
            query.Clear();
            query[0] = ValueForBucket8(bucketKey & 7);
            query[2] = ValueForBucket8((bucketKey >> 3) & 7);
            query[7] = ValueForBucket8((bucketKey >> 6) & 7);
            query[3] = ValueForBucket4((bucketKey >> 9) & 3);
            query[5] = ((bucketKey >> 11) & 1) == 0 ? (short)0 : (short)-Constants.Scale;

            var count = Vectorizer.NeighborKeys(query, dynamic, seen);
            Assert(count == Constants.BucketCount, $"neighbor order should cover all buckets for key {bucketKey}");

            var precomputed = Vectorizer.NeighborKeyOrderForBucketKey(bucketKey);
            for (var i = 0; i < Constants.BucketCount; i++)
            {
                if (dynamic[i] != precomputed[i])
                {
                    throw new InvalidOperationException(
                        $"neighbor order mismatch for key {bucketKey} at index {i}, dynamic={dynamic[i]}, precomputed={precomputed[i]}");
                }
            }
        }
    }

    private static void AssertQueryBuilderMatchesLegacy(string json, string name)
    {
        var body = Encoding.UTF8.GetBytes(json);
        Assert(PayloadParser.TryParse(body, out var payload), $"{name} should parse on legacy path");

        Span<short> legacy = stackalloc short[Constants.PaddedDim];
        Span<short> optimized = stackalloc short[Constants.PaddedDim];
        Vectorizer.Vectorize(payload, legacy);
        Assert(QueryBuilder.TryBuildQuery(body, optimized), $"{name} should build optimized query");

        for (var i = 0; i < Constants.Dim; i++)
        {
            if (legacy[i] != optimized[i])
            {
                throw new InvalidOperationException($"{name}: query mismatch at dim {i}, legacy={legacy[i]}, optimized={optimized[i]}");
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual(int expected, short actual, string name)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
        }
    }

    private static short ValueForBucket8(int bucket)
    {
        return bucket == 0 ? (short)0 : (short)(((Constants.Scale + 1) * bucket + 7) / 8);
    }

    private static short ValueForBucket4(int bucket)
    {
        return bucket == 0 ? (short)0 : (short)(((Constants.Scale + 1) * bucket + 3) / 4);
    }

    private const string LegitPayload = """
{
  "id": "tx-1329056812",
  "transaction": { "amount": 41.12, "installments": 2, "requested_at": "2026-03-11T18:45:53Z" },
  "customer": { "avg_amount": 82.24, "tx_count_24h": 3, "known_merchants": ["MERC-003", "MERC-016"] },
  "merchant": { "id": "MERC-016", "mcc": "5411", "avg_amount": 60.25 },
  "terminal": { "is_online": false, "card_present": true, "km_from_home": 29.23 },
  "last_transaction": null
}
""";

    private const string FraudPayload = """
{
  "id": "tx-3330991687",
  "transaction": { "amount": 9505.97, "installments": 10, "requested_at": "2026-03-14T05:15:12Z" },
  "customer": { "avg_amount": 81.28, "tx_count_24h": 20, "known_merchants": ["MERC-008", "MERC-007", "MERC-005"] },
  "merchant": { "id": "MERC-068", "mcc": "7802", "avg_amount": 54.86 },
  "terminal": { "is_online": false, "card_present": true, "km_from_home": 952.27 },
  "last_transaction": null
}
""";

    private const string ExampleReferences = """
[
  { "vector": [0.0041, 0.1667, 0.05, 0.7826, 0.3333, -1, -1, 0.0292, 0.15, 0, 1, 0, 0.15, 0.006], "label": "legit" },
  { "vector": [0.0050, 0.1667, 0.06, 0.7800, 0.3333, -1, -1, 0.0300, 0.15, 0, 1, 0, 0.15, 0.006], "label": "legit" },
  { "vector": [0.0060, 0.0833, 0.04, 0.8000, 0.3333, -1, -1, 0.0400, 0.10, 0, 1, 0, 0.15, 0.005], "label": "legit" },
  { "vector": [0.9506, 0.8333, 1.0, 0.2174, 0.8333, -1, -1, 0.9523, 1.0, 0, 1, 1, 0.75, 0.0055], "label": "fraud" },
  { "vector": [0.9400, 0.8333, 1.0, 0.2100, 0.8333, -1, -1, 0.9300, 1.0, 0, 1, 1, 0.75, 0.0055], "label": "fraud" },
  { "vector": [0.9000, 0.7500, 1.0, 0.2200, 0.8333, -1, -1, 0.9500, 0.9, 0, 1, 1, 0.80, 0.0060], "label": "fraud" }
]
""";
}
