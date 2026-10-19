namespace DiskImager.Core.Ntfs;

/// <summary>Random-access reads from one volume (a partition inside an image, a raw device, or a file).</summary>
public interface IVolumeReader
{
    long Length { get; }
    /// <summary>Fills <paramref name="buffer"/> with the bytes at <paramref name="offset"/> (zeros past the end).</summary>
    void Read(long offset, byte[] buffer, int index, int count);
}

/// <summary>A partition inside a backup image. Blocks that aren't in the image (free space) read as zeros.</summary>
public sealed class ImageVolumeReader : IVolumeReader
{
    private readonly ImageReader _image;
    private readonly long _start;
    private readonly int _bs;
    private readonly object _lock = new();
    // Small cache of decoded blocks: directory and MFT reads hit the same blocks again and again.
    private readonly Dictionary<long, byte[]> _cache = new();
    private readonly LinkedList<long> _lru = new();
    private const int CacheBlocks = 48;

    public long Length { get; }

    public ImageVolumeReader(ImageReader image, long partitionOffset, long partitionLength)
    {
        _image = image;
        _start = partitionOffset;
        Length = partitionLength;
        _bs = image.BlockSize;
    }

    public void Read(long offset, byte[] buffer, int index, int count)
    {
        lock (_lock)
        {
            while (count > 0)
            {
                if (offset >= Length) { Array.Clear(buffer, index, count); return; }
                long abs = _start + offset;
                long block = abs / _bs;
                int within = (int)(abs % _bs);
                int n = (int)Math.Min(Math.Min(count, _bs - within), Length - offset);
                var data = Block(block);
                if (data == null) Array.Clear(buffer, index, n);
                else Buffer.BlockCopy(data, within, buffer, index, n);
                offset += n; index += n; count -= n;
            }
        }
    }

    private byte[]? Block(long block)
    {
        if (_cache.TryGetValue(block, out var hit))
        {
            _lru.Remove(block);
            _lru.AddFirst(block);
            return hit;
        }
        var blocks = (long[])_image.Blocks;
        if (Array.BinarySearch(blocks, block) < 0) return null; // free space: never backed up
        var buf = new byte[_bs];
        _image.ReadBlock(block, buf, out _);
        _cache[block] = buf;
        _lru.AddFirst(block);
        if (_lru.Count > CacheBlocks)
        {
            _cache.Remove(_lru.Last!.Value);
            _lru.RemoveLast();
        }
        return buf;
    }
}

/// <summary>A volume stored in a file or opened as a device (tests, and reading live snapshots).</summary>
public sealed class StreamVolumeReader : IVolumeReader, IDisposable
{
    private readonly Stream _s;
    private readonly long _start;
    private readonly object _lock = new();

    public long Length { get; }

    public StreamVolumeReader(Stream s, long start, long length)
    {
        _s = s;
        _start = start;
        Length = length;
    }

    public void Read(long offset, byte[] buffer, int index, int count)
    {
        lock (_lock)
        {
            long avail = Math.Max(0, Math.Min(count, Length - offset));
            Array.Clear(buffer, index + (int)avail, count - (int)avail);
            if (avail == 0) return;
            _s.Position = _start + offset;
            _s.ReadExactly(buffer, index, (int)avail);
        }
    }

    public void Dispose() => _s.Dispose();
}

/// <summary>A raw device (e.g. a snapshot volume) read in whole sectors, as Windows requires.</summary>
public sealed class DeviceVolumeReader : IVolumeReader
{
    private readonly RawDevice _dev;
    private readonly object _lock = new();
    private byte[] _buf = new byte[1 << 20];

    public long Length { get; }

    public DeviceVolumeReader(RawDevice dev, long length)
    {
        _dev = dev;
        Length = length;
    }

    public void Read(long offset, byte[] buffer, int index, int count)
    {
        lock (_lock)
        {
            const int sector = 4096;
            long start = offset / sector * sector;
            long end = Math.Min((offset + count + sector - 1) / sector * sector, (Length + sector - 1) / sector * sector);
            int len = (int)Math.Max(0, end - start);
            if (_buf.Length < len) _buf = new byte[len];
            if (len > 0) _dev.ReadAt(start, _buf, 0, len);
            int avail = (int)Math.Max(0, Math.Min(count, len - (offset - start)));
            if (avail > 0) Buffer.BlockCopy(_buf, (int)(offset - start), buffer, index, avail);
            if (avail < count) Array.Clear(buffer, index + avail, count - avail);
        }
    }
}
