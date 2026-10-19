using System.Management;

namespace DiskImager.Core;

/// <summary>
/// A Volume Shadow Copy of one volume, so a live (in-use) volume such as C: can be read
/// in a consistent state. Created and deleted through WMI's Win32_ShadowCopy.
/// </summary>
public sealed class VssSnapshot : IDisposable
{
    public string Id { get; }
    /// <summary>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN</summary>
    public string DevicePath { get; }

    private VssSnapshot(string id, string devicePath)
    {
        Id = id;
        DevicePath = devicePath;
    }

    public static VssSnapshot Create(string volumeRoot)
    {
        using var cls = new ManagementClass(@"root\cimv2", "Win32_ShadowCopy", null);
        using var inParams = cls.GetMethodParameters("Create");
        inParams["Volume"] = volumeRoot;
        inParams["Context"] = "ClientAccessible";
        using var outParams = cls.InvokeMethod("Create", inParams, null);
        uint rv = Convert.ToUInt32(outParams["ReturnValue"]);
        if (rv != 0)
            throw new InvalidOperationException($"Could not create a snapshot of {volumeRoot}: {Describe(rv)}");

        string id = (string)outParams["ShadowID"];
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", $"SELECT * FROM Win32_ShadowCopy WHERE ID='{id}'");
        foreach (ManagementObject mo in searcher.Get())
            using (mo)
                return new VssSnapshot(id, (string)mo["DeviceObject"]);

        throw new InvalidOperationException($"Snapshot {id} was created but could not be found.");
    }

    private static string Describe(uint code) => code switch
    {
        1 => "access denied",
        2 => "invalid argument",
        3 => "volume not found",
        4 => "volume not supported by VSS",
        5 => "unsupported shadow copy context",
        6 => "not enough free space for the snapshot",
        7 => "volume is in use",
        8 => "maximum number of shadow copies reached",
        9 => "another shadow copy operation is in progress",
        10 => "a shadow copy provider vetoed the operation",
        11 => "shadow copy provider not registered",
        12 => "shadow copy provider failure",
        _ => $"error {code}",
    };

    public void Dispose()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", $"SELECT * FROM Win32_ShadowCopy WHERE ID='{Id}'");
            foreach (ManagementObject mo in searcher.Get())
                using (mo) mo.Delete();
        }
        catch
        {
            // Best effort: Windows also cleans up orphaned snapshots under storage pressure.
        }
    }
}
