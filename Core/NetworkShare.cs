using System.ComponentModel;

namespace DiskImager.Core;

/// <summary>Logs on to an SMB share (NAS, file server) with explicit credentials.</summary>
public static class NetworkShare
{
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
    private const int ERROR_ALREADY_ASSIGNED = 85;

    public static bool IsNetworkPath(string path) => path.StartsWith(@"\\");

    /// <summary>\\server\share\folder\sub -> \\server\share</summary>
    public static string ShareRoot(string path)
    {
        var parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException($"'{path}' is not a valid network path. Use \\\\server\\share\\folder.");
        return $@"\\{parts[0]}\{parts[1]}";
    }

    public static void Connect(string path, string? user, string? password, Logger? log)
    {
        if (!IsNetworkPath(path) || string.IsNullOrWhiteSpace(user)) return;
        string root = ShareRoot(path);
        var nr = new Native.NETRESOURCE { dwType = 1 /* RESOURCETYPE_DISK */, lpRemoteName = root };

        int r = Native.WNetAddConnection2W(ref nr, password, user, 0);
        if (r == ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            // Already connected with other credentials: drop that connection and retry.
            Native.WNetCancelConnection2W(root, 0, true);
            r = Native.WNetAddConnection2W(ref nr, password, user, 0);
        }
        if (r != 0 && r != ERROR_ALREADY_ASSIGNED && r != ERROR_SESSION_CREDENTIAL_CONFLICT)
            throw new IOException($"Could not connect to {root} as {user}: {new Win32Exception(r).Message}");
        log?.Info($"Connected to {root} as {user}.");
    }

    /// <summary>Connects, creates the folder and writes/deletes a test file.</summary>
    public static void Test(string path, string? user, string? password)
    {
        Connect(path, user, password, null);
        Directory.CreateDirectory(path);
        string probe = System.IO.Path.Combine(path, $".diskimager-test-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "test");
        File.Delete(probe);
    }
}
