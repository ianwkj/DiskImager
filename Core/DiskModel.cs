namespace DiskImager.Core;

public enum DiskStyle { MBR = 0, GPT = 1, RAW = 2 }

public sealed class VolumeInfo
{
    /// <summary>\\?\Volume{guid}\ (with trailing backslash).</summary>
    public string GuidPath { get; init; } = "";
    public int DiskNumber { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public bool Spanned { get; init; }
    /// <summary>e.g. "C:" or null when the volume has no drive letter.</summary>
    public string? DriveLetter { get; init; }
    public string FileSystem { get; init; } = "RAW";
    public string Label { get; init; } = "";
    public string DevicePath => GuidPath.TrimEnd('\\');
}

public sealed class PartitionInfo
{
    public int Number { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public long End => Offset + Length;
    public bool IsContainer { get; init; }
    public string TypeName { get; init; } = "";
    public VolumeInfo? Volume { get; set; }

    public string Describe()
    {
        var parts = new List<string>();
        if (Volume?.DriveLetter != null) parts.Add(Volume.DriveLetter);
        if (!string.IsNullOrEmpty(Volume?.Label)) parts.Add(Volume!.Label);
        else parts.Add(TypeName);
        if (Volume != null && Volume.FileSystem != "RAW") parts.Add(Volume.FileSystem);
        return string.Join("  ", parts);
    }
}

public sealed class DiskInfo
{
    private readonly string? _path;

    public int Number { get; init; }
    /// <summary>Device path; can be set to a file for testing against a disk image file.</summary>
    public string Path { get => _path ?? $@"\\.\PhysicalDrive{Number}"; init => _path = value; }
    public long Size { get; init; }
    public int SectorSize { get; init; }
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string BusType { get; init; } = "";
    public DiskStyle Style { get; init; }
    public uint MbrSignature { get; init; }
    public Guid GptDiskId { get; init; }
    public List<PartitionInfo> Partitions { get; init; } = new();
    public bool IsSystem { get; set; }

    /// <summary>Stable identity used by jobs: survives disk renumbering after reboots.</summary>
    public string Id => Style switch
    {
        DiskStyle.GPT => "GPT:" + GptDiskId.ToString("D").ToUpperInvariant(),
        DiskStyle.MBR => $"MBR:{MbrSignature:X8}",
        _ => $"RAW:{Serial}:{Size}",
    };

    public long MaxPartitionEnd => Partitions.Count == 0 ? 0 : Partitions.Max(p => p.End);

    public IEnumerable<PartitionInfo> DataPartitions => Partitions.Where(p => !p.IsContainer).OrderBy(p => p.Offset);

    public string Title => $"Disk {Number} – {Model} ({Util.FormatBytes(Size)})";

    public string Summary
    {
        get
        {
            var letters = Partitions.Select(p => p.Volume?.DriveLetter).Where(l => l != null).ToList();
            return letters.Count == 0 ? Title : $"{Title}  [{string.Join(", ", letters)}]";
        }
    }

    public override string ToString() => Summary;
}
