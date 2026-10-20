// End-to-end test of the imaging engine against fake GPT disks stored as files.
// Usage: dotnet run -- <work folder>
using System.Text;
using DiskImager.Core;

if (args.Length >= 3 && args[0] == "--tray") return TrayTest.Run(args[1], args[2]);
if (args.Length >= 3 && args[0] == "--ui") return UiTest.Run(args[1], args[2]);
if (args.Length >= 3 && args[0] == "--layout") return UiTest.LayoutTest(args[1], args[2]);
if (args.Length >= 4 && args[0] == "--setup") return SetupTest.Run(args[1], args[2], args[3]);
if (args.Length >= 4 && args[0] == "--restore") return UiTest.RestoreTest(args[1], args[2], args[3]);
if (args.Length >= 4 && args[0] == "--update") return UpdateUiTest.Run(args[1], args[2], args[3]);
if (args.Length >= 1 && args[0] == "--health-live") return HealthTest.Live();
if (args.Length >= 2 && args[0] == "--health-render") return HealthTest.Render(args[1]);
if (args.Length >= 3 && args[0] == "--health-xml")
{
    // Task XML for each interval, for registering with Task Scheduler by hand (needs admin).
    foreach (var every in Enum.GetValues<HealthInterval>())
        File.WriteAllText(Path.Combine(args[1], $"health-{every}.xml"), TaskSchedulerService.BuildHealthXml(args[2], new HealthSettings { Every = every }), Encoding.Unicode);
    return 0;
}
if (args.Length >= 3 && args[0] == "--health-ui") return UiTest.HealthUi(args[1], args[2]);
if (args.Length >= 2 && args[0] == "--screenfit") return UiTest.ScreenFitUi(args[1]);
if (args.Length >= 3 && args[0] == "--wizard") return UiTest.WizardUi(args[1], args[2]);

string work = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "diskimager-smoke");
if (Directory.Exists(work))
{
    foreach (var f in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
    Directory.Delete(work, true);
}
Directory.CreateDirectory(work);
var log = new Logger();
log.LineWritten += l => Console.WriteLine("    " + l);
int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

const int SS = 512;
const long MiB = 1 << 20;
long srcSize = 64 * MiB + 7 * SS; // deliberately not a multiple of the block size
var rnd = new Random(42);

Check(Crc32.Compute(Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926, "CRC32 check value");

// ---- Build a source disk: protective MBR + GPT + two partitions.
string srcPath = Path.Combine(work, "source.disk");
var p1 = (Offset: 1 * MiB, Length: 8 * MiB);
var p2 = (Offset: 10 * MiB, Length: 40 * MiB);
var p3 = (Offset: 52 * MiB, Length: 4 * MiB); // "Recovery" after the main partition
using (var fs = new FileStream(srcPath, FileMode.Create)) fs.SetLength(srcSize);
{
    var block0 = new byte[MiB];
    block0[446 + 4] = 0xEE; BitConverter.TryWriteBytes(block0.AsSpan(446 + 8), 1u);
    block0[510] = 0x55; block0[511] = 0xAA;
    int h = SS;
    Encoding.ASCII.GetBytes("EFI PART").CopyTo(block0, h);
    BitConverter.TryWriteBytes(block0.AsSpan(h + 8), 0x00010000);
    BitConverter.TryWriteBytes(block0.AsSpan(h + 12), 92);
    BitConverter.TryWriteBytes(block0.AsSpan(h + 24), 1L);
    BitConverter.TryWriteBytes(block0.AsSpan(h + 40), 34L);
    Guid.NewGuid().ToByteArray().CopyTo(block0, h + 56);
    BitConverter.TryWriteBytes(block0.AsSpan(h + 72), 2L);
    BitConverter.TryWriteBytes(block0.AsSpan(h + 80), 128);
    BitConverter.TryWriteBytes(block0.AsSpan(h + 84), 128);
    var basic = new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7").ToByteArray();
    foreach (var (i, p) in new[] { (0, p1), (1, p2), (2, p3) })
    {
        int e = 2 * SS + i * 128;
        basic.CopyTo(block0, e);
        Guid.NewGuid().ToByteArray().CopyTo(block0, e + 16);
        BitConverter.TryWriteBytes(block0.AsSpan(e + 32), p.Offset / SS);
        BitConverter.TryWriteBytes(block0.AsSpan(e + 40), (p.Offset + p.Length) / SS - 1);
    }
    var fix = Gpt.ForTarget(block0, block0.Length, SS, srcSize, newIds: false);
    using var fs = new FileStream(srcPath, FileMode.Open);
    fs.Write(block0);
    fs.Position = fix.BackupOffset; fs.Write(fix.Backup);
    // Partition 1: random. Partition 2: 20 MiB random then zeros.
    var data = new byte[p1.Length]; rnd.NextBytes(data);
    fs.Position = p1.Offset; fs.Write(data);
    data = new byte[20 * MiB]; rnd.NextBytes(data);
    fs.Position = p2.Offset; fs.Write(data);
    data = new byte[p3.Length]; rnd.NextBytes(data);
    fs.Position = p3.Offset; fs.Write(data);
}
string origPath = Path.Combine(work, "source-original.disk");
File.Copy(srcPath, origPath);

DiskInfo MakeDisk(int n, string path, long size) => new()
{
    Number = n, Path = path, Size = size, SectorSize = SS, Style = DiskStyle.GPT, Model = "TestDisk",
    GptDiskId = new Guid("11111111-2222-3333-4444-555555555555"),
    Partitions = new()
    {
        new PartitionInfo { Number = 1, Offset = p1.Offset, Length = p1.Length, TypeName = "Basic Data" },
        new PartitionInfo { Number = 2, Offset = p2.Offset, Length = p2.Length, TypeName = "Basic Data" },
        new PartitionInfo { Number = 3, Offset = p3.Offset, Length = p3.Length, TypeName = "Recovery" },
    },
};
var src = MakeDisk(900, srcPath, srcSize);
string outDir = Path.Combine(work, "images");

BackupResult Backup(bool incr, int keep = 2) => BackupEngine.Run(new BackupRequest
{
    Disk = src, DestinationFolder = outDir, JobName = "Smoke", Incremental = incr, KeepFullSets = keep, MaxIncrementals = 5,
}, null, CancellationToken.None, log);

// ---- Full, then change data, then incremental.
var full = Backup(incr: false);
Check(full.WasFull && File.Exists(full.ImagePath), "full image created");
Thread.Sleep(1100);
using (var fs = new FileStream(srcPath, FileMode.Open))
{
    var chg = new byte[3 * MiB]; rnd.NextBytes(chg);
    fs.Position = p2.Offset + 5 * MiB + 4096; fs.Write(chg);
    fs.Position = p1.Offset + 2 * MiB; fs.Write(new byte[MiB]);
}
var incr = Backup(incr: true);
Check(!incr.WasFull, "second run was incremental");
Check(incr.BytesWritten < full.BytesWritten / 2, $"incremental is small ({Util.FormatBytes(incr.BytesWritten)} vs full {Util.FormatBytes(full.BytesWritten)})");

using (var r = new ImageReader(incr.ImagePath))
    Check(r.Verify(null, CancellationToken.None, log) == 0, "incremental verifies (reads blocks from both files)");

bool SameRange(string a, string b, long off, long len)
{
    using var fa = File.OpenRead(a); using var fb = File.OpenRead(b);
    var ba = new byte[len]; var bb = new byte[len];
    fa.Position = off; fa.ReadExactly(ba); fb.Position = off; fb.ReadExactly(bb);
    return ba.AsSpan().SequenceEqual(bb);
}

bool GptValid(string path, long size, out string why)
{
    using var fs = File.OpenRead(path);
    var hdr = new byte[SS]; fs.Position = SS; fs.ReadExactly(hdr);
    long last = size / SS - 1;
    why = "";
    if (Encoding.ASCII.GetString(hdr, 0, 8) != "EFI PART") { why = "no primary header"; return false; }
    var copy = hdr[..92]; uint crc = BitConverter.ToUInt32(copy, 16); BitConverter.TryWriteBytes(copy.AsSpan(16), 0u);
    if (Crc32.Compute(copy) != crc) { why = "primary header CRC"; return false; }
    if (BitConverter.ToInt64(hdr, 32) != last) { why = "AlternateLBA"; return false; }
    var ents = new byte[128 * 128]; fs.Position = 2 * SS; fs.ReadExactly(ents);
    if (Crc32.Compute(ents) != BitConverter.ToUInt32(hdr, 88)) { why = "entries CRC"; return false; }
    var bh = new byte[SS]; fs.Position = last * SS; fs.ReadExactly(bh);
    if (Encoding.ASCII.GetString(bh, 0, 8) != "EFI PART" || BitConverter.ToInt64(bh, 24) != last) { why = "backup header"; return false; }
    var bcopy = bh[..92]; crc = BitConverter.ToUInt32(bcopy, 16); BitConverter.TryWriteBytes(bcopy.AsSpan(16), 0u);
    if (Crc32.Compute(bcopy) != crc) { why = "backup header CRC"; return false; }
    var bents = new byte[128 * 128]; fs.Position = BitConverter.ToInt64(bh, 72) * SS; fs.ReadExactly(bents);
    if (!bents.AsSpan().SequenceEqual(ents)) { why = "backup entries differ"; return false; }
    return true;
}

string MakeTarget(string name, long size)
{
    string p = Path.Combine(work, name);
    using var fs = new FileStream(p, FileMode.Create);
    var junk = new byte[MiB]; Array.Fill(junk, (byte)0xAB);
    for (long o = 0; o < size; o += MiB) fs.Write(junk, 0, (int)Math.Min(MiB, size - o));
    return p;
}

// ---- Restore the incremental to a disk twice the size.
long bigSize = 2 * srcSize - 3 * SS;
string t1 = MakeTarget("restore-incr.disk", bigSize);
using (var r = new ImageReader(incr.ImagePath))
    DiskWriterEngine.Write(r, MakeDisk(901, t1, bigSize), false, "Restoring", null, CancellationToken.None, log);
Check(SameRange(srcPath, t1, p1.Offset, p1.Length) && SameRange(srcPath, t1, p2.Offset, p2.Length),
    "restored incremental matches the CHANGED source byte-for-byte");
Check(GptValid(t1, bigSize, out var why1), "GPT on larger target is valid (relocated backup, CRCs) " + why1);

// ---- Restore the full to an equal-size disk: must match the ORIGINAL data.
string t2 = MakeTarget("restore-full.disk", srcSize);
using (var r = new ImageReader(full.ImagePath))
    DiskWriterEngine.Write(r, MakeDisk(902, t2, srcSize), false, "Restoring", null, CancellationToken.None, log);
Check(SameRange(origPath, t2, p1.Offset, p1.Length) && SameRange(origPath, t2, p2.Offset, p2.Length),
    "restored full matches the ORIGINAL source");
Check(GptValid(t2, srcSize, out var why2), "GPT on same-size target is valid " + why2);

// ---- Clone directly.
string t3 = MakeTarget("clone.disk", bigSize);
using (var live = LiveDiskSource.Open(src, useVss: false, log, CancellationToken.None))
    DiskWriterEngine.Write(live, MakeDisk(903, t3, bigSize), true, "Cloning", null, CancellationToken.None, log);
Check(SameRange(srcPath, t3, p1.Offset, p1.Length) && SameRange(srcPath, t3, p2.Offset, p2.Length), "clone matches source");
Check(GptValid(t3, bigSize, out var why3), "GPT on clone (with new IDs) is valid " + why3);

// ---- Too-small target is refused before anything is written.
string t4 = MakeTarget("small.disk", 30 * MiB);
try
{
    using var r = new ImageReader(full.ImagePath);
    DiskWriterEngine.Write(r, MakeDisk(904, t4, 30 * MiB), false, "Restoring", null, CancellationToken.None, log);
    Check(false, "too-small target refused");
}
catch (InvalidOperationException) { Check(File.ReadAllBytes(t4)[0] == 0xAB, "too-small target refused and left untouched"); }

// ---- Retention: two more full backups with keep=2 leaves 2 sets.
Thread.Sleep(1100); Backup(incr: false, keep: 2);
Thread.Sleep(1100); Backup(incr: false, keep: 2);
var chains = ImageCatalog.ChainsForJob(outDir, "Smoke");
Check(chains.Count == 2 && chains.All(c => c.IsComplete), $"retention kept 2 newest sets (found {chains.Count})");

// ---- Task Scheduler XML is well-formed (registration itself needs admin).
var job = new BackupJob { Name = "Smoke", Full = new Schedule { Kind = ScheduleKind.Monthly, DayOfMonth = 1 }, Incremental = new Schedule { Kind = ScheduleKind.Weekly, Days = { DayOfWeek.Monday, DayOfWeek.Friday } } };
var xmlMethod = typeof(TaskSchedulerService).GetMethod("BuildXml", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
foreach (bool inc in new[] { false, true })
{
    string xml = (string)xmlMethod.Invoke(null, new object[] { job, @"C:\Program Files\DiskImager\DiskImager.exe", inc })!;
    try { System.Xml.Linq.XDocument.Parse(xml); Check(xml.StartsWith("<?xml"), $"task XML ({(inc ? "incremental" : "full")}) parses"); }
    catch (Exception ex) { Check(false, "task XML parses: " + ex.Message); }
}

// ================================================================ Resizing / moving partitions

bool SameAt(string a, long aOff, string b, long bOff, long len)
{
    using var fa = File.OpenRead(a); using var fb = File.OpenRead(b);
    var ba = new byte[len]; var bb = new byte[len];
    fa.Position = aOff; fa.ReadExactly(ba); fb.Position = bOff; fb.ReadExactly(bb);
    return ba.AsSpan().SequenceEqual(bb);
}

// Describe the source as Windows would: p2 is "C:" (NTFS, 20 MB used), p1 is FAT (fixed), p3 NTFS recovery.
var desc = new SourceDescription
{
    Title = "TestDisk", Style = DiskStyle.GPT, SectorSize = SS, DiskSize = srcSize, RequiredForOriginalLayout = p3.Offset + p3.Length,
    Parts = new()
    {
        new SourcePart { Number = 1, Offset = p1.Offset, Length = p1.Length, Name = "EFI", Type = "EFI System", FileSystem = "FAT32", UsedBytes = p1.Length },
        new SourcePart { Number = 2, Offset = p2.Offset, Length = p2.Length, Name = "C:", Type = "Basic Data", FileSystem = "NTFS", HasLetter = true, UsedBytes = 20 * MiB },
        new SourcePart { Number = 3, Offset = p3.Offset, Length = p3.Length, Name = "Recovery", Type = "Recovery", FileSystem = "NTFS", UsedBytes = p3.Length },
    },
};

// Auto-fit onto a 2x larger drive: C: grows, Recovery moves to the end.
long growSize = 2 * srcSize;
var grow = Layout.Original(desc, growSize, SS);
Layout.AutoFit(grow);
Check(grow.Validate() == null, "auto-fit plan for larger drive is valid " + grow.Validate());
Check(grow.Parts[1].TargetLength > p2.Length && grow.Parts[0].TargetLength == p1.Length && grow.Parts[2].TargetLength == p3.Length,
    $"auto-fit grows C: ({Util.FormatBytes(p2.Length)} -> {Util.FormatBytes(grow.Parts[1].TargetLength)}) and keeps others");
Check(grow.Parts[2].TargetOffset > p3.Offset && grow.Parts[2].TargetOffset % MiB == 0, "recovery partition moved and still 1 MB aligned");
Check(grow.UsableEnd - grow.End < MiB, $"auto-fit fills the drive (slack {grow.UsableEnd - grow.End} bytes)");

string g1 = MakeTarget("grow.disk", growSize);
string latest = ImageCatalog.ChainsForJob(outDir, "Smoke")[0].Images[^1].Path; // older sets were removed by retention
using (var r = new ImageReader(latest))
    DiskWriterEngine.Write(r, MakeDisk(905, g1, growSize), grow, false, "Restoring", null, CancellationToken.None, log);
Check(SameAt(srcPath, p1.Offset, g1, grow.Parts[0].TargetOffset, p1.Length), "grow: EFI data intact");
Check(SameAt(srcPath, p2.Offset, g1, grow.Parts[1].TargetOffset, p2.Length), "grow: C: data intact at start of the bigger partition");
Check(SameAt(srcPath, p3.Offset, g1, grow.Parts[2].TargetOffset, p3.Length), "grow: recovery data found at its new position");
Check(GptValid(g1, growSize, out var whyG), "grow: GPT valid " + whyG);
using (var fs = File.OpenRead(g1))
{
    var ent = new byte[3 * 128]; fs.Position = 2 * SS; fs.ReadExactly(ent);
    bool ok = true;
    for (int i = 0; i < 3; i++)
    {
        ok &= BitConverter.ToInt64(ent, i * 128 + 32) == grow.Parts[i].TargetOffset / SS;
        ok &= BitConverter.ToInt64(ent, i * 128 + 40) == grow.Parts[i].TargetEnd / SS - 1;
    }
    Check(ok, "grow: GPT entries hold the new start/end of every partition");
}

// Custom sizes: C: set to 70 MB by hand.
var custom = Layout.Original(desc, growSize, SS);
custom.Parts[1].TargetLength = 70 * MiB;
Layout.Relayout(custom);
Check(custom.Validate() == null && custom.Parts[2].TargetOffset == 82 * MiB, "custom size: recovery follows C: with the original 2 MB gap");

// Too small: a 40 MB drive can't hold C:'s data, the plan says why and the writer refuses.
var tiny = Layout.Original(desc, 40 * MiB, SS);
Layout.AutoFit(tiny);
string? tinyErr = tiny.Validate();
Check(tinyErr != null && tinyErr.Contains("C:"), "too-small drive gives a readable reason: " + tinyErr);

// Shrink plan is valid in principle but must go through staging (not the direct writer).
var shrinkDesc = new SourceDescription
{
    Title = desc.Title, Style = desc.Style, SectorSize = SS, DiskSize = 4L << 30, RequiredForOriginalLayout = 4L << 30,
    Parts = new() { new SourcePart { Number = 1, Offset = MiB, Length = (4L << 30) - 2 * MiB, Name = "C:", FileSystem = "NTFS", HasLetter = true, UsedBytes = 1L << 30 } },
};
var shrink = Layout.Original(shrinkDesc, 3L << 30, SS);
Layout.AutoFit(shrink);
Check(shrink.Validate() == null && shrink.NeedsShrink, $"auto-fit shrinks a 4 GB C: (1 GB used) to {Util.FormatBytes(shrink.Parts[0].TargetLength)} for a 3 GB drive");
try
{
    using var r = new ImageReader(latest);
    DiskWriterEngine.Write(r, MakeDisk(906, MakeTarget("shrink.disk", 8 * MiB), 3L << 30), shrink, false, "x", null, CancellationToken.None, log);
    Check(false, "direct writer refuses shrink plans");
}
catch (InvalidOperationException) { Check(true, "direct writer refuses shrink plans (they go through the staging copy)"); }

// MBR: a two-partition MBR table is rewritten for a grown layout.
{
    var mbr = new byte[MiB];
    void Entry(int i, byte type, long off, long len)
    {
        int pe = 446 + i * 16; mbr[pe + 4] = type;
        BitConverter.TryWriteBytes(mbr.AsSpan(pe + 8), (uint)(off / SS));
        BitConverter.TryWriteBytes(mbr.AsSpan(pe + 12), (uint)(len / SS));
    }
    Entry(0, 0x07, MiB, 10 * MiB); Entry(1, 0x27, 11 * MiB, 2 * MiB);
    mbr[510] = 0x55; mbr[511] = 0xAA;
    var mdesc = new SourceDescription
    {
        Style = DiskStyle.MBR, SectorSize = SS, DiskSize = 16 * MiB, RequiredForOriginalLayout = 13 * MiB,
        Parts = new()
        {
            new SourcePart { Number = 1, Offset = MiB, Length = 10 * MiB, Name = "C:", FileSystem = "NTFS", HasLetter = true, UsedBytes = MiB },
            new SourcePart { Number = 2, Offset = 11 * MiB, Length = 2 * MiB, Name = "Recovery", FileSystem = "NTFS", UsedBytes = 2 * MiB },
        },
    };
    var mplan = Layout.Original(mdesc, 100 * MiB, SS);
    Layout.AutoFit(mplan);
    var t = PartitionTable.Build(mbr[..(int)MiB], mplan, newIds: false);
    uint s0 = BitConverter.ToUInt32(t.Primary, 446 + 8), n0 = BitConverter.ToUInt32(t.Primary, 446 + 12);
    uint s1 = BitConverter.ToUInt32(t.Primary, 462 + 8), n1 = BitConverter.ToUInt32(t.Primary, 462 + 12);
    Check(s0 * (long)SS == MiB && n0 * (long)SS == mplan.Parts[0].TargetLength && s1 * (long)SS == mplan.Parts[1].TargetOffset && n1 * (long)SS == 2 * MiB
          && (s1 + n1) * (long)SS <= 100 * MiB && t.Primary[510] == 0x55,
        $"MBR entries rewritten (C: {Util.FormatBytes(n0 * (long)SS)}, recovery moved to {Util.FormatBytes(s1 * (long)SS)})");
}

// ================================================================ Email notifications (fake local SMTP server)
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var transcript = new StringBuilder();
    var server = Task.Run(() =>
    {
        using var client = listener.AcceptTcpClient();
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        writer.WriteLine("220 fake ESMTP");
        bool data = false;
        while (reader.ReadLine() is { } line)
        {
            transcript.AppendLine(line);
            if (data)
            {
                if (line == ".") { data = false; writer.WriteLine("250 queued"); }
                continue;
            }
            string cmd = line.Split(' ')[0].ToUpperInvariant();
            if (cmd == "EHLO") { writer.WriteLine("250-fake"); writer.WriteLine("250 AUTH PLAIN LOGIN"); }
            else if (cmd == "AUTH") writer.WriteLine("235 authenticated");
            else if (cmd == "DATA") { data = true; writer.WriteLine("354 go ahead"); }
            else if (cmd == "QUIT") { writer.WriteLine("221 bye"); break; }
            else writer.WriteLine("250 ok");
        }
    });

    var mailSettings = new EmailSettings
    {
        Enabled = true, Host = "127.0.0.1", Port = port, Security = EmailSecurity.None,
        User = "backup@example.com", PasswordProtected = JobStore.Protect("app-password"),
        From = "backup@example.com", To = "ian@example.com, it@example.com",
    };
    var failJob = new BackupJob { Name = "Nightly", SourceDiskDescription = "Disk 0", Destination = @"\\NAS\Backups", Notify = NotifyWhen.OnFailure };
    failJob.SourceDiskDescription = "Disk 1 – XPG GAMMIX S70 BLADE (953.9 GB)";
    failJob.After = PostAction.Shutdown;
    var failMsg = Notifier.Compose(failJob, incremental: true, null, "Could not connect to \\\\NAS\\Backups as backupuser: The network path was not found.", DateTime.Now.AddMinutes(-5), log);
    Check(failMsg.Subject.Contains("FAILED") && failMsg.Subject.Contains("Nightly") && failMsg.Text.Contains("Could not connect") && failMsg.Html.Contains("#DC2626"),
        "failure email: clear subject, error shown, red banner");
    var okMsg = Notifier.Compose(failJob, false, new BackupResult { ImagePath = @"\\NAS\Backups\Nightly_20261019-010002_F.dimg", WasFull = true, BytesRead = 212L << 30, BytesWritten = 98L << 30, Duration = TimeSpan.FromMinutes(42) }, null, DateTime.Now, log);
    Check(okMsg.Subject.Contains("succeeded") && okMsg.Html.Contains("Nightly_20261019-010002_F.dimg") && okMsg.Text.Contains("42m") && okMsg.Html.Contains("#16A34A"),
        "success email: image file, duration, green banner");
    var missJob = failJob.Clone();
    missJob.LastSuccessUtc = DateTime.UtcNow.AddDays(-2);
    var missMsg = Notifier.ComposeMissed(missJob, DateTime.Today.AddHours(1), 2);
    Check(missMsg.Subject.Contains("missed") && missMsg.Html.Contains("#D97706") && missMsg.Text.Contains("2 in a row"), "missed email: amber banner and missed count");
    Check(okMsg.Html.Contains("TechSource Digital") && okMsg.Text.Contains("TechSource Digital"), "emails carry the TechSource Digital footer");
    var nasty = failJob.Clone(); nasty.Name = "<b>x</b> & co";
    Check(Notifier.Compose(nasty, false, null, "e", DateTime.Now, log).Html.Contains("&lt;b&gt;x&lt;/b&gt; &amp; co"), "job names are HTML-escaped");
    File.WriteAllText(Path.Combine(work, "email-success.html"), okMsg.Html);
    File.WriteAllText(Path.Combine(work, "email-failed.html"), failMsg.Html);
    File.WriteAllText(Path.Combine(work, "email-missed.html"), missMsg.Html);

    try
    {
        Notifier.Send(mailSettings, failMsg);
        server.Wait(5000);
        string t = transcript.ToString();
        Check(t.Contains("AUTH PLAIN") && t.Contains("RCPT TO:<ian@example.com>") && t.Contains("RCPT TO:<it@example.com>")
              && t.Contains("multipart/alternative") && t.Contains("text/html") && t.Contains("text/plain"),
            "email delivered via SMTP: logged in, both recipients, HTML + plain-text parts");
    }
    catch (Exception ex) { Check(false, "email delivered via SMTP: " + ex.Message); }
    finally { listener.Stop(); }

    try { Notifier.Send(new EmailSettings { Host = "127.0.0.1", Port = 1, To = "a@b.c", From = "a@b.c", Security = EmailSecurity.None }, Notifier.TestMessage()); Check(false, "unreachable server reports an error"); }
    catch (Exception ex) { Check(ex.Message.Length > 0, "unreachable server reports an error: " + ex.Message.Split('\n')[0]); }
}

// ================================================================ Missed-backup detection
{
    var now = new DateTime(2026, 10, 20, 9, 0, 0); // Tuesday 09:00
    BackupJob Daily(Action<BackupJob> setup)
    {
        var j = new BackupJob
        {
            Name = "Daily", RegisteredTasks = { "x" },
            Full = new Schedule { Kind = ScheduleKind.None },
            Incremental = new Schedule { Kind = ScheduleKind.Daily, Time = "01:00" },
            ScheduleSinceUtc = new DateTime(2026, 10, 1).ToUniversalTime(),
        };
        setup(j);
        return j;
    }
    DateTime At(int day, int h, int m) => new DateTime(2026, 10, day, h, m, 0).ToUniversalTime();

    var onTime = Daily(j => j.RecentAttemptsUtc.AddRange(new[] { At(19, 1, 0), At(20, 1, 1) }));
    Check(MissedCheck.Find(new[] { onTime }, now).Count == 0, "missed check: on-time runs are not reported");

    var pcOff = Daily(j => j.RecentAttemptsUtc.Add(At(19, 1, 0)));
    var m1 = MissedCheck.Find(new[] { pcOff }, now);
    Check(m1.Count == 1 && m1[0].ScheduledLocal == new DateTime(2026, 10, 20, 1, 0, 0) && m1[0].Count == 1, "missed check: PC off at 01:00 today is reported once");

    var lateRun = Daily(j => j.RecentAttemptsUtc.AddRange(new[] { At(19, 1, 0), At(20, 8, 50) })); // ran late after PC turned on
    Check(MissedCheck.Find(new[] { lateRun }, now).Count == 1, "missed check: a late catch-up run still counts as missed");

    var days = Daily(j => j.RecentAttemptsUtc.Add(At(17, 1, 0)));
    var m3 = MissedCheck.Find(new[] { days }, now);
    Check(m3.Count == 1 && m3[0].Count == 3, $"missed check: three days off reported as 3 in a row (got {(m3.Count > 0 ? m3[0].Count : 0)})");

    var already = Daily(j => { j.RecentAttemptsUtc.Add(At(19, 1, 0)); j.MissedNotifiedForUtc = At(20, 1, 0); });
    Check(MissedCheck.Find(new[] { already }, now).Count == 0, "missed check: the same miss isn't emailed twice");

    var justDue = Daily(j => j.RecentAttemptsUtc.Add(At(19, 1, 0)));
    Check(MissedCheck.Find(new[] { justDue }, new DateTime(2026, 10, 20, 1, 10, 0)).Count == 0, "missed check: a run due 10 minutes ago gets time to start");

    var fresh = Daily(j => j.ScheduleSinceUtc = At(20, 8, 0));
    Check(MissedCheck.Find(new[] { fresh }, now).Count == 0, "missed check: times before the job was saved are ignored");

    var weekly = Daily(j =>
    {
        j.Incremental = new Schedule { Kind = ScheduleKind.None };
        j.Full = new Schedule { Kind = ScheduleKind.Weekly, Days = { DayOfWeek.Sunday }, Time = "22:00" };
        j.RecentAttemptsUtc.Add(At(11, 22, 0)); // ran Sunday 11 Oct, missed Sunday 18 Oct
    });
    var m4 = MissedCheck.Find(new[] { weekly }, now);
    Check(m4.Count == 1 && m4[0].ScheduledLocal == new DateTime(2026, 10, 18, 22, 0, 0), "missed check: weekly Sunday full missed");

    var neverRan = Daily(j => { });
    var m5 = MissedCheck.Find(new[] { neverRan }, now);
    Check(m5.Count == 1 && m5[0].Count == 20, $"missed check: a job that never ran reports every day since it was saved (got {(m5.Count > 0 ? m5[0].Count : 0)})");

    var xml = TaskSchedulerService.BuildMissedCheckXml(@"C:\Program Files\DiskImager\DiskImager.exe");
    try
    {
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var ns = doc.Root!.Name.Namespace;
        Check(doc.Descendants(ns + "BootTrigger").Any() && doc.Descendants(ns + "EventTrigger").Any() && doc.Descendants(ns + "CalendarTrigger").Any()
              && doc.Descendants(ns + "Subscription").First().Value.Contains("Power-Troubleshooter"),
            "missed-check task: startup, wake-from-sleep and daily triggers");
    }
    catch (Exception ex) { Check(false, "missed-check task XML parses: " + ex.Message); }
}

// ================================================================ USB stick with no partition table ("superfloppy")
{
    long sfSize = 16 * MiB;
    string sfPath = Path.Combine(work, "superfloppy.disk");
    var sfData = new byte[sfSize];
    rnd.NextBytes(sfData);
    sfData[510] = 0x55; sfData[511] = 0xAA; // a FAT boot sector at sector 0, no partition table
    File.WriteAllBytes(sfPath, sfData);
    var sf = new DiskInfo
    {
        Number = 907, Path = sfPath, Size = sfSize, SectorSize = SS, Style = DiskStyle.MBR, Model = "USB stick",
        Partitions = new() { new PartitionInfo { Number = 1, Offset = 0, Length = sfSize, TypeName = "FAT32" } },
    };
    var sfDesc = SourceDescription.FromDisk(sf);
    Check(sfDesc.ResizeBlockedReason?.Contains("no partition table") == true, "stick with no partition table: resizing is turned off, copy as-is");

    var sfImg = BackupEngine.Run(new BackupRequest { Disk = sf, DestinationFolder = Path.Combine(work, "sf-images"), JobName = "Stick", UseVss = false },
        null, CancellationToken.None, log);
    string sfTarget = MakeTarget("superfloppy-restore.disk", 32 * MiB);
    var sfPlan = Layout.Original(SourceDescription.FromDisk(sf), 32 * MiB, SS);
    Layout.AutoFit(sfPlan);
    using (var r = new ImageReader(sfImg.ImagePath))
        DiskCopy.Execute(r, sfPlan, MakeDisk(908, sfTarget, 32 * MiB), false, null, "Restoring", null, CancellationToken.None, log);
    Check(sfPlan.IsIdentity && SameAt(sfPath, 0, sfTarget, 0, sfSize), "stick with no partition table: restored byte-for-byte onto a larger stick");
}

// ---- Background-run status shown in the window.
using (var pub = new BackgroundStatus.Publisher("Smoke status test"))
{
    pub.SetWaiting(true);
    var seen = BackgroundStatus.ReadAll().FirstOrDefault(s => s.JobName == "Smoke status test");
    Check(seen != null && seen.Waiting, "background status: a waiting scheduled run is visible to the window");
    pub.SetWaiting(false);
    pub.Report(new ProgressInfo("Creating full image", 50, 200, 10));
    Thread.Sleep(1100);
    pub.Report(new ProgressInfo("Creating full image", 100, 200, 10));
    seen = BackgroundStatus.ReadAll().FirstOrDefault(s => s.JobName == "Smoke status test");
    Check(seen != null && !seen.Waiting && seen.Done == 100 && seen.Total == 200, "background status: progress of a running scheduled backup is visible");
}
Check(!BackgroundStatus.ReadAll().Any(s => s.JobName == "Smoke status test"), "background status: cleared when the run ends");

// ---- Skipping pagefile.sys / hiberfil.sys / swapfile.sys
{
    const int bs = 1 << 20; const long cl = 4096;
    var used = Enumerable.Repeat(true, 100).ToArray();
    // A 20 MB file starting at volume cluster 2560 (10 MB into the volume), volume at 1 MB on the disk.
    var runs = new List<(long, long, long)> { (0, 2560, 20 * 256) };
    long skipped = LiveDiskSource.UnmarkExtents(used, 1L << 20, cl, bs, runs, LiveDiskSource.KeepStartOfSkippedFile);
    // File occupies disk bytes 11 MB..31 MB; first 1 MB kept → blocks 12..30 skipped (19 blocks).
    Check(skipped == 19L * bs && used[11] && !used[12] && !used[30] && used[31], $"page/hibernation file blocks are skipped, first 1 MB kept (skipped {Util.FormatBytes(skipped)})");

    var used2 = Enumerable.Repeat(true, 10).ToArray();
    var unaligned = new List<(long, long, long)> { (0, 100, 300), (300, -1, 50) }; // partial blocks + a sparse run
    LiveDiskSource.UnmarkExtents(used2, 0, cl, bs, unaligned, 0);
    Check(used2.All(b => b), "blocks only partly covered by the file (or sparse runs) are never skipped");

    try
    {
        var ext = LiveDiskSource.FileExtents(@"C:\pagefile.sys");
        long size = ext.Where(e => e.Lcn >= 0).Sum(e => e.Clusters) * 4096;
        Console.WriteLine($"INFO  this PC's C:\\pagefile.sys: {ext.Count} run(s), about {Util.FormatBytes(size)} (assuming 4 KB clusters)");
    }
    catch (Exception ex) { Console.WriteLine("INFO  couldn't read C:\\pagefile.sys layout without admin: " + ex.Message); }
}

// ---- Verify image when complete
{
    string vDir = Path.Combine(work, "verify-images");
    BackupResult VBackup(bool incr) => BackupEngine.Run(new BackupRequest { Disk = src, DestinationFolder = vDir, JobName = "Verify", Incremental = incr }, null, CancellationToken.None, log);

    var vFull = VBackup(false);
    BackupEngine.VerifyNewImage(vFull, null, CancellationToken.None, log);
    Check(vFull.Verified, "verify after backup: a good full image passes");

    Thread.Sleep(1100);
    using (var fs = new FileStream(srcPath, FileMode.Open)) { var b = new byte[MiB]; rnd.NextBytes(b); fs.Position = p2.Offset + 2 * MiB; fs.Write(b); }
    var vIncr = VBackup(true);
    long ownBlocks;
    using (var r = new ImageReader(vIncr.ImagePath)) ownBlocks = r.Index.Count(e => e.Ordinal == r.Header.ChainOrdinal);
    BackupEngine.VerifyNewImage(vIncr, null, CancellationToken.None, log);
    Check(!vIncr.WasFull && vIncr.Verified && ownBlocks < 5, $"verify after backup: incremental checks only its own {ownBlocks} block(s)");

    // Damage one data block in the incremental, as a failing NAS disk might.
    using (var r = new ImageReader(vIncr.ImagePath))
    {
        var e = r.Index.First(x => x.Ordinal == r.Header.ChainOrdinal && x.Flags != BlockFlags.Zero);
        r.Dispose();
        using var fs = new FileStream(vIncr.ImagePath, FileMode.Open);
        fs.Position = e.Offset + e.Length / 2;
        int old = fs.ReadByte(); fs.Position--; fs.WriteByte((byte)(old ^ 0xFF));
    }
    try
    {
        BackupEngine.VerifyNewImage(vIncr, null, CancellationToken.None, log);
        Check(false, "verify after backup: damage is detected");
    }
    catch (InvalidDataException ex) { Check(ex.Message.Contains("damaged block"), "verify after backup: damage is detected and reported"); }
    Check(ImageCatalog.Scan(vDir).Single(i => i.Path == vIncr.ImagePath).Damaged, "damaged image is flagged (shows as Damaged on Restore)");
    Thread.Sleep(1100);
    var next = VBackup(true);
    Check(next.WasFull, "after a failed verify, the next run starts a fresh full backup");
}

// ---- Free-space check before a backup
{
    string fDir = Path.Combine(work, "space-images");
    BackupResult FBackup(bool incr) => BackupEngine.Run(new BackupRequest { Disk = src, DestinationFolder = fDir, JobName = "Space", Incremental = incr }, null, CancellationToken.None, log);
    var realProvider = BackupEngine.FreeSpaceProvider;
    try
    {
        BackupEngine.FreeSpaceProvider = _ => 5L << 20; // 5 MB free
        try { FBackup(false); Check(false, "not enough space stops the backup"); }
        catch (IOException ex)
        {
            Check(ex.Message.Contains("Not enough space") && !Directory.EnumerateFiles(fDir).Any(),
                "not enough space: stopped before writing anything, with a clear message");
        }

        BackupEngine.FreeSpaceProvider = realProvider;
        var first = FBackup(false);
        Check(first.SpaceWarning == null, "plenty of space: no warning");

        // The estimate for the next full is based on this one's real size.
        var images = ImageCatalog.Scan(fDir);
        long usedNow = images[0].Header.UsedBytes;
        long est = BackupEngine.EstimateSize(true, usedNow, null, images);
        Check(est >= first.BytesWritten && est < first.BytesWritten * 1.3, $"full-backup estimate follows the previous full ({Util.FormatBytes(est)} vs {Util.FormatBytes(first.BytesWritten)})");

        BackupEngine.FreeSpaceProvider = _ => (long)(est * 1.1); // enough, but tight
        Thread.Sleep(1100);
        var tight = FBackup(false);
        Check(tight.SpaceWarning?.Contains("tight") == true, "tight space: backup runs but warns");
        Check(Notifier.Compose(new BackupJob { Name = "Space" }, false, tight, null, DateTime.Now, log).Text.Contains("Space is tight"),
            "tight-space warning appears in the email");
    }
    finally { BackupEngine.FreeSpaceProvider = realProvider; }
}

// ---- Clean-up never lets a damaged set push out a good one
{
    string rDir = Path.Combine(work, "retention-damaged");
    BackupResult RBackup() => BackupEngine.Run(new BackupRequest { Disk = src, DestinationFolder = rDir, JobName = "Ret", DeferRetention = true }, null, CancellationToken.None, log);
    var a = RBackup(); Thread.Sleep(1100);
    var b = RBackup(); Thread.Sleep(1100);
    var c = RBackup();
    File.WriteAllText(ImageEntry.DamagedMarker(c.ImagePath), "test");      // newest set failed verification
    ImageCatalog.ApplyRetention(rDir, "Ret", 2, c.ChainId, log);
    Check(File.Exists(a.ImagePath) && File.Exists(b.ImagePath) && File.Exists(c.ImagePath),
        "keep 2 with a damaged newest set: both good older sets are kept");
    Thread.Sleep(1100);
    var d = RBackup();
    ImageCatalog.ApplyRetention(rDir, "Ret", 2, d.ChainId, log);
    Check(!File.Exists(a.ImagePath) && File.Exists(b.ImagePath) && File.Exists(d.ImagePath),
        "once there are 2 newer good sets, the oldest is deleted");
}

// ---- Time box rules (typed digit by digit)
{
    string Type(string keys) { string d = ""; foreach (var c in keys) d = DiskImager.InputBehaviors.AddTimeDigit(d, c); return DiskImager.InputBehaviors.FormatTime(d); }
    Check(Type("1500") == "15:00", "time box: 1500 → 15:00");
    Check(Type("15009") == "15:00", "time box: extra digits are ignored");
    Check(Type("1a5:0x0") == "15:00", "time box: letters and symbols are ignored");
    Check(Type("2500") == "20:0", "time box: hour 25 can't be entered (5 rejected)");
    Check(Type("1570") == "15:0", "time box: minute 7x can't be entered");
    Check(Type("930") == "09:30", "time box: a first digit of 3–9 means 03–09");
    Check(DiskImager.InputBehaviors.CompleteTime("9") == "09:00" && DiskImager.InputBehaviors.CompleteTime("15") == "15:00"
          && DiskImager.InputBehaviors.CompleteTime("15:3") == "15:30" && DiskImager.InputBehaviors.CompleteTime("") == "",
        "time box: partial times are completed when leaving the box");
}

// ---- Keep-awake power request.
using (var awake = new KeepAwake("DiskImager smoke test", log))
    Check(awake.Active, "Windows accepted the stay-awake request");

// ---- Password protection round-trip.
Check(JobStore.Unprotect(JobStore.Protect("s3cret!")) == "s3cret!", "NAS password encrypt/decrypt");

// ---- Exclude folders: rules, matching and emptying (the disk-level part runs in the server test).
{
    Check(Exclusions.Validate(@"C:\Users\*\Downloads") == null && Exclusions.Validate(@"D:\Data\Cache\") == null, "exclude: normal folder patterns accepted");
    Check(Exclusions.Validate(@"C:\") != null && Exclusions.Validate(@"C:\Windows") != null && Exclusions.Validate(@"c:\users") != null
          && Exclusions.Validate(@"C:\*") != null && Exclusions.Validate(@"Downloads") != null && Exclusions.Validate(@"C:\a\..\Windows") != null,
        "exclude: drive roots, system folders, relative and .. paths refused");

    string ex = Path.Combine(work, "exclude");
    foreach (var u in new[] { "amy", "bob", "Public" }) Directory.CreateDirectory(Path.Combine(ex, "Users", u, "Downloads", "sub"));
    Directory.Delete(Path.Combine(ex, "Users", "Public", "Downloads"), true);
    File.WriteAllText(Path.Combine(ex, "Users", "amy", "Downloads", "a.txt"), "a");
    File.WriteAllText(Path.Combine(ex, "Users", "amy", "Downloads", "sub", "b.txt"), "b");
    File.SetAttributes(Path.Combine(ex, "Users", "amy", "Downloads", "a.txt"), FileAttributes.ReadOnly | FileAttributes.Hidden);
    // A junction inside an excluded folder must not lead outside it.
    string outside = Path.Combine(ex, "Keep");
    Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
    var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
        $"/c mklink /J \"{Path.Combine(ex, "Users", "bob", "Downloads", "link")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
    mk.WaitForExit();

    var found = Exclusions.Expand(ex, @"\Users\*\Downloads");
    Check(found.Count == 2 && found.Contains(@"\Users\amy\Downloads") && found.Contains(@"\Users\bob\Downloads"),
        $"exclude: * matches each user's folder ({string.Join(", ", found)})");
    Check(Exclusions.Expand(ex, @"\Users\amy\Downloads").Count == 1 && Exclusions.Expand(ex, @"\Users\zed\Downloads").Count == 0,
        "exclude: exact folders found, missing ones ignored");
    var files = Exclusions.FilesBelow(Path.Combine(ex, "Users", "bob", "Downloads")).ToList();
    Check(files.Count == 0, "exclude: files behind a junction are not counted as excluded");
    Check(Exclusions.FilesBelow(Path.Combine(ex, "Users", "amy", "Downloads")).Count() == 2, "exclude: hidden, read-only and nested files included");

    int removed = Exclusions.EmptyFolder(Path.Combine(ex, "Users", "amy", "Downloads"), log)
                + Exclusions.EmptyFolder(Path.Combine(ex, "Users", "bob", "Downloads"), log);
    Check(removed == 2 && Directory.Exists(Path.Combine(ex, "Users", "amy", "Downloads"))
          && !Directory.EnumerateFileSystemEntries(Path.Combine(ex, "Users", "amy", "Downloads")).Any()
          && !Directory.EnumerateFileSystemEntries(Path.Combine(ex, "Users", "bob", "Downloads")).Any(),
        "exclude: restored folders emptied but kept");
    Check(File.Exists(Path.Combine(outside, "precious.txt")), "exclude: emptying never follows a junction out of the folder");
}

// ---- Encryption: nothing readable on disk, wrong/missing password, incrementals, restore, tampering.
{
    bool FileContains(string path, byte[] needle) => File.ReadAllBytes(path).AsSpan().IndexOf(needle) >= 0;
    var marker = Encoding.ASCII.GetBytes("TOP-SECRET-CLIENT-DATA-0123456789 ");
    var secret = new byte[MiB];
    for (int i = 0; i + marker.Length <= secret.Length; i += marker.Length) marker.CopyTo(secret, i);
    using (var fs = new FileStream(srcPath, FileMode.Open)) { fs.Position = p2.Offset + 30 * MiB; fs.Write(secret); }

    string eDir = Path.Combine(work, "enc"), plainDir = Path.Combine(work, "enc-plain");
    const string pw = "correct horse battery";
    BackupResult EBackup(string dir, bool incr, string? password) => BackupEngine.Run(new BackupRequest
    {
        Disk = src, DestinationFolder = dir, JobName = "Enc", Incremental = incr, Compression = CompressionChoice.None, EncryptionPassword = password,
    }, null, CancellationToken.None, log);

    var plainImg = EBackup(plainDir, false, null);
    var ef = EBackup(eDir, false, pw);
    var efHeader = ImageFormat.ReadHeader(ef.ImagePath);
    Check(FileContains(plainImg.ImagePath, marker), "encryption: (control) an unencrypted image holds the data as-is");
    Check(efHeader.IsEncrypted && !FileContains(ef.ImagePath, marker), "encryption: the encrypted image holds nothing readable");
    Check(!FileContains(ef.ImagePath, Encoding.UTF8.GetBytes(pw)), "encryption: the password itself isn't stored in the image");

    using (var r = new ImageReader(ef.ImagePath))
    {
        bool locked = false, wrong = false;
        try { r.Verify(null, CancellationToken.None, log); } catch (ImageLockedException) { locked = true; }
        try { r.Unlock("not the password"); } catch (WrongPasswordException) { wrong = true; }
        Check(locked && wrong, "encryption: can't be read without the password; a wrong password is refused");
        long secretBlock = (p2.Offset + 30 * MiB) / MiB;
        var e = r.Index.First(x => x.BlockIndex == secretBlock);
        Check(!e.Hash.AsSpan().SequenceEqual(ImageFormat.Hash(secret, secret.Length)), "encryption: block checksums don't reveal the data");
        r.Unlock(pw);
        Check(r.Verify(null, CancellationToken.None, log) == 0, "encryption: verifies with the right password");
    }

    using (var fs = new FileStream(srcPath, FileMode.Open)) { var chg = new byte[2 * MiB]; rnd.NextBytes(chg); fs.Position = p1.Offset + MiB; fs.Write(chg); }
    Thread.Sleep(1100);
    var ei = EBackup(eDir, true, pw);
    Check(!ei.WasFull && ei.BytesWritten < ef.BytesWritten / 2, $"encryption: incremental with the same password stays small ({Util.FormatBytes(ei.BytesWritten)})");
    BackupEngine.VerifyNewImage(ei, null, CancellationToken.None, log, pw);
    Check(ei.Verified, "encryption: verify image when complete works with the job's password");

    string et = MakeTarget("restore-encrypted.disk", srcSize);
    using (var r = new ImageReader(ei.ImagePath))
    {
        r.Unlock(pw);
        DiskWriterEngine.Write(r, MakeDisk(950, et, srcSize), false, "Restoring", null, CancellationToken.None, log);
    }
    Check(SameRange(srcPath, et, p1.Offset, p1.Length) && SameRange(srcPath, et, p2.Offset, p2.Length),
        "encryption: restoring the encrypted incremental gives back the source byte-for-byte");

    Thread.Sleep(1100);
    var changed = EBackup(eDir, true, "a different password");
    Thread.Sleep(1100);
    var off = EBackup(eDir, true, null);
    Check(changed.WasFull && ImageFormat.ReadHeader(changed.ImagePath).IsEncrypted, "encryption: a changed password starts a new full backup");
    Check(off.WasFull && !ImageFormat.ReadHeader(off.ImagePath).IsEncrypted, "encryption: turning it off starts a new full backup");

    // Tampering: change one byte inside the stored data.
    using (var fs = new FileStream(ef.ImagePath, FileMode.Open))
    {
        fs.Position = fs.Length / 2;
        int b = fs.ReadByte();
        fs.Position = fs.Length / 2;
        fs.WriteByte((byte)(b ^ 0x40));
    }
    using (var r = new ImageReader(ef.ImagePath))
    {
        r.Unlock(pw);
        Check(r.Verify(null, CancellationToken.None, log) > 0, "encryption: a single changed byte is detected");
    }
}

// ---- Weekly report: schedule gaps, PC health, grouping by client, the email itself.
{
    Check(WeeklyReport.StatusFolder(@"\\NAS\Backups\PC1") == @"\\NAS\Backups\_DiskImagerStatus"
          && WeeklyReport.StatusFolder(@"E:\Backups\Office\PC2") == @"E:\_DiskImagerStatus", "report: status notes go to the share or drive root");
    Check(WeeklyReport.Interval(new Schedule { Kind = ScheduleKind.Daily }) == 24
          && WeeklyReport.Interval(new Schedule { Kind = ScheduleKind.Weekly, Days = { DayOfWeek.Friday } }) == 168
          && WeeklyReport.Interval(new Schedule { Kind = ScheduleKind.Weekly, Days = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday } }) == 72,
        "report: expected gaps (daily 24 h, weekly 7 days, weekdays 3 days over the weekend)");

    var now = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
    string folder = Path.Combine(work, "report", WeeklyReport.FolderName);
    Directory.CreateDirectory(folder);
    PcStatus Pc(string name, string client, string location, DateTime noteAt, DateTime? lastOk, string? result, double every = 24) => new()
    {
        MachineId = Guid.NewGuid(), ComputerName = name, ClientName = client, Location = location, LastUser = "john.tan", UpdatedUtc = noteAt,
        Jobs = { new JobStatus { Name = "Nightly", LastSuccessUtc = lastOk, LastRunUtc = noteAt, LastResult = result, ExpectedEveryHours = every, LastImageBytes = 60L << 30 } },
    };
    var pcs = new[]
    {
        Pc("ACCOUNTS-01", "ACME Pte Ltd", "Accounts", now.AddHours(-2), now.AddHours(-2), "OK – Incremental, 3 GB"),
        Pc("MANAGER-PC", "ACME Pte Ltd", "", now.AddHours(-20), now.AddHours(-20), "OK – Full, 60 GB"),
        Pc("RECEPTION-PC", "ACME Pte Ltd", "Front desk", now.AddDays(-1), now.AddDays(-9), "OK – Full, 50 GB"),
        Pc("FINANCE-02", "ACME Pte Ltd", "", now.AddHours(-3), now.AddDays(-1), "FAILED – Could not connect to \\\\NAS\\Backups"),
        Pc("DESIGN-03", "ACME Pte Ltd", "", now.AddDays(-6), now.AddDays(-6), "OK – Incremental, 1 GB"),
        Pc("SERVER-01", "Kim and Partners", "Server room", now.AddHours(-1), now.AddHours(-1), "OK – Incremental, 9 GB"),
    };
    foreach (var p in pcs) File.WriteAllText(Path.Combine(folder, p.MachineId + ".json"), System.Text.Json.JsonSerializer.Serialize(p, JobStore.Json));
    File.WriteAllText(Path.Combine(folder, "garbage.json"), "{ not json");

    Check(WeeklyReport.Health(pcs[0], now) == PcHealth.Ok && WeeklyReport.Health(pcs[2], now) == PcHealth.Late
          && WeeklyReport.Health(pcs[3], now) == PcHealth.Failing && WeeklyReport.Health(pcs[4], now) == PcHealth.Silent,
        "report: OK, late (9 days), failed and silent (no note for 6 days) told apart");
    Check(WeeklyReport.Health(Pc("WEEKLY", "x", "", now.AddDays(-5), now.AddDays(-5), "OK", every: 168), now) == PcHealth.Ok,
        "report: a weekly PC last seen 5 days ago is still OK");

    var reports = WeeklyReport.Build(new[] { folder }, now);
    var acme = reports.FirstOrDefault(r => r.Client == "ACME Pte Ltd");
    Check(reports.Count == 2 && acme?.Pcs.Count == 5, $"report: one report per client ({reports.Count} reports; ACME has {acme?.Pcs.Count} PCs)");
    Check(acme != null && acme.Subject == "[ACME Pte Ltd] Weekly backup report: 2 of 5 PCs OK", $"report: subject names the client and counts ({acme?.Subject})");
    Check(acme != null && acme.Html.Contains("RECEPTION-PC hasn&#39;t backed up since") && acme.Html.Contains("DESIGN-03 hasn&#39;t reported in since")
          && acme.Html.Contains("Could not connect to") && acme.Html.Contains("Front desk &#183; used by john.tan") && acme.Html.Contains("9 days"),
        "report: problems explained in plain words, with location and signed-in user");
    Check(acme != null && !acme.Html.Contains("SERVER-01") && acme.Text.Contains("RECEPTION-PC"), "report: other clients' PCs aren't included; plain-text version too");
    File.WriteAllText(Path.Combine(work, "weekly-report-sample.html"), acme?.Html ?? "");

    // ---- Shared sending: several PCs may send; the first one on after the due time does, the others skip.
    {
        var sun = new DateTime(2026, 10, 18, 8, 0, 0); // a Sunday, local time
        Check(WeeklyReport.LastDue(DayOfWeek.Sunday, "08:00", sun.AddHours(26)) == sun
              && WeeklyReport.LastDue(DayOfWeek.Sunday, "08:00", sun.AddMinutes(-1)) == sun.AddDays(-7)
              && WeeklyReport.LastDue(DayOfWeek.Sunday, "08:00", sun) == sun,
            "shared report: due time is the latest Sunday 08:00 at or before now");

        string shared = Path.Combine(work, "report-shared", WeeklyReport.FolderName);
        Directory.CreateDirectory(shared);
        foreach (var p in pcs) File.WriteAllText(Path.Combine(shared, p.MachineId + ".json"), System.Text.Json.JsonSerializer.Serialize(p, JobStore.Json));
        var email = new EmailSettings { Enabled = true, Host = "test", To = "x@y" };
        var sentBy = new List<string>();
        ReportSettings Sender() => new() { SendWeekly = true, Day = DayOfWeek.Sunday, Time = "08:00", MachineId = Guid.NewGuid(),
            SendingSinceUtc = sun.AddDays(-2).ToUniversalTime() }; // saved on the Friday before
        string Run(string name, ReportSettings r, DateTime local, bool fail = false) =>
            WeeklyReport.SendScheduled(log, local.ToUniversalTime(), r, email, _ => { }, _ => new List<string> { shared }, WeeklyReport.Build,
                (_, reps) => { if (fail) throw new IOException("SMTP server unreachable"); sentBy.Add(name); });
        bool NoClaimLeft() => !File.Exists(Path.Combine(shared, "_report-sending.lock"));

        var a = Sender(); var b = Sender(); var c = Sender();
        Check(Run("A", a, sun.AddMinutes(-30)) == "Not due yet." && sentBy.Count == 0, "shared report: nothing before the due time");
        string ra = Run("A", a, sun.AddMinutes(17));
        Check(sentBy.SequenceEqual(new[] { "A" }) && ra.StartsWith("Sent") && NoClaimLeft(), $"shared report: the first PC on after the due time sends it ({ra})");
        string rb = Run("B", b, sun.AddHours(3));
        Check(sentBy.Count == 1 && rb.Contains("already sent by"), $"shared report: a second PC skips it ({rb})");
        Check(Run("A", a, sun.AddHours(4)).Contains("already gone out") && sentBy.Count == 1, "shared report: hourly checks after that stay quiet");
        Check(Run("B", b, sun.AddHours(5)).Contains("already gone out"), "shared report: …on every PC");

        // Next week A is off: B is first on, on Tuesday.
        string rb2 = Run("B", b, sun.AddDays(9));
        Check(sentBy.SequenceEqual(new[] { "A", "B" }), $"shared report: next week, the PC that's on sends it, even days late ({rb2})");
        Check(Run("A", a, sun.AddDays(9).AddHours(2)).Contains("already sent by") && sentBy.Count == 2, "shared report: the PC that was off doesn't send it again");

        // Week 3: A starts sending and its claim is in place; B must wait, then take over once the claim is stale.
        var wk3 = sun.AddDays(14).AddMinutes(5);
        Check(WeeklyReport.TryClaim(shared, sun.AddDays(14).ToUniversalTime(), wk3.ToUniversalTime()) == WeeklyReport.Claim.Claimed, "shared report: claim taken");
        string waiting = Run("B", b, wk3.AddMinutes(1));
        Check(waiting.Contains("right now") && sentBy.Count == 2, $"shared report: while another PC is sending, this one waits ({waiting})");
        File.SetLastWriteTimeUtc(Path.Combine(shared, "_report-sending.lock"), wk3.ToUniversalTime().AddHours(-2));
        string takeover = Run("B", b, wk3.AddHours(1));
        Check(sentBy.Count == 3 && sentBy[2] == "B" && NoClaimLeft(), $"shared report: a claim left by a PC that died mid-send is taken over ({takeover})");

        // Week 4: the first PC can't reach the mail server; the next PC on sends it.
        var wk4 = sun.AddDays(21).AddMinutes(20);
        string failed = Run("A", a, wk4, fail: true);
        Check(failed.StartsWith("Couldn't send") && sentBy.Count == 3 && NoClaimLeft() && a.LastHandledDueUtc < sun.AddDays(21).ToUniversalTime(),
            "shared report: a failed send releases the claim and isn't counted as sent");
        Run("C", c, wk4.AddHours(1));
        Check(sentBy.Count == 4 && sentBy[3] == "C", "shared report: another PC sends it after a failure");
        Run("A", a, wk4.AddHours(2));
        Check(sentBy.Count == 4, "shared report: …and the PC that failed doesn't send a second copy later");

        // Two PCs claiming at the same instant: exactly one wins.
        int raceWins = 0;
        for (int round = 0; round < 20; round++)
        {
            var due = sun.AddDays(28 + 7 * round).ToUniversalTime();
            int wins = 0;
            Parallel.For(0, 4, _ => { if (WeeklyReport.TryClaim(shared, due, due.AddMinutes(1)) == WeeklyReport.Claim.Claimed) Interlocked.Increment(ref wins); });
            if (wins == 1) raceWins++;
            WeeklyReport.MarkSent(shared, due.AddMinutes(1), Guid.Empty);
            WeeklyReport.Release(shared);
        }
        Check(raceWins == 20, $"shared report: PCs checking at the same moment never both send ({raceWins}/20 rounds had exactly one winner)");

        // Upgrades and saving.
        var upgraded = new ReportSettings { SendWeekly = true, Day = DayOfWeek.Sunday, Time = "08:00" };
        var savedSince = upgraded.SendingSinceUtc;
        Check(Run("U", upgraded, sun.AddDays(400).AddHours(2)) == "Not due yet." && upgraded.SendingSinceUtc != null,
            "shared report: a PC upgraded from 1.3.0 doesn't send a duplicate of this week's report");
        var justSaved = new ReportSettings { SendWeekly = true, Day = DayOfWeek.Sunday, Time = "08:00", SendingSinceUtc = sun.AddDays(500).AddHours(3).ToUniversalTime() };
        Check(Run("S", justSaved, sun.AddDays(500).AddHours(4)) == "Not due yet.", "shared report: saving the settings after the due time doesn't send a late report");
        Check(Run("N", new ReportSettings { SendWeekly = false }, sun.AddDays(600)).Contains("doesn't send"), "shared report: PCs without the box ticked never send");

        string xml = TaskSchedulerService.BuildReportXml(@"C:\Program Files\DiskImager\DiskImager.exe", DayOfWeek.Sunday, "08:00");
        var doc = System.Xml.Linq.XDocument.Parse(xml.Replace("encoding=\"UTF-16\"", ""));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Check(doc.Descendants(ns + "Sunday").Any() && doc.Descendants(ns + "BootTrigger").Any() && doc.Descendants(ns + "Interval").Single().Value == "PT1H",
            "shared report: task checks at the due time, at startup and hourly");
    }
}

// ---- NTFS reader, file-level restore and change tracking on real NTFS volumes.
NtfsTest.Run(work, log, Check);

// ---- Removed after-backup choices: old jobs set to Sleep/Hibernate still load, as "do nothing".
{
    var loaded = JobStore.Parse("[{\"Name\":\"A\",\"After\":\"Sleep\"},{\"Name\":\"B\",\"After\":\"Hibernate\"},{\"Name\":\"C\",\"After\":\"Shutdown\"},{\"Name\":\"D\",\"After\":\"Restart\"}]");
    Check(loaded.Count == 4, "jobs saved with Sleep/Hibernate by older versions still load");
    Check(loaded.Select(j => j.After).SequenceEqual(new[] { PostAction.None, PostAction.None, PostAction.Shutdown, PostAction.Restart }),
        "Sleep/Hibernate become 'do nothing'; Shut down and Restart are kept");
}

// ---- Disk health: parsing, Good/Caution/Bad, alert limits, scheduled task, email.
HealthTest.Run(work, Check);

// ---- Auto-update: version tags, signatures, and a fake GitHub serving releases.
await UpdaterTest.Run(work, log, Check);

Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
