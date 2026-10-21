using System.Text.Json;

namespace SshTailscaleManager.Services;

public sealed record TailscalePeer(string HostName, string DnsName, string Ip, string Os, string Online);

public sealed class TailscaleStatus
{
    public bool Installed { get; init; }
    public string BackendState { get; init; } = "Unknown";
    public string? HostName { get; init; }
    public string? DnsName { get; init; }
    public IReadOnlyList<string> Ips { get; init; } = Array.Empty<string>();
    public string? Tailnet { get; init; }
    public IReadOnlyList<TailscalePeer> Peers { get; init; } = Array.Empty<TailscalePeer>();
    public string? Error { get; init; }

    public bool IsRunning => BackendState.Equals("Running", StringComparison.OrdinalIgnoreCase);
    public string? IPv4 => Ips.FirstOrDefault(ip => ip.Contains('.'));
}

public static class TailscaleService
{
    public const string AdminConsoleUrl = "https://login.tailscale.com/admin/machines";

    public static string? FindExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
        };
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        candidates.AddRange(pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim().Trim('"'), "tailscale.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<TailscaleStatus> GetStatusAsync()
    {
        var exe = FindExecutable();
        if (exe is null) return new TailscaleStatus { Installed = false, BackendState = "Not installed" };

        var r = await ProcessRunner.RunAsync(exe, new[] { "status", "--json" }, TimeSpan.FromSeconds(20));
        var json = r.StdOut.Trim();
        if (!json.StartsWith('{'))
        {
            return new TailscaleStatus { Installed = true, BackendState = "Unavailable", Error = r.Combined };
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var self = root.TryGetProperty("Self", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;

            string? tailnetName = null, suffix = Str(root, "MagicDNSSuffix");
            if (root.TryGetProperty("CurrentTailnet", out var ct) && ct.ValueKind == JsonValueKind.Object)
            {
                tailnetName = Str(ct, "Name");
                suffix ??= Str(ct, "MagicDNSSuffix");
            }
            var tailnet = tailnetName is not null && suffix is not null && tailnetName != suffix
                ? $"{suffix} ({tailnetName})"
                : tailnetName ?? suffix;

            var peers = new List<TailscalePeer>();
            if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in peerMap.EnumerateObject())
                {
                    var v = p.Value;
                    peers.Add(new TailscalePeer(
                        Str(v, "HostName") ?? "",
                        TrimDot(Str(v, "DNSName")) ?? "",
                        Ips(v).FirstOrDefault(ip => ip.Contains('.')) ?? Ips(v).FirstOrDefault() ?? "",
                        Str(v, "OS") ?? "",
                        IsOnline(v) ? "Yes" : "No"));
                }
            }

            return new TailscaleStatus
            {
                Installed = true,
                BackendState = Str(root, "BackendState") ?? "Unknown",
                HostName = Str(self, "HostName"),
                DnsName = TrimDot(Str(self, "DNSName")),
                Ips = Ips(self),
                Tailnet = tailnet,
                Peers = peers.OrderByDescending(p => p.Online).ThenBy(p => p.HostName, StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }
        catch (JsonException ex)
        {
            return new TailscaleStatus { Installed = true, BackendState = "Unavailable", Error = "Could not parse status: " + ex.Message };
        }
    }

    public static Task<ProcessResult> UpAsync() => RunAsync("up");

    public static Task<ProcessResult> DownAsync() => RunAsync("down");

    private static async Task<ProcessResult> RunAsync(params string[] args)
    {
        var exe = FindExecutable();
        if (exe is null) return ProcessResult.Fail("tailscale.exe was not found. Install Tailscale from https://tailscale.com/download.");
        return await ProcessRunner.RunAsync(exe, args, TimeSpan.FromSeconds(60));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static List<string> Ips(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("TailscaleIPs", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : new List<string>();

    private static bool IsOnline(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Online", out var v) && v.ValueKind == JsonValueKind.True;

    private static string? TrimDot(string? s) => string.IsNullOrEmpty(s) ? null : s.TrimEnd('.');
}
