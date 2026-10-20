// Live UI test: dotnet run -- --ui <path to DiskImager.exe> <screenshot folder>
// Opens the main window (without admin), opens the job editor, types into the time box with real
// keystrokes, checks the network-login row follows the destination, and saves screenshots.
using System.Diagnostics;
using System.Windows.Automation;
using SendKeys = System.Windows.Forms.SendKeys;

static class UiTest
{
    public static int Run(string exe, string shots)
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
        Directory.CreateDirectory(shots);

        var app = Process.Start(new ProcessStartInfo(exe, "--no-elevate") { UseShellExecute = false })!;
        try
        {
            var main = WaitFor(() =>
            {
                app.Refresh();
                return app.MainWindowHandle != IntPtr.Zero ? AutomationElement.FromHandle(app.MainWindowHandle) : null;
            }, 20);
            Check(main != null, "main window opens");
            if (main == null) return 1;
            Thread.Sleep(3000);
            ShotWindow(shots, "ui-1-main", main);

            Select(main, "Scheduled backups");
            var newJob = WaitFor(() => All(main, ControlType.Button).FirstOrDefault(b => b.Current.Name.StartsWith("New job")), 5);
            Check(newJob != null, "New job button found");
            if (newJob == null)
            {
                Console.WriteLine("  buttons: " + string.Join(" | ", All(main, ControlType.Button).Select(b => b.Current.Name)));
                return 1;
            }
            Invoke(newJob);
            var title = new PropertyCondition(AutomationElement.NameProperty, "New scheduled backup");
            var dlg = WaitFor(() => main.FindFirst(TreeScope.Children, title)
                                    ?? AutomationElement.RootElement.FindFirst(TreeScope.Children, title), 8);
            Check(dlg != null, "job editor opens");
            if (dlg == null) return 1;

            var time = WaitFor(() => ById(dlg, "TimeBox"), 3);
            Check(time != null, "time box found");
            if (time != null)
            {
                time.SetFocus();
                Thread.Sleep(300);
                SendKeys.SendWait("1500");
                Thread.Sleep(300);
                Check(Value(time) == "15:00", $"typing 1500 gives 15:00 (got '{Value(time)}')");
                SendKeys.SendWait("7");
                Check(Value(time) == "15:00", "a fifth digit is ignored");
                SendKeys.SendWait("{BACKSPACE}{BACKSPACE}{BACKSPACE}");
                Check(Value(time) == "1", $"backspace removes digits and the colon (got '{Value(time)}')");
                SendKeys.SendWait("{BACKSPACE}ab9");
                Thread.Sleep(200);
                Check(Value(time) == "09", $"letters ignored, 9 becomes 09 (got '{Value(time)}')");
                ShotWindow(shots, "ui-2-time-typing", dlg);
                SendKeys.SendWait("{TAB}");
                Thread.Sleep(300);
                Check(Value(time) == "09:00", $"leaving the box completes it to 09:00 (got '{Value(time)}')");
            }

            var dest = ById(dlg, "DestBox");
            if (dest != null)
            {
                SetValue(dest, @"E:\Backups");
                Thread.Sleep(400);
                Check(ById(dlg, "UserBox") == null, "network login hidden for a local folder");
                SetValue(dest, @"\\NAS\Backups\Test");
                Thread.Sleep(400);
                Check(ById(dlg, "UserBox") != null, "network login shown for a \\\\ network path");
                ShotWindow(shots, "ui-3-network-login", dlg);
            }

            var keep = ById(dlg, "KeepBox");
            if (keep != null)
            {
                keep.SetFocus();
                Thread.Sleep(200);
                SendKeys.SendWait("x3y");
                Thread.Sleep(200);
                Check(Value(keep) == "3", $"keep box accepts digits only (got '{Value(keep)}')");
            }
            // Scroll to the bottom for the clean-up section screenshot.
            var scroll = dlg.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane));
            if (scroll?.TryGetCurrentPattern(ScrollPattern.Pattern, out var sp) == true) ((ScrollPattern)sp).SetScrollPercent(ScrollPattern.NoScroll, 100);
            Thread.Sleep(500);
            ShotWindow(shots, "ui-4-cleanup", dlg);
            SendKeys.SendWait("{ESC}");
            Thread.Sleep(500);

            Select(main, "Restore");
            var loc = WaitFor(() => ById(main, "LocationBox"), 3);
            if (loc != null)
            {
                Check(ById(main, "RestoreUserBox") == null, "restore: network login hidden when no location");
                SetValue(loc, @"\\NAS\Backups");
                Thread.Sleep(400);
                Check(ById(main, "RestoreUserBox") != null, "restore: network login shown for a network path");
                ShotWindow(shots, "ui-5-restore", main);
            }
            Select(main, "Settings");
            Thread.Sleep(800);
            ShotWindow(shots, "ui-6-settings", main);
        }
        finally
        {
            try { app.Kill(); } catch { }
        }
        Console.WriteLine(failures == 0 ? "\nUI TESTS PASSED" : $"\n{failures} UI TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>dotnet run -- --restore exe imagesFolder shots : Restore-page buttons follow the selection.</summary>
    public static int RestoreTest(string exe, string folder, string shots)
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
        var app = Process.Start(new ProcessStartInfo(exe, "--no-elevate") { UseShellExecute = false })!;
        try
        {
            var main = WaitFor(() => { app.Refresh(); return app.MainWindowHandle != IntPtr.Zero ? AutomationElement.FromHandle(app.MainWindowHandle) : null; }, 20)!;
            Thread.Sleep(2500);
            Select(main, "Restore");
            Check(!All(main, ControlType.Button).Any(b => b.Current.Name == "Show in Explorer"), "Show in Explorer button removed");
            var verify = ById(main, "VerifyButton")!;
            var restore = ById(main, "RestoreButton")!;
            Check(!verify.Current.IsEnabled && !restore.Current.IsEnabled, "Verify and Restore are greyed out with nothing selected");

            SetValue(ById(main, "LocationBox")!, folder);
            Invoke(All(main, ControlType.Button).First(b => b.Current.Name == "Find images"));
            Thread.Sleep(3000);
            Check(!verify.Current.IsEnabled, "still greyed out after finding images (nothing selected yet)");
            var row = ById(main, "ImagesGrid")!.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
            ((SelectionItemPattern?)row?.GetCurrentPattern(SelectionItemPattern.Pattern))?.Select();
            Thread.Sleep(600);
            Check(verify.Current.IsEnabled && restore.Current.IsEnabled, "Verify and Restore enable once a backup is selected");
            ShotWindow(shots, "ui-7-restore-selected", main);

            Select(main, "Scheduled backups");
            Check(!All(main, ControlType.Button).Any(b => b.Current.Name == "Open destination"), "Open destination button removed");
        }
        finally { try { app.Kill(); } catch { } }
        Console.WriteLine(failures == 0 ? "\nRESTORE PAGE TESTS PASSED" : $"\n{failures} RESTORE PAGE TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// dotnet run -- --layout exe shots : opens Clone for the Windows disk (nothing is written), picks another
    /// disk as the target, drags the C: handle with the real mouse, and checks the size changed.
    /// </summary>
    public static int LayoutTest(string exe, string shots)
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
        Directory.CreateDirectory(shots);
        var app = Process.Start(new ProcessStartInfo(exe, "--no-elevate") { UseShellExecute = false })!;
        try
        {
            var main = WaitFor(() => { app.Refresh(); return app.MainWindowHandle != IntPtr.Zero ? AutomationElement.FromHandle(app.MainWindowHandle) : null; }, 20)!;
            Thread.Sleep(3000);

            // Job editor: disks are cards now.
            Select(main, "Scheduled backups");
            Invoke(All(main, ControlType.Button).First(b => b.Current.Name.StartsWith("New job")));
            var editor = WaitFor(() => main.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "New scheduled backup")), 8);
            Check(editor != null && ById(editor, "DiskList") != null && ById(editor, "DiskBox") == null, "job editor shows disk cards instead of a drop-down");
            if (editor != null) { ShotWindow(shots, "ui-8-editor-cards", editor); SendKeys.SendWait("{ESC}"); Thread.Sleep(600); }

            // Clone the Windows disk → layout window.
            Select(main, "Disks");
            var cards = ById(main, "DiskList")!.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().ToList();
            var winCard = cards.FirstOrDefault(c => c.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "WINDOWS")) != null) ?? cards[0];
            ((SelectionItemPattern)winCard.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Thread.Sleep(400);
            Invoke(All(main, ControlType.Button).First(b => b.Current.Name.StartsWith("Clone")));
            var dlg = WaitFor(() => main.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window))
                .Cast<AutomationElement>().FirstOrDefault(w => w.Current.Name.StartsWith("Clone Disk")), 8);
            Check(dlg != null, "clone sizing window opens");
            if (dlg == null) return 1;

            var combo = ById(dlg, "TargetBox")!;
            ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            Thread.Sleep(500);
            var item = combo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().FirstOrDefault(i => i.Current.IsEnabled);
            ((SelectionItemPattern?)item?.GetCurrentPattern(SelectionItemPattern.Pattern))?.Select();
            ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
            Thread.Sleep(1200);
            ShotWindow(shots, "ui-9-layout-auto", dlg);

            var handle = WaitFor(() => dlg.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .FirstOrDefault(e => e.Current.Name.StartsWith("Resize C:")), 4);
            Check(handle != null, "C: has a drag handle on the drive bar");
            string before = Value(ById(dlg, "SizeBox")!);
            if (handle != null)
            {
                var r = handle.Current.BoundingRectangle;
                int x = (int)(r.Left + r.Width / 2), y = (int)(r.Top + r.Height / 2);
                SetForegroundWindow(new IntPtr(dlg.Current.NativeWindowHandle));
                Thread.Sleep(300);
                SetCursorPos(x, y); Thread.Sleep(150);
                mouse_event(0x0002, 0, 0, 0, 0); // left down
                for (int i = 1; i <= 20; i++) { SetCursorPos(x - i * 8, y); Thread.Sleep(30); }
                Thread.Sleep(400);
                ShotWindow(shots, "ui-10-layout-dragging", dlg);
                mouse_event(0x0004, 0, 0, 0, 0); // left up
                Thread.Sleep(700);
                ShotWindow(shots, "ui-11-layout-after-drag", dlg);
            }
            string after = Value(ById(dlg, "SizeBox")!);
            var custom = ById(dlg, "CustomRadio")!;
            Check(after != before, $"dragging the handle changes the size ({before} GB → {after} GB)");
            Check(((SelectionItemPattern)custom.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected, "dragging switches to \"Choose sizes myself\"");

            // Type an exact size, then Fill free space.
            var box = ById(dlg, "SizeBox")!;
            SetValue(box, "600");
            box.SetFocus(); SendKeys.SendWait("{ENTER}");
            Thread.Sleep(600);
            Check(Value(box).StartsWith("600"), $"typing 600 sets C: to 600 GB (box shows {Value(box)})");
            SetValue(box, "100");
            box.SetFocus(); SendKeys.SendWait("{ENTER}");
            Thread.Sleep(600);
            Check(double.Parse(Value(box).Replace(",", "")) > 100, $"typing a size below the minimum stops at the minimum ({Value(box)} GB)");
            Invoke(All(dlg, ControlType.Button).First(b => b.Current.Name == "Fill free space"));
            Thread.Sleep(800);
            Check(double.Parse(Value(box).Replace(",", "")) > 500, $"Fill free space grows C: to the end of the drive ({Value(box)} GB)");
            ShotWindow(shots, "ui-12-layout-filled", dlg);
            SendKeys.SendWait("{ESC}");
        }
        finally { try { app.Kill(); } catch { } }
        Console.WriteLine(failures == 0 ? "\nLAYOUT TESTS PASSED" : $"\n{failures} LAYOUT TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private static T? WaitFor<T>(Func<T?> f, int seconds) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            try { var r = f(); if (r != null) return r; } catch { }
            Thread.Sleep(250);
        } while (DateTime.UtcNow < until);
        return null;
    }

    private static IEnumerable<AutomationElement> All(AutomationElement root, ControlType type) =>
        root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type)).Cast<AutomationElement>();

    private static AutomationElement? ById(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static void Select(AutomationElement root, string tab)
    {
        var t = root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
            new PropertyCondition(AutomationElement.NameProperty, tab)));
        ((SelectionItemPattern?)t?.GetCurrentPattern(SelectionItemPattern.Pattern))?.Select();
        Thread.Sleep(800);
    }

    private static void Invoke(AutomationElement e) => Task.Run(() => ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke());

    private static string Value(AutomationElement e) => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    private static void SetValue(AutomationElement e, string v) => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(v);

    /// <summary>Disk health page with this PC's real drives, then the monitoring settings: dotnet run -- --health-ui exe shots</summary>
    public static int HealthUi(string exe, string shots)
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
        Directory.CreateDirectory(shots);
        var app = Process.Start(new ProcessStartInfo(exe, "--no-elevate") { UseShellExecute = false })!;
        try
        {
            var main = WaitFor(() =>
            {
                app.Refresh();
                return app.MainWindowHandle != IntPtr.Zero ? AutomationElement.FromHandle(app.MainWindowHandle) : null;
            }, 20);
            Check(main != null, "main window opens");
            if (main == null) return 1;
            Thread.Sleep(2500);
            var nav = All(main, ControlType.TabItem).Select(t => t.Current.Name).ToList();
            int h = nav.IndexOf("Disk health"), s = nav.IndexOf("Settings");
            Check(h >= 0 && s == h + 1, $"Disk health tab sits right above Settings ({string.Join(", ", nav)})");

            Select(main, "Disk health");
            var card = WaitFor(() => main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().FirstOrDefault(i => All(i, ControlType.Text).Any(t => t.Current.Name.StartsWith("Disk "))), 15);
            Check(card != null, "a card per drive");
            Thread.Sleep(800);
            var texts = All(main, ControlType.Text).Select(t => t.Current.Name).ToList();
            Check(texts.Contains("HEALTH STATUS") && texts.Contains("TEMPERATURE"), "health and temperature badges shown");
            Check(texts.Any(t => t.EndsWith('%')), "health percentage shown");
            Check(texts.Contains("NVMe health readings") || texts.Contains("SMART readings") || texts.Contains("Health data isn't available for this drive"),
                "readings table (or the reason there's none)");
            ShotWindow(shots, "health-1-first-drive", main);

            var items = main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().Where(i => All(i, ControlType.Text).Any(t => t.Current.Name.StartsWith("Disk "))).ToList();
            if (items.Count > 1)
            {
                ((SelectionItemPattern)items[1].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                Thread.Sleep(800);
                ShotWindow(shots, "health-2-second-drive", main);
            }

            Select(main, "Settings");
            Thread.Sleep(800);
            var group = WaitFor(() => All(main, ControlType.Text).FirstOrDefault(t => t.Current.Name == "Disk health monitoring"), 5);
            Check(group != null, "Disk health monitoring settings shown");
            var every = WaitFor(() => ById(main, "HealthEveryBox"), 3);
            if (every != null)
            {
                var ec = (ExpandCollapsePattern)every.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                ec.Expand();
                Thread.Sleep(700);
                // The open list lives in a popup window of its own, not under the combo box.
                var options = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id))
                    .Cast<AutomationElement>().SelectMany(w => All(w, ControlType.ListItem)).Select(i => i.Current.Name)
                    .Where(n => n.StartsWith("1 ")).Distinct().ToList();
                Check(options.SequenceEqual(new[] { "1 hour", "1 day", "1 week", "1 month" }), $"interval choices: {string.Join(", ", options)}");
                ec.Collapse();
            }
            // Scroll the settings page down to the alert limits.
            var scroller = All(main, ControlType.Pane).Concat(new[] { main })
                .Select(p => p.TryGetCurrentPattern(ScrollPattern.Pattern, out var sp) ? (ScrollPattern)sp : null)
                .FirstOrDefault(sp => sp != null && sp.Current.VerticallyScrollable);
            try { scroller?.SetScrollPercent(ScrollPattern.NoScroll, 38); } catch { }
            var life = ById(main, "HealthLifeText");
            Thread.Sleep(700);
            Check(life != null && Value(life) == "20", "health limit defaults to 20%");
            ShotWindow(shots, "health-3-settings", main);
        }
        finally
        {
            try { app.Kill(); } catch { }
        }
        Console.WriteLine(failures == 0 ? "HEALTH UI TESTS PASSED" : $"{failures} HEALTH UI TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void ShotWindow(string folder, string name, AutomationElement window)
    {
        try
        {
            var h = new IntPtr(window.Current.NativeWindowHandle);
            if (h != IntPtr.Zero) SetForegroundWindow(h);
            Thread.Sleep(400);
            var r = window.Current.BoundingRectangle;
            using var bmp = new System.Drawing.Bitmap((int)r.Width, (int)r.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen((int)r.Left, (int)r.Top, 0, 0, bmp.Size);
            bmp.Save(Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Console.WriteLine($"  (screenshot {name} failed: {ex.Message})"); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr h);
}
