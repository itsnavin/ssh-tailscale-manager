using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace SshTailscaleManager.Services;

public enum KeyTarget
{
    Administrators,
    User,
}

public static class KeyService
{
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string SystemSid = "S-1-5-18";

    private static readonly Regex KeyRegex = new(
        @"^(?:ssh-(?:ed25519|rsa|dss)|ecdsa-sha2-nistp(?:256|384|521)|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-nistp256@openssh\.com)\s+[A-Za-z0-9+/]+={0,3}(?:\s+.*)?$",
        RegexOptions.Compiled);

    // sshd rejects authorized_keys files with a BOM or UTF-16 encoding, so always write UTF-8 without BOM.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static bool IsCurrentUserAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        return id.Groups?.Any(g => g.Value == AdministratorsSid) == true;
    }

    public static string GetPath(KeyTarget target) => target == KeyTarget.Administrators
        ? Path.Combine(SshService.SshDataDir, "administrators_authorized_keys")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys");

    public static List<string> ReadKeys(KeyTarget target)
    {
        var path = GetPath(target);
        if (!File.Exists(path)) return new List<string>();
        return File.ReadAllLines(path)
            .Select(l => l.Trim().TrimStart('\uFEFF'))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
    }

    public static bool IsValidKey(string line) => KeyRegex.IsMatch(line.Trim());

    public static async Task<ProcessResult> AddKeysAsync(KeyTarget target, string text)
    {
        var candidates = text.Split('\n')
            .Select(l => Regex.Replace(l.Trim(), @"[ \t]+", " "))
            .Where(l => l.Length > 0)
            .ToList();
        if (candidates.Count == 0) return ProcessResult.Fail("Paste a public key first (e.g. the contents of id_ed25519.pub).");

        var invalid = candidates.FirstOrDefault(l => !IsValidKey(l));
        if (invalid is not null)
        {
            var preview = invalid.Length > 60 ? invalid[..60] + "…" : invalid;
            return ProcessResult.Fail($"Not a valid OpenSSH public key: {preview}\nPaste the .pub key (starts with ssh-ed25519, ssh-rsa or ecdsa-sha2-…), never the private key.");
        }

        var path = GetPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).Select(l => l.TrimStart('\uFEFF')).ToList() : new List<string>();
        var existingBodies = lines.Select(KeyBody).Where(b => b is not null).ToHashSet();

        var added = 0;
        foreach (var key in candidates)
        {
            if (existingBodies.Add(KeyBody(key)))
            {
                lines.Add(key);
                added++;
            }
        }

        WriteLines(path, lines);
        var acl = await FixPermissionsAsync(target);
        var message = $"Added {added} key(s) to {path}" + (candidates.Count > added ? $" ({candidates.Count - added} already present)." : ".");
        return new ProcessResult(acl.ExitCode, message + Environment.NewLine + acl.StdOut.Trim(), acl.StdErr);
    }

    public static async Task<ProcessResult> RemoveKeyAsync(KeyTarget target, string key)
    {
        var path = GetPath(target);
        if (!File.Exists(path)) return ProcessResult.Fail($"{path} does not exist.");
        var lines = File.ReadAllLines(path).Select(l => l.TrimStart('\uFEFF')).ToList();
        var removed = lines.RemoveAll(l => l.Trim() == key.Trim());
        WriteLines(path, lines);
        var acl = await FixPermissionsAsync(target);
        return new ProcessResult(acl.ExitCode, $"Removed {removed} key(s) from {path}.{Environment.NewLine}{acl.StdOut.Trim()}", acl.StdErr);
    }

    /// <summary>
    /// Applies the ACLs sshd requires: administrators_authorized_keys must only be accessible by
    /// Administrators and SYSTEM; a user's authorized_keys by the user, SYSTEM and Administrators.
    /// Also rewrites the file as UTF-8 without BOM.
    /// </summary>
    public static async Task<ProcessResult> FixPermissionsAsync(KeyTarget target)
    {
        var path = GetPath(target);
        if (!File.Exists(path)) return ProcessResult.Fail($"{path} does not exist yet. Add a key first.");

        WriteLines(path, File.ReadAllLines(path).Select(l => l.TrimStart('\uFEFF')));

        var icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
        var args = new List<string> { path, "/inheritance:r", "/grant", $"*{AdministratorsSid}:F", "/grant", $"*{SystemSid}:F" };
        if (target == KeyTarget.User)
        {
            using var id = WindowsIdentity.GetCurrent();
            if (id.User is not null) args.AddRange(new[] { "/grant", $"*{id.User.Value}:F" });
        }

        var grant = await ProcessRunner.RunAsync(icacls, args, TimeSpan.FromSeconds(30));
        if (!grant.Success) return grant;

        // Drop broad explicit grants (Authenticated Users, Users, Everyone) that would make sshd ignore the file.
        await ProcessRunner.RunAsync(icacls, new[] { path, "/remove:g", "*S-1-5-11", "*S-1-5-32-545", "*S-1-1-0" }, TimeSpan.FromSeconds(30));
        return ProcessResult.Ok($"Permissions fixed on {path}.");
    }

    private static string? KeyBody(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static void WriteLines(string path, IEnumerable<string> lines)
    {
        var content = string.Join("\n", lines.Where(l => l.Trim().Length > 0)) ;
        if (content.Length > 0) content += "\n";
        File.WriteAllText(path, content, Utf8NoBom);
    }
}
