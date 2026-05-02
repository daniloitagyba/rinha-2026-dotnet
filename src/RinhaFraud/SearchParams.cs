namespace RinhaFraud;

using System;

internal readonly struct SearchParams
{
    public readonly int EarlyCandidates;
    public readonly int MinCandidates;
    public readonly int MaxCandidates;
    public readonly bool Flat;
    public readonly bool ProfileFastPath;
    public readonly int ProfileMinCount;

    public SearchParams(
        int earlyCandidates,
        int minCandidates,
        int maxCandidates,
        bool flat,
        bool profileFastPath,
        int profileMinCount)
    {
        EarlyCandidates = Math.Clamp(earlyCandidates, Constants.K, minCandidates);
        MinCandidates = minCandidates;
        MaxCandidates = Math.Max(maxCandidates, minCandidates);
        Flat = flat;
        ProfileFastPath = profileFastPath;
        ProfileMinCount = Math.Max(1, profileMinCount);
    }

    public static SearchParams FromEnvironment()
    {
        var minCandidates = EnvInt("MIN_CANDIDATES", 36_000);
        var maxCandidates = Math.Max(EnvInt("MAX_CANDIDATES", 72_000), minCandidates);
        var earlyCandidates = EnvInt("EARLY_CANDIDATES", 30_000);
        return new SearchParams(
            earlyCandidates,
            minCandidates,
            maxCandidates,
            Environment.GetEnvironmentVariable("SEARCH_MODE") == "flat",
            EnvBool("PROFILE_FASTPATH", true),
            EnvInt("PROFILE_MIN_COUNT", 20));
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

}
