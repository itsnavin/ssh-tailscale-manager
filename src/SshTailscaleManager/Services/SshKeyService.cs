using System.Security.Principal;
using System.Text.RegularExpressions;

namespace SshTailscaleManager.Services;

public enum SshKeyType
{
    Ed25519,
    Rsa4096,
}

public sealed record KeyGenRequest(
    SshKeyType Type,
    string Directory,
    string FileName,
    string Comment,
    string Passphrase,
    bool Overwrite);

public sealed record KeyGenResult(bool Success, string Message, string PrivatePath, string PublicPath, string PublicKey, string Fingerprint)
{
    public static KeyGenResult Fail(string message) => new(false, message, "", "", "", "");
}

public static class SshKeyService
{
    public const int MinPassphraseLength = 5; // ssh-keygen refuses shorter passphrases

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    public static string DefaultFileName(SshKeyType type) => type == SshKeyType.Ed25519 ? "id_ed25519" : "id_rsa";

    /// <summary>Finds the Windows OpenSSH client's ssh-keygen.exe (optional Windows feature), then PATH.</summary>
    public static string? FindSshKeygen()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe"),
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        candidates.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 0)
            .Select(p => Path.Combine(p, "ssh-keygen.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static Task<ProcessResult> InstallClientAsync() => ProcessRunner.PowerShellAsync(
        "Add-WindowsCapability -Online -Name 'OpenSSH.Client~~~~0.0.1.0' | Out-String",
        TimeSpan.FromMinutes(15));

    /// <summary>Returns an error message, or null if the file name is acceptable (a plain name, no path parts).</summary>
    public static string? ValidateFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a file name for the key (e.g. id_ed25519).";
        if (name != name.Trim() || name.EndsWith('.')) return "The file name can't start/end with spaces or end with a dot.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(':'))
            return "The file name contains characters that aren't allowed. Use just a name, not a path.";
        if (name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)) return "Don't add .pub: the public key file gets that extension automatically.";
        return null;
    }

    public static string? ValidatePassphrase(string passphrase, string confirm)
    {
        if (passphrase != confirm) return "The passphrases don't match.";
        if (passphrase.Length > 0 && passphrase.Length < MinPassphraseLength)
            return $"A passphrase must be at least {MinPassphraseLength} characters (or leave it empty for none).";
        return null;
    }

    public static IReadOnlyList<string> BuildArguments(KeyGenRequest r, string privatePath)
    {
        var args = new List<string> { "-q", "-t", r.Type == SshKeyType.Ed25519 ? "ed25519" : "rsa" };
        if (r.Type == SshKeyType.Rsa4096) args.AddRange(new[] { "-b", "4096" });
        args.AddRange(new[] { "-f", privatePath, "-C", r.Comment, "-N", r.Passphrase });
        return args;
    }

    public static string DefaultComment() => $"{Environment.UserName}@{Environment.MachineName}";

    public static async Task<KeyGenResult> GenerateAsync(KeyGenRequest r)
    {
        var keygen = FindSshKeygen();
        if (keygen is null)
            return KeyGenResult.Fail("ssh-keygen.exe was not found. Install the \"OpenSSH Client\" Windows feature (button above) and try again.");

        var nameError = ValidateFileName(r.FileName);
        if (nameError is not null) return KeyGenResult.Fail(nameError);
        if (string.IsNullOrWhiteSpace(r.Directory)) return KeyGenResult.Fail("Choose a folder for the key.");

        var privatePath = Path.GetFullPath(Path.Combine(r.Directory, r.FileName));
        var publicPath = privatePath + ".pub";
        if ((File.Exists(privatePath) || File.Exists(publicPath)) && !r.Overwrite)
            return KeyGenResult.Fail($"{privatePath} (or its .pub) already exists. Nothing was changed; choose another name or confirm overwrite.");

        try
        {
            Directory.CreateDirectory(r.Directory);
            // ssh-keygen would prompt interactively about overwriting, so remove the old pair after explicit confirmation.
            if (File.Exists(privatePath)) File.Delete(privatePath);
            if (File.Exists(publicPath)) File.Delete(publicPath);
        }
        catch (Exception ex)
        {
            return KeyGenResult.Fail("Could not prepare the key folder: " + ex.Message);
        }

        var run = await ProcessRunner.RunAsync(keygen, BuildArguments(r, privatePath), TimeSpan.FromSeconds(60));
        if (!run.Success || !File.Exists(privatePath) || !File.Exists(publicPath))
            return KeyGenResult.Fail("ssh-keygen failed: " + Scrub(run.Combined, r.Passphrase));

        var acl = await FixPrivateKeyPermissionsAsync(privatePath);
        var publicKey = (await File.ReadAllTextAsync(publicPath)).Trim();
        var fp = await GetFingerprintAsync(publicPath);
        var message = $"Created {privatePath} and {publicPath}." + (acl.Success ? "" : Environment.NewLine + "! Could not lock down permissions: " + acl.Combined);
        return new KeyGenResult(true, message, privatePath, publicPath, publicKey, fp);
    }

    public static async Task<string> GetFingerprintAsync(string publicPath)
    {
        var keygen = FindSshKeygen();
        if (keygen is null || !File.Exists(publicPath)) return "";
        var r = await ProcessRunner.RunAsync(keygen, new[] { "-l", "-E", "sha256", "-f", publicPath }, TimeSpan.FromSeconds(15));
        return r.Success ? r.StdOut.Trim() : "";
    }

    /// <summary>Makes the private key readable only by the current user (OpenSSH rejects keys with broader access).</summary>
    public static async Task<ProcessResult> FixPrivateKeyPermissionsAsync(string privatePath)
    {
        if (!File.Exists(privatePath)) return ProcessResult.Fail($"{privatePath} does not exist.");
        using var id = WindowsIdentity.GetCurrent();
        if (id.User is null) return ProcessResult.Fail("Could not determine the current user.");
        var icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
        return await ProcessRunner.RunAsync(icacls,
            new[] { privatePath, "/inheritance:r", "/grant:r", $"*{id.User.Value}:F" }, TimeSpan.FromSeconds(30));
    }

    public static string BuildSshCommand(string host, int port, string user, string privatePath)
    {
        var target = user.Contains(' ') ? $"\"{user}@{host}\"" : $"{user}@{host}";
        var portPart = port == 22 ? "" : $" -p {port}";
        return $"ssh -i \"{privatePath}\"{portPart} {target}";
    }

    public static string BuildConfigSnippet(string alias, string host, int port, string user, string privatePath)
    {
        var lines = new List<string>
        {
            $"Host {alias}",
            $"    HostName {host}",
            $"    User {user}",
        };
        if (port != 22) lines.Add($"    Port {port}");
        lines.Add($"    IdentityFile \"{privatePath}\"");
        lines.Add("    IdentitiesOnly yes");
        return string.Join(Environment.NewLine, lines);
    }

    public static string SanitizeAlias(string name) => Regex.Replace(name.Trim(), @"[^A-Za-z0-9._-]+", "-").Trim('-').ToLowerInvariant() is { Length: > 0 } a ? a : "my-pc";

    private static string Scrub(string text, string secret) =>
        secret.Length == 0 ? text : text.Replace(secret, "***");
}
