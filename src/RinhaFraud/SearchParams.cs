namespace RinhaFraud;

using System;

internal readonly struct SearchParams
{
    public readonly int MinCandidates;
    public readonly int MaxCandidates;
    public readonly bool Flat;

    public SearchParams(int minCandidates, int maxCandidates, bool flat)
    {
        MinCandidates = minCandidates;
        MaxCandidates = Math.Max(maxCandidates, minCandidates);
        Flat = flat;
    }

    public static SearchParams FromEnvironment()
    {
        var minCandidates = EnvInt("MIN_CANDIDATES", 10_000);
        var maxCandidates = Math.Max(EnvInt("MAX_CANDIDATES", 40_000), minCandidates);
        return new SearchParams(
            minCandidates,
            maxCandidates,
            Environment.GetEnvironmentVariable("SEARCH_MODE") == "flat");
    }

    private static int EnvInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
    }

}
