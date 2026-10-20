using System.Buffers.Binary;
using System.Xml.Linq;
using DiskImager;
using DiskImager.Core;

/// <summary>Disk health: NVMe/SATA parsing, the Good/Caution/Bad rules, alert limits, the scheduled task and email.</summary>
static class HealthTest
{
    public static void Run(string work, Action<bool, string> check)
    {
        // ---- NVMe health log
        {
            var d = Nvme(warning: 0, kelvin: 311, spare: 100, spareThr: 10, used: 4, written: 20_000_000, read: 30_000_000, hours: 5123, cycles: 812, mediaErrors: 0);
            check(d.Kind == DriveKind.Nvme && d.Status == HealthStatus.Good, "NVMe: healthy drive is Good");
            check(d.TemperatureC == 38, "NVMe: temperature converted from kelvin (311 K = 38 °C)");
            check(d.LifePercent == 96 && d.StatusText == "Good 96%", "NVMe: 4% used shows as Good 96%");
            check(d.PowerOnHours == 5123 && d.PowerCycles == 812, "NVMe: power-on hours and power cycles");
            check(d.DataWrittenBytes == 20_000_000L * 512000 && d.DataReadBytes == 30_000_000L * 512000, "NVMe: data units are 512,000 bytes");
            check(d.Readings.Count >= 15, "NVMe: readings table filled");

            check(Nvme(used: 92).Status == HealthStatus.Caution, "NVMe: 8% life left is Caution");
            check(Nvme(used: 130).LifePercent == 0, "NVMe: more than 100% used shows 0% life");
            check(Nvme(mediaErrors: 3).Status == HealthStatus.Caution && Nvme(mediaErrors: 3).BadSectors == 3, "NVMe: media errors are Caution");
            check(Nvme(warning: 0x02).Status == HealthStatus.Caution, "NVMe: over-temperature warning is Caution");
            check(Nvme(warning: 0x04).Status == HealthStatus.Bad, "NVMe: reliability degraded is Bad");
            check(Nvme(warning: 0x08).Status == HealthStatus.Bad, "NVMe: read-only mode is Bad");
            check(Nvme(spare: 5, spareThr: 10).Status == HealthStatus.Bad, "NVMe: spare below threshold is Bad");
            check(Nvme(warning: 0x04).Reasons.Count > 0, "NVMe: Bad comes with a reason");
        }

        // ---- SATA SMART
        {
            var ssd = Ata(new() { [9] = (100, 4321), [12] = (99, 77), [194] = (64, 36), [5] = (100, 0), [197] = (100, 0), [198] = (100, 0), [177] = (93, 120) },
                new() { [5] = 10, [177] = 0 }, ssd: true);
            check(ssd.Kind == DriveKind.Ssd && ssd.Status == HealthStatus.Good, "SATA SSD: healthy drive is Good");
            check(ssd.LifePercent == 93 && ssd.StatusText == "Good 93%", "SATA SSD: life from the wear attribute (Samsung 177)");
            check(ssd.TemperatureC == 36 && ssd.PowerOnHours == 4321 && ssd.PowerCycles == 77, "SATA: temperature, hours and cycles from raw values");
            check(ssd.Attributes.Count == 7 && ssd.Attributes.First(a => a.Id == 5).Threshold == 10, "SATA: attributes with thresholds");
            check(ssd.BadSectors == 0, "SATA: no bad sectors");

            var hdd = Ata(new() { [9] = (90, 20000), [194] = (110, 41), [5] = (100, 0) }, new() { [5] = 36 }, ssd: false);
            check(hdd.Kind == DriveKind.Hdd && hdd.LifePercent == null && hdd.StatusText == "Good", "HDD: no life percentage, just the status");

            var realloc = Ata(new() { [5] = (98, 8), [197] = (100, 2), [198] = (100, 1) }, new() { [5] = 36 }, ssd: false);
            check(realloc.Status == HealthStatus.Caution && realloc.BadSectors == 11, "HDD: reallocated/pending/uncorrectable sectors are Caution (8+2+1)");
            check(realloc.Reasons.Count == 3, "HDD: one reason per kind of bad sector");

            var failing = Ata(new() { [5] = (30, 2000), [9] = (90, 1) }, new() { [5] = 36 }, ssd: false);
            check(failing.Status == HealthStatus.Bad, "SATA: attribute at or below its threshold is Bad");
            check(failing.Attributes.First(a => a.Id == 5).Status == HealthStatus.Bad, "SATA: the failing attribute is marked Bad");

            var worn = Ata(new() { [231] = (7, 0) }, new(), ssd: null);
            check(worn.Kind == DriveKind.Ssd && worn.LifePercent == 7 && worn.Status == HealthStatus.Caution, "SATA: SSD detected from wear attribute; 7% life is Caution");

            var noThresholds = Ata(new() { [194] = (60, 0x0000_2800_1E00_0028) }, new(), ssd: false);
            check(noThresholds.TemperatureC == 40, "SATA: temperature uses the low byte of the raw value");
        }

        // ---- Alert limits: once when crossed, again only after recovering
        {
            var s = new HealthSettings();
            var state = new HealthState();
            var t0 = DateTime.UtcNow;
            List<HealthAlert> Check(DriveHealth d) => HealthMonitor.Compare(state, new[] { d }, s, t0 = t0.AddHours(1));

            check(Check(Nvme(used: 10, kelvin: 313)).Count == 0, "Alerts: first check of a healthy drive sends nothing");
            check(Check(Nvme(used: 10, kelvin: 313)).Count == 0, "Alerts: nothing changed, nothing sent");
            var a = Check(Nvme(used: 10, mediaErrors: 1, kelvin: 313));
            check(a.Any(x => x.Title.Contains("Good to Caution")) && a.Any(x => x.Title.Contains("new bad sector")), "Alerts: Good → Caution and a new bad sector");
            check(Check(Nvme(used: 10, mediaErrors: 1, kelvin: 313)).Count == 0, "Alerts: the same Caution isn't repeated");
            check(Check(Nvme(used: 10, mediaErrors: 1, kelvin: 313, warning: 0x04)).Single().Title.Contains("Caution to Bad"), "Alerts: Caution → Bad");
            check(Check(Nvme(used: 10, kelvin: 313)).Single().Title.Contains("Bad to Good"), "Alerts: recovery is reported too");

            // Low life (limit 20%): 79% used = 21% left, then 81% used = 19% left.
            check(Check(Nvme(used: 79, kelvin: 313)).Count == 0, "Alerts: 21% life with a 20% limit sends nothing");
            check(Check(Nvme(used: 81, kelvin: 313)).Single().Title.Contains("below 20%"), "Alerts: 19% life crosses the 20% limit");
            check(Check(Nvme(used: 82, kelvin: 313)).Count == 0, "Alerts: low life isn't repeated every check");
            s.LowLifePercent = 30;
            check(Check(Nvme(used: 82, kelvin: 313)).Count == 0, "Alerts: raising the limit doesn't re-alert while still low");

            // Temperature (SSD limit 55 °C, 3 °C hysteresis).
            s = new HealthSettings();
            state = new HealthState();
            check(Check(Nvme(kelvin: 273 + 55)).Count == 0, "Alerts: exactly at the limit isn't above it");
            check(Check(Nvme(kelvin: 273 + 58)).Single().Title.Contains("58 °C"), "Alerts: 58 °C is above a 55 °C limit");
            check(Check(Nvme(kelvin: 273 + 60)).Count == 0, "Alerts: still hot, not repeated");
            check(Check(Nvme(kelvin: 273 + 54)).Count == 0, "Alerts: 54 °C hasn't cooled enough to re-arm");
            check(Check(Nvme(kelvin: 273 + 57)).Count == 0, "Alerts: …so 57 °C again doesn't re-alert");
            check(Check(Nvme(kelvin: 273 + 50)).Count == 0 && Check(Nvme(kelvin: 273 + 56)).Count == 1, "Alerts: after cooling to 50 °C, 56 °C alerts again");
            var hddHot = Ata(new() { [194] = (100, 51) }, new(), ssd: false);
            check(HealthMonitor.Compare(new HealthState(), new[] { hddHot }, new HealthSettings(), DateTime.UtcNow).Single().Title.Contains("51 °C"),
                "Alerts: hard disks use their own limit (51 °C is above 50 °C)");

            // Bad-sector increase threshold.
            s = new HealthSettings { BadSectorIncrease = 5 };
            state = new HealthState();
            s.EmailOnStatusChange = false;
            check(Check(Ata(new() { [5] = (100, 2) }, new(), ssd: false)).Count == 0, "Alerts: sectors already bad at the first check are the baseline");
            check(Check(Ata(new() { [5] = (100, 5) }, new(), ssd: false)).Count == 0, "Alerts: +3 sectors with a limit of 5 sends nothing");
            var sec = Check(Ata(new() { [5] = (100, 8) }, new(), ssd: false));
            check(sec.Single().Title == "6 new bad sectors", "Alerts: +6 sectors since the last alert crosses a limit of 5");
            check(Check(Ata(new() { [5] = (100, 9) }, new(), ssd: false)).Count == 0, "Alerts: the count restarts from the alert");

            // Turned off, and drives that can't be read.
            var off = new HealthSettings { EmailOnStatusChange = false, EmailOnLowLife = false, EmailOnHot = false, EmailOnBadSectors = false };
            var st2 = new HealthState();
            HealthMonitor.Compare(st2, new[] { Nvme() }, off, DateTime.UtcNow);
            check(HealthMonitor.Compare(st2, new[] { Nvme(used: 95, kelvin: 350, mediaErrors: 9, warning: 0x04) }, off, DateTime.UtcNow).Count == 0,
                "Alerts: every alert can be switched off");
            var st3 = new HealthState();
            HealthMonitor.Compare(st3, new[] { Nvme() }, new HealthSettings(), DateTime.UtcNow);
            var unreadable = new DriveHealth { Model = "Test NVMe", Serial = "SN1", Unavailable = "asleep" };
            check(HealthMonitor.Compare(st3, new[] { unreadable }, new HealthSettings(), DateTime.UtcNow).Count == 0
                  && st3.Drives.Single().Value.Status == HealthStatus.Good, "Alerts: an unreadable drive keeps its last state and sends nothing");
            check(HealthMonitor.Compare(new HealthState(), new[] { Nvme(warning: 0x04) }, new HealthSettings(), DateTime.UtcNow).Any(x => x.Serious),
                "Alerts: a drive that's already Bad at the first check is reported");

            // State survives a save and load (as JSON, the way SaveState/LoadState store it; the real file isn't touched).
            var json = System.Text.Json.JsonSerializer.Serialize(st3, JobStore.Json);
            var back = System.Text.Json.JsonSerializer.Deserialize<HealthState>(json, JobStore.Json)!;
            check(back.Drives.TryGetValue(Nvme().Key, out var saved) && saved.Status == HealthStatus.Good && json.Contains("\"Good\""),
                "Alerts: state is saved between checks (statuses stored as words)");
        }

        // ---- Scheduled task XML for each interval
        foreach (var every in Enum.GetValues<HealthInterval>())
        {
            string xml = TaskSchedulerService.BuildHealthXml(@"C:\Program Files\DiskImager\DiskImager.exe", every);
            XDocument doc;
            try { doc = XDocument.Parse(xml.Replace("encoding=\"UTF-16\"", "")); }
            catch (Exception ex) { check(false, $"Health task XML ({every}) is well-formed: {ex.Message}"); continue; }
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var trig = doc.Root!.Element(ns + "Triggers")!;
            bool ok = every switch
            {
                HealthInterval.Hour => trig.Descendants(ns + "Interval").Single().Value == "PT1H" && trig.Element(ns + "BootTrigger") != null,
                HealthInterval.Day => trig.Descendants(ns + "DaysInterval").Single().Value == "1",
                HealthInterval.Week => trig.Descendants(ns + "Monday").Any(),
                _ => trig.Descendants(ns + "Day").Single().Value == "1" && trig.Descendants(ns + "December").Any(),
            };
            check(ok && doc.Descendants(ns + "Arguments").Single().Value == "--health-check"
                     && doc.Descendants(ns + "UserId").Single().Value == "S-1-5-18",
                $"Health task XML ({HealthSettings.Describe(every)}) is well-formed, runs as SYSTEM with --health-check");
        }

        // ---- Email
        {
            var bad = Nvme(warning: 0x04, mediaErrors: 2);
            var alerts = HealthMonitor.Compare(new HealthState(), new[] { bad }, new HealthSettings(), DateTime.UtcNow);
            var m = HealthMonitor.Compose(alerts, new ReportSettings { ClientName = "ACME Pte Ltd" });
            check(m.Subject.StartsWith("[ACME Pte Ltd]") && m.Subject.Contains("Drive health alert") && m.Tone == EmailTone.Failure,
                "Email: subject has the client and says it's a drive health alert");
            check(m.Html.Contains("Act now") && !m.Html.Contains(">Missed<") && m.Text.Contains("Test NVMe"), "Email: status pill says Act now; body names the drive");
            File.WriteAllText(Path.Combine(work, "health-alert-sample.html"), m.Html);
            var warn = HealthMonitor.Compose(HealthMonitor.Compare(new HealthState(), new[] { Nvme(kelvin: 340) }, new HealthSettings(), DateTime.UtcNow), new ReportSettings());
            check(warn.Tone == EmailTone.Warning && warn.Html.Contains("Check soon"), "Email: a hot drive is a warning (Check soon), not a failure");
        }

        // ---- Settings round-trip
        {
            var s = new AppSettings();
            s.Health.Every = HealthInterval.Week;
            s.Health.LowLifePercent = 15;
            var json = System.Text.Json.JsonSerializer.Serialize(s, JobStore.Json);
            var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, JobStore.Json)!;
            check(back.Health.Every == HealthInterval.Week && back.Health.LowLifePercent == 15 && back.Health.Monitor, "Settings: disk health settings are saved");
            var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Email\":{\"Host\":\"x\"}}", JobStore.Json)!;
            check(old.Health.Monitor && old.Health.Every == HealthInterval.Hour && old.Health.HotSsdC == 55,
                "Settings: settings saved by 1.3.0 get monitoring on, hourly, with the default limits");
        }
    }

    /// <summary>Reads this PC's real drives (needs administrator rights).</summary>
    public static int Live()
    {
        var drives = SmartReader.ReadAll();
        foreach (var d in drives)
        {
            Console.WriteLine($"Disk {d.DiskNumber}: {d.Model} [{d.Bus}, {d.KindText}] fw {d.Firmware} S/N {d.Serial}");
            if (d.Unavailable != null) { Console.WriteLine("    unavailable: " + d.Unavailable); continue; }
            Console.WriteLine($"    {d.StatusText}, {d.TemperatureC} °C, {d.PowerOnHours} h, {d.PowerCycles} cycles, " +
                              $"written {(d.DataWrittenBytes is long w ? Util.FormatBytes(w) : "-")}, bad {d.BadSectors}");
            foreach (var r in d.Reasons) Console.WriteLine("    reason: " + r);
        }
        int read = drives.Count(d => d.Unavailable == null);
        Console.WriteLine(read > 0 ? $"LIVE OK: {read} of {drives.Count} drive(s) read" : "LIVE FAILED: no drive's health could be read");
        return read > 0 ? 0 : 1;
    }

    /// <summary>Renders the details page for simulated drives (SATA SSD in Caution, failing hard disk, unreadable USB) to PNGs.</summary>
    public static int Render(string shots)
    {
        Directory.CreateDirectory(shots);
        int code = 1;
        var t = new Thread(() =>
        {
            var s = new HealthSettings();
            var caution = Ata(new() { [5] = (100, 12), [9] = (99, 21873), [12] = (99, 1504), [177] = (14, 3120), [187] = (100, 0),
                [194] = (52, 48), [197] = (100, 0), [198] = (100, 0), [199] = (100, 0), [241] = (99, 123456789) },
                new() { [5] = 10, [177] = 0, [187] = 0 }, ssd: true);
            var bad = Ata(new() { [1] = (200, 0), [3] = (175, 4250), [5] = (30, 1880), [9] = (40, 44123), [12] = (100, 812), [194] = (110, 41),
                [196] = (1, 412), [197] = (200, 16), [198] = (200, 9), [199] = (200, 0) },
                new() { [1] = 51, [3] = 21, [5] = 140, [196] = 0, [197] = 0, [198] = 0 }, ssd: false);
            var usb = new DriveHealth { DiskNumber = 3, Model = "Generic USB enclosure", Bus = "USB", Size = 2L << 40,
                Unavailable = "This USB drive or its enclosure doesn't pass health data through to Windows." };
            int n = 0;
            foreach (var (name, d) in new[] { ("sata-ssd-caution", caution), ("hdd-bad", bad), ("usb-unavailable", usb) })
            {
                var root = new System.Windows.Controls.Grid { Width = 1000, Background = DiskBars.Hex("#F1F5F9") };
                root.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(310) });
                root.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(16) });
                root.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                var cards = new System.Windows.Controls.StackPanel();
                foreach (var c in new[] { caution, bad, usb }) { var card = HealthView.Card(c, s); card.Margin = new System.Windows.Thickness(0, 0, 0, 8); cards.Children.Add(card); }
                root.Children.Add(cards);
                var details = HealthView.Details(d, s);
                System.Windows.Controls.Grid.SetColumn(details, 2);
                root.Children.Add(details);
                var page = new System.Windows.Controls.Border { Padding = new System.Windows.Thickness(24), Background = DiskBars.Hex("#F1F5F9"), Child = root };
                page.Measure(new System.Windows.Size(1048, double.PositiveInfinity));
                page.Arrange(new System.Windows.Rect(page.DesiredSize));
                page.UpdateLayout();
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(page);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var f = File.Create(Path.Combine(shots, $"health-sim-{name}.png"));
                enc.Save(f);
                n++;
            }
            code = n == 3 ? 0 : 1;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Console.WriteLine(code == 0 ? "RENDER OK" : "RENDER FAILED");
        return code;
    }

    // ---- builders

    static DriveHealth Nvme(byte warning = 0, int kelvin = 310, byte spare = 100, byte spareThr = 10, byte used = 2,
        ulong written = 1000, ulong read = 2000, ulong hours = 100, ulong cycles = 50, ulong mediaErrors = 0)
    {
        var log = new byte[512];
        log[0] = warning;
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), (ushort)kelvin);
        log[3] = spare; log[4] = spareThr; log[5] = used;
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(32), read);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(48), written);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(112), cycles);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(128), hours);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(160), mediaErrors);
        var d = new DriveHealth { DiskNumber = 1, Model = "Test NVMe", Serial = "SN1", Bus = "NVMe", Size = 1L << 40 };
        d.ParseNvme(log);
        return d;
    }

    static DriveHealth Ata(Dictionary<byte, (byte Cur, ulong Raw)> attrs, Dictionary<byte, byte> thresholds, bool? ssd)
    {
        var values = new byte[512];
        var thr = new byte[512];
        int i = 0;
        foreach (var (id, (cur, raw)) in attrs)
        {
            int o = 2 + i * 12;
            values[o] = id; values[o + 3] = cur; values[o + 4] = cur;
            for (int b = 0; b < 6; b++) values[o + 5 + b] = (byte)(raw >> (8 * b));
            thr[o] = id; thr[o + 1] = thresholds.TryGetValue(id, out var t) ? t : (byte)0;
            i++;
        }
        var d = new DriveHealth { DiskNumber = 2, Model = "Test SATA", Serial = "SN2", Bus = "SATA", Size = 1L << 40 };
        d.ParseAta(values, thr, ssd);
        return d;
    }
}
