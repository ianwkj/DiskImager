using System.Diagnostics;
using System.Text;

namespace DiskImager.Core;

/// <summary>
/// Runs a restore or clone with a layout plan. Plans that shrink a partition go through a
/// temporary virtual disk (VHDX): the source is copied there unchanged, Windows' own
/// Resize-Partition shrinks it (moving files as needed), and the result is copied to the target.
/// The original source is never modified.
/// </summary>
public static class DiskCopy
{
    public static void Execute(IBlockSource src, LayoutPlan plan, DiskInfo target, bool newIds, string? stagingFolder,
        string stage, IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log)
    {
        var excluded = (src as ImageReader)?.Header.Exclusions;
        if (!plan.NeedsShrink)
        {
            DiskWriterEngine.Write(src, target, plan, newIds, stage, progress, ct, log, emptyAfter: excluded);
            return;
        }
        if (string.IsNullOrEmpty(stagingFolder))
            throw new InvalidOperationException("Shrinking needs a folder for the temporary working copy.");
        if (src.SectorSize != 512)
            throw new InvalidOperationException("Shrinking is only supported for disks with 512-byte sectors.");

        // The partition table to give the target: the original one (with the source's real disk IDs).
        byte[] table = DiskWriterEngine.ReadTableArea(src, plan);

        Directory.CreateDirectory(stagingFolder);
        string vhd = Path.Combine(stagingFolder, $"DiskImager-working-copy-{DateTime.Now:yyyyMMdd-HHmmss}.vhdx");
        var before = DiskEnumerator.GetDisks().Select(d => d.Number).ToHashSet();
        log.Info($"Creating a temporary working copy at {vhd} so Windows can shrink the partitions.");
        try
        {
            long sizeMb = (src.DiskSize + Layout.MiB - 1) / Layout.MiB;
            Diskpart($"create vdisk file=\"{vhd}\" maximum={sizeMb} type=expandable\r\nattach vdisk\r\n");
            var work = WaitForNewDisk(before, src.DiskSize, ct)
                       ?? throw new InvalidOperationException("The temporary virtual disk didn't appear in Windows.");

            // 1. Copy everything as-is, with new IDs so it doesn't collide with the source.
            DiskWriterEngine.Write(src, work, newIds: true, "Step 1 of 2: making a working copy", progress, ct, log);

            // 2. Let Windows shrink the partitions on the copy.
            foreach (var p in plan.Parts.Where(p => p.Shrinks))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ProgressInfo($"Shrinking {p.Source.Name} (Windows is moving files; this can take a while)", 0, 0, 0));
                if (DiskWriterEngine.WaitForVolume(work.Number, p.Source.Offset, TimeSpan.FromSeconds(60), ct) == null)
                    throw new InvalidOperationException($"{p.Source.Name}: Windows didn't mount the working copy, so it can't be shrunk.");
                Shrink(work.Number, p, log);
            }

            // 3. Copy the shrunk working copy to the target with the final layout.
            work = DiskEnumerator.GetDisks().First(d => d.Number == work.Number);
            var staged = new SourceDescription
            {
                Title = plan.Source.Title,
                Style = plan.Source.Style,
                SectorSize = plan.Source.SectorSize,
                DiskSize = plan.Source.DiskSize,
                RequiredForOriginalLayout = plan.Source.RequiredForOriginalLayout,
                Parts = plan.Parts.Select(p =>
                {
                    var now = work.Partitions.FirstOrDefault(x => x.Offset == p.Source.Offset)
                              ?? throw new InvalidOperationException($"{p.Source.Name} disappeared from the working copy.");
                    if (now.Length > p.TargetLength)
                        throw new InvalidOperationException($"{p.Source.Name} could only be shrunk to {Util.FormatBytes(now.Length)}.");
                    return new SourcePart
                    {
                        Number = p.Source.Number, Offset = p.Source.Offset, Length = now.Length, Name = p.Source.Name,
                        Type = p.Source.Type, FileSystem = p.Source.FileSystem, HasLetter = p.Source.HasLetter, UsedBytes = p.Source.UsedBytes,
                    };
                }).ToList(),
            };
            var finalPlan = new LayoutPlan
            {
                Source = staged,
                TargetSize = plan.TargetSize,
                SectorSize = plan.SectorSize,
                Parts = staged.Parts.Zip(plan.Parts, (s, p) => new PlanPart { Source = s, TargetOffset = p.TargetOffset, TargetLength = p.TargetLength }).ToList(),
            };
            using var workSource = LiveDiskSource.Open(work, useVss: false, log, ct);
            DiskWriterEngine.Write(workSource, target, finalPlan, newIds, "Step 2 of 2: " + stage.ToLowerInvariant(), progress, ct, log,
                tableOverride: table, emptyAfter: excluded);
        }
        finally
        {
            try { Diskpart($"select vdisk file=\"{vhd}\"\r\ndetach vdisk\r\n"); } catch (Exception ex) { log.Warn("Could not detach the working copy: " + ex.Message); }
            try { if (File.Exists(vhd)) File.Delete(vhd); log.Info("Temporary working copy removed."); }
            catch (Exception ex) { log.Warn($"Could not delete {vhd}: {ex.Message}. You can delete it yourself."); }
        }
    }

    private static void Shrink(int disk, PlanPart p, Logger log)
    {
        string script =
            "$ErrorActionPreference = 'Stop'; Update-HostStorageCache; " +
            $"$p = Get-Partition -DiskNumber {disk} | Where-Object {{ $_.Offset -eq {p.Source.Offset} }}; " +
            "if (-not $p) { Write-Output 'NOTFOUND'; exit 4 }; " +
            "$s = $p | Get-PartitionSupportedSize; " +
            $"if ({p.TargetLength} -lt $s.SizeMin) {{ Write-Output ('MIN:' + $s.SizeMin); exit 3 }}; " +
            $"$p | Resize-Partition -Size {p.TargetLength}; Write-Output 'OK'";
        var (code, output) = Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"");
        if (code == 3)
        {
            long min = long.TryParse(output.Split("MIN:").Last().Trim(), out var m) ? m : 0;
            throw new InvalidOperationException(
                $"Windows can only shrink {p.Source.Name} down to {Util.FormatBytes(min)} (some files can't be moved). Choose a larger size or a bigger target drive.");
        }
        if (code != 0) throw new InvalidOperationException($"Windows could not shrink {p.Source.Name}: {output.Trim()}");
        log.Info($"{p.Source.Name}: shrunk to {Util.FormatBytes(p.TargetLength)} on the working copy.");
    }

    private static DiskInfo? WaitForNewDisk(HashSet<int> before, long minSize, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            var d = DiskEnumerator.GetDisks().FirstOrDefault(d => !before.Contains(d.Number) && d.Size >= minSize);
            if (d != null) return d;
            Thread.Sleep(1000);
        }
        return null;
    }

    private static void Diskpart(string script)
    {
        string file = Path.Combine(Path.GetTempPath(), $"diskimager-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, script, Encoding.ASCII);
        try
        {
            var (code, output) = Run("diskpart.exe", $"/s \"{file}\"");
            if (code != 0) throw new InvalidOperationException("diskpart failed: " + output.Trim());
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static (int Code, string Output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        var errTask = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, output + errTask.Result);
    }

    /// <summary>A sensible default place for the working copy: the local drive with the most free space
    /// that isn't on the source or target disk.</summary>
    public static string? SuggestStagingFolder(int targetDisk, int? sourceDisk, long needed)
    {
        var candidates = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.AvailableFreeSpace >= needed)
            .Select(d => (Drive: d, Disk: DiskEnumerator.DiskNumberOfPath(d.Name)))
            .Where(x => x.Disk != targetDisk)
            .OrderBy(x => x.Disk == sourceDisk)
            .ThenByDescending(x => x.Drive.AvailableFreeSpace)
            .ToList();
        return candidates.Count == 0 ? null : Path.Combine(candidates[0].Drive.Name, "DiskImagerTemp");
    }
}
