namespace DiskImager.Core.Ntfs;

/// <summary>
/// Pure .NET versions of the two Windows decompressors NTFS uses, for the Linux rescue system (Windows uses
/// ntdll's own): LZNT1 (NTFS compression) and XPRESS Huffman (Windows' per-file compression, "compact /exe").
/// Both follow Microsoft's published format description [MS-XCA].
/// </summary>
public static class Decompressors
{
    /// <summary>LZNT1: a series of chunks, each holding up to 4 KB of output. Returns the bytes produced.</summary>
    public static int Lznt1(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int ip = 0, chunkOut = 0, produced = 0;
        while (ip + 2 <= input.Length && chunkOut < output.Length)
        {
            int header = input[ip] | input[ip + 1] << 8;
            ip += 2;
            if (header == 0) break;
            int size = (header & 0x0FFF) + 1;
            if (ip + size > input.Length) throw new InvalidDataException("LZNT1 data is cut short.");
            var chunk = input.Slice(ip, size);
            ip += size;
            int limit = Math.Min(4096, output.Length - chunkOut);
            var dest = output.Slice(chunkOut, limit);
            int n;
            if ((header & 0x8000) == 0)
            {
                n = Math.Min(size, limit);
                chunk[..n].CopyTo(dest);
            }
            else n = Lznt1Chunk(chunk, dest);
            produced = chunkOut + n;
            chunkOut += 4096; // every chunk but the last stands for a full 4 KB (short ones are zero-padded)
        }
        return Math.Min(produced, output.Length);
    }

    private static int Lznt1Chunk(ReadOnlySpan<byte> src, Span<byte> dest)
    {
        int sp = 0, dp = 0;
        while (sp < src.Length && dp < dest.Length)
        {
            byte flags = src[sp++];
            for (int bit = 0; bit < 8 && sp < src.Length && dp < dest.Length; bit++)
            {
                if ((flags & (1 << bit)) == 0)
                {
                    dest[dp++] = src[sp++];
                    continue;
                }
                if (sp + 2 > src.Length) throw new InvalidDataException("LZNT1 token is cut short.");
                int token = src[sp] | src[sp + 1] << 8;
                sp += 2;
                // The split between offset and length bits depends on how far into the chunk we are.
                int lg = 0;
                for (int i = dp - 1; i >= 0x10; i >>= 1) lg++;
                int offset = (token >> (12 - lg)) + 1;
                int length = (token & (0xFFF >> lg)) + 3;
                if (offset > dp) throw new InvalidDataException("LZNT1 match points before the start of the chunk.");
                for (int k = 0; k < length && dp < dest.Length; k++, dp++) dest[dp] = dest[dp - offset];
            }
        }
        return dp;
    }

    /// <summary>XPRESS Huffman: blocks of up to 64 KB output, each starting with a 256-byte table of code lengths.</summary>
    public static int XpressHuffman(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int ip = 0, op = 0;
        var lengths = new byte[512];
        var table = new ushort[1 << 15];
        while (op < output.Length)
        {
            if (ip + 256 + 4 > input.Length) throw new InvalidDataException("XPRESS data is cut short.");
            for (int i = 0; i < 256; i++)
            {
                lengths[2 * i] = (byte)(input[ip + i] & 0x0F);
                lengths[2 * i + 1] = (byte)(input[ip + i] >> 4);
            }
            ip += 256;
            BuildTable(lengths, table);

            uint Read16(ReadOnlySpan<byte> s, ref int p)
            {
                uint v = p + 2 <= s.Length ? (uint)(s[p] | s[p + 1] << 8) : 0;
                p += 2;
                return v;
            }
            uint next = Read16(input, ref ip) << 16 | Read16(input, ref ip);
            int extra = 16;
            int blockEnd = Math.Min(op + 65536, output.Length);

            while (op < blockEnd)
            {
                int symbol = table[next >> 17];
                int len = lengths[symbol];
                if (len == 0) throw new InvalidDataException("XPRESS data is damaged.");
                next <<= len;
                extra -= len;
                if (extra < 0)
                {
                    next |= Read16(input, ref ip) << -extra;
                    extra += 16;
                }
                if (symbol < 256)
                {
                    output[op++] = (byte)symbol;
                    continue;
                }
                symbol -= 256;
                int matchLength = symbol & 0x0F;
                int offsetBits = symbol >> 4;
                if (matchLength == 15)
                {
                    if (ip >= input.Length) throw new InvalidDataException("XPRESS data is cut short.");
                    matchLength = input[ip++];
                    if (matchLength == 255)
                    {
                        if (ip + 2 > input.Length) throw new InvalidDataException("XPRESS data is cut short.");
                        matchLength = input[ip] | input[ip + 1] << 8;
                        ip += 2;
                        if (matchLength == 0)
                        {
                            if (ip + 4 > input.Length) throw new InvalidDataException("XPRESS data is cut short.");
                            matchLength = BitConverter.ToInt32(input.Slice(ip, 4));
                            ip += 4;
                        }
                        if (matchLength < 15) throw new InvalidDataException("XPRESS data is damaged.");
                        matchLength -= 15;
                    }
                    matchLength += 15;
                }
                matchLength += 3;
                int offset = (offsetBits == 0 ? 0 : (int)(next >> (32 - offsetBits))) + (1 << offsetBits);
                next = offsetBits == 0 ? next : next << offsetBits;
                extra -= offsetBits;
                if (extra < 0)
                {
                    next |= Read16(input, ref ip) << -extra;
                    extra += 16;
                }
                if (offset > op) throw new InvalidDataException("XPRESS match points before the start of the data.");
                for (int k = 0; k < matchLength && op < output.Length; k++, op++) output[op] = output[op - offset];
            }
        }
        return op;
    }

    /// <summary>Canonical Huffman decoding table: index = the next 15 bits, value = the symbol.</summary>
    private static void BuildTable(byte[] lengths, ushort[] table)
    {
        int pos = 0;
        for (int len = 1; len <= 15; len++)
            for (int sym = 0; sym < 512; sym++)
            {
                if (lengths[sym] != len) continue;
                int count = 1 << (15 - len);
                if (pos + count > table.Length) throw new InvalidDataException("XPRESS code table is invalid.");
                Array.Fill(table, (ushort)sym, pos, count);
                pos += count;
            }
        if (pos < table.Length) Array.Fill(table, (ushort)0, pos, table.Length - pos);
    }
}
