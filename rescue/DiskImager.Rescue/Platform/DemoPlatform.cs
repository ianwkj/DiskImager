using DiskImager.Core;

namespace DiskImager.Rescue.Platform;

/// <summary>
/// Development and test stand-in (also used on Windows): backups come from &lt;folder&gt;\backups, and the
/// "disks" are the files in &lt;folder&gt;\disks (written to for real, so restores can be checked byte for byte).
/// </summary>
public sealed class DemoPlatform : IRescuePlatform
{
    private readonly string _folder;

    public DemoPlatform(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(Path.Combine(folder, "backups"));
        Directory.CreateDirectory(Path.Combine(folder, "disks"));
        Directory.CreateDirectory(Path.Combine(folder, "usb"));
    }

    public string Name => "Demo";

    public List<DiskInfo> GetDisks()
    {
        var disks = new List<DiskInfo>
        {
            new() { Number = 0, Path = Path.Combine(_folder, "rescue-stick"), Size = 32L << 30, SectorSize = 512, Model = "SanDisk Ultra USB 3.0", BusType = "USB", Style = DiskStyle.MBR, IsSystem = true,
                    Partitions = { new PartitionInfo { Number = 1, Offset = 1 << 20, Length = (32L << 30) - (1 << 20), TypeName = "Rescue", Volume = new VolumeInfo { FileSystem = "FAT32", Label = "DIRESCUE" } } } },
        };
        int n = 1;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(_folder, "disks"), "*.disk").OrderBy(f => f))
            disks.Add(new DiskInfo
            {
                Number = n++, Path = f, Size = new FileInfo(f).Length, SectorSize = 512,
                Model = Path.GetFileNameWithoutExtension(f).Replace('-', ' '), BusType = "NVMe", Style = DiskStyle.RAW,
            });
        return disks;
    }

    public List<ImageLocation> FindImageLocations(Logger log)
    {
        string b = Path.Combine(_folder, "backups");
        int n = ImageCatalog.Scan(b, depth: 4).Count;
        return n > 0 ? new() { new ImageLocation("BACKUPS · WD Elements (USB)", b, "usb", n) } : new();
    }

    public string ConnectShare(string unc, string? user, string? password, Logger log)
    {
        string share = Path.Combine(_folder, "nas");
        if (!Directory.Exists(share)) throw new IOException("Couldn't connect: the NAS name wasn't found on the network (demo).");
        log.Info($"Connected to {unc} (demo).");
        return share;
    }

    public int? DiskOfPath(string path) => null;

    public void Restore(ImageReader image, DiskInfo target, LayoutPlan plan, bool newIds, IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log)
    {
        if (target.IsSystem) throw new InvalidOperationException("That's the rescue stick itself.");
        RestoreWriter.Write(image, target.Path, target.Size, target.SectorSize, plan, newIds, progress, ct, log);
    }

    public List<DiskHealth> CheckHealth(Logger log) => GetDisks().Select(d => d.IsSystem
        ? new DiskHealth(d, HealthStatus.Unknown, "This drive doesn't report its health (common for USB sticks).", new())
        : new DiskHealth(d, HealthStatus.Good, "No problems reported.", new() { ("Temperature", "38 °C"), ("Powered on", "6,120 hours"), ("Wear", "4% used") })).ToList();

    public string NetworkSummary() => "Wired · 192.168.1.42 (demo)";
    public void OpenNetworkSettings() { }
    public List<(string Name, string Path)> WritableDrives(Logger log) => new() { ("USB · Kingston DataTraveler", Path.Combine(_folder, "usb")) };
    public void Reboot() { }
    public void PowerOff() { }
}
