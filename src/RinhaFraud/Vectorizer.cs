namespace RinhaFraud;

using System;

internal static class Vectorizer
{
    public static void Vectorize(in Payload payload, Span<short> output)
    {
        if (output.Length < Constants.Dim)
        {
            throw new ArgumentException("output span is too small", nameof(output));
        }

        if (!TryParseTime(payload.RequestedAt, out var year, out var month, out var day, out var hour, out var minute))
        {
            year = 2026;
            month = 1;
            day = 1;
            hour = 0;
            minute = 0;
        }

        var dayOfWeek = DayOfWeek(year, month, day);

        output[0] = Quantize(Clamp01(payload.Amount / 10_000.0));
        output[1] = Quantize(Clamp01(payload.Installments / 12.0));
        output[2] = Quantize(Clamp01(AmountVsAverage(payload.Amount, payload.CustomerAvgAmount)));
        output[3] = Quantize(hour / 23.0);
        output[4] = Quantize(dayOfWeek / 6.0);

        if (payload.HasLastTransaction)
        {
            var current = EpochMinutes(year, month, day, hour, minute);
            var last = TryParseTime(payload.LastTimestamp, out var ly, out var lm, out var ld, out var lh, out var lmin)
                ? EpochMinutes(ly, lm, ld, lh, lmin)
                : current;
            var minutesSinceLast = Math.Max(0, current - last);
            output[5] = Quantize(Clamp01(minutesSinceLast / 1440.0));
            output[6] = Quantize(Clamp01(payload.LastKmFromCurrent / 1000.0));
        }
        else
        {
            output[5] = -Constants.Scale;
            output[6] = -Constants.Scale;
        }

        output[7] = Quantize(Clamp01(payload.KmFromHome / 1000.0));
        output[8] = Quantize(Clamp01(payload.TxCount24h / 20.0));
        output[9] = payload.IsOnline ? (short)Constants.Scale : (short)0;
        output[10] = payload.CardPresent ? (short)Constants.Scale : (short)0;
        output[11] = ContainsQuoted(payload.KnownMerchants, payload.MerchantId) ? (short)0 : (short)Constants.Scale;
        output[12] = Quantize(MccRisk(payload.Mcc));
        output[13] = Quantize(Clamp01(payload.MerchantAvgAmount / 10_000.0));
    }

    public static short QuantizeReference(double value)
    {
        return value <= -0.9999 ? (short)-Constants.Scale : Quantize(Clamp01(value));
    }

    public static long DistanceSquared(ReadOnlySpan<short> a, ReadOnlySpan<short> b)
    {
        long sum = 0;
        for (var i = 0; i < Constants.Dim; i++)
        {
            var d = (long)a[i] - b[i];
            sum += d * d;
        }

        return sum;
    }

    public static ushort BucketKey(ReadOnlySpan<short> vector)
    {
        var amount = Bucket8(vector[0]);
        var ratio = Bucket8(vector[2]);
        var kmHome = Bucket8(vector[7]);
        var hour = Bucket4(vector[3]);
        var noLast = vector[5] < 0 ? 1 : 0;
        return (ushort)(amount | (ratio << 3) | (kmHome << 6) | (hour << 9) | (noLast << 11));
    }

    public static int NeighborKeys(ReadOnlySpan<short> query, Span<ushort> output, Span<byte> seen)
    {
        var amount = Bucket8(query[0]);
        var ratio = Bucket8(query[2]);
        var kmHome = Bucket8(query[7]);
        var hour = Bucket4(query[3]);
        var noLast = query[5] < 0 ? 1 : 0;
        var count = 0;

        seen.Clear();
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
                                output[count++] = (ushort)key;
                            }
                        }
                    }
                }
            }
        }

        return count;
    }

    private static double AmountVsAverage(double amount, double average)
    {
        return average <= 0.0 ? 1.0 : (amount / average) / 10.0;
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value))
        {
            return 0.0;
        }

        return Math.Clamp(value, 0.0, 1.0);
    }

    private static short Quantize(double value)
    {
        return (short)Math.Round(value * Constants.Scale, MidpointRounding.AwayFromZero);
    }

    public static int Bucket8(short value)
    {
        return value <= 0 ? 0 : Math.Clamp(value * 8 / (Constants.Scale + 1), 0, 7);
    }

    public static int Bucket16(short value)
    {
        return value <= 0 ? 0 : Math.Clamp(value * 16 / (Constants.Scale + 1), 0, 15);
    }

    public static int Bucket4(short value)
    {
        return value <= 0 ? 0 : Math.Clamp(value * 4 / (Constants.Scale + 1), 0, 3);
    }

    private static double MccRisk(ReadOnlySpan<byte> mcc)
    {
        if (mcc.SequenceEqual("5411"u8)) return 0.15;
        if (mcc.SequenceEqual("5812"u8)) return 0.30;
        if (mcc.SequenceEqual("5912"u8)) return 0.20;
        if (mcc.SequenceEqual("5944"u8)) return 0.45;
        if (mcc.SequenceEqual("7801"u8)) return 0.80;
        if (mcc.SequenceEqual("7802"u8)) return 0.75;
        if (mcc.SequenceEqual("7995"u8)) return 0.85;
        if (mcc.SequenceEqual("4511"u8)) return 0.35;
        if (mcc.SequenceEqual("5311"u8)) return 0.25;
        if (mcc.SequenceEqual("5999"u8)) return 0.50;
        return 0.50;
    }

    private static bool ContainsQuoted(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length + 2)
        {
            return false;
        }

        for (var pos = 0; pos + needle.Length + 2 <= haystack.Length; pos++)
        {
            if (haystack[pos] == (byte)'"' &&
                haystack[(pos + 1)..(pos + 1 + needle.Length)].SequenceEqual(needle) &&
                haystack[pos + 1 + needle.Length] == (byte)'"')
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseTime(ReadOnlySpan<byte> timestamp, out int year, out int month, out int day, out int hour, out int minute)
    {
        year = month = day = hour = minute = 0;
        if (timestamp.Length < 16)
        {
            return false;
        }

        return TryParse4(timestamp[0..4], out year) &&
            TryParse2(timestamp[5..7], out month) &&
            TryParse2(timestamp[8..10], out day) &&
            TryParse2(timestamp[11..13], out hour) &&
            TryParse2(timestamp[14..16], out minute);
    }

    private static bool TryParse4(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        for (var i = 0; i < 4; i++)
        {
            var d = bytes[i] - (byte)'0';
            if ((uint)d > 9)
            {
                return false;
            }

            value = value * 10 + d;
        }

        return true;
    }

    private static bool TryParse2(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        for (var i = 0; i < 2; i++)
        {
            var d = bytes[i] - (byte)'0';
            if ((uint)d > 9)
            {
                return false;
            }

            value = value * 10 + d;
        }

        return true;
    }

    private static int DayOfWeek(int year, int month, int day)
    {
        ReadOnlySpan<int> t = [0, 3, 2, 5, 0, 3, 5, 1, 4, 6, 2, 4];
        var y = year;
        if (month < 3)
        {
            y--;
        }

        var dow = (y + y / 4 - y / 100 + y / 400 + t[month - 1] + day) % 7;
        return (dow + 6) % 7;
    }

    private static long EpochMinutes(int year, int month, int day, int hour, int minute)
    {
        return DaysFromCivil(year, month, day) * 1440L + hour * 60L + minute;
    }

    private static long DaysFromCivil(int year, int month, int day)
    {
        var y = year - (month <= 2 ? 1 : 0);
        var era = (y >= 0 ? y : y - 399) / 400;
        var yoe = y - era * 400;
        var mp = month + (month > 2 ? -3 : 9);
        var doy = (153 * mp + 2) / 5 + day - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097L + doe - 719468L;
    }
}
