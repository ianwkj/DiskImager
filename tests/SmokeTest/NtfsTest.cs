using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DiskImager.Core;
using DiskImager.Core.Ntfs;

/// <summary>
/// NTFS reader, file-level restore and change tracking, against real NTFS volumes made by
/// tests\fixtures\make-ntfs-fixture.ps1 (state A, and state B after edits, deletes, renames and a defrag).
/// </summary>
static class NtfsTest
{
    private sealed record ManifestEntry(string Path, long Size, string Sha256, int Attributes, long LastWriteUtcTicks);

    public static void Run(string work, Logger log, Action<bool, string> check)
    {
        string fixtures = FindFixtures();
        if (fixtures == "")
        {
            check(false, "ntfs: test volumes missing (run tests\\fixtures\\make-ntfs-fixture.ps1 as administrator)");
            return;
        }
        string a = Unzip(Path.Combine(fixtures, "ntfs-fixture-a.vhd.gz"), Path.Combine(work, "ntfs-a.vhd"));
        string b = Unzip(Path.Combine(fixtures, "ntfs-fixture-b.vhd.gz"), Path.Combine(work, "ntfs-b.vhd"));

        // A fixed VHD is the raw disk plus a 512-byte footer; the volume is the first partition of a GPT disk.
        (long Offset, long Length) Partition(string vhd)
        {
            using var fs = File.OpenRead(vhd);
            var gpt = new byte[512 * 34];
            fs.ReadExactly(gpt);
            for (int i = 0; i < 128; i++)
            {
                int e = 1024 + i * 128;
                long first = BitConverter.ToInt64(gpt, e + 32), last = BitConverter.ToInt64(gpt, e + 40);
                var type = new Guid(gpt.AsSpan(e, 16));
                if (type == new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")) return (first * 512, (last - first + 1) * 512);
            }
            throw new InvalidDataException("no data partition");
        }

        CheckVolume(a, Partition(a), Path.Combine(fixtures, "ntfs-fixture-a.json"), "A", work, log, check);
        CheckVolume(b, Partition(b), Path.Combine(fixtures, "ntfs-fixture-b.json"), "B", work, log, check);
        CheckTracking(a, b, Partition(a), check);
        CheckThroughImage(a, Partition(a), Path.Combine(fixtures, "ntfs-fixture-a.json"), work, log, check);
    }

    private static string FindFixtures()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string f = Path.Combine(d.FullName, "tests", "fixtures");
            if (File.Exists(Path.Combine(f, "ntfs-fixture-a.vhd.gz"))) return f;
        }
        return "";
    }

    private static string Unzip(string gz, string target)
    {
        using var input = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress);
        using var output = File.Create(target);
        input.CopyTo(output);
        return target;
    }

    private static List<ManifestEntry> Manifest(string json) =>
        JsonSerializer.Deserialize<List<ManifestEntry>>(File.ReadAllText(json), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static void CheckVolume(string vhd, (long Offset, long Length) part, string manifestFile, string label, string work, Logger log, Action<bool, string> check)
    {
        var manifest = Manifest(manifestFile);
        using var reader = new StreamVolumeReader(File.OpenRead(vhd), part.Offset, part.Length);
        var vol = new NtfsVolume(reader);
        check(vol.ClusterSize == 4096 && vol.RecordSize == 1024, $"ntfs {label}: boot sector read (cluster {vol.ClusterSize}, record {vol.RecordSize})");

        var root = vol.ListDirectory(NtfsVolume.RootRecord, "\\");
        check(root.Any(e => e.Name == "Docs" && e.IsDirectory) && !root.Any(e => e.Name.StartsWith("$")),
            $"ntfs {label}: root folder listed without NTFS's own files ({string.Join(", ", root.Select(e => e.Name))})");
        if (label == "A")
        {
            var big = vol.Resolve("\\Big");
            check(big != null && vol.ListDirectory(big.Record, big.Path).Count == 1500, "ntfs A: a 1,500-file folder lists completely (index blocks)");
            var docs = vol.Resolve("\\Docs")!;
            var docList = vol.ListDirectory(docs.Record, docs.Path);
            check(docList.Any(e => e.Name == "Ünïcødé 名前 файл.txt") && !docList.Any(e => e.Name.Contains('~')),
                "ntfs A: Unicode names kept, 8.3 short names hidden");
            var hello = docList.First(e => e.Name == "hello.txt");
            vol.Refresh(hello);
            check(hello.Attributes.HasFlag(FileAttributes.Hidden) && hello.Attributes.HasFlag(FileAttributes.ReadOnly)
                  && hello.ModifiedUtc == new DateTime(2024, 2, 29, 13, 37, 0, DateTimeKind.Utc), "ntfs A: hidden/read-only flags and modified time read");
            var links = vol.Resolve("\\Links")!;
            var junction = vol.ListDirectory(links.Record, links.Path).FirstOrDefault(e => e.Name == "junction-to-docs");
            check(junction != null && junction.IsLink, "ntfs A: junctions recognised as links");
        }

        // Every file's contents, compared with Windows' own SHA-256s.
        int ok = 0;
        var bad = new List<string>();
        foreach (var m in manifest)
        {
            var e = vol.Resolve(m.Path);
            if (e == null) { bad.Add(m.Path + " (not found)"); continue; }
            try
            {
                using var s = vol.OpenFile(e.Record);
                string hash = Convert.ToHexString(SHA256.HashData(s));
                if (s.Length == m.Size && hash == m.Sha256) ok++;
                else bad.Add($"{m.Path} (size {s.Length} vs {m.Size})");
            }
            catch (NotSupportedException ex) when (m.Path.EndsWith("lzx.txt")) { if (ex.Message.Contains("LZX")) ok++; }
            catch (Exception ex) { bad.Add($"{m.Path} ({ex.Message})"); }
        }
        check(bad.Count == 0, $"ntfs {label}: {ok} of {manifest.Count} files read byte-for-byte (resident, fragmented, sparse, LZNT1, XPRESS)" +
                              (bad.Count > 0 ? ": " + string.Join("; ", bad.Take(5)) : ""));

        if (label == "A")
        {
            // File-level restore of a folder tree, then compare.
            string dest = Path.Combine(work, "ntfs-restore");
            var picks = new[] { vol.Resolve("\\Docs")!, vol.Resolve("\\Deep")!, vol.Resolve("\\Compressed")! };
            var result = FileRestore.Copy(vol, picks, dest, new[] { "\\Compressed" }, null, CancellationToken.None, log);
            var report = manifest.First(m => m.Path == "\\Docs\\report.txt");
            string copied = Path.Combine(dest, "Docs", "report.txt");
            check(File.Exists(copied) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copied))) == report.Sha256
                  && File.Exists(Path.Combine(dest, "Deep", "level1", "level2", "level3", "level4", "level5", "level6", "level7", "level8", "level9", "level10", "level11", "level12", "deep.txt")),
                $"file restore: folders copied out with their contents ({result.Files} files)");
            var helloCopy = new FileInfo(Path.Combine(dest, "Docs", "hello.txt"));
            check(helloCopy.Exists && helloCopy.LastWriteTimeUtc == new DateTime(2024, 2, 29, 13, 37, 0, DateTimeKind.Utc)
                  && helloCopy.Attributes.HasFlag(FileAttributes.Hidden), "file restore: dates and hidden flag kept");
            check(!File.Exists(Path.Combine(dest, "Compressed", "lznt1.txt")) && result.Problems.Any(p => p.Contains("excluded")),
                "file restore: files in an excluded folder aren't restored (their data wasn't saved)");
        }
    }

    /// <summary>Every block whose bytes differ between A and B must be reported as changed.</summary>
    private static void CheckTracking(string a, string b, (long Offset, long Length) part, Action<bool, string> check)
    {
        const int bs = 1 << 20;
        ChangeTracker.Scan scanA, scanB;
        using (var ra = new StreamVolumeReader(File.OpenRead(a), part.Offset, part.Length)) scanA = ChangeTracker.ScanVolume(new NtfsVolume(ra), bs, part.Length);
        using var rb = new StreamVolumeReader(File.OpenRead(b), part.Offset, part.Length);
        var volB = new NtfsVolume(rb);
        scanB = ChangeTracker.ScanVolume(volB, bs, part.Length);
        check(scanA.Journal != null && scanA.JournalId == scanB.JournalId && scanB.NextUsn > scanA.NextUsn,
            $"tracking: change journal found (A at {scanA.NextUsn:N0}, B at {scanB.NextUsn:N0})");

        var stateA = new VolumeTrackingState { BlockSize = bs, ClusterSize = 4096, JournalId = scanA.JournalId, NextUsn = scanA.NextUsn, Owners = scanA.Owners };
        var changed = ChangeTracker.ChangedBlocks(scanB, stateA, bs, 4096, out string why);
        check(changed != null, "tracking: changes since A worked out " + why);
        if (changed == null) return;

        // Ground truth: compare the two volumes block by block (only blocks in use in B matter for a backup).
        var bufA = new byte[bs];
        var bufB = new byte[bs];
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        long blocks = (part.Length + bs - 1) / bs;
        int differ = 0, missed = 0;
        var missedList = new List<long>();
        for (long i = 0; i < blocks; i++)
        {
            int n = (int)Math.Min(bs, part.Length - i * bs);
            fa.Position = part.Offset + i * bs; fa.ReadExactly(bufA, 0, n);
            fb.Position = part.Offset + i * bs; fb.ReadExactly(bufB, 0, n);
            if (bufA.AsSpan(0, n).SequenceEqual(bufB.AsSpan(0, n))) continue;
            differ++;
            if (!changed.Contains(i)) { missed++; missedList.Add(i); }
        }
        check(missed == 0, $"tracking: every one of the {differ} changed blocks is caught, including files moved by defrag" +
                           (missed > 0 ? $" (MISSED: {string.Join(", ", missedList.Take(10))})" : ""));
        // Free blocks count as "changed" but are never read anyway; what matters is the blocks files use.
        var owned = Enumerable.Range(0, (int)blocks).Where(i => scanB.Owners[i] != 0x9E3779B97F4A7C15UL).Select(i => (long)i).ToHashSet();
        int ownedChanged = owned.Count(i => changed.Contains(i));
        Console.WriteLine($"    tracking detail: {owned.Count} blocks in use, {ownedChanged} flagged ({scanB.MetadataBlocks.Count} NTFS metadata), {differ} actually differ");
        var same = ChangeTracker.ChangedBlocks(scanA, stateA, bs, 4096, out _)!;
        int sameOwned = Enumerable.Range(0, (int)blocks).Count(i => scanA.Owners[i] != 0x9E3779B97F4A7C15UL && same.Contains(i));
        Console.WriteLine($"    tracking detail: with no changes at all, {sameOwned} in-use blocks are flagged ({scanA.MetadataBlocks.Count} metadata)");
        check(sameOwned <= scanA.MetadataBlocks.Count, $"tracking: with nothing changed, only NTFS's own metadata is re-read ({sameOwned} blocks)");

        var reset = new VolumeTrackingState { BlockSize = bs, ClusterSize = 4096, JournalId = scanA.JournalId + 1, NextUsn = scanA.NextUsn, Owners = scanA.Owners };
        check(ChangeTracker.ChangedBlocks(scanB, reset, bs, 4096, out string w2) == null && w2.Contains("reset"), "tracking: a reset journal means read everything");
    }

    /// <summary>The same volume read back out of a DiskImager backup (via ImageVolumeReader).</summary>
    private static void CheckThroughImage(string vhd, (long Offset, long Length) part, string manifestFile, string work, Logger log, Action<bool, string> check)
    {
        long size = new FileInfo(vhd).Length - 512;
        var disk = new DiskInfo
        {
            Number = 970, Path = vhd, Size = size, SectorSize = 512, Style = DiskStyle.GPT, Model = "NTFS fixture",
            GptDiskId = Guid.NewGuid(),
            Partitions = { new PartitionInfo { Number = 1, Offset = part.Offset, Length = part.Length, TypeName = "Basic Data" } },
        };
        string dir = Path.Combine(work, "ntfs-images");
        var r = BackupEngine.Run(new BackupRequest { Disk = disk, DestinationFolder = dir, JobName = "Ntfs", UseVss = false, EncryptionPassword = "fixture pass" },
            null, CancellationToken.None, log);
        using var image = new ImageReader(r.ImagePath);
        image.Unlock("fixture pass");
        var vols = FileRestore.OpenVolumes(image);
        check(vols.Count == 1, "file restore: the NTFS drive inside an (encrypted) backup is found");
        if (vols.Count == 0) return;
        var vol = vols[0].Volume;
        var manifest = Manifest(manifestFile);
        int ok = 0;
        foreach (var m in manifest.Where(m => !m.Path.EndsWith("lzx.txt")))
        {
            var e = vol.Resolve(m.Path);
            if (e == null) continue;
            using var s = vol.OpenFile(e.Record);
            if (Convert.ToHexString(SHA256.HashData(s)) == m.Sha256) ok++;
        }
        check(ok == manifest.Count(m => !m.Path.EndsWith("lzx.txt")), $"file restore: {ok} files read correctly straight out of the backup image");
    }
}
