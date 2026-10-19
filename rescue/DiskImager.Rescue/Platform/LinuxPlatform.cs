using System.Net.NetworkInformation;
using System.Text.Json;
using DiskImager.Core;

namespace DiskImager.Rescue.Platform;

/// <summary>
/// The rescue stick's Linux system (runs as root): disks from lsblk, drives mounted read-only under
/// /mnt/diskimager, NAS shares through mount.cifs, file systems grown with ntfsresize, health from smartctl.
/// </summary>
public sealed class LinuxPlatform : IRescuePlatform
{
    private const string MountRoot = "/mnt/diskimager";
    private readonly Dictionary<string, string> _mounted = new(); // device -> mount point we created

    public string Name => "Linux";

    // ------------------------------------------------------------------ disks

    private sealed record Part(string Dev, int Number, long Offset, long Length, string FsType, string Label, string? MountPoint, string TypeName);
    private sealed record Disk(string Dev, long Size, int SectorSize, string Model, string Serial, string Tran, bool Removable, string PtType, string PtUuid, List<Part> Parts);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private static long Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.TryParse(v.GetString(), out long n) ? n : 0;
    }

    private static long Sys(string dev, string file)
    {
        try { return long.Parse(File.ReadAllText($"/sys/class/block/{Path.GetFileName(dev)}/{file}").Trim()); }
        catch { return 0; }
    }

    private List<Disk> ListDisks()
    {
        var (code, json) = Shell.Run("lsblk", "-J", "-b", "-p", "-o", "NAME,TYPE,SIZE,MODEL,SERIAL,TRAN,RM,LOG-SEC,PTTYPE,PTUUID,FSTYPE,LABEL,MOUNTPOINT,PARTTYPENAME");
        if (code != 0) throw new InvalidOperationException("Couldn't list the disks: " + json);
        var disks = new List<Disk>();
        using var doc = JsonDocument.Parse(json);
        foreach (var d in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            string name = Str(d, "name");
            if (Str(d, "type") != "disk" || name.StartsWith("/dev/loop") || name.StartsWith("/dev/zram") || name.StartsWith("/dev/sr")) continue;
            var parts = new List<Part>();
            if (d.TryGetProperty("children", out var kids))
                foreach (var c in kids.EnumerateArray().Where(c => Str(c, "type") == "part"))
                {
                    string dev = Str(c, "name");
                    parts.Add(new Part(dev, (int)Sys(dev, "partition"), Sys(dev, "start") * 512, Num(c, "size"),
                        Str(c, "fstype"), Str(c, "label"), Str(c, "mountpoint") is { Length: > 0 } m ? m : null, Str(c, "parttypename")));
                }
            int ss = (int)Num(d, "log-sec");
            disks.Add(new Disk(name, Num(d, "size"), ss > 0 ? ss : 512, Str(d, "model").Trim(), Str(d, "serial").Trim(), Str(d, "tran"),
                Str(d, "rm") is "1" or "true" || Num(d, "rm") == 1, Str(d, "pttype"), Str(d, "ptuuid"), parts.OrderBy(p => p.Offset).ToList()));
        }
        return disks;
    }

    private static string FsName(string fs) => fs switch
    {
        "ntfs" or "ntfs3" => "NTFS",
        "vfat" => "FAT32",
        "exfat" => "exFAT",
        "" => "RAW",
        _ => fs,
    };

    /// <summary>The device the live system booted from (the rescue stick).</summary>
    private static string? BootDevice()
    {
        var (code, src) = Shell.Run("findmnt", "-n", "-o", "SOURCE", "/run/live/medium");
        if (code != 0 || src.Length == 0) return null;
        var (c2, parent) = Shell.Run("lsblk", "-n", "-p", "-o", "PKNAME", src.Trim());
        return c2 == 0 && parent.Trim().Length > 0 ? parent.Trim() : src.Trim();
    }

    public List<DiskInfo> GetDisks()
    {
        string? boot = BootDevice();
        return ListDisks().Select((d, i) => new DiskInfo
        {
            Number = i,
            Path = d.Dev,
            Size = d.Size,
            SectorSize = d.SectorSize,
            Model = d.Model.Length > 0 ? d.Model : Path.GetFileName(d.Dev),
            Serial = d.Serial,
            BusType = d.Tran.ToUpperInvariant() switch { "USB" => "USB", "NVME" => "NVMe", "SATA" => "SATA", "MMC" => "SD", var t => t },
            Style = d.PtType switch { "gpt" => DiskStyle.GPT, "dos" => DiskStyle.MBR, _ => DiskStyle.RAW },
            GptDiskId = Guid.TryParse(d.PtUuid, out var g) ? g : Guid.Empty,
            IsSystem = d.Dev == boot,
            Partitions = d.Parts.Select(p => new PartitionInfo
            {
                Number = p.Number,
                Offset = p.Offset,
                Length = p.Length,
                TypeName = p.TypeName.Length > 0 ? p.TypeName : "Partition",
                Volume = new VolumeInfo { GuidPath = p.Dev, FileSystem = FsName(p.FsType), Label = p.Label, DiskNumber = i, Offset = p.Offset, Length = p.Length },
            }).ToList(),
        }).ToList();
    }

    // ------------------------------------------------------------------ mounting

    private string MountReadOnly(Part p)
    {
        if (p.MountPoint != null) return p.MountPoint;
        if (_mounted.TryGetValue(p.Dev, out var known)) return known;
        string mp = Path.Combine(MountRoot, Path.GetFileName(p.Dev));
        Directory.CreateDirectory(mp);
        var tries = p.FsType == "ntfs"
            ? new[] { new[] { "-t", "ntfs3", "-o", "ro", p.Dev, mp }, new[] { "-t", "ntfs-3g", "-o", "ro", p.Dev, mp } }
            : new[] { new[] { "-o", "ro", p.Dev, mp } };
        foreach (var args in tries)
            if (Shell.Run("mount", args, null, 60).Code == 0)
            {
                _mounted[p.Dev] = mp;
                return mp;
            }
        throw new IOException($"Couldn't open {p.Dev}.");
    }

    public List<ImageLocation> FindImageLocations(Logger log)
    {
        var result = new List<ImageLocation>();
        string? boot = BootDevice();
        foreach (var d in ListDisks().Where(d => d.Dev != boot))
            foreach (var p in d.Parts.Where(p => p.FsType is "ntfs" or "vfat" or "exfat" or "ext4" or "ext3" or "btrfs" or "xfs"))
            {
                try
                {
                    string mp = MountReadOnly(p);
                    int n = ImageCatalog.Scan(mp, depth: 4).Count;
                    if (n > 0)
                        result.Add(new ImageLocation($"{(p.Label.Length > 0 ? p.Label : Path.GetFileName(p.Dev))} · {(d.Model.Length > 0 ? d.Model : d.Dev)}", mp, d.Tran == "usb" ? "usb" : "disk", n));
                }
                catch (Exception ex) { log.Info($"{p.Dev}: {ex.Message}"); }
            }
        return result;
    }

    public string ConnectShare(string unc, string? user, string? password, Logger log)
    {
        var parts = unc.Replace('/', '\\').TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("Enter the share like \\\\NAS\\Backups.");
        string server = ResolveHost(parts[0]);
        string mp = Path.Combine(MountRoot, $"share-{parts[0]}-{parts[1]}".Replace(' ', '_'));
        Shell.Run("umount", mp);
        Directory.CreateDirectory(mp);

        string cred = $"/run/diskimager-cred-{Guid.NewGuid():N}";
        string domain = "";
        string name = user ?? "";
        if (name.Contains('\\')) { domain = name[..name.IndexOf('\\')]; name = name[(name.IndexOf('\\') + 1)..]; }
        File.WriteAllText(cred, $"username={name}\npassword={password ?? ""}\n" + (domain.Length > 0 ? $"domain={domain}\n" : ""));
        try
        {
            File.SetUnixFileMode(cred, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string last = "";
            foreach (var vers in new[] { "", "vers=3.0", "vers=2.1", "vers=1.0" })
            {
                string opts = "ro,iocharset=utf8,credentials=" + cred + (string.IsNullOrEmpty(user) ? ",guest" : "") + (vers.Length > 0 ? "," + vers : "");
                var (code, output) = Shell.Run("mount", new[] { "-t", "cifs", $"//{server}/{parts[1]}", mp, "-o", opts }, null, 60);
                if (code == 0)
                {
                    log.Info($"Connected to \\\\{parts[0]}\\{parts[1]}{(user is { Length: > 0 } ? " as " + user : "")}.");
                    return Path.Combine(new[] { mp }.Concat(parts.Skip(2)).ToArray());
                }
                last = output;
            }
            throw new IOException($"Couldn't connect to \\\\{parts[0]}\\{parts[1]}: {Friendly(last)}");
        }
        finally { try { File.Delete(cred); } catch { } }
    }

    private static string Friendly(string mountError) =>
        mountError.Contains("Permission denied") ? "the user name or password was refused." :
        mountError.Contains("could not resolve") || mountError.Contains("Unable to find") ? "the NAS name wasn't found on the network. Try its IP address, e.g. \\\\192.168.1.10\\Backups." :
        mountError.Contains("Host is down") || mountError.Contains("No route") ? "the NAS didn't answer. Check the network cable or Wi-Fi." :
        mountError.Trim();

    /// <summary>NAS names often only resolve the Windows way (NetBIOS), which Linux DNS lookups miss.</summary>
    private static string ResolveHost(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out _)) return host;
        try { if (System.Net.Dns.GetHostAddresses(host).Length > 0) return host; } catch { }
        var (code, output) = Shell.Run("nmblookup", host);
        var ip = output.Split('\n').Select(l => l.Trim().Split(' ')[0]).FirstOrDefault(s => System.Net.IPAddress.TryParse(s, out _));
        return code == 0 && ip != null ? ip : host;
    }

    public int? DiskOfPath(string path)
    {
        var (code, src) = Shell.Run("findmnt", "-n", "-o", "SOURCE", "-T", path);
        if (code != 0 || !src.StartsWith("/dev/")) return null;
        var (c2, parent) = Shell.Run("lsblk", "-n", "-p", "-o", "PKNAME", src.Trim());
        string disk = c2 == 0 && parent.Trim().Length > 0 ? parent.Trim() : src.Trim();
        return GetDisks().FirstOrDefault(d => d.Path == disk)?.Number;
    }

    // ------------------------------------------------------------------ restore

    public void Restore(ImageReader image, DiskInfo target, LayoutPlan plan, bool newIds, IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log)
    {
        if (target.IsSystem) throw new InvalidOperationException("That's the rescue stick itself.");
        // Nothing on the target may stay mounted while it's overwritten.
        foreach (var p in ListDisks().First(d => d.Dev == target.Path).Parts)
        {
            Shell.Run("umount", p.Dev);
            _mounted.Remove(p.Dev);
        }
        RestoreWriter.Write(image, target.Path, target.Size, target.SectorSize, plan, newIds, progress, ct, log);

        progress?.Report(new ProgressInfo("Finishing up", 0, 0, 0));
        Shell.Run("blockdev", "--rereadpt", target.Path);
        Shell.Run("partprobe", target.Path);
        Shell.Run("udevadm", "settle");

        Part? PartAt(long offset) => ListDisks().FirstOrDefault(d => d.Dev == target.Path)?.Parts.FirstOrDefault(p => p.Offset == offset);

        foreach (var p in plan.Parts.Where(p => p.Grows && p.Source.FileSystem == "NTFS"))
        {
            progress?.Report(new ProgressInfo($"Enlarging {p.Source.Name}", 0, 0, 0));
            var part = PartAt(p.TargetOffset);
            if (part == null) { log.Warn($"{p.Source.Name}: the enlarged partition wasn't found; Windows can extend it later (Disk Management → Extend Volume)."); continue; }
            var (code, output) = Shell.Run("ntfsresize", new[] { "--force", "--force", "--no-progress-bar", part.Dev }, "y\n", 1800);
            if (code == 0) log.Info($"{p.Source.Name}: file system enlarged to fill {Util.FormatBytes(p.TargetLength)}. Windows checks the drive once at its next start.");
            else log.Warn($"{p.Source.Name}: couldn't enlarge the file system ({output.Split('\n').LastOrDefault()}). Windows can extend it later (Disk Management → Extend Volume).");
        }

        foreach (var group in image.Header.Exclusions.GroupBy(x => x.PartitionOffset))
        {
            var planPart = plan.Parts.FirstOrDefault(p => p.Source.Offset == group.Key);
            var part = planPart == null ? null : PartAt(planPart.TargetOffset);
            if (part == null) continue;
            string mp = Path.Combine(MountRoot, "restored-" + Path.GetFileName(part.Dev));
            Directory.CreateDirectory(mp);
            if (Shell.Run("mount", "-t", "ntfs-3g", part.Dev, mp).Code != 0)
            {
                log.Warn($"Couldn't open the restored {planPart!.Source.Name} to empty the excluded folders ({string.Join(", ", group.Select(x => x.Folder))}); delete their contents in Windows.");
                continue;
            }
            try
            {
                int removed = group.Sum(x => Exclusions.EmptyFolder(mp + x.Folder.Replace('\\', '/'), log));
                log.Info($"Excluded folders emptied on the restored disk ({removed} files that weren't backed up were removed).");
            }
            finally { Shell.Run("umount", mp); }
        }
        Shell.Run("sync");
        log.Info($"Restore onto {target.Path} finished.");
    }

    // ------------------------------------------------------------------ health, network, power

    public List<DiskHealth> CheckHealth(Logger log)
    {
        var list = new List<DiskHealth>();
        foreach (var d in GetDisks())
        {
            var (_, json) = Shell.Run("smartctl", "-j", "-H", "-A", "-i", d.Path);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                var details = new List<(string, string)>();
                bool? passed = r.TryGetProperty("smart_status", out var s) && s.TryGetProperty("passed", out var ps) ? ps.GetBoolean() : null;
                if (r.TryGetProperty("temperature", out var t) && t.TryGetProperty("current", out var tc)) details.Add(("Temperature", $"{tc.GetInt32()} °C"));
                if (r.TryGetProperty("power_on_time", out var pt) && pt.TryGetProperty("hours", out var ph)) details.Add(("Powered on", $"{ph.GetInt64():N0} hours"));
                int worn = r.TryGetProperty("nvme_smart_health_information_log", out var nv) && nv.TryGetProperty("percentage_used", out var pu) ? pu.GetInt32() : -1;
                if (worn >= 0) details.Add(("Wear", $"{worn}% used"));
                long realloc = 0, pending = 0;
                if (r.TryGetProperty("ata_smart_attributes", out var at) && at.TryGetProperty("table", out var tbl))
                    foreach (var a in tbl.EnumerateArray())
                    {
                        int id = a.GetProperty("id").GetInt32();
                        long raw = a.TryGetProperty("raw", out var rw) && rw.TryGetProperty("value", out var rv) ? rv.GetInt64() : 0;
                        if (id == 5) realloc = raw;
                        if (id == 197) pending = raw;
                    }
                if (realloc > 0) details.Add(("Reallocated sectors", realloc.ToString("N0")));
                if (pending > 0) details.Add(("Unreadable sectors waiting", pending.ToString("N0")));
                var status = passed == false ? HealthStatus.Failing
                    : realloc > 0 || pending > 0 || worn >= 90 ? HealthStatus.Warning
                    : passed == true ? HealthStatus.Good : HealthStatus.Unknown;
                string summary = status switch
                {
                    HealthStatus.Failing => "The drive reports it is failing. Copy anything important off it now and replace it.",
                    HealthStatus.Warning => "The drive is wearing out or has damaged areas. Plan to replace it.",
                    HealthStatus.Good => "No problems reported.",
                    _ => "This drive doesn't report its health (common for USB sticks and some USB adapters).",
                };
                list.Add(new DiskHealth(d, status, summary, details));
            }
            catch { list.Add(new DiskHealth(d, HealthStatus.Unknown, "This drive doesn't report its health.", new())); }
        }
        return list;
    }

    public string NetworkSummary()
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var ip = n.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address;
            if (ip == null || n.Name.StartsWith("tailscale")) continue;
            bool wifi = Directory.Exists($"/sys/class/net/{n.Name}/wireless");
            return $"{(wifi ? "Wi-Fi" : "Wired")} · {ip}";
        }
        return "Not connected";
    }

    public void OpenNetworkSettings()
    {
        if (File.Exists("/usr/bin/nm-connection-editor")) Shell.Start("nm-connection-editor");
        else Shell.Start("xfce4-terminal", "-e", "nmtui");
    }

    public List<(string Name, string Path)> WritableDrives(Logger log)
    {
        var result = new List<(string, string)>();
        string? boot = BootDevice();
        foreach (var d in ListDisks().Where(d => d.Dev != boot && (d.Removable || d.Tran == "usb")))
            foreach (var p in d.Parts.Where(p => p.FsType is "ntfs" or "vfat" or "exfat" or "ext4"))
            {
                string mp = Path.Combine(MountRoot, "rw-" + Path.GetFileName(p.Dev));
                if (_mounted.TryGetValue(p.Dev, out var ro) && ro != mp) { Shell.Run("umount", ro); _mounted.Remove(p.Dev); }
                Directory.CreateDirectory(mp);
                var args = p.FsType == "ntfs" ? new[] { "-t", "ntfs-3g", p.Dev, mp } : new[] { p.Dev, mp };
                if (_mounted.ContainsKey(p.Dev) || Shell.Run("mount", args, null, 60).Code == 0)
                {
                    _mounted[p.Dev] = mp;
                    result.Add(($"{(p.Label.Length > 0 ? p.Label : Path.GetFileName(p.Dev))} · {(d.Model.Length > 0 ? d.Model : d.Dev)}", mp));
                }
            }
        return result;
    }

    public void Reboot() => Shell.Start("systemctl", "reboot");
    public void PowerOff() => Shell.Start("systemctl", "poweroff");
}
