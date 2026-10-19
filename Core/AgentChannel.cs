using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace DiskImager.Core;

public static class UserAction
{
    public const string Start = "Start";
    public const string Postpone = "Postpone";
    public const string Cancel = "Cancel";
}

public sealed class UserResponse
{
    public string Action { get; set; } = UserAction.Start;
    public int Minutes { get; set; }
    public string User { get; set; } = "";
}

/// <summary>
/// Messages between the tray helper (running as the signed-in user) and scheduled runs (running as SYSTEM),
/// exchanged as small files in %ProgramData%\DiskImager\Requests, a folder ordinary users may write to.
/// </summary>
public static class AgentChannel
{
    public static string Folder => Path.Combine(JobStore.DataFolder, "Requests");
    private static string ExitFlag => Path.Combine(Folder, "agents-exit.flag");

    /// <summary>Creates the Requests folder so standard users (the tray helper) can write to it.</summary>
    public static void EnsureFolder()
    {
        try
        {
            var dir = new DirectoryInfo(Folder);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var rule = new FileSystemAccessRule(users, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
            if (!dir.Exists)
            {
                Directory.CreateDirectory(JobStore.DataFolder);
                var ds = new DirectorySecurity();
                ds.AddAccessRule(rule);
                dir.Create(ds);
            }
            else
            {
                var ds = dir.GetAccessControl();
                ds.AddAccessRule(rule);
                dir.SetAccessControl(ds);
            }
        }
        catch
        {
            // Not fatal: without it the pop-up can't answer, and backups simply start on their own.
        }
    }

    // ---- tray helper -> scheduled run

    public static void Respond(Guid promptId, string action, int minutes = 0) =>
        WriteJson(Path.Combine(Folder, $"response-{promptId:N}.json"),
            new UserResponse { Action = action, Minutes = minutes, User = Environment.UserName });

    public static void RequestCancel(int processId) =>
        WriteJson(Path.Combine(Folder, $"cancel-{processId}.json"), new UserResponse { Action = UserAction.Cancel, User = Environment.UserName });

    /// <summary>The tray helper calls this every few seconds so runs know someone can answer a pop-up.</summary>
    public static void Heartbeat() =>
        WriteJson(Path.Combine(Folder, $"agent-{Environment.ProcessId}.json"), new { At = DateTime.UtcNow, User = Environment.UserName });

    public static void RemoveHeartbeat()
    {
        try { File.Delete(Path.Combine(Folder, $"agent-{Environment.ProcessId}.json")); } catch { }
    }

    // ---- scheduled run side

    public static bool AnyAgentAlive()
    {
        try
        {
            return Directory.EnumerateFiles(Folder, "agent-*.json")
                .Any(f => DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromSeconds(20));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Waits for the user's answer to a pop-up; null means no answer in time.</summary>
    public static UserResponse? WaitForResponse(Guid promptId, TimeSpan timeout)
    {
        string path = Path.Combine(Folder, $"response-{promptId:N}.json");
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(path))
            {
                try
                {
                    var r = JsonSerializer.Deserialize<UserResponse>(File.ReadAllText(path));
                    File.Delete(path);
                    return r;
                }
                catch (IOException) { /* still being written */ }
            }
            Thread.Sleep(250);
        }
        return null;
    }

    public static UserResponse? CancelRequested(int processId)
    {
        string path = Path.Combine(Folder, $"cancel-{processId}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var r = JsonSerializer.Deserialize<UserResponse>(File.ReadAllText(path));
            File.Delete(path);
            return r;
        }
        catch
        {
            return null;
        }
    }

    // ---- updating the installed copy: tray helpers exit so their exe can be replaced

    public static void AskAgentsToExit()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(ExitFlag, DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }

    public static bool ExitRequestedSince(DateTime startedUtc)
    {
        try { return File.Exists(ExitFlag) && File.GetLastWriteTimeUtc(ExitFlag) > startedUtc; }
        catch { return false; }
    }

    private static void WriteJson(string path, object value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }
}
