using System.Text.Json;
using System.Text.RegularExpressions;

namespace SshTailscaleManager.Services;

public sealed class SshStatus
{
    public string CapabilityState { get; set; } = "Unknown";
    public bool ServiceExists { get; set; }
    public string ServiceState { get; set; } = "";
    public string StartMode { get; set; } = "";
    public bool FirewallRuleExists { get; set; }
    public bool FirewallRuleEnabled { get; set; }
    public string? DefaultShell { get; set; }
    public int Port { get; set; } = 22;
    public string? Error { get; set; }

    public bool IsRunning => ServiceExists && ServiceState.Equals("Running", StringComparison.OrdinalIgnoreCase);

    public bool IsInstalled => ServiceExists || CapabilityState.Equals("Installed", StringComparison.OrdinalIgnoreCase);
}

public sealed record ShellOption(string Name, string? Path)
{
    public override string ToString() => Name;
}

public static class SshService
{
    public const string FirewallRuleName = "OpenSSH-Server-In-TCP";

    public static string SshDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh");

    public static string ConfigPath => Path.Combine(SshDataDir, "sshd_config");

    private const string StatusScript = """
        $cap = 'Unknown'
        try {
            $c = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
            if ($c) { $cap = [string]$c.State }
        } catch { $cap = 'Unknown' }
        $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='sshd'" -ErrorAction SilentlyContinue
        $fw = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
        $shell = $null
        try { $shell = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -ErrorAction Stop).DefaultShell } catch { }
        [pscustomobject]@{
            CapabilityState     = $cap
            ServiceExists       = [bool]$svc
            ServiceState        = if ($svc) { [string]$svc.State } else { '' }
            StartMode           = if ($svc) { [string]$svc.StartMode } else { '' }
            FirewallRuleExists  = [bool]$fw
            FirewallRuleEnabled = if ($fw) { [string]$fw.Enabled -eq 'True' } else { $false }
            DefaultShell        = $shell
        } | ConvertTo-Json -Compress
        """;

    public static async Task<SshStatus> GetStatusAsync()
    {
        var r = await ProcessRunner.PowerShellAsync(StatusScript, TimeSpan.FromMinutes(2));
        var json = r.StdOut.Trim();
        var start = json.IndexOf('{');
        SshStatus status;
        if (start < 0)
        {
            status = new SshStatus { Error = r.Combined.Length > 0 ? r.Combined : "No status output." };
        }
        else
        {
            try
            {
                status = JsonSerializer.Deserialize<SshStatus>(json[start..],
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SshStatus();
            }
            catch (JsonException ex)
            {
                status = new SshStatus { Error = "Could not parse status: " + ex.Message };
            }
        }
        status.Port = GetConfiguredPort();
        return status;
    }

    /// <summary>Reads the global Port directive from sshd_config (defaults to 22).</summary>
    public static int GetConfiguredPort()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return 22;
            foreach (var raw in File.ReadLines(ConfigPath))
            {
                var line = raw.Trim();
                if (line.StartsWith('#')) continue;
                if (line.StartsWith("Match ", StringComparison.OrdinalIgnoreCase)) break;
                var m = Regex.Match(line, @"^Port\s+(\d+)", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var port)) return port;
            }
        }
        catch
        {
            // Unreadable config: fall back to the default port.
        }
        return 22;
    }

    public static Task<ProcessResult> InstallAsync() => ProcessRunner.PowerShellAsync("""
        $c = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
        if (-not $c) { throw 'OpenSSH.Server capability not found on this system.' }
        if ($c.State -eq 'Installed') { 'OpenSSH Server is already installed.'; return }
        Add-WindowsCapability -Online -Name $c.Name | Format-List | Out-String
        'OpenSSH Server installed.'
        """, TimeSpan.FromMinutes(30));

    public static Task<ProcessResult> StartAsync() =>
        ProcessRunner.PowerShellAsync("Start-Service -Name sshd; 'sshd status: ' + (Get-Service -Name sshd).Status");

    public static Task<ProcessResult> StopAsync() =>
        ProcessRunner.PowerShellAsync("Stop-Service -Name sshd -Force; 'sshd status: ' + (Get-Service -Name sshd).Status");

    public static Task<ProcessResult> RestartAsync() =>
        ProcessRunner.PowerShellAsync("Restart-Service -Name sshd -Force; 'sshd status: ' + (Get-Service -Name sshd).Status");

    public static Task<ProcessResult> SetStartupTypeAsync(bool automatic) =>
        ProcessRunner.PowerShellAsync(
            $"Set-Service -Name sshd -StartupType {(automatic ? "Automatic" : "Manual")}; " +
            "'sshd startup type: ' + (Get-Service -Name sshd).StartType");

    public static Task<ProcessResult> EnableFirewallAsync(int port) => ProcessRunner.PowerShellAsync($$"""
        $rule = Get-NetFirewallRule -Name '{{FirewallRuleName}}' -ErrorAction SilentlyContinue
        if ($rule) {
            Set-NetFirewallRule -Name '{{FirewallRuleName}}' -Enabled True -LocalPort {{port}}
        } else {
            New-NetFirewallRule -Name '{{FirewallRuleName}}' -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort {{port}} | Out-Null
        }
        'Firewall rule enabled for inbound TCP port {{port}}.'
        """);

    public static Task<ProcessResult> DisableFirewallAsync() => ProcessRunner.PowerShellAsync($"""
        Disable-NetFirewallRule -Name '{FirewallRuleName}'
        'Firewall rule disabled.'
        """);

    public static IReadOnlyList<ShellOption> GetShellOptions()
    {
        var list = new List<ShellOption>
        {
            new("Default (cmd.exe)", null),
            new("Windows PowerShell", Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")),
        };
        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh)) list.Add(new ShellOption("PowerShell 7 (pwsh)", pwsh));
        return list;
    }

    public static Task<ProcessResult> SetDefaultShellAsync(string? shellPath)
    {
        if (string.IsNullOrEmpty(shellPath))
        {
            return ProcessRunner.PowerShellAsync("""
                Remove-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -ErrorAction SilentlyContinue
                'Default shell reset to cmd.exe.'
                """);
        }

        var escaped = shellPath.Replace("'", "''");
        return ProcessRunner.PowerShellAsync($$"""
            if (-not (Test-Path 'HKLM:\SOFTWARE\OpenSSH')) { New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' | Out-Null }
            New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Value '{{escaped}}' -PropertyType String -Force | Out-Null
            'Default shell set to {{escaped}}. New SSH sessions will use it.'
            """);
    }
}
