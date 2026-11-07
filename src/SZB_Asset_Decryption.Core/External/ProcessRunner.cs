using System.Diagnostics;
using System.Text;

namespace SzbAssetDecryption.Core.External;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs an external executable with argument-list semantics (no shell, no quoting bugs) and
/// captures both streams. All external tools in this project are invoked through here.
/// </summary>
public static class ProcessRunner
{
    public static ProcessResult Run(
        string exePath,
        IEnumerable<string> args,
        int timeoutMs = 600_000,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new ProcessResult(-1, stdout.ToString(), "进程超时被终止\n" + stderr);
        }

        proc.WaitForExit();     // flush async readers
        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
