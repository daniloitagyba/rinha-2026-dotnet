namespace RinhaFraud;

using System;

internal readonly struct RiskyFallbackFilter : IEquatable<RiskyFallbackFilter>
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

    public RiskyFallbackFilter(
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

    public static RiskyFallbackFilter PrecomputedDefault()
    {
        return new RiskyFallbackFilter(
            400,
            3000,
            2200,
            6200,
            850,
            250,
            4000,
            1500,
            5800,
            0,
            420);
    }

    public bool Equals(RiskyFallbackFilter other)
    {
        return AmountMin == other.AmountMin &&
               AmountMax == other.AmountMax &&
               InstallmentsMin == other.InstallmentsMin &&
               InstallmentsMax == other.InstallmentsMax &&
               RatioMin == other.RatioMin &&
               KmHomeMin == other.KmHomeMin &&
               KmHomeMax == other.KmHomeMax &&
               Tx24hMin == other.Tx24hMin &&
               Tx24hMax == other.Tx24hMax &&
               MerchantAverageMin == other.MerchantAverageMin &&
               MerchantAverageMax == other.MerchantAverageMax;
    }

    public override bool Equals(object? obj)
    {
        return obj is RiskyFallbackFilter other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(AmountMin);
        hash.Add(AmountMax);
        hash.Add(InstallmentsMin);
        hash.Add(InstallmentsMax);
        hash.Add(RatioMin);
        hash.Add(KmHomeMin);
        hash.Add(KmHomeMax);
        hash.Add(Tx24hMin);
        hash.Add(Tx24hMax);
        hash.Add(MerchantAverageMin);
        hash.Add(MerchantAverageMax);
        return hash.ToHashCode();
    }

    private static int EnvInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
    }
}
