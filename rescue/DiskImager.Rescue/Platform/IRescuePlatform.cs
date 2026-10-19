using DiskImager.Core;

namespace DiskImager.Rescue.Platform;

/// <summary>A drive or share that holds backups.</summary>
public sealed record ImageLocation(string Name, string Path, string Kind, int Images);

public enum HealthStatus { Good, Warning, Failing, Unknown }

public sealed record DiskHealth(DiskInfo Disk, HealthStatus Status, string Summary, List<(string Label, string Value)> Details);

/// <summary>What the rescue app needs from the system it runs on (Linux on the stick; a demo on Windows).</summary>
public interface IRescuePlatform
{
    string Name { get; }
    /// <summary>All disks. The rescue stick itself is marked <see cref="DiskInfo.IsSystem"/>.</summary>
    List<DiskInfo> GetDisks();
    /// <summary>Drives (mounted read-only as needed) that contain DiskImager backups.</summary>
    List<ImageLocation> FindImageLocations(Logger log);
    /// <summary>Connects to \\server\share[\folder] and returns the local path of that folder.</summary>
    string ConnectShare(string unc, string? user, string? password, Logger log);
    /// <summary>The disk a file lives on (to stop a backup being restored onto its own drive), or null.</summary>
    int? DiskOfPath(string path);
    void Restore(ImageReader image, DiskInfo target, LayoutPlan plan, bool newIds, IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log);
    List<DiskHealth> CheckHealth(Logger log);
    /// <summary>e.g. "Wired · 192.168.1.42", "Wi-Fi · 10.0.0.7", "Not connected".</summary>
    string NetworkSummary();
    void OpenNetworkSettings();
    /// <summary>Drives files can be copied to (mounted writable): name and folder.</summary>
    List<(string Name, string Path)> WritableDrives(Logger log);
    void Reboot();
    void PowerOff();
}
