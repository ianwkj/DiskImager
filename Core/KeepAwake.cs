using System.Runtime.InteropServices;

namespace DiskImager.Core;

/// <summary>
/// Stops Windows from going to sleep (idle sleep) while a backup, restore or clone is running.
/// Uses a power request, which works for the SYSTEM account with nobody signed in, shows up in
/// "powercfg /requests" with the reason given, and is released automatically if the process exits.
/// The screen may still turn off; only system sleep is blocked.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    private const int PowerRequestSystemRequired = 1;
    private const int PowerRequestAwayModeRequired = 2;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private IntPtr _request;
    private bool _awayMode;
    private readonly bool _threadFallback;
    private bool _disposed;

    /// <summary>True when Windows accepted the request to stay awake.</summary>
    public bool Active { get; }

    public KeepAwake(string reason, Logger? log)
    {
        var ctx = new REASON_CONTEXT { Version = 0, Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING, SimpleReasonString = reason };
        _request = PowerCreateRequest(ref ctx);
        if (_request != IntPtr.Zero && _request != new IntPtr(-1) && PowerSetRequest(_request, PowerRequestSystemRequired))
        {
            // Away mode (where enabled) turns a sleep request into "looks asleep but keeps working".
            _awayMode = PowerSetRequest(_request, PowerRequestAwayModeRequired);
            Active = true;
        }
        else
        {
            if (_request != IntPtr.Zero && _request != new IntPtr(-1)) CloseHandle(_request);
            _request = IntPtr.Zero;
            // Older fallback: only holds while this thread is alive, so it's used only if the above fails.
            _threadFallback = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED) != 0;
            Active = _threadFallback;
        }

        if (Active) log?.Info("Sleep is blocked while this runs.");
        else log?.Warn("Could not stop Windows from sleeping during this operation; check the power settings.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_request != IntPtr.Zero)
        {
            if (_awayMode) PowerClearRequest(_request, PowerRequestAwayModeRequired);
            PowerClearRequest(_request, PowerRequestSystemRequired);
            CloseHandle(_request);
            _request = IntPtr.Zero;
        }
        if (_threadFallback) SetThreadExecutionState(ES_CONTINUOUS);
    }
}
