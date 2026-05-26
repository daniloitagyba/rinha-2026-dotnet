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
        long elapsedTicks,
        int kdSearchedPartitions = 0,
        int kdCandidatePartitions = 0,
        int kdVisitedNodes = 0,
        int kdPrunedNodes = 0,
        int kdScannedLeaves = 0,
        int kdScannedVectors = 0,
        int kdMaxStackDepth = 0,
        int kdPrimaryPartition = -1)
    {
        FraudCount = fraudCount;
        Path = path;
        ProfileKey = profileKey;
        PrimaryBucket = primaryBucket;
        Candidates = candidates;
        FallbackCandidates = fallbackCandidates;
        ElapsedTicks = elapsedTicks;
        KdSearchedPartitions = kdSearchedPartitions;
        KdCandidatePartitions = kdCandidatePartitions;
        KdVisitedNodes = kdVisitedNodes;
        KdPrunedNodes = kdPrunedNodes;
        KdScannedLeaves = kdScannedLeaves;
        KdScannedVectors = kdScannedVectors;
        KdMaxStackDepth = kdMaxStackDepth;
        KdPrimaryPartition = kdPrimaryPartition;
    }

    public int FraudCount { get; }

    public ClassificationPath Path { get; }

    public int ProfileKey { get; }

    public int PrimaryBucket { get; }

    public int Candidates { get; }

    public int FallbackCandidates { get; }

    public long ElapsedTicks { get; }

    public int KdSearchedPartitions { get; }

    public int KdCandidatePartitions { get; }

    public int KdVisitedNodes { get; }

    public int KdPrunedNodes { get; }

    public int KdScannedLeaves { get; }

    public int KdScannedVectors { get; }

    public int KdMaxStackDepth { get; }

    public int KdPrimaryPartition { get; }
}
