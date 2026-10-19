namespace DiskImager.Core;

/// <summary>
/// Command-line use for scripts and IT admins (runs without a window; needs admin rights):
///   DiskImager.exe --cli verify  "&lt;image.dimg&gt;"
///   DiskImager.exe --cli restore "&lt;image.dimg&gt;" &lt;disk number&gt; [--layout auto|keep] [--new-ids]
/// Encrypted backups also need --password "&lt;password&gt;".
/// Results go to the DiskImager log and, with --out "&lt;file&gt;", to a text file. Exit code 0 = OK, 1 = failed.
/// A restore refuses the disk Windows runs from and the disk holding the image.
/// </summary>
public static class Cli
{
    public static int Run(string[] args)
    {
        var log = new Logger();
        int o = Array.IndexOf(args, "--out");
        string? outFile = o >= 0 && o + 1 < args.Length ? args[o + 1] : null;
        void Say(string m)
        {
            log.Info("CLI: " + m);
            if (outFile != null) try { File.AppendAllText(outFile, m + Environment.NewLine); } catch { }
        }

        int pw = Array.IndexOf(args, "--password");
        string? password = pw >= 0 && pw + 1 < args.Length ? args[pw + 1] : null;
        void Unlock(ImageReader r)
        {
            if (!r.IsEncrypted) return;
            if (password == null) throw new InvalidOperationException("This backup is encrypted: add --password \"<password>\".");
            r.Unlock(password);
        }

        var a = args.SkipWhile(x => x != "--cli").Skip(1).ToList();
        try
        {
            if (a.Count >= 2 && a[0] == "verify")
            {
                using var reader = new ImageReader(a[1]);
                Unlock(reader);
                var missing = reader.MissingChainFiles();
                if (missing.Count > 0) throw new InvalidOperationException(missing[0]);
                int bad = reader.Verify(null, CancellationToken.None, log);
                Say(bad == 0 ? $"VERIFY OK {a[1]}" : $"VERIFY FAILED {a[1]}: {bad} damaged block(s)");
                return bad == 0 ? 0 : 1;
            }

            if (a.Count >= 3 && a[0] == "restore" && int.TryParse(a[2], out int diskNumber))
            {
                string image = a[1];
                bool keep = a.Contains("keep");
                var target = DiskEnumerator.GetDisks().FirstOrDefault(d => d.Number == diskNumber)
                             ?? throw new InvalidOperationException($"Disk {diskNumber} not found.");
                if (target.IsSystem) throw new InvalidOperationException("Refusing to overwrite the disk Windows is running from.");
                if (DiskEnumerator.DiskNumberOfPath(Path.GetFullPath(image)) == diskNumber)
                    throw new InvalidOperationException("The image is stored on the target disk.");

                using var reader = new ImageReader(image);
                Unlock(reader);
                var missing = reader.MissingChainFiles();
                if (missing.Count > 0) throw new InvalidOperationException(missing[0]);
                var plan = Layout.Original(SourceDescription.FromImage(reader), target.Size, target.SectorSize);
                if (!keep) Layout.AutoFit(plan);
                string? problem = plan.Validate();
                if (problem != null) throw new InvalidOperationException(problem);
                if (plan.NeedsShrink)
                    throw new InvalidOperationException("This restore needs partitions shrunk, which needs a working folder; use the DiskImager window.");

                Say($"RESTORE START {image} -> Disk {diskNumber} ({Util.FormatBytes(target.Size)}), layout {(keep ? "keep" : "auto")}");
                DiskCopy.Execute(reader, plan, target, a.Contains("--new-ids"), null, "Restoring", null, CancellationToken.None, log);
                foreach (var p in plan.Parts)
                    Say($"PARTITION {p.Source.Name}: {p.Source.Length} -> {p.TargetLength} bytes at {p.TargetOffset}");
                Say("RESTORE OK");
                return 0;
            }

            Say("Usage: --cli verify <image> | --cli restore <image> <disk number> [--layout auto|keep] [--new-ids] [--password <pw>] [--out <file>]");
            return 1;
        }
        catch (Exception ex)
        {
            Say("FAILED: " + ex.Message);
            log.Error("CLI failed: " + ex);
            return 1;
        }
    }
}
