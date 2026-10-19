using System.Runtime.InteropServices;

namespace DiskImager.Core.Ntfs;

/// <summary>Windows' own decompressors (ntdll), available everywhere including the rescue environment.</summary>
internal static class RtlCompression
{
    public const ushort Lznt1 = 2, Xpress = 3, XpressHuff = 4;

    [DllImport("ntdll.dll")]
    private static extern int RtlGetCompressionWorkSpaceSize(ushort format, out uint bufferWorkSpace, out uint fragmentWorkSpace);

    [DllImport("ntdll.dll")]
    private static extern unsafe int RtlDecompressBufferEx(ushort format, byte* output, uint outputSize, byte* input, uint inputSize, out uint finalSize, byte* workSpace);

    /// <summary>Decompresses <paramref name="input"/> into <paramref name="output"/>; returns the number of bytes produced.</summary>
    public static unsafe int Decompress(ushort format, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (RtlGetCompressionWorkSpaceSize(format, out uint ws, out _) != 0) throw new InvalidDataException("Unsupported compression format.");
        var work = new byte[Math.Max(ws, 1)];
        fixed (byte* o = output) fixed (byte* i = input) fixed (byte* w = work)
        {
            int status = RtlDecompressBufferEx(format, o, (uint)output.Length, i, (uint)input.Length, out uint final, w);
            if (status < 0) throw new InvalidDataException($"Compressed data is damaged (0x{status:X8}).");
            return (int)final;
        }
    }
}

/// <summary>Reads an NTFS attribute's data: runs, sparse holes, NTFS (LZNT1) compression and uninitialized tails.</summary>
internal sealed class NtfsAttributeStream : Stream
{
    private readonly NtfsVolume _vol;
    private readonly NtfsAttribute _a;
    private readonly int _cs;
    private long _pos;
    // Decoded compression unit cache (compressed files are read a whole unit at a time).
    private long _unitStart = -1;
    private byte[] _unit = Array.Empty<byte>();

    public NtfsAttributeStream(NtfsVolume vol, NtfsAttribute a)
    {
        _vol = vol;
        _a = a;
        _cs = vol.ClusterSize;
        if (a.IsEncrypted) throw new NotSupportedException("encrypted by Windows (EFS); copy it from Windows with the owner's account instead");
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _a.RealSize;
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos >= Length) return 0;
        count = (int)Math.Min(count, Length - _pos);
        if (!_a.NonResident)
        {
            Buffer.BlockCopy(_a.Value!, (int)_pos, buffer, offset, count);
            _pos += count;
            return count;
        }
        int done = 0;
        while (done < count)
        {
            int n = _a.IsCompressed && _a.CompressionUnit > 0
                ? ReadCompressed(_pos, buffer, offset + done, count - done)
                : ReadPlain(_pos, buffer, offset + done, count - done);
            if (n <= 0) break;
            _pos += n;
            done += n;
        }
        // Data past the "initialized" size was never written and reads as zeros.
        long initEnd = _a.InitializedSize;
        long startPos = _pos - done;
        if (startPos + done > initEnd)
        {
            long zeroFrom = Math.Max(startPos, initEnd);
            Array.Clear(buffer, offset + (int)(zeroFrom - startPos), (int)(startPos + done - zeroFrom));
        }
        return done;
    }

    private int ReadPlain(long pos, byte[] buffer, int offset, int count)
    {
        long vcn = pos / _cs;
        int within = (int)(pos % _cs);
        foreach (var run in _a.Runs)
        {
            if (vcn < run.Vcn || vcn >= run.Vcn + run.Length) continue;
            long bytesLeftInRun = (run.Vcn + run.Length - vcn) * _cs - within;
            int n = (int)Math.Min(count, bytesLeftInRun);
            if (run.Lcn < 0) Array.Clear(buffer, offset, n);
            else _vol.Reader.Read((run.Lcn + (vcn - run.Vcn)) * _cs + within, buffer, offset, n);
            return n;
        }
        // Beyond the last run: the rest is unallocated.
        int z = (int)Math.Min(count, Length - pos);
        Array.Clear(buffer, offset, z);
        return z;
    }

    /// <summary>
    /// NTFS compression works in units of 16 clusters (usually 64 KB). A unit whose clusters are all
    /// allocated is stored as-is; one that's partly sparse holds LZNT1 data in its first clusters; one that's
    /// entirely sparse is zeros.
    /// </summary>
    private int ReadCompressed(long pos, byte[] buffer, int offset, int count)
    {
        long unitClusters = 1L << _a.CompressionUnit;
        long unitBytes = unitClusters * _cs;
        long unitStart = pos / unitBytes * unitBytes;
        if (unitStart != _unitStart)
        {
            if (_unit.Length != unitBytes) _unit = new byte[unitBytes];
            DecodeUnit(unitStart / _cs, unitClusters, _unit);
            _unitStart = unitStart;
        }
        int within = (int)(pos - unitStart);
        int n = (int)Math.Min(count, unitBytes - within);
        Buffer.BlockCopy(_unit, within, buffer, offset, n);
        return n;
    }

    private void DecodeUnit(long firstVcn, long clusters, byte[] into)
    {
        var allocated = new List<(long Vcn, long Lcn, long Count)>();
        long sparse = 0;
        for (long v = firstVcn; v < firstVcn + clusters;)
        {
            var run = _a.Runs.FirstOrDefault(r => v >= r.Vcn && v < r.Vcn + r.Length);
            long take = run.Length == 0 ? firstVcn + clusters - v : Math.Min(run.Vcn + run.Length - v, firstVcn + clusters - v);
            if (run.Length == 0 || run.Lcn < 0) sparse += take;
            else allocated.Add((v, run.Lcn + (v - run.Vcn), take));
            v += take;
        }
        Array.Clear(into);
        if (allocated.Count == 0) return; // all sparse: zeros
        var raw = new byte[allocated.Sum(x => x.Count) * _cs];
        int at = 0;
        foreach (var (_, lcn, n) in allocated)
        {
            _vol.ReadClusters(lcn, n, raw, at);
            at += (int)(n * _cs);
        }
        if (sparse == 0) { Buffer.BlockCopy(raw, 0, into, 0, Math.Min(raw.Length, into.Length)); return; }
        RtlCompression.Decompress(RtlCompression.Lznt1, raw, into);
    }

    public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _pos + offset,
        _ => Length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Files Windows compressed with "compact /exe" or CompactOS (WOF): the real data is in the
/// WofCompressedData stream as independently compressed chunks with an offset table in front.
/// </summary>
internal sealed class WofStream : Stream
{
    private readonly Stream _compressed;
    private readonly long _length;
    private readonly int _chunk;
    private readonly ushort _format;
    private readonly long[] _chunkEnds;
    private readonly long _dataStart;
    private long _pos;
    private int _cachedChunk = -1;
    private readonly byte[] _chunkBuf;

    public WofStream(Stream compressed, long uncompressedLength, int algorithm)
    {
        _compressed = compressed;
        _length = uncompressedLength;
        (_chunk, _format) = algorithm switch
        {
            0 => (4096, RtlCompression.XpressHuff),
            2 => (8192, RtlCompression.XpressHuff),
            3 => (16384, RtlCompression.XpressHuff),
            1 => throw new NotSupportedException("compressed by Windows with LZX, which DiskImager can't unpack yet"),
            _ => throw new NotSupportedException($"compressed by Windows with an unknown method ({algorithm})"),
        };
        long chunks = (_length + _chunk - 1) / _chunk;
        int entry = _length > uint.MaxValue ? 8 : 4;
        var table = new byte[Math.Max(0, (chunks - 1) * entry)];
        _compressed.Position = 0;
        _compressed.ReadExactly(table);
        _dataStart = table.Length;
        _chunkEnds = new long[chunks];
        for (long i = 0; i < chunks - 1; i++)
            _chunkEnds[i] = entry == 8 ? BitConverter.ToInt64(table, (int)(i * 8)) : BitConverter.ToUInt32(table, (int)(i * 4));
        if (chunks > 0) _chunkEnds[chunks - 1] = _compressed.Length - _dataStart;
        _chunkBuf = new byte[_chunk];
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos >= _length) return 0;
        int i = (int)(_pos / _chunk);
        if (i != _cachedChunk) { Decode(i); _cachedChunk = i; }
        int within = (int)(_pos % _chunk);
        int size = (int)Math.Min(_chunk, _length - (long)i * _chunk);
        int n = Math.Min(count, size - within);
        Buffer.BlockCopy(_chunkBuf, within, buffer, offset, n);
        _pos += n;
        return n;
    }

    private void Decode(int i)
    {
        long start = i == 0 ? 0 : _chunkEnds[i - 1];
        int compressedSize = (int)(_chunkEnds[i] - start);
        int size = (int)Math.Min(_chunk, _length - (long)i * _chunk);
        var raw = new byte[compressedSize];
        _compressed.Position = _dataStart + start;
        _compressed.ReadExactly(raw);
        if (compressedSize == size) { Buffer.BlockCopy(raw, 0, _chunkBuf, 0, size); return; } // stored uncompressed
        int got = RtlCompression.Decompress(_format, raw, _chunkBuf.AsSpan(0, size));
        if (got != size) throw new InvalidDataException("A compressed chunk is damaged.");
    }

    public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _pos + offset,
        _ => _length + offset,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _compressed.Dispose();
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
