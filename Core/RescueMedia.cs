using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskImager.Core;

/// <summary>
/// Builds the rescue USB stick (or ISO): the Windows Recovery Environment that's already on this PC (WinRE,
/// a small Windows PE), with DiskImager added and set to start in rescue mode, plus this PC's own network and
/// storage drivers. Boots on UEFI PCs (Secure Boot included: only Microsoft's signed boot files are used) and,
/// from a USB stick, on older BIOS PCs too. Needs administrator rights (it uses DISM and bcdedit).
/// </summary>
public static class RescueMedia
{
    public const string Label = "DIRESCUE";

    /// <summary>Where the recovery image comes from: the recovery partition (via reagentc), or the copy Windows keeps when WinRE is off.</summary>
    public static string? FindWinRe(Logger log)
    {
        try
        {
            var (_, info) = Run("reagentc.exe", "/info", TimeSpan.FromMinutes(1));
            var m = Regex.Match(info, @"(\\\\\?\\GLOBALROOT\\device\\[^\r\n]+?WindowsRE)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string wim = Path.Combine(m.Groups[1].Value.Trim(), "Winre.wim");
                if (File.Exists(wim)) return wim;
            }
        }
        catch (Exception ex) { log.Info("reagentc: " + ex.Message); }
        string local = Path.Combine(Environment.SystemDirectory, "Recovery", "Winre.wim");
        return File.Exists(local) ? local : null;
    }

    public sealed class Options
    {
        /// <summary>USB disk to erase and write (null when making an ISO).</summary>
        public DiskInfo? UsbDisk { get; init; }
        /// <summary>ISO file to create (null when writing a USB stick).</summary>
        public string? IsoPath { get; init; }
        public bool AddDrivers { get; init; } = true;
        /// <summary>Use this boot.wim / Winre.wim instead of looking for WinRE (e.g. from Windows install media).</summary>
        public string? SourceWim { get; init; }
        /// <summary>Tests: a JSON file copied to the media root as rescue-autotest.json (runs a restore unattended).</summary>
        public string? AutotestFile { get; init; }
    }

    public static void Build(Options o, IProgress<ProgressInfo>? progress, CancellationToken ct, Logger log)
    {
        if (o.UsbDisk == null && o.IsoPath == null) throw new ArgumentException("Choose a USB stick or an ISO file.");
        void Step(string s) { log.Info("Rescue media: " + s); progress?.Report(new ProgressInfo(s, 0, 0, 0)); }

        string work = Path.Combine(JobStore.DataFolder, "RescueBuild");
        string mount = Path.Combine(work, "mount"), media = Path.Combine(work, "media"), drivers = Path.Combine(work, "drivers");
        CleanupMount(mount, log);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(mount);
        Directory.CreateDirectory(media);

        Step("Finding the Windows Recovery Environment…");
        string source = o.SourceWim ?? FindWinRe(log)
            ?? throw new InvalidOperationException("This PC has no Windows Recovery Environment (Winre.wim) to build from. Choose a Windows install ISO's sources\\boot.wim instead.");
        string wim = Path.Combine(media, "sources", "boot.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(wim)!);
        File.Copy(source, wim);
        File.SetAttributes(wim, FileAttributes.Normal);
        ct.ThrowIfCancellationRequested();

        Step("Opening the recovery image…");
        Dism($"/Mount-Image /ImageFile:\"{wim}\" /Index:1 /MountDir:\"{mount}\"");
        bool committed = false;
        try
        {
            Step("Adding DiskImager…");
            string appDir = Path.Combine(mount, "DiskImager");
            Directory.CreateDirectory(appDir);
            File.Copy(Environment.ProcessPath!, Path.Combine(appDir, "DiskImager.exe"), overwrite: true);
            // Start networking, then DiskImager; a command prompt if DiskImager is closed; restart after that.
            File.WriteAllText(Path.Combine(mount, "Windows", "System32", "winpeshl.ini"),
                "[LaunchApps]\r\n%SYSTEMROOT%\\System32\\wpeinit.exe\r\nX:\\DiskImager\\DiskImager.exe, --rescue\r\n%SYSTEMROOT%\\System32\\cmd.exe, /k echo DiskImager rescue command prompt. Type DiskImager\\DiskImager.exe --rescue to start it again, or wpeutil reboot.\r\n",
                Encoding.ASCII);

            if (o.AddDrivers)
            {
                Step("Adding this PC's network and storage drivers…");
                int added = ExportDrivers(drivers, log);
                if (added > 0)
                {
                    try { Dism($"/Image:\"{mount}\" /Add-Driver /Driver:\"{drivers}\" /Recurse /ForceUnsigned"); }
                    catch (Exception ex) { log.Warn("Some drivers couldn't be added: " + ex.Message); }
                }
            }
            ct.ThrowIfCancellationRequested();

            Step("Saving the rescue image (a few minutes)…");
            Dism($"/Unmount-Image /MountDir:\"{mount}\" /Commit");
            committed = true;
        }
        finally
        {
            if (!committed) CleanupMount(mount, log);
        }

        Step("Adding boot files…");
        AddBootFiles(media);
        if (o.AutotestFile != null) File.Copy(o.AutotestFile, Path.Combine(media, "rescue-autotest.json"), overwrite: true);

        if (o.UsbDisk != null) WriteUsb(o.UsbDisk, media, Step, log);
        else MakeIso(media, o.IsoPath!, Step);

        try { Directory.Delete(work, true); } catch { }
        Step("Rescue media ready.");
    }

    private static void CleanupMount(string mount, Logger log)
    {
        try
        {
            if (Directory.Exists(mount) && Directory.EnumerateFileSystemEntries(mount).Any())
                Dism($"/Unmount-Image /MountDir:\"{mount}\" /Discard");
        }
        catch (Exception ex) { log.Warn("Couldn't close a previous rescue build: " + ex.Message); }
        try { Run("dism.exe", "/Cleanup-Mountpoints", TimeSpan.FromMinutes(5)); } catch { }
    }

    /// <summary>Copies this PC's third-party network and storage drivers into a folder. Returns how many.</summary>
    private static int ExportDrivers(string folder, Logger log)
    {
        Directory.CreateDirectory(folder);
        var infs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT InfName, DeviceClass FROM Win32_PnPSignedDriver WHERE DeviceClass='NET' OR DeviceClass='SCSIADAPTER' OR DeviceClass='HDC' OR DeviceClass='USB'");
            foreach (System.Management.ManagementObject d in s.Get())
                if (d["InfName"] is string inf && inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase)) infs.Add(inf);
        }
        catch (Exception ex) { log.Warn("Couldn't list drivers: " + ex.Message); }
        int n = 0;
        foreach (var inf in infs)
        {
            string target = Path.Combine(folder, Path.GetFileNameWithoutExtension(inf));
            Directory.CreateDirectory(target);
            var (code, _) = Run("pnputil.exe", $"/export-driver {inf} \"{target}\"", TimeSpan.FromMinutes(2));
            if (code == 0) n++;
        }
        log.Info($"Rescue media: {n} driver package(s) from this PC included.");
        return n;
    }

    /// <summary>UEFI and BIOS boot files, and a boot menu (BCD) that starts boot.wim from a RAM disk.</summary>
    private static void AddBootFiles(string media)
    {
        string boot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Boot");
        void Copy(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
            File.SetAttributes(to, FileAttributes.Normal);
        }
        Copy(Path.Combine(boot, "EFI", "bootmgfw.efi"), Path.Combine(media, "EFI", "Boot", "bootx64.efi"));
        Copy(Path.Combine(boot, "DVD", "EFI", "boot.sdi"), Path.Combine(media, "boot", "boot.sdi"));
        if (Directory.Exists(Path.Combine(boot, "Fonts")))
            foreach (var f in Directory.GetFiles(Path.Combine(boot, "Fonts")))
                Copy(f, Path.Combine(media, "EFI", "Microsoft", "Boot", "Fonts", Path.GetFileName(f)));
        string efisys = Path.Combine(boot, "DVD", "EFI", "en-US", "efisys_noprompt.bin");
        if (File.Exists(efisys)) Copy(efisys, Path.Combine(media, "efi", "microsoft", "boot", "efisys_noprompt.bin"));
        MakeBcd(Path.Combine(media, "EFI", "Microsoft", "Boot", "BCD"), "\\windows\\system32\\boot\\winload.efi");

        string pcat = Path.Combine(boot, "PCAT", "bootmgr");
        if (File.Exists(pcat))
        {
            Copy(pcat, Path.Combine(media, "bootmgr"));
            MakeBcd(Path.Combine(media, "boot", "BCD"), "\\windows\\system32\\boot\\winload.exe");
        }
    }

    private static void MakeBcd(string path, string loader)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);
        Bcd($"/createstore \"{path}\"");
        Bcd($"/store \"{path}\" /create {{bootmgr}} /d \"DiskImager Rescue\"");
        Bcd($"/store \"{path}\" /set {{bootmgr}} timeout 3");
        Bcd($"/store \"{path}\" /create {{ramdiskoptions}} /d \"DiskImager Rescue\"");
        Bcd($"/store \"{path}\" /set {{ramdiskoptions}} ramdisksdidevice boot");
        Bcd($"/store \"{path}\" /set {{ramdiskoptions}} ramdisksdipath \\boot\\boot.sdi");
        string output = Bcd($"/store \"{path}\" /create /d \"DiskImager Rescue\" /application osloader");
        var id = Regex.Match(output, @"\{[0-9a-fA-F\-]{36}\}").Value;
        if (id.Length == 0) throw new InvalidOperationException("bcdedit didn't create the boot entry: " + output);
        foreach (var s in new[]
        {
            $"/set {id} device ramdisk=[boot]\\sources\\boot.wim,{{ramdiskoptions}}",
            $"/set {id} osdevice ramdisk=[boot]\\sources\\boot.wim,{{ramdiskoptions}}",
            $"/set {id} path {loader}",
            $"/set {id} systemroot \\windows",
            $"/set {id} winpe yes",
            $"/set {id} detecthal yes",
            $"/displayorder {id} /addlast",
            $"/set {{bootmgr}} default {id}",
        })
            Bcd($"/store \"{path}\" {s}");
    }

    private static void WriteUsb(DiskInfo disk, string media, Action<string> step, Logger log)
    {
        if (disk.IsSystem) throw new InvalidOperationException("That's the disk Windows is running from.");
        long bytes = Directory.EnumerateFiles(media, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        if (disk.Size < bytes + (64L << 20)) throw new InvalidOperationException($"The USB stick is too small: it needs at least {Util.FormatBytes(bytes + (64L << 20))}.");
        step($"Erasing Disk {disk.Number} and making it bootable…");
        // FAT32 (what UEFI firmware reads) is limited to 32 GB when Windows formats it: use at most 16 GB of a big stick.
        long sizeMb = Math.Min(disk.Size >> 20, 16384) - 2;
        var before = DiskEnumerator.GetVolumes().Select(v => v.GuidPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (code, output) = RunDiskpart(
            $"select disk {disk.Number}\r\nclean\r\nconvert mbr\r\ncreate partition primary size={sizeMb}\r\nformat fs=fat32 quick label={Label}\r\nactive\r\nassign\r\n");
        if (code != 0) throw new InvalidOperationException("Windows couldn't prepare the USB stick: " + output.Trim());

        string? root = null;
        for (int i = 0; i < 30 && root == null; i++)
        {
            root = DiskEnumerator.GetVolumes().FirstOrDefault(v => v.DiskNumber == disk.Number && !before.Contains(v.GuidPath))?.GuidPath
                   ?? DiskEnumerator.GetVolumes().FirstOrDefault(v => v.DiskNumber == disk.Number && v.Label == Label)?.GuidPath;
            if (root == null) Thread.Sleep(1000);
        }
        if (root == null) throw new InvalidOperationException("The USB stick was formatted but Windows didn't make it available.");
        step("Copying the rescue system to the USB stick…");
        foreach (var f in Directory.EnumerateFiles(media, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(media, f);
            string to = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, overwrite: true);
        }
        log.Info($"Rescue USB written to {disk.Title}.");
    }

    // ------------------------------------------------------------------ ISO (Windows' built-in IMAPI2, UEFI boot)

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template,
        [MarshalAs(UnmanagedType.Interface)] out System.Runtime.InteropServices.ComTypes.IStream stream);

    private static void MakeIso(string media, string isoPath, Action<string> step)
    {
        step("Creating the ISO file…");
        string efisys = Path.Combine(media, "efi", "microsoft", "boot", "efisys_noprompt.bin");
        if (!File.Exists(efisys)) throw new InvalidOperationException("This PC is missing Windows' DVD boot files (efisys_noprompt.bin); write a USB stick instead.");
        dynamic fsi = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage")!)!;
        fsi.FileSystemsToCreate = 4 | 1; // UDF + ISO 9660
        fsi.VolumeName = Label;
        fsi.FreeMediaBlocks = 0; // no size limit
        fsi.Root.AddTree(media, false);
        SHCreateStreamOnFileEx(efisys, 0x20 /* read, share deny write */, 0, false, IntPtr.Zero, out var bootStream);
        dynamic boot = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2FS.BootOptions")!)!;
        boot.AssignBootImage(bootStream);
        boot.PlatformId = 0xEF; // UEFI
        boot.Emulation = 0;     // no emulation
        fsi.BootImageOptions = boot;
        dynamic result = fsi.CreateResultImage();
        var image = (System.Runtime.InteropServices.ComTypes.IStream)result.ImageStream;
        using var file = new FileStream(isoPath, FileMode.Create, FileAccess.Write);
        var buf = new byte[1 << 20];
        IntPtr readPtr = Marshal.AllocHGlobal(4);
        try
        {
            while (true)
            {
                image.Read(buf, buf.Length, readPtr);
                int n = Marshal.ReadInt32(readPtr);
                if (n <= 0) break;
                file.Write(buf, 0, n);
            }
        }
        finally { Marshal.FreeHGlobal(readPtr); }
    }

    // ------------------------------------------------------------------ helpers

    private static void Dism(string args)
    {
        var (code, output) = Run("dism.exe", args + " /English", TimeSpan.FromMinutes(30));
        if (code != 0 && code != 3010) throw new InvalidOperationException($"DISM failed ({code}): {Tail(output)}");
    }

    private static string Bcd(string args)
    {
        var (code, output) = Run("bcdedit.exe", args, TimeSpan.FromMinutes(1));
        if (code != 0) throw new InvalidOperationException($"bcdedit {args.Split(' ')[0]} failed: {output.Trim()}");
        return output;
    }

    private static (int Code, string Output) RunDiskpart(string script)
    {
        string file = Path.Combine(Path.GetTempPath(), $"diskimager-rescue-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, script, Encoding.ASCII);
        try { return Run("diskpart.exe", $"/s \"{file}\"", TimeSpan.FromMinutes(10)); }
        finally { File.Delete(file); }
    }

    private static string Tail(string s) => string.Join(" ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));

    private static (int Code, string Output) Run(string exe, string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { try { p.Kill(); } catch { } throw new TimeoutException($"{exe} took too long."); }
        return (p.ExitCode, output + err.Result);
    }
}
