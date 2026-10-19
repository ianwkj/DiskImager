// Auto-update in the real app: dotnet run -- --update <DiskImager.exe> <signed setup.exe> <screenshot folder>
// Serves a fake GitHub release "9.9.0" (the given installer and its .sig) on localhost and points DiskImager at it.
//   1. The pop-up appears on opening with a countdown; Later closes it and nothing is downloaded.
//   2. Settings > Check for updates now shows it without a countdown.
//   3. Opened again and left alone, DiskImager downloads, verifies, starts setup and closes itself.
// Run it WITHOUT admin rights: setup then stops at its admin check instead of really installing.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

static class UpdateUiTest
{
    public static int Run(string exe, string setup, string shots)
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
        using (var id = WindowsIdentity.GetCurrent())
            if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("FAIL  run this test without administrator rights (it would really install the update)");
                return 1;
            }
        Directory.CreateDirectory(shots);
        byte[] setupBytes = File.ReadAllBytes(setup);
        string sig = File.ReadAllText(setup + ".sig");

        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        string baseUrl = $"http://localhost:{port}";
        int setupDownloads = 0;
        using var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl + "/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { break; }
                string path = ctx.Request.Url!.AbsolutePath;
                byte[] body = path switch
                {
                    "/repos/ianwkj/DiskImager/releases/latest" => JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        tag_name = "v9.9.0",
                        html_url = baseUrl + "/release",
                        body = "### In this version\n- Automatic updates from GitHub\n- Faster restores to USB drives\n- Clearer error messages",
                        assets = new object[]
                        {
                            new { name = "DiskImagerSetup-9.9.0.exe", size = setupBytes.Length, browser_download_url = baseUrl + "/dl/setup" },
                            new { name = "DiskImagerSetup-9.9.0.exe.sig", size = sig.Length, browser_download_url = baseUrl + "/dl/sig" },
                        },
                    }),
                    "/dl/setup" => setupBytes,
                    "/dl/sig" => Encoding.ASCII.GetBytes(sig),
                    _ => Array.Empty<byte>(),
                };
                if (path == "/dl/setup") Interlocked.Increment(ref setupDownloads);
                ctx.Response.ContentLength64 = body.Length;
                try { await ctx.Response.OutputStream.WriteAsync(body); ctx.Response.Close(); } catch { }
            }
        });

        Process Launch()
        {
            var psi = new ProcessStartInfo(exe, "--no-elevate") { UseShellExecute = false };
            psi.Environment["DISKIMAGER_UPDATE_API"] = baseUrl;
            return Process.Start(psi)!;
        }
        // An owned window can show up at the top level or under the main window.
        AutomationElement? UpdatePopup(Process app) => WaitFor(() =>
        {
            var tops = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id));
            foreach (AutomationElement top in tops)
            {
                if (top.Current.Name.Contains("update available")) return top;
                foreach (AutomationElement child in top.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)))
                    if (child.Current.Name.Contains("update available")) return child;
            }
            return null;
        }, 45);

        // Start clean: no earlier attempt recorded (that switches the countdown off) and no earlier download.
        string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiskImager");
        string saved = Path.Combine(dataFolder, "Updates", "DiskImagerSetup-9.9.0.exe");
        void ForgetAttempt()
        {
            string file = Path.Combine(dataFolder, "settings.json");
            if (!File.Exists(file)) return;
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            if (json["Updates"] is System.Text.Json.Nodes.JsonObject u) u["LastAttemptVersion"] = null;
            File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        ForgetAttempt();
        try { File.Delete(saved); } catch { }

        // ---- 1 + 2: countdown, Later, then Check now.
        var app = Launch();
        try
        {
            var popup = UpdatePopup(app);
            Check(popup != null, "update pop-up appears when DiskImager opens");
            if (popup == null) return 1;
            Thread.Sleep(1500);
            string title = ById(popup, "TitleText")?.Current.Name ?? "";
            string count = ById(popup, "CountText")?.Current.Name ?? "";
            Check(title == "DiskImager 9.9.0", $"pop-up names the new version ('{title}')");
            Check(count.StartsWith("Updating automatically in "), $"pop-up counts down ('{count}')");
            Shot(popup, Path.Combine(shots, "update-1-countdown.png"));

            var later = WaitFor(() => Button(popup, "Later"), 3);
            Check(later != null, "Later button shown");
            if (later != null) Invoke(later);
            Thread.Sleep(12000); // past the end of the countdown
            Check(!app.HasExited && setupDownloads == 0, "after Later: DiskImager stays open and nothing is downloaded");

            app.Refresh();
            var main = AutomationElement.FromHandle(app.MainWindowHandle);
            Select(main, "Settings");
            var checkNow = WaitFor(() => Button(main, "Check for updates now"), 5);
            Check(checkNow != null, "Settings has 'Check for updates now'");
            if (checkNow != null)
            {
                Invoke(checkNow);
                popup = UpdatePopup(app);
                Thread.Sleep(1000);
                count = popup != null ? ById(popup, "CountText")?.Current.Name ?? "" : "";
                Check(popup != null && count.StartsWith("Install it now?"), $"Check now asks without a countdown ('{count}')");
                if (popup != null)
                {
                    Shot(popup, Path.Combine(shots, "update-2-check-now.png"));
                    Thread.Sleep(11000);
                    Check(!app.HasExited && setupDownloads == 0, "without a countdown nothing happens until Update now");
                    var close = Button(popup, "Later");
                    if (close != null) Invoke(close);
                    Thread.Sleep(1000);
                    var status = ById(main, "UpdateStatus")?.Current.Name ?? "";
                    Check(status == "Version 9.9.0 is available.", $"Settings shows the available version ('{status}')");
                    ShotWindow(main, Path.Combine(shots, "update-3-settings.png"));
                }
            }
        }
        finally { try { if (!app.HasExited) app.Kill(); } catch { } }

        // ---- 3: left alone, it updates itself.
        string setupLog = Path.Combine(Path.GetTempPath(), "DiskImagerSetup.log");
        long logBefore = File.Exists(setupLog) ? new FileInfo(setupLog).Length : 0;
        app = Launch();
        try
        {
            var popup = UpdatePopup(app);
            Check(popup != null, "second opening: pop-up appears again");
            Thread.Sleep(6000);
            if (popup != null) Shot(popup, Path.Combine(shots, "update-4-downloading.png"));
            bool closed = app.WaitForExit(90000);
            Check(closed, "left alone, DiskImager downloads the update and closes itself for setup");
            Check(setupDownloads == 1, $"installer downloaded once ({setupDownloads})");
            Check(File.Exists(saved) && new FileInfo(saved).Length == setupBytes.Length, "verified installer saved in ProgramData\\DiskImager\\Updates");
            Thread.Sleep(4000);
            string newLog = "";
            if (File.Exists(setupLog))
                using (var f = new FileStream(setupLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    f.Seek(Math.Min(logBefore, f.Length), SeekOrigin.Begin);
                    newLog = new StreamReader(f).ReadToEnd();
                }
            Check(newLog.Contains("Silent install needs to be run as administrator"),
                "setup was started in silent mode (stopped at its admin check, as expected in this test)");
        }
        finally { try { if (!app.HasExited) app.Kill(); } catch { } }

        // ---- 4: that attempt didn't install anything, so the next opening asks instead of trying again by itself.
        app = Launch();
        try
        {
            var popup = UpdatePopup(app);
            Thread.Sleep(1000);
            string count = popup != null ? ById(popup, "CountText")?.Current.Name ?? "" : "";
            Check(popup != null && count.StartsWith("Install it now?"), $"after an update that didn't install, it asks instead of retrying by itself ('{count}')");
        }
        finally { try { if (!app.HasExited) app.Kill(); } catch { } listener.Stop(); ForgetAttempt(); }

        Console.WriteLine(failures == 0 ? "\nUPDATE UI TESTS PASSED" : $"\n{failures} UPDATE UI TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    static T? WaitFor<T>(Func<T?> f, int seconds) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        do { try { var r = f(); if (r != null) return r; } catch { } Thread.Sleep(300); } while (DateTime.UtcNow < until);
        return null;
    }

    static AutomationElement? ById(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    static AutomationElement? Button(AutomationElement root, string name) =>
        root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().FirstOrDefault(b => b.Current.Name.StartsWith(name));

    static void Invoke(AutomationElement e) => Task.Run(() => ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke());

    static void Select(AutomationElement root, string tab)
    {
        var item = WaitFor(() => root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
            .Cast<AutomationElement>().FirstOrDefault(t => t.Current.Name.Contains(tab)), 5);
        if (item != null) ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Thread.Sleep(600);
    }

    static void ShotWindow(AutomationElement w, string file) => Shot(w, file);

    static void Shot(AutomationElement w, string file)
    {
        try
        {
            var r = w.Current.BoundingRectangle;
            using var bmp = new System.Drawing.Bitmap((int)r.Width, (int)r.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen((int)r.Left, (int)r.Top, 0, 0, bmp.Size);
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch { }
    }
}
