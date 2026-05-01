namespace RinhaFraud;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

internal static class BinaryIndexBuilder
{
    private static ReadOnlySpan<byte> Magic => "RINHA26I"u8;
    private const uint Version = 1;
    private const int HeaderLength = 80;

    public static void Build(string outputPath, Stream input)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        Span<byte> header = stackalloc byte[HeaderLength];
        output.Write(header);
        var vectorsOffset = output.Position;

        var labels = new List<byte>(3_000_000);
        var keys = new List<ushort>(3_000_000);
        Span<uint> bucketCounts = stackalloc uint[Constants.BucketCount];
        var scanner = new JsonScanner(input);
        Span<short> vector = stackalloc short[Constants.Dim];
        Span<byte> twoBytes = stackalloc byte[2];

        while (scanner.Find("\"vector\""u8))
        {
            scanner.ExpectUntil((byte)'[');
            for (var i = 0; i < Constants.Dim; i++)
            {
                scanner.SkipWhiteSpaceAndCommas();
                vector[i] = Vectorizer.QuantizeReference(scanner.ReadNumber());
            }

            scanner.FindRequired("\"label\""u8);
            scanner.ExpectUntil((byte)':');
            scanner.SkipWhiteSpace();
            var label = scanner.ReadLabel();
            var key = Vectorizer.BucketKey(vector);
            bucketCounts[key]++;

            for (var i = 0; i < Constants.Dim; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(twoBytes, vector[i]);
                output.Write(twoBytes);
            }

            labels.Add(label);
            keys.Add(key);
        }

        if (labels.Count == 0)
        {
            throw new InvalidOperationException("no reference vectors found");
        }

        var offsets = new uint[Constants.BucketCount + 1];
        for (var i = 0; i < Constants.BucketCount; i++)
        {
            offsets[i + 1] = offsets[i] + bucketCounts[i];
        }

        var writePositions = new uint[Constants.BucketCount];
        offsets.AsSpan(0, Constants.BucketCount).CopyTo(writePositions);
        var items = new uint[labels.Count];
        for (var id = 0; id < keys.Count; id++)
        {
            var key = keys[id];
            var itemPos = writePositions[key]++;
            items[(int)itemPos] = (uint)id;
        }

        var labelsOffset = output.Position;
        output.Write(CollectionsMarshal.AsSpan(labels));

        var bucketOffsetsOffset = output.Position;
        Span<byte> fourBytes = stackalloc byte[4];
        foreach (var value in offsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fourBytes, value);
            output.Write(fourBytes);
        }

        var bucketItemsOffset = output.Position;
        foreach (var value in items)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fourBytes, value);
            output.Write(fourBytes);
        }

        var fileLength = output.Position;
        output.Position = 0;
        WriteHeader(output, labels.Count, vectorsOffset, labelsOffset, bucketOffsetsOffset, bucketItemsOffset, fileLength);
        Console.Error.WriteLine($"indexed {labels.Count} vectors into {outputPath} ({Constants.BucketCount} buckets, {fileLength} bytes)");
    }

    private static void WriteHeader(
        FileStream output,
        int count,
        long vectorsOffset,
        long labelsOffset,
        long bucketOffsetsOffset,
        long bucketItemsOffset,
        long fileLength)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Constants.Dim);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], Constants.Scale);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], Constants.BucketCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)vectorsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], (ulong)labelsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], (ulong)bucketOffsetsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[56..], (ulong)bucketItemsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[64..], (ulong)fileLength);
        output.Write(header);
    }

    private sealed class JsonScanner
    {
        private readonly Stream _input;
        private readonly byte[] _buffer;
        private int _pos;
        private int _len;

        public JsonScanner(Stream input)
        {
            _input = input;
            _buffer = new byte[64 * 1024];
        }

        public bool Find(ReadOnlySpan<byte> needle)
        {
            var matched = 0;
            while (TryReadByte(out var b))
            {
                if (b == needle[matched])
                {
                    matched++;
                    if (matched == needle.Length)
                    {
                        return true;
                    }
                }
                else
                {
                    matched = b == needle[0] ? 1 : 0;
                }
            }

            return false;
        }

        public void FindRequired(ReadOnlySpan<byte> needle)
        {
            if (!Find(needle))
            {
                throw new InvalidOperationException("unexpected EOF while scanning JSON");
            }
        }

        public void ExpectUntil(byte expected)
        {
            while (TryReadByte(out var b))
            {
                if (b == expected)
                {
                    return;
                }
            }

            throw new InvalidOperationException("unexpected EOF while scanning JSON");
        }

        public void SkipWhiteSpace()
        {
            while (TryReadByte(out var b))
            {
                if (IsWhiteSpace(b))
                {
                    continue;
                }

                Unread();
                return;
            }
        }

        public void SkipWhiteSpaceAndCommas()
        {
            while (TryReadByte(out var b))
            {
                if (IsWhiteSpace(b) || b == (byte)',')
                {
                    continue;
                }

                Unread();
                return;
            }

            throw new InvalidOperationException("unexpected EOF while reading vector");
        }

        public double ReadNumber()
        {
            Span<byte> token = stackalloc byte[64];
            var len = 0;
            while (TryReadByte(out var b))
            {
                if (IsDigit(b) || b is (byte)'-' or (byte)'+' or (byte)'.' or (byte)'e' or (byte)'E')
                {
                    if (len >= token.Length)
                    {
                        throw new InvalidOperationException("numeric token too long");
                    }

                    token[len++] = b;
                    continue;
                }

                Unread();
                break;
            }

            if (len == 0 ||
                !System.Buffers.Text.Utf8Parser.TryParse(token[..len], out double value, out var consumed) ||
                consumed != len)
            {
                throw new InvalidOperationException("bad numeric token");
            }

            return value;
        }

        public byte ReadLabel()
        {
            Span<byte> text = stackalloc byte[8];
            var len = ReadString(text);
            var label = text[..len];
            if (label.SequenceEqual("fraud"u8))
            {
                return 1;
            }

            if (label.SequenceEqual("legit"u8))
            {
                return 0;
            }

            throw new InvalidOperationException("unknown label");
        }

        private int ReadString(Span<byte> output)
        {
            if (!TryReadByte(out var first) || first != (byte)'"')
            {
                throw new InvalidOperationException("expected string");
            }

            var len = 0;
            var escaped = false;
            while (TryReadByte(out var b))
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (b == (byte)'\\')
                {
                    escaped = true;
                    continue;
                }
                else if (b == (byte)'"')
                {
                    return len;
                }

                if (len >= output.Length)
                {
                    throw new InvalidOperationException("string token too long");
                }

                output[len++] = b;
            }

            throw new InvalidOperationException("unexpected EOF while reading string");
        }

        private bool TryReadByte(out byte value)
        {
            if (_pos >= _len)
            {
                _len = _input.Read(_buffer, 0, _buffer.Length);
                _pos = 0;
                if (_len == 0)
                {
                    value = 0;
                    return false;
                }
            }

            value = _buffer[_pos++];
            return true;
        }

        private void Unread()
        {
            _pos--;
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
}
