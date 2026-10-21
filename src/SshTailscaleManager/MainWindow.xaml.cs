using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SshTailscaleManager.Services;

namespace SshTailscaleManager;

public partial class MainWindow : Window
{
    private SshStatus? _ssh;
    private TailscaleStatus? _ts;

    public MainWindow()
    {
        InitializeComponent();
        ShellCombo.ItemsSource = SshService.GetShellOptions();
        ShellCombo.SelectedIndex = 0;

        ThemeCombo.ItemsSource = Enum.GetValues<AppTheme>();
        ThemeCombo.SelectedItem = ThemeManager.Preference;
        ThemeCombo.SelectionChanged += (_, _) =>
        {
            if (ThemeCombo.SelectedItem is AppTheme theme) ThemeManager.SetPreference(theme);
        };

        // Default to the file sshd actually reads for this account.
        if (KeyService.IsCurrentUserAdmin()) AdminKeysRadio.IsChecked = true;
        else UserKeysRadio.IsChecked = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this);
    }

    private KeyTarget CurrentKeyTarget => AdminKeysRadio.IsChecked == true ? KeyTarget.Administrators : KeyTarget.User;

    // ---------- Refresh ----------

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private async void RefreshAll_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private async Task RefreshAllAsync()
    {
        SetBusy("Refreshing status…");
        try
        {
            var sshTask = SshService.GetStatusAsync();
            var tsTask = TailscaleService.GetStatusAsync();
            _ssh = await sshTask;
            _ts = await tsTask;
            ApplySshStatus();
            ApplyTailscaleStatus();
            RefreshKeys();
        }
        catch (Exception ex)
        {
            Log("✖ Refresh failed: " + ex.Message);
        }
        finally
        {
            UpdateDerivedInfo();
            SetBusy(null);
        }
    }

    private async Task RefreshSshAsync()
    {
        SetBusy("Refreshing SSH status…");
        try
        {
            _ssh = await SshService.GetStatusAsync();
            ApplySshStatus();
        }
        catch (Exception ex)
        {
            Log("✖ SSH refresh failed: " + ex.Message);
        }
        finally
        {
            UpdateDerivedInfo();
            SetBusy(null);
        }
    }

    private async Task RefreshTailscaleAsync()
    {
        SetBusy("Refreshing Tailscale status…");
        try
        {
            _ts = await TailscaleService.GetStatusAsync();
            ApplyTailscaleStatus();
        }
        catch (Exception ex)
        {
            Log("✖ Tailscale refresh failed: " + ex.Message);
        }
        finally
        {
            UpdateDerivedInfo();
            SetBusy(null);
        }
    }

    private void ApplySshStatus()
    {
        var s = _ssh;
        if (s is null) return;
        if (s.Error is not null) Log("! SSH status: " + s.Error);

        var capability = s.CapabilityState == "Unknown" && s.ServiceExists ? "Installed (sshd service found)" : s.CapabilityState;
        SetStatus(SshCapabilityText, capability, s.IsInstalled);
        SetStatus(SshServiceText, s.ServiceExists ? s.ServiceState : "Not installed", s.IsRunning);
        SetStatus(SshStartupText, s.ServiceExists ? FormatStartMode(s.StartMode) : "—",
            s.ServiceExists ? s.StartMode.Equals("Auto", StringComparison.OrdinalIgnoreCase) : null);
        SetStatus(SshPortText, s.Port.ToString(), null);
        SetStatus(SshFirewallText,
            s.FirewallRuleExists ? (s.FirewallRuleEnabled ? $"Enabled ({SshService.FirewallRuleName})" : $"Disabled ({SshService.FirewallRuleName})") : "Rule not found",
            s.FirewallRuleEnabled);
        SetStatus(SshShellText, string.IsNullOrEmpty(s.DefaultShell) ? "Default (cmd.exe)" : s.DefaultShell, null);

        InstallSshButton.IsEnabled = !s.IsInstalled;
        StartSshButton.IsEnabled = s.ServiceExists && !s.IsRunning;
        StopSshButton.IsEnabled = s.IsRunning;
        RestartSshButton.IsEnabled = s.ServiceExists;

        if (ShellCombo.ItemsSource is IReadOnlyList<ShellOption> options && options.Count > 0)
        {
            ShellCombo.SelectedItem = string.IsNullOrEmpty(s.DefaultShell)
                ? options[0]
                : options.FirstOrDefault(o => string.Equals(o.Path, s.DefaultShell, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }
    }

    private void ApplyTailscaleStatus()
    {
        var t = _ts;
        if (t is null) return;
        if (t.Error is not null) Log("! Tailscale: " + t.Error);

        SetStatus(TsStateText, t.Installed ? t.BackendState : "Not installed (https://tailscale.com/download)", t.IsRunning);
        TsHostText.Text = t.HostName ?? "—";
        TsDnsText.Text = t.DnsName ?? "—";
        TsIpsText.Text = t.Ips.Count > 0 ? string.Join(", ", t.Ips) : "—";
        TsTailnetText.Text = t.Tailnet ?? "—";
        PeersGrid.ItemsSource = t.Peers;
        TsUpButton.IsEnabled = t.Installed;
        TsDownButton.IsEnabled = t.Installed;
    }

    private void UpdateDerivedInfo()
    {
        var port = _ssh?.Port ?? SshService.GetConfiguredPort();
        var user = Environment.UserName;
        var lan = NetworkInfo.GetLanIPv4Addresses();
        var tsIp = _ts?.IsRunning == true ? _ts.IPv4 : null;
        var tsDns = _ts?.IsRunning == true ? _ts.DnsName : null;

        ConnTsIpText.Text = tsIp ?? "(Tailscale not connected)";
        ConnDnsText.Text = tsDns ?? "(MagicDNS name unavailable)";
        ConnLanText.Text = lan.FirstOrDefault() ?? "(no LAN address)";
        ConnLanOtherText.Text = lan.Count > 1 ? "Other local addresses: " + string.Join(", ", lan.Skip(1)) : "";
        ConnPortText.Text = port.ToString();
        ConnUserText.Text = user;

        var host = tsIp ?? lan.FirstOrDefault() ?? "HOST";
        var target = user.Contains(' ') ? $"\"{user}@{host}\"" : $"{user}@{host}";
        ConnCmdText.Text = port == 22 ? $"ssh {target}" : $"ssh -p {port} {target}";
        ConnUserHintText.Text = "Username is your local Windows account name. If you sign in to Windows with a Microsoft account, " +
                                "the password is your Microsoft account password (a Windows Hello PIN does not work over SSH)." +
                                (user.Contains(' ') ? " Your username contains spaces — Termius accepts it as-is; in a terminal wrap it in quotes." : "");

        var sshText = _ssh is null ? "unknown" : _ssh.ServiceExists ? _ssh.ServiceState : "not installed";
        var tsText = _ts is null ? "unknown" : _ts.BackendState + (tsIp is null ? "" : $" ({tsIp})");
        HeaderStatus.Text = $"sshd: {sshText}  ·  port {port}  ·  Tailscale: {tsText}  ·  user: {user}";
    }

    // ---------- Actions ----------

    private async Task<bool> RunActionAsync(string description, Func<Task<ProcessResult>> action)
    {
        SetBusy(description + "…");
        try
        {
            Log("> " + description);
            var r = await action();
            if (r.Combined.Length > 0) Log(r.Combined);
            Log(r.Success ? $"✔ {description} succeeded." : $"✖ {description} failed (exit code {r.ExitCode}).");
            return r.Success;
        }
        catch (Exception ex)
        {
            Log($"✖ {description} failed: {ex.Message}");
            return false;
        }
        finally
        {
            SetBusy(null);
        }
    }

    private async void InstallSsh_Click(object sender, RoutedEventArgs e)
    {
        Log("Installing the OpenSSH Server capability can take several minutes…");
        await RunActionAsync("Install OpenSSH Server", SshService.InstallAsync);
        await RefreshSshAsync();
    }

    private async void StartSsh_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("Start sshd", SshService.StartAsync);
        await RefreshSshAsync();
    }

    private async void StopSsh_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("Stop sshd", SshService.StopAsync);
        await RefreshSshAsync();
    }

    private async void RestartSsh_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("Restart sshd", SshService.RestartAsync);
        await RefreshSshAsync();
    }

    private async void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("Set sshd to start automatically", () => SshService.SetStartupTypeAsync(true));
        await RefreshSshAsync();
    }

    private async void ManualStart_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("Set sshd to manual start", () => SshService.SetStartupTypeAsync(false));
        await RefreshSshAsync();
    }

    private async void FirewallEnable_Click(object sender, RoutedEventArgs e)
    {
        var port = _ssh?.Port ?? SshService.GetConfiguredPort();
        await RunActionAsync($"Allow TCP {port} through Windows Firewall", () => SshService.EnableFirewallAsync(port));
        await RefreshSshAsync();
    }

    private async void FirewallDisable_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("Disable SSH firewall rule", SshService.DisableFirewallAsync);
        await RefreshSshAsync();
    }

    private async void ApplyShell_Click(object sender, RoutedEventArgs e)
    {
        if (ShellCombo.SelectedItem is not ShellOption option) return;
        await RunActionAsync($"Set default shell to {option.Name}", () => SshService.SetDefaultShellAsync(option.Path));
        await RefreshSshAsync();
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(SshService.ConfigPath))
        {
            Log($"! {SshService.ConfigPath} not found. Start sshd once to generate it.");
            return;
        }
        StartShell("notepad.exe", $"\"{SshService.ConfigPath}\"");
        Log("Restart sshd after editing sshd_config for changes to take effect.");
    }

    private async void RefreshSsh_Click(object sender, RoutedEventArgs e) => await RefreshSshAsync();

    private async void TailscaleUp_Click(object sender, RoutedEventArgs e)
    {
        Log("If a login URL appears below, open it in a browser to authenticate this device.");
        await RunActionAsync("tailscale up", TailscaleService.UpAsync);
        await RefreshTailscaleAsync();
    }

    private async void TailscaleDown_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Disconnecting Tailscale will drop any SSH session that is using it (including from your phone). Continue?",
                "Disconnect Tailscale", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunActionAsync("tailscale down", TailscaleService.DownAsync);
        await RefreshTailscaleAsync();
    }

    private async void RefreshTailscale_Click(object sender, RoutedEventArgs e) => await RefreshTailscaleAsync();

    private void TailscaleAdmin_Click(object sender, RoutedEventArgs e) => StartShell(TailscaleService.AdminConsoleUrl);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TextBox box } || string.IsNullOrWhiteSpace(box.Text)) return;
        try
        {
            Clipboard.SetText(box.Text);
            BusyText.Text = "Copied: " + box.Text;
        }
        catch (Exception ex)
        {
            Log("! Copy failed: " + ex.Message);
        }
    }

    // ---------- Keys ----------

    private void KeyTarget_Changed(object sender, RoutedEventArgs e) => RefreshKeys();

    private void ReloadKeys_Click(object sender, RoutedEventArgs e) => RefreshKeys();

    private void RefreshKeys()
    {
        var target = CurrentKeyTarget;
        var path = KeyService.GetPath(target);
        KeysPathText.Text = path;
        try
        {
            KeysList.ItemsSource = KeyService.ReadKeys(target);
        }
        catch (Exception ex)
        {
            KeysList.ItemsSource = null;
            Log($"! Could not read {path}: {ex.Message}");
        }

        var isAdmin = KeyService.IsCurrentUserAdmin();
        KeyHintText.Text = target == KeyTarget.Administrators
            ? (isAdmin
                ? "Your account is an administrator. With the default sshd_config, sshd reads this file for admin accounts and ignores ~/.ssh/authorized_keys."
                : "This file is used only for members of the Administrators group.")
            : (isAdmin
                ? "Warning: your account is an administrator, so with the default sshd_config sshd ignores this file. Use administrators_authorized_keys instead."
                : "Keys here let you log in as " + Environment.UserName + ".");
    }

    private async void AddKey_Click(object sender, RoutedEventArgs e)
    {
        var text = NewKeyBox.Text;
        var ok = await RunActionAsync("Add public key", () => KeyService.AddKeysAsync(CurrentKeyTarget, text));
        if (ok) NewKeyBox.Clear();
        RefreshKeys();
    }

    private void LoadKeyFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a public key",
            Filter = "Public keys (*.pub)|*.pub|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                NewKeyBox.Text = File.ReadAllText(dlg.FileName).Trim();
            }
            catch (Exception ex)
            {
                Log("! Could not read file: " + ex.Message);
            }
        }
    }

    private async void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (KeysList.SelectedItem is not string key) return;
        var preview = key.Length > 80 ? key[..80] + "…" : key;
        if (MessageBox.Show(this, $"Remove this key?\n\n{preview}", "Remove key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunActionAsync("Remove public key", () => KeyService.RemoveKeyAsync(CurrentKeyTarget, key));
        RefreshKeys();
    }

    private async void FixAcl_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("Fix key file permissions", () => KeyService.FixPermissionsAsync(CurrentKeyTarget));
        RefreshKeys();
    }

    private void OpenKeysFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = KeyService.GetPath(CurrentKeyTarget);
        if (File.Exists(path)) StartShell("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(Path.GetDirectoryName(path))) StartShell("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"");
        else Log($"! {path} does not exist yet.");
    }

    // ---------- Helpers ----------

    private void SetBusy(string? message)
    {
        var busy = message is not null;
        MainTabs.IsEnabled = !busy;
        RefreshAllButton.IsEnabled = !busy;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = message ?? "Ready";
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message.TrimEnd()}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private static void SetStatus(TextBox box, string text, bool? good)
    {
        box.Text = text;
        // Resource references so status colours follow theme switches.
        if (good is null) box.ClearValue(ForegroundProperty);
        else box.SetResourceReference(ForegroundProperty, good.Value ? "GoodBrush" : "BadBrush");
    }

    private static string FormatStartMode(string mode) => mode switch
    {
        "Auto" => "Automatic",
        "" => "—",
        _ => mode,
    };

    private void StartShell(string target, string? args = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { Arguments = args ?? "", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"! Could not open {target}: {ex.Message}");
        }
    }
}
