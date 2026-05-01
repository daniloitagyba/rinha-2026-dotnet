namespace RinhaFraud;

using System;
using System.Buffers.Text;

internal readonly ref struct Payload
{
    public Payload(
        ReadOnlySpan<byte> id,
        double amount,
        double installments,
        ReadOnlySpan<byte> requestedAt,
        double customerAvgAmount,
        double txCount24h,
        ReadOnlySpan<byte> knownMerchants,
        ReadOnlySpan<byte> merchantId,
        ReadOnlySpan<byte> mcc,
        double merchantAvgAmount,
        bool isOnline,
        bool cardPresent,
        double kmFromHome,
        bool hasLastTransaction,
        ReadOnlySpan<byte> lastTimestamp,
        double lastKmFromCurrent)
    {
        Id = id;
        Amount = amount;
        Installments = installments;
        RequestedAt = requestedAt;
        CustomerAvgAmount = customerAvgAmount;
        TxCount24h = txCount24h;
        KnownMerchants = knownMerchants;
        MerchantId = merchantId;
        Mcc = mcc;
        MerchantAvgAmount = merchantAvgAmount;
        IsOnline = isOnline;
        CardPresent = cardPresent;
        KmFromHome = kmFromHome;
        HasLastTransaction = hasLastTransaction;
        LastTimestamp = lastTimestamp;
        LastKmFromCurrent = lastKmFromCurrent;
    }

    public readonly ReadOnlySpan<byte> Id;
    public readonly double Amount;
    public readonly double Installments;
    public readonly ReadOnlySpan<byte> RequestedAt;
    public readonly double CustomerAvgAmount;
    public readonly double TxCount24h;
    public readonly ReadOnlySpan<byte> KnownMerchants;
    public readonly ReadOnlySpan<byte> MerchantId;
    public readonly ReadOnlySpan<byte> Mcc;
    public readonly double MerchantAvgAmount;
    public readonly bool IsOnline;
    public readonly bool CardPresent;
    public readonly double KmFromHome;
    public readonly bool HasLastTransaction;
    public readonly ReadOnlySpan<byte> LastTimestamp;
    public readonly double LastKmFromCurrent;
}

internal static class PayloadParser
{
    public static bool TryParse(ReadOnlySpan<byte> body, out Payload payload)
    {
        payload = default;

        if (!TryObjectSlice(body, "\"transaction\""u8, out var transaction) ||
            !TryObjectSlice(body, "\"customer\""u8, out var customer) ||
            !TryObjectSlice(body, "\"merchant\""u8, out var merchant) ||
            !TryObjectSlice(body, "\"terminal\""u8, out var terminal) ||
            !TryAfterColon(body, "\"last_transaction\""u8, out var lastPos))
        {
            return false;
        }

        lastPos = SkipWhiteSpace(body, lastPos);
        var hasLast = !body[lastPos..].StartsWith("null"u8);
        ReadOnlySpan<byte> lastTimestamp = default;
        double lastKm = 0;
        if (hasLast)
        {
            if (!TryObjectSlice(body, "\"last_transaction\""u8, out var last) ||
                !TryStringField(last, "\"timestamp\""u8, out lastTimestamp) ||
                !TryNumberField(last, "\"km_from_current\""u8, out lastKm))
            {
                return false;
            }
        }

        if (!TryStringField(body, "\"id\""u8, out var id) ||
            !TryNumberField(transaction, "\"amount\""u8, out var amount) ||
            !TryNumberField(transaction, "\"installments\""u8, out var installments) ||
            !TryStringField(transaction, "\"requested_at\""u8, out var requestedAt) ||
            !TryNumberField(customer, "\"avg_amount\""u8, out var customerAvg) ||
            !TryNumberField(customer, "\"tx_count_24h\""u8, out var txCount24h) ||
            !TryArraySlice(customer, "\"known_merchants\""u8, out var knownMerchants) ||
            !TryStringField(merchant, "\"id\""u8, out var merchantId) ||
            !TryStringField(merchant, "\"mcc\""u8, out var mcc) ||
            !TryNumberField(merchant, "\"avg_amount\""u8, out var merchantAvg) ||
            !TryBoolField(terminal, "\"is_online\""u8, out var isOnline) ||
            !TryBoolField(terminal, "\"card_present\""u8, out var cardPresent) ||
            !TryNumberField(terminal, "\"km_from_home\""u8, out var kmFromHome))
        {
            return false;
        }

        payload = new Payload(
            id,
            amount,
            installments,
            requestedAt,
            customerAvg,
            txCount24h,
            knownMerchants,
            merchantId,
            mcc,
            merchantAvg,
            isOnline,
            cardPresent,
            kmFromHome,
            hasLast,
            lastTimestamp,
            lastKm);
        return true;
    }

    private static bool TryObjectSlice(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!TryAfterColon(source, key, out var pos))
        {
            return false;
        }

        pos = SkipWhiteSpace(source, pos);
        if ((uint)pos >= (uint)source.Length || source[pos] != (byte)'{')
        {
            return false;
        }

        var start = pos;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (; pos < source.Length; pos++)
        {
            var b = source[pos];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (b == (byte)'\\')
                {
                    escaped = true;
                }
                else if (b == (byte)'"')
                {
                    inString = false;
                }
            }
            else if (b == (byte)'"')
            {
                inString = true;
            }
            else if (b == (byte)'{')
            {
                depth++;
            }
            else if (b == (byte)'}')
            {
                depth--;
                if (depth == 0)
                {
                    slice = source[start..(pos + 1)];
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryArraySlice(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!TryAfterColon(source, key, out var pos))
        {
            return false;
        }

        pos = SkipWhiteSpace(source, pos);
        if ((uint)pos >= (uint)source.Length || source[pos] != (byte)'[')
        {
            return false;
        }

        var start = pos;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (; pos < source.Length; pos++)
        {
            var b = source[pos];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (b == (byte)'\\')
                {
                    escaped = true;
                }
                else if (b == (byte)'"')
                {
                    inString = false;
                }
            }
            else if (b == (byte)'"')
            {
                inString = true;
            }
            else if (b == (byte)'[')
            {
                depth++;
            }
            else if (b == (byte)']')
            {
                depth--;
                if (depth == 0)
                {
                    slice = source[start..(pos + 1)];
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryNumberField(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key, out double value)
    {
        value = 0;
        if (!TryAfterColon(source, key, out var pos))
        {
            return false;
        }

        pos = SkipWhiteSpace(source, pos);
        var start = pos;
        while (pos < source.Length)
        {
            var b = source[pos];
            if (IsDigit(b) || b is (byte)'-' or (byte)'+' or (byte)'.' or (byte)'e' or (byte)'E')
            {
                pos++;
                continue;
            }

            break;
        }

        return pos > start && Utf8Parser.TryParse(source[start..pos], out value, out var consumed) && consumed == pos - start;
    }

    private static bool TryStringField(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryAfterColon(source, key, out var pos))
        {
            return false;
        }

        pos = SkipWhiteSpace(source, pos);
        if ((uint)pos >= (uint)source.Length || source[pos] != (byte)'"')
        {
            return false;
        }

        pos++;
        var start = pos;
        var escaped = false;
        while (pos < source.Length)
        {
            var b = source[pos];
            if (escaped)
            {
                escaped = false;
            }
            else if (b == (byte)'\\')
            {
                escaped = true;
            }
            else if (b == (byte)'"')
            {
                value = source[start..pos];
                return true;
            }

            pos++;
        }

        return false;
    }

    private static bool TryBoolField(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key, out bool value)
    {
        value = false;
        if (!TryAfterColon(source, key, out var pos))
        {
            return false;
        }

        pos = SkipWhiteSpace(source, pos);
        if (source[pos..].StartsWith("true"u8))
        {
            value = true;
            return true;
        }

        if (source[pos..].StartsWith("false"u8))
        {
            value = false;
            return true;
        }

        return false;
    }

    private static bool TryAfterColon(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key, out int pos)
    {
        pos = source.IndexOf(key);
        if (pos < 0)
        {
            return false;
        }

        pos += key.Length;
        while (pos < source.Length)
        {
            var b = source[pos];
            if (b == (byte)':')
            {
                pos++;
                return true;
            }

            if (!IsWhiteSpace(b))
            {
                return false;
            }

            pos++;
        }

        return false;
    }

    private static int SkipWhiteSpace(ReadOnlySpan<byte> source, int pos)
    {
        while (pos < source.Length && IsWhiteSpace(source[pos]))
        {
            pos++;
        }

        return pos;
    }

    private static bool IsWhiteSpace(byte b)
    {
        return b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';
    }

    private static bool IsDigit(byte b)
    {
        return (uint)(b - (byte)'0') <= 9;
    }
}
