using System.Buffers.Binary;

namespace DiskImager.Core;

public enum HealthStatus { Unknown, Good, Caution, Bad }

public enum DriveKind { Unknown, Nvme, Ssd, Hdd }

/// <summary>One SATA/ATA SMART attribute, as shown in the readings table.</summary>
public sealed record SmartAttribute(byte Id, string Name, byte Current, byte Worst, byte Threshold, ulong Raw, HealthStatus Status)
{
    public string RawHex => Raw.ToString("X12");
}

/// <summary>One NVMe health reading (name and formatted value).</summary>
public sealed record HealthReading(string Name, string Value, HealthStatus Status);

/// <summary>
/// The health of one drive, read from its own SMART / NVMe health data (see <see cref="SmartReader"/>) and judged
/// the way CrystalDiskInfo does: Bad when the drive says it's failing, Caution on bad sectors or little life left.
/// </summary>
public sealed class DriveHealth
{
    public int DiskNumber { get; init; }
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Firmware { get; set; } = "";
    public string Bus { get; init; } = "";
    public long Size { get; init; }
    public string DriveLetters { get; init; } = "";
    public bool IsSystem { get; init; }
    public DriveKind Kind { get; set; }

    /// <summary>Why no health data could be read (null when it was read).</summary>
    public string? Unavailable { get; set; }

    public HealthStatus Status { get; set; } = HealthStatus.Unknown;
    /// <summary>Life remaining, 0–100, for SSDs that report it. Hard disks don't.</summary>
    public int? LifePercent { get; set; }
    public int? TemperatureC { get; set; }
    public long? PowerOnHours { get; set; }
    public long? PowerCycles { get; set; }
    public long? DataWrittenBytes { get; set; }
    public long? DataReadBytes { get; set; }
    /// <summary>SATA: reallocated + pending + uncorrectable sectors. NVMe: media and data integrity errors.</summary>
    public long? BadSectors { get; set; }
    public long? UnsafeShutdowns { get; set; }

    /// <summary>Plain-language reasons for a Caution or Bad status.</summary>
    public List<string> Reasons { get; } = new();
    public List<SmartAttribute> Attributes { get; } = new();
    public List<HealthReading> Readings { get; } = new();

    /// <summary>Identity across checks (disk numbers can change after a reboot).</summary>
    public string Key => Serial.Length > 0 ? $"{Model}|{Serial}" : $"{Model}|{Size}";

    public string Name => $"Disk {DiskNumber} – {Model}";

    public string StatusText => Status == HealthStatus.Unknown ? "Unknown"
        : LifePercent is int p ? $"{Status} {p}%" : Status.ToString();

    public string KindText => Kind switch
    {
        DriveKind.Nvme => "NVMe SSD",
        DriveKind.Ssd => "SSD",
        DriveKind.Hdd => "Hard disk",
        _ => "Drive",
    };

    // ------------------------------------------------------------------ NVMe

    /// <summary>Reads the 512-byte NVMe SMART / Health Information log page (log identifier 02h).</summary>
    public void ParseNvme(ReadOnlySpan<byte> log)
    {
        if (log.Length < 512) throw new ArgumentException("The NVMe health log is 512 bytes.");
        Kind = DriveKind.Nvme;
        byte warning = log[0];
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        int spare = log[3], spareThreshold = log[4], used = log[5];
        ulong unitsRead = BinaryPrimitives.ReadUInt64LittleEndian(log[32..]);
        ulong unitsWritten = BinaryPrimitives.ReadUInt64LittleEndian(log[48..]);
        ulong cycles = BinaryPrimitives.ReadUInt64LittleEndian(log[112..]);
        ulong hours = BinaryPrimitives.ReadUInt64LittleEndian(log[128..]);
        ulong unsafeShutdowns = BinaryPrimitives.ReadUInt64LittleEndian(log[144..]);
        ulong mediaErrors = BinaryPrimitives.ReadUInt64LittleEndian(log[160..]);
        ulong errorLog = BinaryPrimitives.ReadUInt64LittleEndian(log[176..]);
        uint warnTempMinutes = BinaryPrimitives.ReadUInt32LittleEndian(log[192..]);
        uint critTempMinutes = BinaryPrimitives.ReadUInt32LittleEndian(log[196..]);

        if (kelvin > 200 && kelvin < 400) TemperatureC = kelvin - 273;
        LifePercent = Math.Max(0, 100 - used);
        PowerCycles = Clamp(cycles);
        PowerOnHours = Clamp(hours);
        UnsafeShutdowns = Clamp(unsafeShutdowns);
        BadSectors = Clamp(mediaErrors);
        // A data unit is 1000 sectors of 512 bytes.
        DataReadBytes = unitsRead < long.MaxValue / 512000 ? (long)unitsRead * 512000 : long.MaxValue;
        DataWrittenBytes = unitsWritten < long.MaxValue / 512000 ? (long)unitsWritten * 512000 : long.MaxValue;

        var status = HealthStatus.Good;
        void Flag(HealthStatus s, string why) { if (s > status) status = s; Reasons.Add(why); }
        if ((warning & 0x01) != 0 || spare < spareThreshold) Flag(HealthStatus.Bad, $"Spare space is below the safe level ({spare}% left, the drive needs {spareThreshold}%).");
        if ((warning & 0x04) != 0) Flag(HealthStatus.Bad, "The drive reports that its reliability is degraded by media or internal errors.");
        if ((warning & 0x08) != 0) Flag(HealthStatus.Bad, "The drive has switched itself to read-only to protect your data.");
        if ((warning & 0x10) != 0) Flag(HealthStatus.Bad, "The drive's power-loss protection has failed.");
        if ((warning & 0x02) != 0) Flag(HealthStatus.Caution, "The drive is running too hot (above its own temperature limit).");
        if (LifePercent <= 10) Flag(HealthStatus.Caution, $"Only {LifePercent}% of the drive's rated life is left.");
        if (mediaErrors > 0) Flag(HealthStatus.Caution, $"{mediaErrors} unrecovered read/write error(s) (media and data integrity errors).");
        Status = status;

        HealthStatus Warn(bool bad) => bad ? HealthStatus.Caution : HealthStatus.Good;
        Readings.Add(new("Critical warning", warning == 0 ? "None" : $"0x{warning:X2}", warning == 0 ? HealthStatus.Good : (warning & 0x1D) != 0 ? HealthStatus.Bad : HealthStatus.Caution));
        Readings.Add(new("Temperature", TemperatureC is int t ? $"{t} °C" : "–", HealthStatus.Good));
        Readings.Add(new("Available spare", $"{spare}%", spare < spareThreshold ? HealthStatus.Bad : HealthStatus.Good));
        Readings.Add(new("Available spare threshold", $"{spareThreshold}%", HealthStatus.Good));
        Readings.Add(new("Percentage used", $"{used}%", Warn(used >= 90)));
        Readings.Add(new("Data read", $"{Util.FormatBytes(DataReadBytes!.Value)} ({unitsRead:N0} units)", HealthStatus.Good));
        Readings.Add(new("Data written", $"{Util.FormatBytes(DataWrittenBytes!.Value)} ({unitsWritten:N0} units)", HealthStatus.Good));
        Readings.Add(new("Host read commands", $"{BinaryPrimitives.ReadUInt64LittleEndian(log[64..]):N0}", HealthStatus.Good));
        Readings.Add(new("Host write commands", $"{BinaryPrimitives.ReadUInt64LittleEndian(log[80..]):N0}", HealthStatus.Good));
        Readings.Add(new("Controller busy time", $"{BinaryPrimitives.ReadUInt64LittleEndian(log[96..]):N0} min", HealthStatus.Good));
        Readings.Add(new("Power cycles", $"{cycles:N0}", HealthStatus.Good));
        Readings.Add(new("Power-on hours", $"{hours:N0}", HealthStatus.Good));
        Readings.Add(new("Unsafe shutdowns", $"{unsafeShutdowns:N0}", HealthStatus.Good));
        Readings.Add(new("Media and data integrity errors", $"{mediaErrors:N0}", Warn(mediaErrors > 0)));
        Readings.Add(new("Error log entries", $"{errorLog:N0}", HealthStatus.Good));
        Readings.Add(new("Time above warning temperature", $"{warnTempMinutes:N0} min", HealthStatus.Good));
        Readings.Add(new("Time above critical temperature", $"{critTempMinutes:N0} min", HealthStatus.Good));
    }

    // ------------------------------------------------------------------ SATA / ATA SMART

    /// <summary>Attributes whose normalised value is the SSD's life remaining (0–100), most specific first.</summary>
    private static readonly byte[] LifeAttributes = { 231, 202, 233, 177, 169, 173 };

    /// <summary>
    /// Reads SMART READ DATA (attribute values, 512 bytes) and SMART READ THRESHOLDS (512 bytes, optional).
    /// <paramref name="ssd"/>: true/false when Windows knows whether the drive spins, null if unknown.
    /// </summary>
    public void ParseAta(ReadOnlySpan<byte> values, ReadOnlySpan<byte> thresholds, bool? ssd)
    {
        if (values.Length < 362) throw new ArgumentException("SMART data is 512 bytes.");
        var limits = new Dictionary<byte, byte>();
        if (thresholds.Length >= 362)
            for (int i = 0; i < 30; i++)
            {
                byte id = thresholds[2 + i * 12];
                if (id != 0) limits[id] = thresholds[3 + i * 12];
            }

        var raw = new Dictionary<byte, ulong>();
        var current = new Dictionary<byte, byte>();
        for (int i = 0; i < 30; i++)
        {
            int o = 2 + i * 12;
            byte id = values[o];
            if (id == 0) continue;
            byte cur = values[o + 3], worst = values[o + 4];
            ulong r = 0;
            for (int b = 5; b >= 0; b--) r = (r << 8) | values[o + 5 + b];
            byte thr = limits.TryGetValue(id, out var t) ? t : (byte)0;
            raw[id] = r;
            current[id] = cur;

            var st = HealthStatus.Good;
            if (thr > 0 && cur > 0 && cur <= thr) st = HealthStatus.Bad;
            else if (id is 5 or 197 or 198 && (r & 0xFFFFFFFF) > 0) st = HealthStatus.Caution;
            Attributes.Add(new SmartAttribute(id, AttributeName(id), cur, worst, thr, r, st));
        }

        bool wear = LifeAttributes.Any(current.ContainsKey);
        Kind = ssd == true || (ssd == null && wear) ? DriveKind.Ssd : ssd == false ? DriveKind.Hdd : DriveKind.Unknown;
        if (Kind == DriveKind.Ssd)
            foreach (byte id in LifeAttributes)
                if (current.TryGetValue(id, out byte v) && v is > 0 and <= 100) { LifePercent = v; break; }

        ulong Low32(byte id) => raw.TryGetValue(id, out var v) ? v & 0xFFFFFFFF : 0;
        foreach (byte id in new byte[] { 194, 190 })
            if (raw.TryGetValue(id, out var v) && (v & 0xFF) is > 0 and < 120) { TemperatureC = (int)(v & 0xFF); break; }
        if (raw.ContainsKey(9)) PowerOnHours = (long)Low32(9);
        if (raw.ContainsKey(12)) PowerCycles = (long)Low32(12);
        if (raw.ContainsKey(174)) UnsafeShutdowns = (long)Low32(174);
        if (raw.ContainsKey(5) || raw.ContainsKey(197) || raw.ContainsKey(198))
            BadSectors = (long)(Low32(5) + Low32(197) + Low32(198));

        var status = HealthStatus.Good;
        void Flag(HealthStatus s, string why) { if (s > status) status = s; Reasons.Add(why); }
        foreach (var a in Attributes.Where(a => a.Status == HealthStatus.Bad))
            Flag(HealthStatus.Bad, $"{a.Name} has reached the drive's failure threshold (value {a.Current}, threshold {a.Threshold}).");
        if (Low32(5) > 0) Flag(HealthStatus.Caution, $"{Low32(5)} reallocated sector(s): parts of the disk have failed and been replaced by spares.");
        if (Low32(197) > 0) Flag(HealthStatus.Caution, $"{Low32(197)} sector(s) waiting to be reallocated (they couldn't be read reliably).");
        if (Low32(198) > 0) Flag(HealthStatus.Caution, $"{Low32(198)} uncorrectable sector(s): data in them couldn't be read.");
        if (LifePercent <= 10) Flag(HealthStatus.Caution, $"Only {LifePercent}% of the SSD's rated life is left.");
        Status = status;
    }

    private static long Clamp(ulong v) => v > long.MaxValue ? long.MaxValue : (long)v;

    public static string AttributeName(byte id) => id switch
    {
        1 => "Read error rate",
        2 => "Throughput performance",
        3 => "Spin-up time",
        4 => "Start/stop count",
        5 => "Reallocated sectors count",
        7 => "Seek error rate",
        8 => "Seek time performance",
        9 => "Power-on hours",
        10 => "Spin retry count",
        11 => "Recalibration retries",
        12 => "Power cycle count",
        13 => "Soft read error rate",
        160 => "Uncorrectable sector count (read/write)",
        161 => "Valid spare blocks",
        163 => "Initial invalid blocks",
        164 => "Total erase count",
        165 => "Maximum erase count",
        166 => "Minimum erase count",
        167 => "Average erase count",
        168 => "Max NAND erase count",
        169 => "Remaining life",
        170 => "Available reserved space",
        171 => "Program fail count",
        172 => "Erase fail count",
        173 => "Wear leveling count",
        174 => "Unexpected power loss count",
        175 => "Power loss protection failure",
        176 => "Erase fail count (chip)",
        177 => "Wear range delta / wear leveling",
        178 => "Used reserved block count (chip)",
        179 => "Used reserved block count (total)",
        180 => "Unused reserved block count",
        181 => "Program fail count (total)",
        182 => "Erase fail count (total)",
        183 => "Runtime bad blocks",
        184 => "End-to-end error",
        187 => "Reported uncorrectable errors",
        188 => "Command timeout",
        189 => "High fly writes",
        190 => "Airflow temperature",
        191 => "G-sense error rate",
        192 => "Power-off retract count",
        193 => "Load/unload cycle count",
        194 => "Temperature",
        195 => "Hardware ECC recovered",
        196 => "Reallocation event count",
        197 => "Current pending sector count",
        198 => "Uncorrectable sector count",
        199 => "UltraDMA CRC error count",
        200 => "Write error rate",
        201 => "Soft read error rate",
        202 => "Percent lifetime remaining",
        203 => "Run out cancel",
        204 => "Soft ECC correction",
        205 => "Thermal asperity rate",
        206 => "Flying height",
        207 => "Spin high current",
        208 => "Spin buzz",
        209 => "Offline seek performance",
        210 => "RAIN successful recovery count",
        220 => "Disk shift",
        221 => "G-sense error rate",
        222 => "Loaded hours",
        223 => "Load/unload retry count",
        224 => "Load friction",
        225 => "Host writes",
        226 => "Load-in time",
        227 => "Torque amplification count",
        228 => "Power-off retract cycle",
        230 => "Drive life protection status",
        231 => "SSD life left",
        232 => "Endurance remaining",
        233 => "Media wearout indicator",
        234 => "Average/maximum erase count",
        235 => "Good block count / power-fail backups",
        240 => "Head flying hours",
        241 => "Total LBAs written",
        242 => "Total LBAs read",
        243 => "Total LBAs written (expanded)",
        244 => "Total LBAs read (expanded)",
        245 => "Remaining rated write endurance",
        246 => "Cumulative host sectors written",
        247 => "Host program page count",
        248 => "Background program page count",
        249 => "NAND writes",
        250 => "Read error retry rate",
        251 => "Minimum spares remaining",
        252 => "Newly added bad flash block",
        254 => "Free fall protection",
        _ => "Vendor specific",
    };
}
