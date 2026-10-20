using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskImager.Core;

/// <summary>
/// Reads each drive's own health data through Windows' storage drivers (admin rights needed):
///  - NVMe: the SMART / Health Information log page, via the protocol-specific storage query (Windows 10+);
///  - SATA/ATA: SMART READ DATA and READ THRESHOLDS, via SMART_RCV_DRIVE_DATA;
///  - USB: the same SMART commands wrapped in SCSI ATA PASS-THROUGH (SAT), which most USB-to-SATA bridges support.
/// Drives that hide this (virtual disks, many RAID controllers, some USB enclosures) come back as Unknown with a reason.
/// </summary>
public static class SmartReader
{
    private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
    private const uint IOCTL_SCSI_PASS_THROUGH = 0x0004D004;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int ProtocolTypeNvme = 3;
    private const int NVMeDataTypeLogPage = 2;
    private const int NvmeLogHealthInfo = 2;

    /// <summary>Reads every drive this PC can see.</summary>
    public static List<DriveHealth> ReadAll(Logger? log = null) =>
        DiskEnumerator.GetDisks().Select(d => Read(d, log)).ToList();

    public static DriveHealth Read(DiskInfo disk, Logger? log = null)
    {
        var h = new DriveHealth
        {
            DiskNumber = disk.Number,
            Model = disk.Model,
            Serial = disk.Serial,
            Bus = disk.BusType,
            Size = disk.Size,
            IsSystem = disk.IsSystem,
            DriveLetters = string.Join(", ", disk.Partitions.Select(p => p.Volume?.DriveLetter).Where(l => l != null)),
        };
        try
        {
            ReadInto(disk, h);
        }
        catch (Exception ex)
        {
            h.Unavailable = "Couldn't read this drive's health data: " + ex.Message;
            log?.Warn($"Disk health: Disk {disk.Number} ({disk.Model}): {ex.Message}");
        }
        if (h.Unavailable != null)
        {
            h.Status = HealthStatus.Unknown;
            h.LifePercent = null;
        }
        return h;
    }

    private static void ReadInto(DiskInfo disk, DriveHealth h)
    {
        if (disk.BusType is "Virtual" or "iSCSI" or "Fibre" or "SD" or "MMC" or "1394" or "UFS" or "SCM")
        {
            h.Unavailable = disk.BusType == "Virtual"
                ? "This is a virtual disk, so there's no physical drive to report its health."
                : $"{disk.BusType} drives don't report health data to Windows.";
            return;
        }

        using var dev = OpenDevice(disk.Path);
        h.Firmware = ReadFirmware(dev);
        bool? ssd = ReadSeekPenalty(dev) is bool seeks ? !seeks : null;

        if (disk.BusType == "NVMe")
        {
            var log = ReadNvmeHealthLog(dev);
            if (log == null)
            {
                h.Unavailable = "Windows didn't return this NVMe drive's health log (its driver may not support it).";
                return;
            }
            h.ParseNvme(log);
            return;
        }

        // SATA first through the SMART IOCTL, then through SCSI pass-through (USB bridges and some controllers).
        bool usb = disk.BusType == "USB";
        byte[]? values = null, thresholds = null;
        if (!usb)
        {
            values = AtaSmart(dev, 0xD0);
            if (values != null) thresholds = AtaSmart(dev, 0xD1);
        }
        if (values == null)
        {
            values = SatSmart(dev, 0xD0);
            if (values != null) thresholds = SatSmart(dev, 0xD1);
        }
        if (values == null && usb)
        {
            values = AtaSmart(dev, 0xD0);
            if (values != null) thresholds = AtaSmart(dev, 0xD1);
        }
        if (values == null || !values.Skip(2).Take(360).Any(b => b != 0))
        {
            h.Unavailable = disk.BusType switch
            {
                "USB" => "This USB drive or its enclosure doesn't pass health data through to Windows.",
                "RAID" => "This drive is behind a RAID controller, which hides each disk's health data. Use the controller's own tool.",
                "SAS" or "SCSI" => "This drive doesn't report ATA SMART data (SAS/SCSI drives and RAID volumes often don't).",
                _ => "This drive didn't return SMART health data.",
            };
            return;
        }
        h.ParseAta(values, thresholds ?? Array.Empty<byte>(), ssd);
    }

    private static SafeFileHandle OpenDevice(string path)
    {
        // SMART commands need read/write access to the device (nothing is written).
        var h = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        int err = Marshal.GetLastWin32Error();
        h.Dispose();
        h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        throw new IOException(err == 5 ? "access denied (DiskImager needs administrator rights)" : new Win32Exception(err).Message);
    }

    private static string ReadFirmware(SafeFileHandle dev)
    {
        try
        {
            var d = Native.Ioctl(dev, Native.IOCTL_STORAGE_QUERY_PROPERTY, new byte[12], 1024);
            int off = BitConverter.ToInt32(d, 20);
            if (off <= 0 || off >= d.Length) return "";
            int end = Array.IndexOf(d, (byte)0, off);
            return Encoding.ASCII.GetString(d, off, (end < 0 ? d.Length : end) - off).Trim();
        }
        catch { return ""; }
    }

    /// <summary>True for spinning disks, false for SSDs, null if Windows doesn't say.</summary>
    private static bool? ReadSeekPenalty(SafeFileHandle dev)
    {
        try
        {
            var q = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(q, StorageDeviceSeekPenaltyProperty);
            var d = Native.Ioctl(dev, Native.IOCTL_STORAGE_QUERY_PROPERTY, q, 12);
            return d.Length >= 9 ? d[8] != 0 : null;
        }
        catch { return null; }
    }

    /// <summary>The NVMe SMART / Health Information log (512 bytes), or null.</summary>
    public static byte[]? ReadNvmeHealthLog(SafeFileHandle dev)
    {
        // STORAGE_PROPERTY_QUERY (8 bytes before AdditionalParameters) + STORAGE_PROTOCOL_SPECIFIC_DATA (40) + log (512).
        const int specific = 40, header = 8, logSize = 512;
        var buf = new byte[header + specific + logSize];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 0); // PropertyStandardQuery
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], NVMeDataTypeLogPage);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], NvmeLogHealthInfo);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], 0);        // sub-value
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], specific); // data offset, from the start of the protocol data
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], logSize);
        if (!Native.DeviceIoControl(dev, Native.IOCTL_STORAGE_QUERY_PROPERTY, buf, buf.Length, buf, buf.Length, out int got, IntPtr.Zero))
            return null;
        // Output: STORAGE_PROTOCOL_DATA_DESCRIPTOR = Version, Size, then the protocol data (offset/length filled in).
        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(s[(header + 16)..]);
        int dataLength = BinaryPrimitives.ReadInt32LittleEndian(s[(header + 20)..]);
        int start = header + dataOffset;
        if (dataLength < logSize || start + logSize > got) return null;
        return buf.AsSpan(start, logSize).ToArray();
    }

    /// <summary>SMART READ DATA (0xD0) or READ THRESHOLDS (0xD1) through SMART_RCV_DRIVE_DATA, or null.</summary>
    private static byte[]? AtaSmart(SafeFileHandle dev, byte feature)
    {
        // SENDCMDINPARAMS (packed): cBufferSize, IDEREGS (8), bDriveNumber, 3 reserved, 4 DWORDs reserved.
        var input = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(input, 512);
        input[4] = feature;  // features
        input[5] = 1;        // sector count
        input[6] = 1;        // sector number
        input[7] = 0x4F;     // cylinder low  (SMART signature)
        input[8] = 0xC2;     // cylinder high
        input[9] = 0xA0;     // drive/head
        input[10] = 0xB0;    // SMART command
        // SENDCMDOUTPARAMS: cBufferSize, DRIVERSTATUS (12), then the 512-byte sector.
        var output = new byte[16 + 512];
        if (!Native.DeviceIoControl(dev, SMART_RCV_DRIVE_DATA, input, input.Length, output, output.Length, out int got, IntPtr.Zero)
            || got < 16 + 362 || output[4] != 0) return null;
        return output.AsSpan(16, 512).ToArray();
    }

    /// <summary>The same SMART command wrapped in SCSI ATA PASS-THROUGH (12) for USB bridges, or null.</summary>
    private static byte[]? SatSmart(SafeFileHandle dev, byte feature)
    {
        if (IntPtr.Size != 8) return null; // the structure layout below is the 64-bit one
        // SCSI_PASS_THROUGH (x64, 56 bytes: Length, ScsiStatus, PathId, TargetId, Lun, CdbLength, SenseInfoLength, DataIn,
        // DataTransferLength @12, TimeOutValue @16, DataBufferOffset @24, SenseInfoOffset @32, Cdb @36)
        // + 32 bytes of sense data + the 512-byte data buffer.
        const int sptSize = 56, senseOffset = 56, senseSize = 32, dataOffset = 88;
        var buf = new byte[dataOffset + 512];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, sptSize);
        buf[6] = 12;         // CdbLength
        buf[7] = senseSize;  // SenseInfoLength
        buf[8] = 1;          // DataIn = SCSI_IOCTL_DATA_IN
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 512);       // DataTransferLength
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 10);        // TimeOutValue (seconds)
        BinaryPrimitives.WriteUInt64LittleEndian(s[24..], dataOffset); // DataBufferOffset
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], senseOffset); // SenseInfoOffset
        var cdb = s.Slice(36, 16);
        cdb[0] = 0xA1;       // ATA PASS-THROUGH (12)
        cdb[1] = 4 << 1;     // protocol: PIO data-in
        cdb[2] = 0x0E;       // T_DIR from device, BYT_BLOK, T_LENGTH in sector count
        cdb[3] = feature;
        cdb[4] = 1;          // sector count
        cdb[5] = 1;          // LBA low
        cdb[6] = 0x4F;       // LBA mid
        cdb[7] = 0xC2;       // LBA high
        cdb[8] = 0xA0;       // device
        cdb[9] = 0xB0;       // SMART
        if (!Native.DeviceIoControl(dev, IOCTL_SCSI_PASS_THROUGH, buf, buf.Length, buf, buf.Length, out int got, IntPtr.Zero))
            return null;
        byte scsiStatus = buf[2]; // SCSI_PASS_THROUGH.ScsiStatus: 0 = good, 2 = check condition (command refused)
        if (scsiStatus != 0 || got < dataOffset + 362) return null;
        return buf.AsSpan(dataOffset, 512).ToArray();
    }
}
