using System.Diagnostics;

namespace DiskImager.Rescue.Platform;

/// <summary>Runs a command and returns its exit code and output (stdout + stderr).</summary>
public static class Shell
{
    public static (int Code, string Output) Run(string exe, IEnumerable<string> args, string? stdin = null, int timeoutSeconds = 600)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C"; // English, parseable output
        using var p = Process.Start(psi)!;
        if (stdin != null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSeconds * 1000)) { try { p.Kill(true); } catch { } return (-1, $"{exe} took too long"); }
        return (p.ExitCode, (outp.Result + err.Result).Trim());
    }

    public static (int Code, string Output) Run(string exe, params string[] args) => Run(exe, args, null);

    public static void Start(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch { }
    }
}
