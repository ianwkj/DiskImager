using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskImager.Core;

internal static unsafe class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;

    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_EXTEND_VOLUME = 0x000900F0;
    public const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
    public const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;
    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint FILE_SHARE_DELETE = 4;
    public const uint IOCTL_DISK_DELETE_DRIVE_LAYOUT = 0x0007C100;

    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_MORE_DATA = 234;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, int inSize,
        byte[]? outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, byte* buf, int toRead, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle h, byte* buf, int toWrite, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPos, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstVolumeW(char[] name, int len);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool FindNextVolumeW(IntPtr h, char[] name, int len);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FindVolumeClose(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumePathNamesForVolumeNameW(string vol, char[] names, int len, out int needed);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumeInformationW(string root, char[] label, int labelLen, out uint serial,
        out uint maxComp, out uint fsFlags, char[] fsName, int fsLen);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceExW(string dir, out ulong freeAvailable, out ulong total, out ulong totalFree);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NETRESOURCE
    {
        public int dwScope, dwType, dwDisplayType, dwUsage;
        public string? lpLocalName, lpRemoteName, lpComment, lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern int WNetAddConnection2W(ref NETRESOURCE nr, string? password, string? user, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern int WNetCancelConnection2W(string name, int flags, bool force);

    public static SafeFileHandle Open(string path, uint access)
    {
        var h = CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"Cannot open {path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        return h;
    }

    public static byte[] Ioctl(SafeFileHandle h, uint code, byte[]? input, int outSize)
    {
        var outBuf = new byte[outSize];
        if (!DeviceIoControl(h, code, input, input?.Length ?? 0, outBuf, outSize, out int ret, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (ret < outSize) Array.Resize(ref outBuf, ret);
        return outBuf;
    }

    /// <summary>IOCTL whose output size is unknown up front: grows the buffer until it fits.</summary>
    public static byte[] IoctlGrow(SafeFileHandle h, uint code, int initialSize)
    {
        int size = initialSize;
        while (true)
        {
            var outBuf = new byte[size];
            if (DeviceIoControl(h, code, null, 0, outBuf, size, out int ret, IntPtr.Zero))
            {
                Array.Resize(ref outBuf, ret);
                return outBuf;
            }
            int err = Marshal.GetLastWin32Error();
            if ((err != ERROR_INSUFFICIENT_BUFFER && err != ERROR_MORE_DATA) || size > 4 << 20)
                throw new Win32Exception(err);
            size *= 2;
        }
    }

    public static bool TryIoctl(SafeFileHandle h, uint code) =>
        DeviceIoControl(h, code, null, 0, null, 0, out _, IntPtr.Zero);
}
