namespace DiskImager.Core;

/// <summary>One partition of a disk or image being copied, plus how much data it holds.</summary>
public sealed class SourcePart
{
    public int Number { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public bool HasLetter { get; init; }
    public long UsedBytes { get; init; }
    public long End => Offset + Length;

    /// <summary>NTFS and ReFS can be extended in place by Windows.</summary>
    public bool CanGrow => FileSystem is "NTFS" or "ReFS";
    /// <summary>Only NTFS can be shrunk (by Windows, on the staging copy).</summary>
    public bool CanShrink => FileSystem == "NTFS";
}

/// <summary>What is being copied: a live disk (clone) or an image (restore).</summary>
public sealed class SourceDescription
{
    public string Title { get; init; } = "";
    public DiskStyle Style { get; init; }
    public int SectorSize { get; init; }
    public long DiskSize { get; init; }
    public List<SourcePart> Parts { get; init; } = new();
    /// <summary>Space needed to copy with the original layout (includes MBR extended containers).</summary>
    public long RequiredForOriginalLayout { get; init; }
    /// <summary>Why partitions can't be resized/moved for this source, or null if they can.</summary>
    public string? ResizeBlockedReason { get; init; }
    public long UsedBytes => Parts.Sum(p => p.UsedBytes);

    public static SourceDescription FromDisk(DiskInfo d)
    {
        var parts = d.DataPartitions.Select(p =>
        {
            long used = p.Length;
            if (p.Volume != null && Native.GetDiskFreeSpaceExW(p.Volume.GuidPath, out _, out ulong total, out ulong free))
                used = Math.Min(p.Length, (long)(total - free));
            return new SourcePart
            {
                Number = p.Number, Offset = p.Offset, Length = p.Length, Name = p.Describe(), Type = p.TypeName,
                FileSystem = p.Volume?.FileSystem ?? "", HasLetter = p.Volume?.DriveLetter != null, UsedBytes = used,
            };
        }).ToList();
        return new SourceDescription
        {
            Title = d.Title,
            Style = d.Style,
            SectorSize = d.SectorSize,
            DiskSize = d.Size,
            Parts = parts,
            RequiredForOriginalLayout = d.MaxPartitionEnd,
            ResizeBlockedReason = BlockedReason(d.Style, parts, d.Partitions.Any(p => p.IsContainer)),
        };
    }

    public static SourceDescription FromImage(ImageReader r)
    {
        var h = r.Header;
        var blocks = r.Blocks;
        long UsedIn(long start, long end)
        {
            long first = start / h.BlockSize, last = (end - 1) / h.BlockSize;
            int lo = LowerBound(blocks, first), hi = LowerBound(blocks, last + 1);
            return Math.Min(end - start, (long)(hi - lo) * h.BlockSize);
        }
        var parts = h.Partitions.Where(p => !p.IsContainer).OrderBy(p => p.Offset).Select(p => new SourcePart
        {
            Number = p.Number, Offset = p.Offset, Length = p.Length, Type = p.Type, FileSystem = p.FileSystem,
            HasLetter = p.DriveLetter != null,
            Name = string.Join("  ", new[] { p.DriveLetter, p.Label.Length > 0 ? p.Label : p.Type, p.FileSystem }.Where(s => !string.IsNullOrEmpty(s))),
            UsedBytes = UsedIn(p.Offset, p.Offset + p.Length),
        }).ToList();
        return new SourceDescription
        {
            Title = $"{h.DiskModel} ({Util.FormatBytes(h.DiskSize)})",
            Style = h.Style,
            SectorSize = h.SectorSize,
            DiskSize = h.DiskSize,
            Parts = parts,
            RequiredForOriginalLayout = r.MaxPartitionEnd,
            ResizeBlockedReason = BlockedReason(h.Style, parts, h.Partitions.Any(p => p.IsContainer)),
        };
    }

    private static string? BlockedReason(DiskStyle style, List<SourcePart> parts, bool hasExtended) =>
        style == DiskStyle.RAW ? "This disk has no partition table, so it can only be copied as-is."
        : parts.Count == 0 ? "This disk has no partitions to resize."
        // Common on USB sticks: the file system starts at sector 0 and there is no partition table to rewrite.
        : parts.Any(p => p.Offset == 0) ? "This drive has no partition table (its file system starts at the very beginning, as on many USB sticks), so it can only be copied as-is."
        : hasExtended ? "Disks with extended (logical) MBR partitions can only be copied with their original layout."
        : null;

    private static int LowerBound(IReadOnlyList<long> list, long value)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (list[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}

public sealed class PlanPart
{
    public SourcePart Source { get; init; } = null!;
    public long TargetOffset { get; set; }
    public long TargetLength { get; set; }
    public long TargetEnd => TargetOffset + TargetLength;
    public bool Grows => TargetLength > Source.Length;
    public bool Shrinks => TargetLength < Source.Length;

    /// <summary>Smallest size we'll offer: data plus 10% and 512 MB of breathing room.</summary>
    public long MinLength => Source.CanShrink
        ? Math.Min(Source.Length, Layout.AlignUp(Source.UsedBytes + Source.UsedBytes / 10 + (512L << 20), Layout.MiB))
        : Source.Length;
}

/// <summary>Where each source partition goes on the target disk, and how big it becomes.</summary>
public sealed class LayoutPlan
{
    public SourceDescription Source { get; init; } = null!;
    public long TargetSize { get; init; }
    public int SectorSize { get; init; }
    public List<PlanPart> Parts { get; init; } = new();

    public long UsableEnd => Layout.UsableEnd(Source.Style, TargetSize, SectorSize);
    public long End => Parts.Count == 0 ? 0 : Parts.Max(p => p.TargetEnd);
    public bool IsIdentity => Parts.All(p => p.TargetOffset == p.Source.Offset && p.TargetLength == p.Source.Length);
    public bool NeedsShrink => Parts.Any(p => p.Shrinks);
    public bool NeedsExtend => Parts.Any(p => p.Grows);

    /// <summary>Returns a user-readable problem with the plan, or null if it can be carried out.</summary>
    public string? Validate()
    {
        if (IsIdentity)
            return TargetSize >= Source.RequiredForOriginalLayout ? null
                : $"The target drive is too small for the original layout (needs {Util.FormatBytes(Source.RequiredForOriginalLayout)}). Choose \"Resize automatically\" to shrink partitions.";
        if (Source.ResizeBlockedReason != null) return Source.ResizeBlockedReason;
        if (SectorSize != Source.SectorSize) return "The target drive uses a different sector size.";

        foreach (var p in Parts)
        {
            string n = p.Source.Name;
            if (p.Grows && !p.Source.CanGrow) return $"{n}: {DescribeFs(p.Source)} partitions can't be resized.";
            if (p.Shrinks && !p.Source.CanShrink) return $"{n}: {DescribeFs(p.Source)} partitions can't be made smaller.";
            if (p.Shrinks && p.TargetLength < p.MinLength)
                return $"{n} holds {Util.FormatBytes(p.Source.UsedBytes)} of data, so it can't be smaller than {Util.FormatBytes(p.MinLength)}.";
            if (p.TargetLength % SectorSize != 0 || p.TargetOffset % SectorSize != 0) return $"{n}: size isn't a whole number of sectors.";
        }
        for (int i = 1; i < Parts.Count; i++)
            if (Parts[i].TargetOffset < Parts[i - 1].TargetEnd) return "Partitions overlap.";
        if (End > UsableEnd)
            return $"The partitions don't fit: they need {Util.FormatBytes(End - UsableEnd)} more than the target drive has.";
        return null;
    }

    private static string DescribeFs(SourcePart p) => p.FileSystem.Length > 0 ? p.FileSystem : p.Type;
}

public static class Layout
{
    public const long MiB = 1L << 20;

    public static long AlignUp(long v, long a) => (v + a - 1) / a * a;
    public static long AlignDown(long v, long a) => v / a * a;

    /// <summary>First byte past the area partitions may use on a disk of this size.</summary>
    public static long UsableEnd(DiskStyle style, long size, int ss) => style == DiskStyle.GPT
        ? size - ((16384 + ss - 1) / ss + 1) * (long)ss           // backup GPT entries + header
        : Math.Min(size, 0xFFFFFFFFL * ss);                         // MBR's 32-bit sector limit

    /// <summary>Same sizes and positions as the source.</summary>
    public static LayoutPlan Original(SourceDescription src, long targetSize, int targetSectorSize) => new()
    {
        Source = src,
        TargetSize = targetSize,
        SectorSize = targetSectorSize,
        Parts = src.Parts.Select(p => new PlanPart { Source = p, TargetOffset = p.Offset, TargetLength = p.Length }).ToList(),
    };

    /// <summary>
    /// Recomputes offsets after lengths change: the first partition stays put, the gaps between
    /// partitions are kept, and 1 MB alignment is preserved.
    /// </summary>
    public static void Relayout(LayoutPlan plan)
    {
        for (int i = 1; i < plan.Parts.Count; i++)
        {
            var prev = plan.Parts[i - 1];
            var p = plan.Parts[i];
            long gap = Math.Max(0, p.Source.Offset - prev.Source.End);
            long off = prev.TargetEnd + gap;
            if (p.Source.Offset % MiB == 0) off = AlignUp(off, MiB);
            p.TargetOffset = off;
        }
    }

    /// <summary>
    /// Grows or shrinks the largest NTFS/ReFS partition (normally C:) so the layout exactly fills the
    /// target drive. Other partitions keep their size and move as needed.
    /// </summary>
    public static void AutoFit(LayoutPlan plan)
    {
        foreach (var p in plan.Parts) { p.TargetOffset = p.Source.Offset; p.TargetLength = p.Source.Length; }
        if (plan.Source.ResizeBlockedReason != null) return;

        var flex = plan.Parts.Where(p => p.Source.CanGrow)
            .OrderByDescending(p => p.Source.HasLetter).ThenByDescending(p => p.Source.Length).FirstOrDefault();
        if (flex == null) return;

        for (int pass = 0; pass < 3; pass++)
        {
            Relayout(plan);
            long delta = plan.UsableEnd - plan.End;
            if (delta >= 0 && delta < MiB) break;
            long len = AlignDown(flex.TargetLength + delta, MiB);
            if (len < flex.Source.Length && !flex.Source.CanShrink) len = flex.Source.Length;
            flex.TargetLength = Math.Max(MiB, len);
        }
        Relayout(plan);
        if (plan.Parts.All(p => p.TargetLength == p.Source.Length && p.TargetOffset == p.Source.Offset)) return;
        // Don't bother resizing for a few MB.
        if (Math.Abs(flex.TargetLength - flex.Source.Length) < 64 * MiB && plan.End <= plan.UsableEnd && Original(plan.Source, plan.TargetSize, plan.SectorSize).Validate() == null)
        {
            foreach (var p in plan.Parts) { p.TargetOffset = p.Source.Offset; p.TargetLength = p.Source.Length; }
        }
    }
}

/// <summary>Rewrites a copied partition table (GPT or MBR) for a new layout on the target.</summary>
public static class PartitionTable
{
    public sealed class Image
    {
        public byte[] Primary = Array.Empty<byte>();
        public byte[]? Backup;
        public long BackupOffset;
    }

    public static Image Build(byte[] tableArea, LayoutPlan plan, bool newIds)
    {
        var buf = (byte[])tableArea.Clone();
        int ss = plan.SectorSize;
        PlanPart? Match(long startLba) => plan.Parts.FirstOrDefault(p => p.Source.Offset == startLba * ss);

        if (plan.Source.Style == DiskStyle.GPT)
        {
            int h = ss;
            long entriesLba = BitConverter.ToInt64(buf, h + 72);
            int n = BitConverter.ToInt32(buf, h + 80), size = BitConverter.ToInt32(buf, h + 84);
            for (int i = 0; i < n; i++)
            {
                int e = (int)(entriesLba * ss) + i * size;
                if (e + size > buf.Length) break;
                if (buf.AsSpan(e, 16).IndexOfAnyExcept((byte)0) < 0) continue;
                var p = Match(BitConverter.ToInt64(buf, e + 32));
                if (p == null) continue;
                BitConverter.TryWriteBytes(buf.AsSpan(e + 32), p.TargetOffset / ss);
                BitConverter.TryWriteBytes(buf.AsSpan(e + 40), p.TargetEnd / ss - 1);
            }
            var fix = Gpt.ForTarget(buf, buf.Length, ss, plan.TargetSize, newIds);
            return new Image { Primary = buf, Backup = fix.Backup, BackupOffset = fix.BackupOffset };
        }

        if (plan.Source.Style == DiskStyle.MBR)
        {
            for (int i = 0; i < 4; i++)
            {
                int pe = 446 + i * 16;
                if (buf[pe + 4] == 0) continue;
                var p = Match(BitConverter.ToUInt32(buf, pe + 8));
                if (p == null) continue;
                BitConverter.TryWriteBytes(buf.AsSpan(pe + 8), (uint)(p.TargetOffset / ss));
                BitConverter.TryWriteBytes(buf.AsSpan(pe + 12), (uint)(p.TargetLength / ss));
                // CHS values are meaningless on modern disks; use the standard "use LBA" markers.
                buf[pe + 1] = buf[pe + 5] = 0xFE;
                buf[pe + 2] = buf[pe + 3] = buf[pe + 6] = buf[pe + 7] = 0xFF;
            }
            if (newIds) BitConverter.TryWriteBytes(buf.AsSpan(440), (uint)Random.Shared.Next(1, int.MaxValue));
            return new Image { Primary = buf };
        }

        throw new InvalidOperationException("Can't rewrite a disk without a partition table.");
    }
}
