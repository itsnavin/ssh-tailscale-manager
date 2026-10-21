# ssh-tailscale-manager

A small Windows desktop app (WPF, .NET 8) for managing the **Windows OpenSSH Server (sshd)** and **Tailscale** from one window. It's handy for SSH-ing into a Windows PC from a phone, for example with [Termius](https://termius.com/) on Android.

## Features

| Tab | What it does |
| --- | --- |
| **SSH Server** | Shows whether the OpenSSH Server capability is installed, the sshd service state, its startup type, the listening port, the firewall rule and the default shell. Buttons install the capability, start/stop/restart sshd, switch between automatic and manual start, allow/block port 22 (or your configured port) in Windows Firewall, open `sshd_config`, and set the default SSH shell (cmd, Windows PowerShell, or PowerShell 7). |
| **Tailscale** | Shows the backend state, hostname, MagicDNS name, Tailscale IPs, tailnet and the other devices on your tailnet (from `tailscale status --json`). Buttons run `tailscale up` and `tailscale down` and open the admin console. |
| **Connect (Termius)** | Lists the host (Tailscale IP, MagicDNS name, LAN IP), port and username, plus a ready-made `ssh` command, each with a **Copy** button and setup steps for Termius. |
| **Authorized keys** | Lists, adds (paste or load a `.pub` file) and removes public keys in `C:\ProgramData\ssh\administrators_authorized_keys` (admin accounts) or `%USERPROFILE%\.ssh\authorized_keys` (standard accounts). Files are always written as UTF-8 without BOM, and their ACLs are fixed the way sshd requires. |

All command output appears in the **Activity log** at the bottom of the window.

> The app asks for administrator rights at startup (UAC) via its manifest (`requireAdministrator`). It needs them to manage services, Windows capabilities, firewall rules and `HKLM\SOFTWARE\OpenSSH`.

## Requirements

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build (`winget install Microsoft.DotNet.SDK.8`), or the .NET 8 Desktop Runtime to run a framework-dependent build
- [Tailscale for Windows](https://tailscale.com/download/windows), for the Tailscale features

## Build & run

```powershell
git clone https://github.com/itsnavin/ssh-tailscale-manager.git
cd ssh-tailscale-manager
dotnet build -c Release
```

The executable is at `src\SshTailscaleManager\bin\Release\net8.0-windows\SshTailscaleManager.exe`. Double-click it and accept the UAC prompt.

> `dotnet run` only works from an elevated terminal, because the app requires administrator rights.

### Single-file .exe

```powershell
# Needs the .NET 8 Desktop Runtime on the target PC
dotnet publish src\SshTailscaleManager -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish

# Fully self-contained (no runtime needed, larger file)
dotnet publish src\SshTailscaleManager -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Connecting from Termius (Android)

1. Install Tailscale on the phone and sign in to the same tailnet as the PC.
2. In the app, make sure sshd is **Running**, the startup type is **Automatic** and the firewall rule is **Enabled**.
3. In Termius, add a new host and use the values on the **Connect** tab:
   - **Hostname:** the Tailscale IP (e.g. `100.x.y.z`) or MagicDNS name. These work from anywhere. The LAN IP only works on the same Wi-Fi.
   - **Port:** `22`, unless you changed it in `sshd_config`.
   - **Username:** your local Windows account name. Termius accepts names with spaces as-is.
   - **Password:** your Windows account password. If you sign in with a Microsoft account, use that account's password, because a Windows Hello PIN doesn't work over SSH.
4. For key-based login (recommended), create a key in Termius → **Keychain**, copy its public key and add it on the **Authorized keys** tab.
   - For administrator accounts, the default `sshd_config` only reads `administrators_authorized_keys`. The app selects the right file for you.

## Project layout

```
SshTailscaleManager.sln
src/SshTailscaleManager/
  app.manifest              requireAdministrator
  MainWindow.xaml(.cs)      UI
  Services/
    ProcessRunner.cs        process / PowerShell execution
    SshService.cs           sshd capability, service, firewall, default shell
    TailscaleService.cs     tailscale.exe status / up / down
    KeyService.cs           authorized_keys management + ACL fixes
    NetworkInfo.cs          LAN IPv4 discovery
```

## License

MIT. See [LICENSE](LICENSE).
