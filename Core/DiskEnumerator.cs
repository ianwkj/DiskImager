using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskImager.Core;

public static class DiskEnumerator
{
    private static readonly Dictionary<Guid, string> GptTypes = new()
    {
        [new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b")] = "EFI System",
        [new Guid("e3c9e316-0b5c-4db8-817d-f92df00215ae")] = "Microsoft Reserved",
        [new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")] = "Basic Data",
        [new Guid("de94bba4-06d1-4d40-a16a-bfd50179d6ac")] = "Recovery",
        [new Guid("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3")] = "LDM Metadata",
        [new Guid("af9b60a0-1431-4f62-bc68-3311714a69ad")] = "LDM Data",
        [new Guid("0fc63daf-8483-4772-8e79-3d69d8477de4")] = "Linux",
    };

    private static readonly Dictionary<int, string> BusTypes = new()
    {
        [1] = "SCSI", [2] = "ATAPI", [3] = "ATA", [4] = "1394", [6] = "Fibre", [7] = "USB", [8] = "RAID",
        [9] = "iSCSI", [10] = "SAS", [11] = "SATA", [12] = "SD", [13] = "MMC", [14] = "Virtual",
        [15] = "Virtual", [17] = "NVMe", [18] = "SCM", [19] = "UFS",
    };

    public static List<DiskInfo> GetDisks()
    {
        var volumes = GetVolumes();
        string sysLetter = System.IO.Path.GetPathRoot(Environment.SystemDirectory)!.Substring(0, 2).ToUpperInvariant();
        var disks = new List<DiskInfo>();

        for (int i = 0; i < 64; i++)
        {
            // Query-only access is enough for the layout/geometry IOCTLs used here.
            using var h = Native.CreateFileW($@"\\.\PhysicalDrive{i}", 0,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            try
            {
                var disk = ReadDisk(i, h, volumes);
                disk.IsSystem = disk.Partitions.Any(p =>
                    string.Equals(p.Volume?.DriveLetter, sysLetter, StringComparison.OrdinalIgnoreCase));
                disks.Add(disk);
            }
            catch
            {
                // Card readers without media, etc.
            }
        }
        return disks;
    }

    public static DiskInfo? FindDisk(string id) => GetDisks().FirstOrDefault(d => d.Id == id);

    /// <summary>Disk number holding a local path such as E:\Backups, or null for network paths.</summary>
    public static int? DiskNumberOfPath(string path)
    {
        if (path.Length < 2 || path[1] != ':') return null;
        string letter = path.Substring(0, 2).ToUpperInvariant();
        return GetVolumes().FirstOrDefault(v => v.DriveLetter == letter)?.DiskNumber;
    }

    private static DiskInfo ReadDisk(int number, SafeFileHandle h, List<VolumeInfo> volumes)
    {
        var geo = Native.Ioctl(h, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 256);
        int sectorSize = BitConverter.ToInt32(geo, 20);
        long size = BitConverter.ToInt64(geo, 24);

        string model = "Disk", serial = "", bus = "";
        try
        {
            var query = new byte[12]; // StorageDeviceProperty, PropertyStandardQuery
            var d = Native.Ioctl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, query, 1024);
            string vendor = AnsiAt(d, BitConverter.ToInt32(d, 12));
            string product = AnsiAt(d, BitConverter.ToInt32(d, 16));
            serial = AnsiAt(d, BitConverter.ToInt32(d, 24));
            model = $"{vendor} {product}".Trim();
            if (model.Length == 0) model = "Disk";
            bus = BusTypes.TryGetValue(BitConverter.ToInt32(d, 28), out var b) ? b : "";
        }
        catch { }

        var layout = Native.IoctlGrow(h, Native.IOCTL_DISK_GET_DRIVE_LAYOUT_EX, 4096);
        var style = (DiskStyle)BitConverter.ToInt32(layout, 0);
        int count = BitConverter.ToInt32(layout, 4);
        uint sig = style == DiskStyle.MBR ? BitConverter.ToUInt32(layout, 8) : 0;
        Guid diskId = style == DiskStyle.GPT ? new Guid(layout.AsSpan(8, 16)) : Guid.Empty;

        var parts = new List<PartitionInfo>();
        for (int i = 0; i < count; i++)
        {
            int o = 48 + i * 144;
            if (o + 144 > layout.Length) break;
            long start = BitConverter.ToInt64(layout, o + 8);
            long len = BitConverter.ToInt64(layout, o + 16);
            int num = BitConverter.ToInt32(layout, o + 24);
            if (len <= 0) continue;

            string typeName;
            bool container = false;
            if (style == DiskStyle.MBR)
            {
                byte type = layout[o + 32];
                if (type == 0) continue;
                container = type is 0x05 or 0x0F or 0x85;
                typeName = type switch
                {
                    0x07 => "NTFS/exFAT", 0x0B or 0x0C => "FAT32", 0x27 => "Recovery", 0x83 => "Linux",
                    0x05 or 0x0F or 0x85 => "Extended", 0xEE => "GPT Protective", _ => $"Type 0x{type:X2}",
                };
            }
            else if (style == DiskStyle.GPT)
            {
                var type = new Guid(layout.AsSpan(o + 32, 16));
                if (type == Guid.Empty) continue;
                string name = Encoding.Unicode.GetString(layout, o + 72, 72).TrimEnd('\0');
                typeName = GptTypes.TryGetValue(type, out var t) ? t : (name.Length > 0 ? name : "Unknown");
            }
            else typeName = "Raw";

            parts.Add(new PartitionInfo
            {
                Number = num,
                Offset = start,
                Length = len,
                IsContainer = container,
                TypeName = typeName,
                Volume = container ? null : volumes.FirstOrDefault(v => v.DiskNumber == number && v.Offset == start && !v.Spanned),
            });
        }

        // Screenshots for documentation: made-up serial numbers instead of the real ones.
        if (Environment.GetEnvironmentVariable("DISKIMAGER_SCREENSHOT_MODE") == "1" && serial.Length > 0)
            serial = $"S{6 + number}XN{(size >> 20) % 9000 + 1000}T0{number}A";

        return new DiskInfo
        {
            Number = number,
            Size = size,
            SectorSize = sectorSize,
            Model = model,
            Serial = serial,
            BusType = bus,
            Style = style,
            MbrSignature = sig,
            GptDiskId = diskId,
            Partitions = parts.OrderBy(p => p.Offset).ToList(),
        };
    }

    private static string AnsiAt(byte[] buf, int offset)
    {
        if (offset <= 0 || offset >= buf.Length) return "";
        int end = Array.IndexOf(buf, (byte)0, offset);
        if (end < 0) end = buf.Length;
        return Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }

    public static List<VolumeInfo> GetVolumes()
    {
        var list = new List<VolumeInfo>();
        var buf = new char[512];
        IntPtr fh = Native.FindFirstVolumeW(buf, buf.Length);
        if (fh == new IntPtr(-1)) return list;
        try
        {
            do
            {
                string name = new string(buf, 0, Array.IndexOf(buf, '\0'));
                try
                {
                    var v = ReadVolume(name);
                    if (v != null) list.Add(v);
                }
                catch { }
            } while (Native.FindNextVolumeW(fh, buf, buf.Length));
        }
        finally
        {
            Native.FindVolumeClose(fh);
        }
        return list;
    }

    private static VolumeInfo? ReadVolume(string guidPath)
    {
        using var h = Native.CreateFileW(guidPath.TrimEnd('\\'), 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        byte[] ext;
        try { ext = Native.Ioctl(h, Native.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 8 + 24 * 8); }
        catch { return null; }
        int n = BitConverter.ToInt32(ext, 0);
        if (n < 1) return null;

        var names = new char[2048];
        string? letter = null;
        if (Native.GetVolumePathNamesForVolumeNameW(guidPath, names, names.Length, out _))
        {
            foreach (var mp in new string(names).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (mp.Length == 3 && mp[1] == ':') { letter = mp.Substring(0, 2).ToUpperInvariant(); break; }
        }

        string fs = "RAW", label = "";
        var lbl = new char[261];
        var fsn = new char[261];
        if (Native.GetVolumeInformationW(guidPath, lbl, lbl.Length, out _, out _, out _, fsn, fsn.Length))
        {
            fs = new string(fsn, 0, Array.IndexOf(fsn, '\0'));
            label = new string(lbl, 0, Array.IndexOf(lbl, '\0'));
        }

        return new VolumeInfo
        {
            GuidPath = guidPath,
            DiskNumber = BitConverter.ToInt32(ext, 8),
            Offset = BitConverter.ToInt64(ext, 16),
            Length = BitConverter.ToInt64(ext, 24),
            Spanned = n > 1,
            DriveLetter = letter,
            FileSystem = fs,
            Label = label,
        };
    }
}
