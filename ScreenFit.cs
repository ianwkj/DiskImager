using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DiskImager;

/// <summary>
/// Keeps windows usable on small screens (laptops, scaled displays): a window never opens bigger than the usable
/// area of the screen it appears on (the taskbar excluded), so its title bar and minimise/maximise/close buttons
/// stay visible, and it's centred there. The main window opens maximised when its normal size doesn't fit.
/// Tall dialogs scroll inside (their content is in a ScrollViewer).
/// </summary>
public static class ScreenFit
{
    private const double Gap = 12; // breathing room from the screen edges

    /// <param name="maximiseIfTooSmall">Open maximised when the window's normal size doesn't fit the screen.</param>
    public static void Attach(Window w, bool maximiseIfTooSmall = false)
    {
        w.SourceInitialized += (_, _) => Limit(w, maximiseIfTooSmall);
        w.ContentRendered += (_, _) => KeepOnScreen(w); // by then Windows has placed it (centre on owner, etc.)
    }

    /// <summary>The usable area (DIPs) of the screen the window will appear on: its owner's, else the one with the mouse.</summary>
    public static Rect WorkArea(Window w)
    {
        // Tests: pretend the screen is smaller ("left,top,width,height" in DIPs).
        if (Environment.GetEnvironmentVariable("DISKIMAGER_TEST_WORKAREA") is string fake
            && fake.Split(',').Select(s => double.TryParse(s, out var v) ? v : double.NaN).ToArray() is { Length: 4 } f && !f.Any(double.IsNaN))
            return new Rect(f[0], f[1], f[2], f[3]);
        IntPtr mon;
        if (w.Owner != null && new WindowInteropHelper(w.Owner).Handle is var owner && owner != IntPtr.Zero)
            mon = MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST);
        else
        {
            GetCursorPos(out var pt);
            mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        }
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref info)) return SystemParameters.WorkArea;
        double sx = 1, sy = 1;
        try
        {
            if (GetDpiForMonitor(mon, 0, out uint dx, out uint dy) == 0 && dx > 0 && dy > 0) { sx = 96.0 / dx; sy = 96.0 / dy; }
        }
        catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { } // Windows 7/8.0
        var r = info.rcWork;
        return new Rect(r.Left * sx, r.Top * sy, (r.Right - r.Left) * sx, (r.Bottom - r.Top) * sy);
    }

    private static void Limit(Window w, bool maximiseIfTooSmall)
    {
        var area = WorkArea(w);
        double maxW = Math.Max(320, area.Width - 2 * Gap), maxH = Math.Max(240, area.Height - 2 * Gap);
        bool tooSmall = (!double.IsNaN(w.Width) && w.Width > maxW) || (!double.IsNaN(w.Height) && w.Height > maxH)
                        || w.MinWidth > maxW || w.MinHeight > maxH;

        w.MinWidth = Math.Min(w.MinWidth, maxW);
        w.MinHeight = Math.Min(w.MinHeight, maxH);
        w.MaxWidth = Math.Min(w.MaxWidth, Math.Max(maxW, w.MinWidth));
        w.MaxHeight = Math.Min(w.MaxHeight, Math.Max(maxH, w.MinHeight));
        if (!double.IsNaN(w.Width) && w.Width > maxW) w.Width = maxW;
        if (!double.IsNaN(w.Height) && w.Height > maxH) w.Height = maxH;

        if (w.WindowStartupLocation == WindowStartupLocation.CenterScreen || maximiseIfTooSmall)
        {
            // Centre on that screen's usable area ourselves (Windows would centre on the whole screen, taskbar included).
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            if (!double.IsNaN(w.Width)) w.Left = area.Left + (area.Width - w.Width) / 2;
            if (!double.IsNaN(w.Height)) w.Top = area.Top + (area.Height - w.Height) / 2;
        }
        if (maximiseIfTooSmall && tooSmall)
        {
            // A main window that's normally bigger than this screen: use all of it. Restoring gives the fitted size.
            w.MaxWidth = w.MaxHeight = double.PositiveInfinity;
            w.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>After the first layout (when windows sized to their content know their size): pull the window fully onto its screen.</summary>
    private static void KeepOnScreen(Window w)
    {
        if (w.WindowState != WindowState.Normal) return;
        var area = WorkArea(w);
        double width = w.ActualWidth, height = w.ActualHeight;
        if (width <= 0 || height <= 0) return;
        double left = w.Left, top = w.Top;
        if (double.IsNaN(left) || double.IsNaN(top)) return;
        double minLeft = area.Left, maxLeft = area.Right - width, minTop = area.Top, maxTop = area.Bottom - height;
        double newLeft = maxLeft < minLeft ? area.Left + (area.Width - width) / 2 : Math.Clamp(left, minLeft, maxLeft);
        double newTop = maxTop < minTop ? area.Top : Math.Clamp(top, minTop, maxTop); // never above the top: the title bar must show
        if (Math.Abs(newLeft - left) > 0.5) w.Left = newLeft;
        if (Math.Abs(newTop - top) > 0.5) w.Top = newTop;
    }

    // ---- Win32

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
}
