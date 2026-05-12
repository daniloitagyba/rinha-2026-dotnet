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
    public readonly int ExactFallback;
    public readonly bool EarlyEdgeFallback;

    public SearchParams(
        int earlyCandidates,
        int minCandidates,
        int maxCandidates,
        bool flat,
        bool profileFastPath,
        int profileMinCount,
        int profileLegitMinCount,
        int profileFraudMinCount,
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
            EnvBool("PROFILE_FASTPATH", true),
            profileMinCount,
            EnvInt("PROFILE_LEGIT_MIN_COUNT", profileMinCount),
            EnvInt("PROFILE_FRAUD_MIN_COUNT", profileMinCount),
            ExactFallbackMode(Environment.GetEnvironmentVariable("EXACT_FALLBACK")),
            EnvBool("EARLY_EDGE_FALLBACK", false));
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
