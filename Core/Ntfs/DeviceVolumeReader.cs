namespace DiskImager.Core.Ntfs;

/// <summary>A raw Windows device (e.g. a snapshot volume) read in whole sectors, as Windows requires.</summary>
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
