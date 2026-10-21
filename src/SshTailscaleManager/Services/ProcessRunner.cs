using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SshTailscaleManager.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public string Combined =>
        string.Join(Environment.NewLine, new[] { StdOut.Trim(), StdErr.Trim() }.Where(s => s.Length > 0));

    public static ProcessResult Ok(string message) => new(0, message, "");
    public static ProcessResult Fail(string message) => new(-1, "", message);
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return ProcessResult.Fail($"Could not start {fileName}: {ex.Message}");
        }

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            return new ProcessResult(-1, await outTask, (await errTask) + Environment.NewLine + "Timed out.");
        }

        return new ProcessResult(proc.ExitCode, await outTask, await errTask);
    }

    /// <summary>Runs a Windows PowerShell script via -EncodedCommand so no quoting/escaping is needed.</summary>
    public static async Task<ProcessResult> PowerShellAsync(string script, TimeSpan? timeout = null)
    {
        var full = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
                   "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8;\n" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var r = await RunAsync(ps, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded }, timeout);
        return r with { StdErr = CleanClixml(r.StdErr) };
    }

    // Windows PowerShell may emit stderr as CLIXML when output is redirected; extract the readable error text.
    private static string CleanClixml(string s)
    {
        if (!s.TrimStart().StartsWith("#< CLIXML", StringComparison.Ordinal)) return s;
        var parts = Regex.Matches(s, "<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline)
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value.Replace("_x000D_", "").Replace("_x000A_", "\n")));
        return string.Concat(parts).Trim();
    }
}
