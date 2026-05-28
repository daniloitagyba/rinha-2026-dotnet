namespace RinhaFraud;

using System;

internal readonly struct SearchParams
{
    public const int ExactFallbackOff = 0;
    public const int ExactFallbackUncertain = 1;
    public const int ExactFallbackRisky = 2;
    public const int ExactFallbackProfileMiss = 3;

    public readonly int EarlyCandidates;
    public readonly int MinCandidates;
    public readonly int MaxCandidates;
    public readonly bool Flat;
    public readonly bool ProfileFastPath;
    public readonly int ProfileMinCount;
    public readonly int ProfileLegitMinCount;
    public readonly int ProfileFraudMinCount;
    public readonly int ProfileFraudAmountMin;
    public readonly bool ProfileFraudLowAmountFastPath;
    public readonly int ProfileFraudLowAmountKmHomeMin;
    public readonly int ProfileFraudLowAmountTx24hMin;
    public readonly bool ProfileFraudMidAmountNoLastFastPath;
    public readonly int ProfileFraudMidAmountMin;
    public readonly bool ProfileFraudNoLastOnly;
    public readonly bool ProfileDominantFastPath;
    public readonly int ProfileDominantMinCount;
    public readonly int ProfileDominantMaxOpposite;
    public readonly bool ProfileDominantLegitEnabled;
    public readonly bool ProfileDominantFraudEnabled;
    public readonly bool BucketFastPath;
    public readonly int BucketLegitMinCount;
    public readonly int BucketFraudMinCount;
    public readonly bool BucketFraudNoLastOnly;
    public readonly int ExactFallback;
    public readonly bool EarlyEdgeFallback;

    public bool UsesFastPaths => ProfileFastPath || ProfileDominantFastPath || BucketFastPath;

    public SearchParams(
        int earlyCandidates,
        int minCandidates,
        int maxCandidates,
        bool flat,
        bool profileFastPath,
        int profileMinCount,
        int profileLegitMinCount,
        int profileFraudMinCount,
        int profileFraudAmountMin,
        bool profileFraudLowAmountFastPath,
        int profileFraudLowAmountKmHomeMin,
        int profileFraudLowAmountTx24hMin,
        bool profileFraudMidAmountNoLastFastPath,
        int profileFraudMidAmountMin,
        bool profileFraudNoLastOnly,
        bool profileDominantFastPath,
        int profileDominantMinCount,
        int profileDominantMaxOpposite,
        bool profileDominantLegitEnabled,
        bool profileDominantFraudEnabled,
        bool bucketFastPath,
        int bucketLegitMinCount,
        int bucketFraudMinCount,
        bool bucketFraudNoLastOnly,
        int exactFallback,
        bool earlyEdgeFallback)
    {
        EarlyCandidates = Math.Clamp(earlyCandidates, Constants.K, minCandidates);
        MinCandidates = minCandidates;
        MaxCandidates = Math.Max(maxCandidates, minCandidates);
        Flat = flat;
        ProfileFastPath = profileFastPath;
        ProfileMinCount = Math.Max(1, profileMinCount);
        ProfileLegitMinCount = Math.Max(1, profileLegitMinCount);
        ProfileFraudMinCount = Math.Max(1, profileFraudMinCount);
        ProfileFraudAmountMin = Math.Max(0, profileFraudAmountMin);
        ProfileFraudLowAmountFastPath = profileFraudLowAmountFastPath;
        ProfileFraudLowAmountKmHomeMin = Math.Max(0, profileFraudLowAmountKmHomeMin);
        ProfileFraudLowAmountTx24hMin = Math.Max(0, profileFraudLowAmountTx24hMin);
        ProfileFraudMidAmountNoLastFastPath = profileFraudMidAmountNoLastFastPath;
        ProfileFraudMidAmountMin = Math.Max(0, profileFraudMidAmountMin);
        ProfileFraudNoLastOnly = profileFraudNoLastOnly;
        ProfileDominantFastPath = profileDominantFastPath;
        ProfileDominantMinCount = Math.Max(1, profileDominantMinCount);
        ProfileDominantMaxOpposite = Math.Max(0, profileDominantMaxOpposite);
        ProfileDominantLegitEnabled = profileDominantLegitEnabled;
        ProfileDominantFraudEnabled = profileDominantFraudEnabled;
        BucketFastPath = bucketFastPath;
        BucketLegitMinCount = Math.Max(1, bucketLegitMinCount);
        BucketFraudMinCount = Math.Max(1, bucketFraudMinCount);
        BucketFraudNoLastOnly = bucketFraudNoLastOnly;
        ExactFallback = Math.Clamp(exactFallback, ExactFallbackOff, ExactFallbackProfileMiss);
        EarlyEdgeFallback = earlyEdgeFallback;
    }

    public static SearchParams FromEnvironment()
    {
        var minCandidates = EnvInt("MIN_CANDIDATES", 36_000);
        var maxCandidates = Math.Max(EnvInt("MAX_CANDIDATES", 72_000), minCandidates);
        var earlyCandidates = EnvInt("EARLY_CANDIDATES", 30_000);
        var profileMinCount = EnvInt("PROFILE_MIN_COUNT", 20);
        return new SearchParams(
            earlyCandidates,
            minCandidates,
            maxCandidates,
            Environment.GetEnvironmentVariable("SEARCH_MODE") == "flat",
            EnvBool("PROFILE_FASTPATH", false),
            profileMinCount,
            EnvInt("PROFILE_LEGIT_MIN_COUNT", profileMinCount),
            EnvInt("PROFILE_FRAUD_MIN_COUNT", profileMinCount),
            EnvInt("PROFILE_FRAUD_AMOUNT_MIN", 0),
            EnvBool("PROFILE_FRAUD_LOW_AMOUNT_FASTPATH", false),
            EnvInt("PROFILE_FRAUD_LOW_AMOUNT_KM_HOME_MIN", 4750),
            EnvInt("PROFILE_FRAUD_LOW_AMOUNT_TX24H_MIN", 4250),
            EnvBool("PROFILE_FRAUD_MID_AMOUNT_NO_LAST_FASTPATH", false),
            EnvInt("PROFILE_FRAUD_MID_AMOUNT_MIN", 3000),
            EnvBool("PROFILE_FRAUD_NO_LAST_ONLY", false),
            EnvBool("PROFILE_DOMINANT_FASTPATH", false),
            EnvInt("PROFILE_DOMINANT_MIN_COUNT", 15),
            EnvInt("PROFILE_DOMINANT_MAX_OPPOSITE", 2),
            EnvBool("PROFILE_DOMINANT_LEGIT_ENABLED", true),
            EnvBool("PROFILE_DOMINANT_FRAUD_ENABLED", true),
            EnvBool("BUCKET_FASTPATH", false),
            EnvInt("BUCKET_LEGIT_MIN_COUNT", 32),
            EnvInt("BUCKET_FRAUD_MIN_COUNT", 32),
            EnvBool("BUCKET_FRAUD_NO_LAST_ONLY", false),
            ExactFallbackMode(Environment.GetEnvironmentVariable("EXACT_FALLBACK")),
            EnvBool("EARLY_EDGE_FALLBACK", false));
    }

    public SearchParams WithoutFastPaths()
    {
        return new SearchParams(
            EarlyCandidates,
            MinCandidates,
            MaxCandidates,
            Flat,
            false,
            ProfileMinCount,
            ProfileLegitMinCount,
            ProfileFraudMinCount,
            ProfileFraudAmountMin,
            false,
            ProfileFraudLowAmountKmHomeMin,
            ProfileFraudLowAmountTx24hMin,
            false,
            ProfileFraudMidAmountMin,
            false,
            false,
            ProfileDominantMinCount,
            ProfileDominantMaxOpposite,
            false,
            false,
            false,
            BucketLegitMinCount,
            BucketFraudMinCount,
            false,
            ExactFallback,
            EarlyEdgeFallback);
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

    private static int ExactFallbackMode(string? value)
    {
        return value switch
        {
            "1" or "uncertain" or "UNCERTAIN" => ExactFallbackUncertain,
            "2" or "risky" or "RISKY" => ExactFallbackRisky,
            "3" or "profile" or "PROFILE" or "profile_miss" or "PROFILE_MISS" => ExactFallbackProfileMiss,
            _ => ExactFallbackOff
        };
    }

}
