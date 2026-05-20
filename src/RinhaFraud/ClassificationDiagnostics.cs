namespace RinhaFraud;

internal enum ClassificationPath
{
    Unknown = 0,
    ParseError = 1,
    ProfileFastPath = 2,
    AnnBuckets = 3,
    NativeKdTree = 4,
    RiskyFlatFallback = 5,
    FullFlatFallback = 6
}

internal readonly struct ClassificationDiagnostics
{
    public ClassificationDiagnostics(
        int fraudCount,
        ClassificationPath path,
        int profileKey,
        int primaryBucket,
        int candidates,
        int fallbackCandidates,
        long elapsedTicks)
    {
        FraudCount = fraudCount;
        Path = path;
        ProfileKey = profileKey;
        PrimaryBucket = primaryBucket;
        Candidates = candidates;
        FallbackCandidates = fallbackCandidates;
        ElapsedTicks = elapsedTicks;
    }

    public int FraudCount { get; }

    public ClassificationPath Path { get; }

    public int ProfileKey { get; }

    public int PrimaryBucket { get; }

    public int Candidates { get; }

    public int FallbackCandidates { get; }

    public long ElapsedTicks { get; }
}
