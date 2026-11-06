using SshTailscaleManager.Services;
using Xunit;

namespace SshTailscaleManager.Tests;

public class SshKeyServiceTests
{
    [Theory]
    [InlineData("id_ed25519", true)]
    [InlineData("my key", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("..\\evil", false)]
    [InlineData("a/b", false)]
    [InlineData("C:key", false)]
    [InlineData("id.pub", false)]
    [InlineData("trailing.", false)]
    public void ValidateFileName(string name, bool ok) =>
        Assert.Equal(ok, SshKeyService.ValidateFileName(name) is null);

    [Fact]
    public void PassphraseRules()
    {
        Assert.Null(SshKeyService.ValidatePassphrase("", ""));
        Assert.Null(SshKeyService.ValidatePassphrase("hunter22", "hunter22"));
        Assert.NotNull(SshKeyService.ValidatePassphrase("abc", "abc"));
        Assert.NotNull(SshKeyService.ValidatePassphrase("hunter22", "hunter23"));
    }

    [Fact]
    public void ArgumentsForEd25519AndRsa()
    {
        var ed = SshKeyService.BuildArguments(new KeyGenRequest(SshKeyType.Ed25519, "d", "k", "c", "", false), @"C:\k");
        Assert.Equal(new[] { "-q", "-t", "ed25519", "-f", @"C:\k", "-C", "c", "-N", "" }, ed);
        var rsa = SshKeyService.BuildArguments(new KeyGenRequest(SshKeyType.Rsa4096, "d", "k", "c", "pw123", false), @"C:\k");
        Assert.Contains("rsa", rsa);
        Assert.Equal("4096", rsa[rsa.ToList().IndexOf("-b") + 1]);
    }

    [Fact]
    public void SnippetsIncludePortOnlyWhenNonDefault()
    {
        Assert.Equal(@"ssh -i ""C:\k"" me@100.1.1.1", SshKeyService.BuildSshCommand("100.1.1.1", 22, "me", @"C:\k"));
        Assert.Equal(@"ssh -i ""C:\k"" -p 2222 ""my name@h""", SshKeyService.BuildSshCommand("h", 2222, "my name", @"C:\k"));
        var cfg = SshKeyService.BuildConfigSnippet("pc", "h", 2222, "me", @"C:\k");
        Assert.Contains("Port 2222", cfg);
        Assert.DoesNotContain("Port", SshKeyService.BuildConfigSnippet("pc", "h", 22, "me", @"C:\k"));
    }

    [Fact]
    public void RemoteValidationAndCopyCommand()
    {
        Assert.Null(SshKeyService.ValidateRemote("pi", "192.168.1.5", 22));
        Assert.NotNull(SshKeyService.ValidateRemote("a b", "h", 22));
        Assert.NotNull(SshKeyService.ValidateRemote("u", "h; calc", 22));
        Assert.NotNull(SshKeyService.ValidateRemote("u", "h&calc", 22));
        Assert.NotNull(SshKeyService.ValidateRemote("u", "h", 0));
        var cmd = SshKeyService.BuildCopyToHostArguments(@"C:\k.pub", "pi", "host", 2222);
        Assert.StartsWith("/k type \"C:\\k.pub\" | ssh -p 2222 pi@host ", cmd);
        Assert.Contains("authorized_keys", cmd);
    }

    [Fact]
    public async Task ExportCopiesPublicAndOptionallyPrivate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshexport-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var priv = Path.Combine(dir, "k");
            File.WriteAllText(priv, "PRIVATE");
            File.WriteAllText(priv + ".pub", "ssh-ed25519 AAAA test");
            var dest = Path.Combine(dir, "out");
            var pubOnly = await SshKeyService.ExportAsync(priv, dest, false);
            Assert.True(pubOnly.Success);
            Assert.True(File.Exists(Path.Combine(dest, "k.pub")));
            Assert.False(File.Exists(Path.Combine(dest, "k")));
            var both = await SshKeyService.ExportAsync(priv, dest, true);
            Assert.True(both.Success, both.Combined);
            Assert.Equal("PRIVATE", File.ReadAllText(Path.Combine(dest, "k")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AliasIsSanitized() => Assert.Equal("my-pc-1", SshKeyService.SanitizeAlias(" My PC 1 "));

    [Fact]
    public async Task GenerateRefusesOverwriteAndGeneratesWhenKeygenPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshkeytest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "k"), "existing");
            var refused = await SshKeyService.GenerateAsync(new KeyGenRequest(SshKeyType.Ed25519, dir, "k", "c", "", false));
            if (SshKeyService.FindSshKeygen() is not null)
            {
                Assert.False(refused.Success);
                Assert.Equal("existing", File.ReadAllText(Path.Combine(dir, "k")));

                var ok = await SshKeyService.GenerateAsync(new KeyGenRequest(SshKeyType.Ed25519, dir, "k", "test@pc", "", true));
                Assert.True(ok.Success, ok.Message);
                Assert.StartsWith("ssh-ed25519 ", ok.PublicKey);
                Assert.True(KeyService.IsValidKey(ok.PublicKey));
                Assert.Contains("SHA256:", ok.Fingerprint);
                var acl = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls.exe", $"\"{ok.PrivatePath}\"") { RedirectStandardOutput = true })!;
                var aclText = await acl.StandardOutput.ReadToEndAsync();
                Assert.Contains(Environment.UserName, aclText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("BUILTIN\\Users", aclText);
                Assert.DoesNotContain("Everyone", aclText);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
