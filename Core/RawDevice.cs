using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskImager.Core;

/// <summary>Sector-addressed reads/writes on a physical disk, volume or shadow-copy device.</summary>
public sealed unsafe class RawDevice : IDisposable
{
    public SafeFileHandle Handle { get; }
    public string Path { get; }

    private RawDevice(string path, SafeFileHandle handle)
    {
        Path = path;
        Handle = handle;
    }

    public static RawDevice Open(string path, bool write) =>
        new(path, Native.Open(path, write ? Native.GENERIC_READ | Native.GENERIC_WRITE : Native.GENERIC_READ));

    /// <summary>Wraps a handle owned by someone else (it isn't closed when this is disposed).</summary>
    public static RawDevice Borrow(string path, SafeFileHandle handle) =>
        new(path, new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false));

    public long GetLength() =>
        BitConverter.ToInt64(Native.Ioctl(Handle, Native.IOCTL_DISK_GET_LENGTH_INFO, null, 8), 0);

    public void ReadAt(long offset, byte[] buffer, int index, int count)
    {
        Seek(offset);
        fixed (byte* p = buffer)
        {
            int done = 0;
            while (done < count)
            {
                if (!Native.ReadFile(Handle, p + index + done, count - done, out int n, IntPtr.Zero))
                    throw Error("read", offset + done);
                if (n == 0)
                    throw new IOException($"Unexpected end of {Path} at offset {offset + done}.");
                done += n;
            }
        }
    }

    public void WriteAt(long offset, byte[] buffer, int index, int count)
    {
        Seek(offset);
        fixed (byte* p = buffer)
        {
            int done = 0;
            while (done < count)
            {
                if (!Native.WriteFile(Handle, p + index + done, count - done, out int n, IntPtr.Zero))
                    throw Error("write", offset + done);
                if (n == 0)
                    throw new IOException($"Device {Path} refused to write at offset {offset + done}.");
                done += n;
            }
        }
    }

    public void Flush() => Native.FlushFileBuffers(Handle);

    private void Seek(long offset)
    {
        if (!Native.SetFilePointerEx(Handle, offset, out _, 0))
            throw Error("seek", offset);
    }

    private DiskIOException Error(string op, long offset)
    {
        int code = Marshal.GetLastWin32Error();
        return new DiskIOException($"Disk {op} failed on {Path} at offset {offset}: {new Win32Exception(code).Message}", code);
    }

    public void Dispose() => Handle.Dispose();
}

/// <summary>An I/O failure that keeps the Windows error code (e.g. 1 = incorrect function, 5 = access denied).</summary>
public sealed class DiskIOException : IOException
{
    public int Win32Error { get; }

    public DiskIOException(string message, int win32Error) : base(message) => Win32Error = win32Error;
}
