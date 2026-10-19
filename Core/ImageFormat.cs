using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskImager.Core;

/*
 * .dimg file layout
 *   "DIMGHDR1" | int32 header length | UTF-8 JSON header
 *   block data records (deflate / stored), written in disk order
 *   index: N x 48-byte entries (one per used block of the disk at backup time)
 *   int64 index offset | int32 entry count | "DIMGIDX1"
 *
 * Every image's index describes the WHOLE disk at that point in time. Entries for blocks that
 * did not change since the parent image point back into the earlier file of the same set
 * (Ordinal = position in ChainFiles), so restoring any image just walks its own index.
 */

public sealed class ImagePartition
{
    public int Number { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public bool IsContainer { get; set; }
    public string Type { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public string Label { get; set; } = "";
    public string? DriveLetter { get; set; }
}

public sealed class ImageHeader
{
    public int FormatVersion { get; set; } = 1;
    public Guid ImageId { get; set; }
    public Guid ChainId { get; set; }
    public bool IsFull { get; set; }
    public int ChainOrdinal { get; set; }
    public List<string> ChainFiles { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public string JobName { get; set; } = "";
    public string ComputerName { get; set; } = "";
    public int BlockSize { get; set; }
    public long DiskSize { get; set; }
    public int SectorSize { get; set; }
    public string DiskId { get; set; } = "";
    public string DiskModel { get; set; } = "";
    public DiskStyle Style { get; set; }
    public List<ImagePartition> Partitions { get; set; } = new();
    public long UsedBytes { get; set; }

    [JsonIgnore] public string KindText => IsFull ? "Full" : $"Incremental #{ChainOrdinal}";
}

public enum BlockFlags { Deflate = 0, Zero = 1, Stored = 2 }

public sealed class IndexEntry
{
    public const int Size = 48;
    public long BlockIndex;
    public int Ordinal;
    public BlockFlags Flags;
    public long Offset;
    public int Length;
    public byte[] Hash = new byte[16];

    public void Write(BinaryWriter w)
    {
        w.Write(BlockIndex); w.Write(Ordinal); w.Write((int)Flags); w.Write(Offset); w.Write(Length); w.Write(0); w.Write(Hash);
    }

    public static IndexEntry Read(BinaryReader r)
    {
        var e = new IndexEntry { BlockIndex = r.ReadInt64(), Ordinal = r.ReadInt32(), Flags = (BlockFlags)r.ReadInt32(), Offset = r.ReadInt64(), Length = r.ReadInt32() };
        r.ReadInt32();
        e.Hash = r.ReadBytes(16);
        return e;
    }
}

public static class ImageFormat
{
    public static readonly byte[] HeaderMagic = Encoding.ASCII.GetBytes("DIMGHDR1");
    public static readonly byte[] TrailerMagic = Encoding.ASCII.GetBytes("DIMGIDX1");
    public const string Extension = ".dimg";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Hash(byte[] data, int length) => SHA256.HashData(data.AsSpan(0, length))[..16];

    public static ImageHeader ReadHeader(Stream s)
    {
        var magic = new byte[8];
        s.ReadExactly(magic);
        if (!magic.AsSpan().SequenceEqual(HeaderMagic)) throw new InvalidDataException("Not a DiskImager image file.");
        var lenBuf = new byte[4];
        s.ReadExactly(lenBuf);
        int len = BitConverter.ToInt32(lenBuf);
        if (len <= 0 || len > 16 << 20) throw new InvalidDataException("Corrupt image header.");
        var json = new byte[len];
        s.ReadExactly(json);
        return JsonSerializer.Deserialize<ImageHeader>(json, Json) ?? throw new InvalidDataException("Corrupt image header.");
    }

    public static ImageHeader ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadHeader(fs);
    }
}

public sealed class ImageWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly string _tmpPath;
    private readonly List<IndexEntry> _index = new();
    private bool _committed;

    public string FinalPath { get; }
    public ImageHeader Header { get; }
    public long BytesWritten => _fs.Position;

    public ImageWriter(string path, ImageHeader header)
    {
        FinalPath = path;
        Header = header;
        _tmpPath = path + ".partial";
        _fs = new FileStream(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var json = JsonSerializer.SerializeToUtf8Bytes(header, ImageFormat.Json);
        _fs.Write(ImageFormat.HeaderMagic);
        _fs.Write(BitConverter.GetBytes(json.Length));
        _fs.Write(json);
    }

    /// <summary>Block unchanged since the parent image: point at the existing copy.</summary>
    public void AddReference(IndexEntry parentEntry) => _index.Add(parentEntry);

    public void AddBlock(long blockIndex, byte[] hash, BlockFlags flags, byte[]? data, int length)
    {
        _index.Add(new IndexEntry
        {
            BlockIndex = blockIndex,
            Ordinal = Header.ChainOrdinal,
            Flags = flags,
            Offset = _fs.Position,
            Length = length,
            Hash = hash,
        });
        if (length > 0) _fs.Write(data!, 0, length);
    }

    public void Commit()
    {
        long indexOffset = _fs.Position;
        using (var w = new BinaryWriter(_fs, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var e in _index) e.Write(w);
            w.Write(indexOffset);
            w.Write(_index.Count);
            w.Write(ImageFormat.TrailerMagic);
        }
        _fs.Flush(true);
        _fs.Dispose();
        File.Move(_tmpPath, FinalPath, overwrite: true);
        _committed = true;
    }

    public void Dispose()
    {
        if (_committed) return;
        _fs.Dispose();
        try { File.Delete(_tmpPath); } catch { }
    }
}

public sealed class ImageReader : IBlockSource
{
    private readonly IndexEntry[] _index;
    private readonly long[] _blocks;
    private readonly FileStream?[] _streams;
    private byte[] _compBuf = new byte[LiveDiskSource.DefaultBlockSize + 4096];

    public string FilePath { get; }
    public ImageHeader Header { get; }
    public int BlockSize => Header.BlockSize;
    public long DiskSize => Header.DiskSize;
    public int SectorSize => Header.SectorSize;
    public DiskStyle Style => Header.Style;
    public long MaxPartitionEnd => Header.Partitions.Count == 0 ? 0 : Header.Partitions.Max(p => p.Offset + p.Length);
    public IReadOnlyList<long> Blocks => _blocks;
    public IReadOnlyList<IndexEntry> Index => _index;

    public ImageReader(string path)
    {
        FilePath = path;
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            Header = ImageFormat.ReadHeader(fs);
            fs.Seek(-20, SeekOrigin.End);
            using var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            long indexOffset = r.ReadInt64();
            int count = r.ReadInt32();
            if (!r.ReadBytes(8).AsSpan().SequenceEqual(ImageFormat.TrailerMagic))
                throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is incomplete or damaged (no index).");
            fs.Seek(indexOffset, SeekOrigin.Begin);
            using var ir = new BinaryReader(new BufferedStream(fs, 1 << 20), Encoding.UTF8, leaveOpen: true);
            _index = new IndexEntry[count];
            for (int i = 0; i < count; i++) _index[i] = IndexEntry.Read(ir);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
        Array.Sort(_index, (a, b) => a.BlockIndex.CompareTo(b.BlockIndex));
        _blocks = _index.Select(e => e.BlockIndex).ToArray();
        _streams = new FileStream?[Header.ChainOrdinal + 1];
        _streams[Header.ChainOrdinal] = fs;
    }

    public Dictionary<long, IndexEntry> BuildMap() => _index.ToDictionary(e => e.BlockIndex);

    /// <summary>Checks every file this image depends on is present and belongs to the same set.</summary>
    public List<string> MissingChainFiles()
    {
        var missing = new List<string>();
        for (int i = 0; i < Header.ChainOrdinal; i++)
        {
            try { GetStream(i); }
            catch (Exception ex) { missing.Add(ex.Message); }
        }
        return missing;
    }

    private FileStream GetStream(int ordinal)
    {
        var s = _streams[ordinal];
        if (s != null) return s;
        string name = Header.ChainFiles[ordinal];
        string p = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(FilePath)!, name);
        if (!File.Exists(p)) throw new FileNotFoundException($"Missing backup file {name} (needed by this image).");
        var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var h = ImageFormat.ReadHeader(fs);
        if (h.ChainId != Header.ChainId || h.ChainOrdinal != ordinal)
        {
            fs.Dispose();
            throw new InvalidDataException($"{name} does not belong to this backup set.");
        }
        _streams[ordinal] = fs;
        return fs;
    }

    public void ReadBlock(long blockIndex, byte[] buffer, out int length)
    {
        int i = Array.BinarySearch(_blocks, blockIndex);
        if (i < 0) throw new InvalidOperationException($"Block {blockIndex} is not in the image.");
        ReadEntry(_index[i], buffer, out length);
    }

    public void ReadEntry(IndexEntry e, byte[] buffer, out int length)
    {
        length = (int)Math.Min(BlockSize, DiskSize - e.BlockIndex * BlockSize);
        switch (e.Flags)
        {
            case BlockFlags.Zero:
                Array.Clear(buffer, 0, length);
                return;
            case BlockFlags.Stored:
                ReadRaw(e, buffer);
                return;
            default:
                if (_compBuf.Length < e.Length) _compBuf = new byte[e.Length];
                ReadRaw(e, _compBuf);
                using (var ds = new DeflateStream(new MemoryStream(_compBuf, 0, e.Length), CompressionMode.Decompress))
                    ds.ReadExactly(buffer, 0, length);
                return;
        }
    }

    private void ReadRaw(IndexEntry e, byte[] target)
    {
        var s = GetStream(e.Ordinal);
        s.Seek(e.Offset, SeekOrigin.Begin);
        s.ReadExactly(target, 0, e.Length);
    }

    /// <summary>
    /// Decompresses blocks and checks each against its stored hash. With <paramref name="onlyThisFile"/>,
    /// only the blocks stored in this file are checked (for an incremental: what that run added).
    /// </summary>
    public int Verify(IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log, bool onlyThisFile = false)
    {
        var buf = new byte[BlockSize];
        var entries = onlyThisFile ? _index.Where(e => e.Ordinal == Header.ChainOrdinal).ToArray() : _index;
        var tracker = new ProgressTracker(progress, "Verifying image", (long)entries.Length * BlockSize);
        int bad = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = entries[i];
            try
            {
                ReadEntry(e, buf, out int len);
                if (!ImageFormat.Hash(buf, len).AsSpan().SequenceEqual(e.Hash))
                {
                    bad++;
                    log.Error($"Block {e.BlockIndex} failed its checksum.");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                bad++;
                log.Error($"Block {e.BlockIndex}: {ex.Message}");
                if (ex is FileNotFoundException) break;
            }
            tracker.Report((long)(i + 1) * BlockSize);
        }
        return bad;
    }

    public void Dispose()
    {
        foreach (var s in _streams) s?.Dispose();
    }
}
