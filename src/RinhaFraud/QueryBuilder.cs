namespace RinhaFraud;

using System;
internal static class QueryBuilder
{
    public static bool TryBuildQuery(ReadOnlySpan<byte> body, Span<short> output)
    {
        double amount = 0;
        double installments = 0;
        var requestedAtStart = 0;
        var requestedAtLength = 0;
        double customerAvg = 0;
        double txCount24h = 0;
        var knownMerchantsStart = 0;
        var knownMerchantsLength = 0;
        var merchantIdStart = 0;
        var merchantIdLength = 0;
        var mccStart = 0;
        var mccLength = 0;
        double merchantAvg = 0;
        bool isOnline = false;
        bool cardPresent = false;
        double kmFromHome = 0;
        bool hasLastTransaction = false;
        var lastTimestampStart = 0;
        var lastTimestampLength = 0;
        double lastKmFromCurrent = 0;

        var hasTransaction = false;
        var hasCustomer = false;
        var hasMerchant = false;
        var hasTerminal = false;
        var hasLastField = false;

        var pos = 0;
        if (!TryEnterObject(body, ref pos))
        {
            return false;
        }

        while (true)
        {
            pos = SkipWhiteSpace(body, pos);
            if ((uint)pos >= (uint)body.Length)
            {
                return false;
            }

            if (body[pos] == (byte)'}')
            {
                break;
            }

            if (!TryReadStringBounds(body, ref pos, out var keyStart, out var keyLength) ||
                !TryConsumeColon(body, ref pos))
            {
                return false;
            }

            var key = body.Slice(keyStart, keyLength);
            if (key.SequenceEqual("transaction"u8))
            {
                if (!TryParseTransaction(body, ref pos, out amount, out installments, out requestedAtStart, out requestedAtLength))
                {
                    return false;
                }

                hasTransaction = true;
            }
            else if (key.SequenceEqual("customer"u8))
            {
                if (!TryParseCustomer(body, ref pos, out customerAvg, out txCount24h, out knownMerchantsStart, out knownMerchantsLength))
                {
                    return false;
                }

                hasCustomer = true;
            }
            else if (key.SequenceEqual("merchant"u8))
            {
                if (!TryParseMerchant(body, ref pos, out merchantIdStart, out merchantIdLength, out mccStart, out mccLength, out merchantAvg))
                {
                    return false;
                }

                hasMerchant = true;
            }
            else if (key.SequenceEqual("terminal"u8))
            {
                if (!TryParseTerminal(body, ref pos, out isOnline, out cardPresent, out kmFromHome))
                {
                    return false;
                }

                hasTerminal = true;
            }
            else if (key.SequenceEqual("last_transaction"u8))
            {
                if (!TryParseLastTransactionValue(body, ref pos, out hasLastTransaction, out lastTimestampStart, out lastTimestampLength, out lastKmFromCurrent))
                {
                    return false;
                }

                hasLastField = true;
            }
            else if (!SkipValue(body, ref pos))
            {
                return false;
            }

            if (!TrySkipDelimiter(body, ref pos))
            {
                return false;
            }
        }

        if (!(hasTransaction && hasCustomer && hasMerchant && hasTerminal && hasLastField))
        {
            return false;
        }

        var requestedAt = body.Slice(requestedAtStart, requestedAtLength);
        var knownMerchants = body.Slice(knownMerchantsStart, knownMerchantsLength);
        var merchantId = body.Slice(merchantIdStart, merchantIdLength);
        var mcc = body.Slice(mccStart, mccLength);
        var lastTimestamp = body.Slice(lastTimestampStart, lastTimestampLength);

        Vectorizer.Vectorize(
            amount,
            installments,
            requestedAt,
            customerAvg,
            txCount24h,
            ContainsQuoted(knownMerchants, merchantId),
            mcc,
            merchantAvg,
            isOnline,
            cardPresent,
            kmFromHome,
            hasLastTransaction,
            lastTimestamp,
            lastKmFromCurrent,
            output);
        return true;
    }

    public static bool TryBuildProfileQuery(ReadOnlySpan<byte> body, Span<short> output)
    {
        double amount = 0;
        double installments = 0;
        var requestedAtStart = 0;
        var requestedAtLength = 0;
        double customerAvg = 0;
        double txCount24h = 0;
        var knownMerchantsStart = 0;
        var knownMerchantsLength = 0;
        var merchantIdStart = 0;
        var merchantIdLength = 0;
        var mccStart = 0;
        var mccLength = 0;
        double merchantAvg = 0;
        bool isOnline = false;
        bool cardPresent = false;
        double kmFromHome = 0;
        bool hasLastTransaction = false;
        var lastTimestampStart = 0;
        var lastTimestampLength = 0;
        double lastKmFromCurrent = 0;

        var hasTransaction = false;
        var hasCustomer = false;
        var hasMerchant = false;
        var hasTerminal = false;
        var hasLastField = false;

        var pos = 0;
        if (!TryEnterObject(body, ref pos))
        {
            return false;
        }

        while (true)
        {
            pos = SkipWhiteSpace(body, pos);
            if ((uint)pos >= (uint)body.Length)
            {
                return false;
            }

            if (body[pos] == (byte)'}')
            {
                break;
            }

            if (!TryReadStringBounds(body, ref pos, out var keyStart, out var keyLength) ||
                !TryConsumeColon(body, ref pos))
            {
                return false;
            }

            var key = body.Slice(keyStart, keyLength);
            if (key.SequenceEqual("transaction"u8))
            {
                if (!TryParseTransaction(body, ref pos, out amount, out installments, out requestedAtStart, out requestedAtLength))
                {
                    return false;
                }

                hasTransaction = true;
            }
            else if (key.SequenceEqual("customer"u8))
            {
                if (!TryParseCustomer(body, ref pos, out customerAvg, out txCount24h, out knownMerchantsStart, out knownMerchantsLength))
                {
                    return false;
                }

                hasCustomer = true;
            }
            else if (key.SequenceEqual("merchant"u8))
            {
                if (!TryParseMerchant(body, ref pos, out merchantIdStart, out merchantIdLength, out mccStart, out mccLength, out merchantAvg))
                {
                    return false;
                }

                hasMerchant = true;
            }
            else if (key.SequenceEqual("terminal"u8))
            {
                if (!TryParseTerminal(body, ref pos, out isOnline, out cardPresent, out kmFromHome))
                {
                    return false;
                }

                hasTerminal = true;
            }
            else if (key.SequenceEqual("last_transaction"u8))
            {
                if (!TryParseLastTransactionValue(body, ref pos, out hasLastTransaction, out lastTimestampStart, out lastTimestampLength, out lastKmFromCurrent))
                {
                    return false;
                }

                hasLastField = true;
            }
            else if (!SkipValue(body, ref pos))
            {
                return false;
            }

            if (!TrySkipDelimiter(body, ref pos))
            {
                return false;
            }
        }

        _ = requestedAtStart;
        _ = requestedAtLength;
        _ = lastTimestampStart;
        _ = lastTimestampLength;

        if (!(hasTransaction && hasCustomer && hasMerchant && hasTerminal && hasLastField))
        {
            return false;
        }

        var knownMerchants = body.Slice(knownMerchantsStart, knownMerchantsLength);
        var merchantId = body.Slice(merchantIdStart, merchantIdLength);
        var mcc = body.Slice(mccStart, mccLength);

        Vectorizer.VectorizeProfile(
            amount,
            installments,
            customerAvg,
            txCount24h,
            ContainsQuoted(knownMerchants, merchantId),
            mcc,
            merchantAvg,
            isOnline,
            cardPresent,
            kmFromHome,
            hasLastTransaction,
            lastKmFromCurrent,
            output);
        return true;
    }

    private static bool TryParseTransaction(
        ReadOnlySpan<byte> source,
        ref int pos,
        out double amount,
        out double installments,
        out int requestedAtStart,
        out int requestedAtLength)
    {
        amount = 0;
        installments = 0;
        requestedAtStart = 0;
        requestedAtLength = 0;
        var hasAmount = false;
        var hasInstallments = false;
        var hasRequestedAt = false;

        if (!TryEnterObject(source, ref pos))
        {
            return false;
        }

        while (true)
        {
            pos = SkipWhiteSpace(source, pos);
            if ((uint)pos >= (uint)source.Length)
            {
                return false;
            }

            if (source[pos] == (byte)'}')
            {
                pos++;
                return hasAmount && hasInstallments && hasRequestedAt;
            }

            if (!TryReadStringBounds(source, ref pos, out var keyStart, out var keyLength) ||
                !TryConsumeColon(source, ref pos))
            {
                return false;
            }

            var key = source.Slice(keyStart, keyLength);
            if (key.SequenceEqual("amount"u8))
            {
                if (!TryReadNumber(source, ref pos, out amount))
                {
                    return false;
                }

                hasAmount = true;
            }
            else if (key.SequenceEqual("installments"u8))
            {
                if (!TryReadNumber(source, ref pos, out installments))
                {
                    return false;
                }

                hasInstallments = true;
            }
            else if (key.SequenceEqual("requested_at"u8))
            {
                if (!TryReadStringBounds(source, ref pos, out requestedAtStart, out requestedAtLength))
                {
                    return false;
                }

                hasRequestedAt = true;
            }
            else if (!SkipValue(source, ref pos))
            {
                return false;
            }

            if (!TrySkipDelimiter(source, ref pos))
            {
                return false;
            }
        }
    }

    private static bool TryParseCustomer(
        ReadOnlySpan<byte> source,
        ref int pos,
        out double customerAvg,
        out double txCount24h,
        out int knownMerchantsStart,
        out int knownMerchantsLength)
    {
        customerAvg = 0;
        txCount24h = 0;
        knownMerchantsStart = 0;
        knownMerchantsLength = 0;
        var hasCustomerAvg = false;
        var hasTxCount24h = false;
        var hasKnownMerchants = false;

        if (!TryEnterObject(source, ref pos))
        {
            return false;
        }

        while (true)
        {
            pos = SkipWhiteSpace(source, pos);
            if ((uint)pos >= (uint)source.Length)
            {
                return false;
            }

            if (source[pos] == (byte)'}')
            {
                pos++;
                return hasCustomerAvg && hasTxCount24h && hasKnownMerchants;
            }

            if (!TryReadStringBounds(source, ref pos, out var keyStart, out var keyLength) ||
                !TryConsumeColon(source, ref pos))
            {
                return false;
            }

            var key = source.Slice(keyStart, keyLength);
            if (key.SequenceEqual("avg_amount"u8))
            {
                if (!TryReadNumber(source, ref pos, out customerAvg))
                {
                    return false;
                }

                hasCustomerAvg = true;
            }
            else if (key.SequenceEqual("tx_count_24h"u8))
            {
                if (!TryReadNumber(source, ref pos, out txCount24h))
                {
                    return false;
                }

                hasTxCount24h = true;
            }
            else if (key.SequenceEqual("known_merchants"u8))
            {
                if (!TryReadArraySlice(source, ref pos, out knownMerchantsStart, out knownMerchantsLength))
                {
                    return false;
                }

                hasKnownMerchants = true;
            }
            else if (!SkipValue(source, ref pos))
            {
                return false;
            }

            if (!TrySkipDelimiter(source, ref pos))
            {
                return false;
            }
        }
    }

    private static bool TryParseMerchant(
        ReadOnlySpan<byte> source,
        ref int pos,
        out int merchantIdStart,
        out int merchantIdLength,
        out int mccStart,
        out int mccLength,
        out double merchantAvg)
    {
        merchantIdStart = 0;
        merchantIdLength = 0;
        mccStart = 0;
        mccLength = 0;
        merchantAvg = 0;
        var hasMerchantId = false;
        var hasMcc = false;
        var hasMerchantAvg = false;

        if (!TryEnterObject(source, ref pos))
        {
            return false;
        }

        while (true)
        {
            pos = SkipWhiteSpace(source, pos);
            if ((uint)pos >= (uint)source.Length)
            {
                return false;
            }

            if (source[pos] == (byte)'}')
            {
                pos++;
                return hasMerchantId && hasMcc && hasMerchantAvg;
            }

            if (!TryReadStringBounds(source, ref pos, out var keyStart, out var keyLength) ||
                !TryConsumeColon(source, ref pos))
            {
                return false;
            }

            var key = source.Slice(keyStart, keyLength);
            if (key.SequenceEqual("id"u8))
            {
                if (!TryReadStringBounds(source, ref pos, out merchantIdStart, out merchantIdLength))
                {
                    return false;
                }

                hasMerchantId = true;
            }
            else if (key.SequenceEqual("mcc"u8))
            {
                if (!TryReadStringBounds(source, ref pos, out mccStart, out mccLength))
                {
                    return false;
                }

                hasMcc = true;
            }
            else if (key.SequenceEqual("avg_amount"u8))
            {
                if (!TryReadNumber(source, ref pos, out merchantAvg))
                {
                    return false;
                }

                hasMerchantAvg = true;
            }
            else if (!SkipValue(source, ref pos))
            {
                return false;
            }

            if (!TrySkipDelimiter(source, ref pos))
            {
                return false;
            }
        }
    }

    private static bool TryParseTerminal(
        ReadOnlySpan<byte> source,
        ref int pos,
        out bool isOnline,
        out bool cardPresent,
        out double kmFromHome)
    {
        isOnline = false;
        cardPresent = false;
        kmFromHome = 0;
        var hasIsOnline = false;
        var hasCardPresent = false;
        var hasKmFromHome = false;

        if (!TryEnterObject(source, ref pos))
        {
            return false;
        }

        while (true)
        {
            pos = SkipWhiteSpace(source, pos);
            if ((uint)pos >= (uint)source.Length)
            {
                return false;
            }

            if (source[pos] == (byte)'}')
            {
                pos++;
                return hasIsOnline && hasCardPresent && hasKmFromHome;
            }

            if (!TryReadStringBounds(source, ref pos, out var keyStart, out var keyLength) ||
                !TryConsumeColon(source, ref pos))
            {
                return false;
            }

            var key = source.Slice(keyStart, keyLength);
            if (key.SequenceEqual("is_online"u8))
            {
                if (!TryReadBool(source, ref pos, out isOnline))
                {
                    return false;
                }

                hasIsOnline = true;
            }
            else if (key.SequenceEqual("card_present"u8))
            {
                if (!TryReadBool(source, ref pos, out cardPresent))
                {
                    return false;
                }

                hasCardPresent = true;
            }
            else if (key.SequenceEqual("km_from_home"u8))
            {
                if (!TryReadNumber(source, ref pos, out kmFromHome))
                {
                    return false;
                }

                hasKmFromHome = true;
            }
            else if (!SkipValue(source, ref pos))
            {
                return false;
            }

            if (!TrySkipDelimiter(source, ref pos))
            {
                return false;
            }
        }
    }

    private static bool TryParseLastTransactionValue(
        ReadOnlySpan<byte> source,
        ref int pos,
        out bool hasLastTransaction,
        out int lastTimestampStart,
        out int lastTimestampLength,
        out double lastKmFromCurrent)
    {
        hasLastTransaction = false;
        lastTimestampStart = 0;
        lastTimestampLength = 0;
        lastKmFromCurrent = 0;
        pos = SkipWhiteSpace(source, pos);
        if (source[pos..].StartsWith("null"u8))
        {
            pos += 4;
            return true;
        }

        hasLastTransaction = true;
        return TryParseLastTransactionObject(source, ref pos, out lastTimestampStart, out lastTimestampLength, out lastKmFromCurrent);
    }

    private static bool TryParseLastTransactionObject(
        ReadOnlySpan<byte> source,
        ref int pos,
        out int timestampStart,
        out int timestampLength,
        out double kmFromCurrent)
    {
        timestampStart = 0;
        timestampLength = 0;
        kmFromCurrent = 0;
        var hasTimestamp = false;
        var hasKmFromCurrent = false;

        if (!TryEnterObject(source, ref pos))
        {
            return false;
        }

        while (true)
        {
            pos = SkipWhiteSpace(source, pos);
            if ((uint)pos >= (uint)source.Length)
            {
                return false;
            }

            if (source[pos] == (byte)'}')
            {
                pos++;
                return hasTimestamp && hasKmFromCurrent;
            }

            if (!TryReadStringBounds(source, ref pos, out var keyStart, out var keyLength) ||
                !TryConsumeColon(source, ref pos))
            {
                return false;
            }

            var key = source.Slice(keyStart, keyLength);
            if (key.SequenceEqual("timestamp"u8))
            {
                if (!TryReadStringBounds(source, ref pos, out timestampStart, out timestampLength))
                {
                    return false;
                }

                hasTimestamp = true;
            }
            else if (key.SequenceEqual("km_from_current"u8))
            {
                if (!TryReadNumber(source, ref pos, out kmFromCurrent))
                {
                    return false;
                }

                hasKmFromCurrent = true;
            }
            else if (!SkipValue(source, ref pos))
            {
                return false;
            }

            if (!TrySkipDelimiter(source, ref pos))
            {
                return false;
            }
        }
    }

    private static bool TryEnterObject(ReadOnlySpan<byte> source, ref int pos)
    {
        pos = SkipWhiteSpace(source, pos);
        if ((uint)pos >= (uint)source.Length || source[pos] != (byte)'{')
        {
            return false;
        }

        pos++;
        return true;
    }

    private static bool TryReadStringBounds(ReadOnlySpan<byte> source, ref int pos, out int start, out int length)
    {
        start = 0;
        length = 0;
        pos = SkipWhiteSpace(source, pos);
        if ((uint)pos >= (uint)source.Length || source[pos] != (byte)'"')
        {
            return false;
        }

        pos++;
        start = pos;
        var escaped = false;
        while ((uint)pos < (uint)source.Length)
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
                length = pos - start;
                pos++;
                return true;
            }

            pos++;
        }

        return false;
    }

    private static bool TryConsumeColon(ReadOnlySpan<byte> source, ref int pos)
    {
        pos = SkipWhiteSpace(source, pos);
        if ((uint)pos >= (uint)source.Length || source[pos] != (byte)':')
        {
            return false;
        }

        pos++;
        return true;
    }

    private static bool TryReadNumber(ReadOnlySpan<byte> source, ref int pos, out double value)
    {
        value = 0;
        pos = SkipWhiteSpace(source, pos);
        var start = pos;

        var negative = false;
        if ((uint)pos < (uint)source.Length && source[pos] is (byte)'-' or (byte)'+')
        {
            negative = source[pos] == (byte)'-';
            pos++;
        }

        long integer = 0;
        var digits = 0;
        while ((uint)pos < (uint)source.Length)
        {
            var digit = source[pos] - (byte)'0';
            if ((uint)digit > 9)
            {
                break;
            }

            integer = integer * 10 + digit;
            digits++;
            pos++;
        }

        double result = integer;
        if ((uint)pos < (uint)source.Length && source[pos] == (byte)'.')
        {
            pos++;
            var scale = 0.1;
            while ((uint)pos < (uint)source.Length)
            {
                var digit = source[pos] - (byte)'0';
                if ((uint)digit > 9)
                {
                    break;
                }

                result += digit * scale;
                scale *= 0.1;
                digits++;
                pos++;
            }
        }

        if (digits == 0)
        {
            pos = start;
            return false;
        }

        if ((uint)pos < (uint)source.Length && source[pos] is (byte)'e' or (byte)'E')
        {
            pos++;
            if ((uint)pos < (uint)source.Length && source[pos] is (byte)'-' or (byte)'+')
            {
                pos++;
            }

            while ((uint)pos < (uint)source.Length && IsDigit(source[pos]))
            {
                pos++;
            }

            return System.Buffers.Text.Utf8Parser.TryParse(source[start..pos], out value, out var consumed) &&
                   consumed == pos - start;
        }

        value = negative ? -result : result;
        return true;
    }

    private static bool TryReadBool(ReadOnlySpan<byte> source, ref int pos, out bool value)
    {
        value = false;
        pos = SkipWhiteSpace(source, pos);
        if (source[pos..].StartsWith("true"u8))
        {
            value = true;
            pos += 4;
            return true;
        }

        if (source[pos..].StartsWith("false"u8))
        {
            pos += 5;
            return true;
        }

        return false;
    }

    private static bool TryReadArraySlice(ReadOnlySpan<byte> source, ref int pos, out int start, out int length)
    {
        start = 0;
        length = 0;
        pos = SkipWhiteSpace(source, pos);
        if ((uint)pos >= (uint)source.Length || source[pos] != (byte)'[')
        {
            return false;
        }

        return TryReadBalancedSlice(source, ref pos, (byte)'[', (byte)']', out start, out length);
    }

    private static bool TryReadBalancedSlice(
        ReadOnlySpan<byte> source,
        ref int pos,
        byte open,
        byte close,
        out int start,
        out int length)
    {
        start = 0;
        length = 0;
        if ((uint)pos >= (uint)source.Length || source[pos] != open)
        {
            return false;
        }

        start = pos;
        var depth = 0;
        var inString = false;
        var escaped = false;

        while ((uint)pos < (uint)source.Length)
        {
            var b = source[pos++];
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

                continue;
            }

            if (b == (byte)'"')
            {
                inString = true;
            }
            else if (b == open)
            {
                depth++;
            }
            else if (b == close)
            {
                depth--;
                if (depth == 0)
                {
                    length = pos - start;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TrySkipDelimiter(ReadOnlySpan<byte> source, ref int pos)
    {
        pos = SkipWhiteSpace(source, pos);
        if ((uint)pos >= (uint)source.Length)
        {
            return false;
        }

        if (source[pos] == (byte)',')
        {
            pos++;
            return true;
        }

        return source[pos] == (byte)'}';
    }

    private static bool SkipValue(ReadOnlySpan<byte> source, ref int pos)
    {
        pos = SkipWhiteSpace(source, pos);
        if ((uint)pos >= (uint)source.Length)
        {
            return false;
        }

        var start = source[pos];
        if (start == (byte)'"')
        {
            return TryReadStringBounds(source, ref pos, out _, out _);
        }

        if (start == (byte)'{')
        {
            return TryReadBalancedSlice(source, ref pos, (byte)'{', (byte)'}', out _, out _);
        }

        if (start == (byte)'[')
        {
            return TryReadBalancedSlice(source, ref pos, (byte)'[', (byte)']', out _, out _);
        }

        while ((uint)pos < (uint)source.Length)
        {
            var b = source[pos];
            if (b is (byte)',' or (byte)'}' or (byte)']' || IsWhiteSpace(b))
            {
                return true;
            }

            pos++;
        }

        return true;
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

    private static int SkipWhiteSpace(ReadOnlySpan<byte> source, int pos)
    {
        while ((uint)pos < (uint)source.Length && IsWhiteSpace(source[pos]))
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
