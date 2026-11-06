"""Platform logic for SSH + Tailscale Manager (Linux). No GUI imports here so it can be unit tested."""
from __future__ import annotations

import glob
import json
import os
import re
import shlex
import shutil
import socket
import subprocess
import time
from dataclasses import dataclass, field
from typing import Callable, Iterable

__version__ = "1.2.0"

KEY_RE = re.compile(
    r"^(?:ssh-(?:ed25519|rsa|dss)|ecdsa-sha2-nistp(?:256|384|521)|sk-ssh-ed25519@openssh\.com|"
    r"sk-ecdsa-sha2-nistp256@openssh\.com)\s+[A-Za-z0-9+/]+={0,3}(?:\s+.*)?$"
)
SAFE_HOST_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]*$")
SAFE_USER_RE = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9._-]*$")
MIN_PASSPHRASE = 5
ADMIN_CMD = "pkexec"


@dataclass
class Result:
    code: int
    out: str = ""
    err: str = ""

    @property
    def ok(self) -> bool:
        return self.code == 0

    @property
    def combined(self) -> str:
        return "\n".join(s.strip() for s in (self.out, self.err) if s.strip())

    @staticmethod
    def fail(message: str) -> "Result":
        return Result(-1, "", message)

    @staticmethod
    def success(message: str) -> "Result":
        return Result(0, message, "")


def run(args: list[str], timeout: float = 60, input_text: str | None = None) -> Result:
    try:
        p = subprocess.run(args, capture_output=True, text=True, timeout=timeout, input=input_text)
        return Result(p.returncode, p.stdout, p.stderr)
    except FileNotFoundError:
        return Result.fail(f"{args[0]} was not found.")
    except subprocess.TimeoutExpired as ex:
        out = ex.stdout.decode() if isinstance(ex.stdout, bytes) else (ex.stdout or "")
        err = ex.stderr.decode() if isinstance(ex.stderr, bytes) else (ex.stderr or "")
        return Result(-1, out, err + "\nTimed out.")


def admin(args: list[str], timeout: float = 600) -> Result:
    """Runs a command as root (polkit prompt) unless we already are root."""
    if hasattr(os, "geteuid") and os.geteuid() == 0:
        return run(args, timeout)
    if shutil.which(ADMIN_CMD) is None:
        return Result.fail("pkexec (policykit-1) is required for administrator actions. Install it with: sudo apt install policykit-1")
    return run([ADMIN_CMD, *args], timeout)


# ---------- SSH server ----------

@dataclass
class SshStatus:
    installed: bool = False
    active: str = "unknown"
    enabled: str = "unknown"
    port: int = 22
    firewall: str = "unknown"


def configured_port(root: str = "/etc/ssh") -> int:
    # sshd uses the first value found; sshd_config.d drop-ins are included at the top of the default sshd_config.
    ordered = sorted(glob.glob(os.path.join(root, "sshd_config.d", "*.conf"))) + [os.path.join(root, "sshd_config")]
    for path in ordered:
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                for line in f:
                    m = re.match(r"^\s*Port\s+(\d{1,5})\s*(?:#.*)?$", line, re.IGNORECASE)
                    if m and 1 <= int(m.group(1)) <= 65535:
                        return int(m.group(1))
        except OSError:
            continue
    return 22


def ssh_service_name() -> str:
    return "ssh"


def ssh_status() -> SshStatus:
    s = SshStatus()
    s.installed = shutil.which("sshd") is not None or os.path.exists("/usr/sbin/sshd")
    name = ssh_service_name()
    s.active = run(["systemctl", "is-active", name], 15).out.strip() or "unknown"
    s.enabled = run(["systemctl", "is-enabled", name], 15).out.strip() or "unknown"
    s.port = configured_port()
    s.firewall = firewall_state()
    return s


def firewall_state(conf: str = "/etc/ufw/ufw.conf") -> str:
    if shutil.which("ufw") is None:
        return "ufw not installed (no firewall managed)"
    try:
        with open(conf, encoding="utf-8") as f:
            text = f.read()
    except OSError:
        return "ufw installed (status needs administrator)"
    return "ufw enabled" if re.search(r"^\s*ENABLED\s*=\s*yes", text, re.IGNORECASE | re.MULTILINE) else "ufw inactive (port open)"


def install_ssh_server() -> Result:
    return admin(["apt-get", "install", "-y", "openssh-server"], 900)


def ssh_action(action: str) -> Result:
    name = ssh_service_name()
    if action in ("start", "stop", "restart"):
        return admin(["systemctl", action, name])
    if action == "enable":
        return admin(["systemctl", "enable", name])
    if action == "disable":
        return admin(["systemctl", "disable", name])
    return Result.fail("Unknown action " + action)


def firewall_allow(port: int, allow: bool = True) -> Result:
    if shutil.which("ufw") is None:
        return Result.fail("ufw is not installed. Install it with: sudo apt install ufw")
    return admin(["ufw", "allow" if allow else "deny", f"{port}/tcp"])


# ---------- Keys ----------

def default_ssh_dir() -> str:
    return os.path.join(os.path.expanduser("~"), ".ssh")


def default_key_name(key_type: str) -> str:
    return "id_ed25519" if key_type == "ed25519" else "id_rsa"


def default_comment() -> str:
    try:
        user = os.environ.get("USER") or os.getlogin()
    except OSError:
        user = "user"
    return f"{user}@{socket.gethostname()}"


def validate_file_name(name: str | None) -> str | None:
    if not name or not name.strip():
        return "Enter a file name for the key (e.g. id_ed25519)."
    if name != name.strip() or name.endswith("."):
        return "The file name can't start/end with spaces or end with a dot."
    if "/" in name or "\x00" in name or name in (".", ".."):
        return "Use just a file name, not a path."
    if name.lower().endswith(".pub"):
        return "Don't add .pub: the public key file gets that extension automatically."
    return None


def validate_passphrase(p1: str, p2: str) -> str | None:
    if p1 != p2:
        return "The passphrases don't match."
    if 0 < len(p1) < MIN_PASSPHRASE:
        return f"A passphrase must be at least {MIN_PASSPHRASE} characters (or leave it empty for none)."
    return None


def keygen_args(key_type: str, path: str, comment: str, passphrase: str) -> list[str]:
    args = ["ssh-keygen", "-q", "-t", "ed25519" if key_type == "ed25519" else "rsa"]
    if key_type == "rsa":
        args += ["-b", "4096"]
    return args + ["-f", path, "-C", comment, "-N", passphrase]


@dataclass
class KeyGenResult:
    ok: bool
    message: str
    private_path: str = ""
    public_key: str = ""
    fingerprint: str = ""


def generate_key(key_type: str, directory: str, name: str, comment: str, passphrase: str, overwrite: bool) -> KeyGenResult:
    if shutil.which("ssh-keygen") is None:
        return KeyGenResult(False, "ssh-keygen was not found. Install it with: sudo apt install openssh-client")
    err = validate_file_name(name)
    if err:
        return KeyGenResult(False, err)
    if not directory.strip():
        return KeyGenResult(False, "Choose a folder for the key.")
    priv = os.path.abspath(os.path.join(directory, name))
    pub = priv + ".pub"
    if (os.path.exists(priv) or os.path.exists(pub)) and not overwrite:
        return KeyGenResult(False, f"{priv} (or its .pub) already exists. Nothing was changed.")
    try:
        os.makedirs(directory, mode=0o700, exist_ok=True)
        for p in (priv, pub):
            if os.path.exists(p):
                os.remove(p)
    except OSError as ex:
        return KeyGenResult(False, f"Could not prepare the key folder: {ex}")

    r = run(keygen_args(key_type, priv, comment, passphrase), 60)
    if not r.ok or not os.path.exists(priv) or not os.path.exists(pub):
        msg = r.combined.replace(passphrase, "***") if passphrase else r.combined
        return KeyGenResult(False, "ssh-keygen failed: " + msg)
    os.chmod(priv, 0o600)
    with open(pub, encoding="utf-8") as f:
        public_key = f.read().strip()
    return KeyGenResult(True, f"Created {priv} and {pub}.", priv, public_key, fingerprint(pub))


def fingerprint(pub_path: str) -> str:
    if shutil.which("ssh-keygen") is None or not os.path.exists(pub_path):
        return ""
    r = run(["ssh-keygen", "-l", "-E", "sha256", "-f", pub_path], 15)
    return r.out.strip() if r.ok else ""


def is_valid_key(line: str) -> bool:
    return bool(KEY_RE.match(line.strip()))


def authorized_keys_path() -> str:
    return os.path.join(default_ssh_dir(), "authorized_keys")


def read_keys(path: str | None = None) -> list[str]:
    path = path or authorized_keys_path()
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8", errors="replace") as f:
        return [l.strip().lstrip("\ufeff") for l in f if l.strip() and not l.strip().startswith("#")]


def _key_body(line: str) -> str | None:
    parts = line.split()
    return parts[1] if len(parts) >= 2 else None


def _write_keys(path: str, lines: Iterable[str]) -> None:
    os.makedirs(os.path.dirname(path), mode=0o700, exist_ok=True)
    content = "\n".join(l for l in lines if l.strip())
    if content:
        content += "\n"
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(content)
    try:
        os.chmod(path, 0o600)
        os.chmod(os.path.dirname(path), 0o700)
    except OSError:
        pass


def add_keys(text: str, path: str | None = None) -> Result:
    path = path or authorized_keys_path()
    candidates = [re.sub(r"[ \t]+", " ", l.strip()) for l in text.splitlines() if l.strip()]
    if not candidates:
        return Result.fail("Paste a public key first (e.g. the contents of id_ed25519.pub).")
    bad = next((l for l in candidates if not is_valid_key(l)), None)
    if bad is not None:
        return Result.fail(f"Not a valid OpenSSH public key: {bad[:60]}\nPaste the .pub key, never the private key.")
    lines = read_keys(path)
    bodies = {_key_body(l) for l in lines}
    added = 0
    for key in candidates:
        if _key_body(key) not in bodies:
            bodies.add(_key_body(key))
            lines.append(key)
            added += 1
    _write_keys(path, lines)
    msg = f"Added {added} key(s) to {path}"
    if len(candidates) > added:
        msg += f" ({len(candidates) - added} already present)"
    return Result.success(msg + ".")


def remove_key(key: str, path: str | None = None) -> Result:
    path = path or authorized_keys_path()
    if not os.path.exists(path):
        return Result.fail(f"{path} does not exist.")
    lines = read_keys(path)
    kept = [l for l in lines if l.strip() != key.strip()]
    _write_keys(path, kept)
    return Result.success(f"Removed {len(lines) - len(kept)} key(s) from {path}.")


def validate_remote(user: str, host: str, port: int) -> str | None:
    if not SAFE_USER_RE.match(user):
        return "Enter the remote username (letters, digits, . _ - only)."
    if not SAFE_HOST_RE.match(host):
        return "Enter the host name or IP address (no spaces or special characters)."
    if not 1 <= port <= 65535:
        return "The port must be between 1 and 65535."
    return None


def copy_to_host_command(pub_path: str, user: str, host: str, port: int) -> list[str]:
    """ssh-copy-id installs the public key on the remote host; ssh asks for the password in the terminal."""
    return ["ssh-copy-id", "-i", pub_path, "-p", str(port), f"{user}@{host}"]


def terminal_command(inner: list[str]) -> list[str] | None:
    """Wraps a command so it runs in a visible terminal and waits for Enter before closing."""
    shell = " ".join(shlex.quote(a) for a in inner) + '; echo; read -r -p "Press Enter to close…" _'
    for term, flag in (("x-terminal-emulator", "-e"), ("gnome-terminal", "--"), ("konsole", "-e"), ("xfce4-terminal", "-x"), ("xterm", "-e")):
        if shutil.which(term):
            return [term, flag, "bash", "-c", shell]
    return None


def export_key(private_path: str, dest_dir: str, include_private: bool) -> Result:
    pub = private_path + ".pub"
    if not os.path.exists(pub):
        return Result.fail(f"{pub} does not exist.")
    if not dest_dir.strip():
        return Result.fail("Choose a destination folder.")
    try:
        os.makedirs(dest_dir, exist_ok=True)
        dest_pub = os.path.join(dest_dir, os.path.basename(pub))
        shutil.copyfile(pub, dest_pub)
        msg = f"Exported public key to {dest_pub}."
        if include_private:
            if not os.path.exists(private_path):
                return Result.fail(f"{private_path} does not exist.")
            dest_priv = os.path.join(dest_dir, os.path.basename(private_path))
            shutil.copyfile(private_path, dest_priv)
            try:
                os.chmod(dest_priv, 0o600)
                msg += f"\nExported private key to {dest_priv} (readable only by you)."
            except OSError:
                msg += f"\nExported private key to {dest_priv}. ! Could not restrict its permissions on this destination."
        return Result.success(msg)
    except OSError as ex:
        return Result.fail(f"Export failed: {ex}")


def ssh_command(host: str, port: int, user: str, private_path: str) -> str:
    port_part = "" if port == 22 else f" -p {port}"
    return f"ssh -i {shlex.quote(private_path)}{port_part} {user}@{host}"


def config_snippet(alias: str, host: str, port: int, user: str, private_path: str) -> str:
    lines = [f"Host {alias}", f"    HostName {host}", f"    User {user}"]
    if port != 22:
        lines.append(f"    Port {port}")
    lines += [f"    IdentityFile {private_path}", "    IdentitiesOnly yes"]
    return "\n".join(lines)


def sanitize_alias(name: str) -> str:
    alias = re.sub(r"[^A-Za-z0-9._-]+", "-", name.strip()).strip("-").lower()
    return alias or "my-pc"


def lan_addresses() -> list[str]:
    r = run(["hostname", "-I"], 5)
    ips = [ip for ip in r.out.split() if re.match(r"^\d+\.\d+\.\d+\.\d+$", ip) and not ip.startswith(("127.", "169.254.", "100."))]
    return ips


# ---------- Tailscale ----------

@dataclass
class TailscaleStatus:
    installed: bool = False
    state: str = "Unknown"
    hostname: str = ""
    dns_name: str = ""
    ips: list[str] = field(default_factory=list)
    tailnet: str = ""
    peers: list[tuple[str, str, str, str, str]] = field(default_factory=list)
    error: str = ""

    @property
    def running(self) -> bool:
        return self.state.lower() == "running"

    @property
    def ipv4(self) -> str | None:
        return next((ip for ip in self.ips if "." in ip), None)


def parse_tailscale_status(raw: str) -> TailscaleStatus:
    try:
        root = json.loads(raw)
    except (json.JSONDecodeError, TypeError) as ex:
        return TailscaleStatus(installed=True, state="Unavailable", error=f"Could not parse status: {ex}")
    me = root.get("Self") or {}
    ct = root.get("CurrentTailnet") or {}
    suffix = root.get("MagicDNSSuffix") or ct.get("MagicDNSSuffix") or ""
    name = ct.get("Name") or ""
    tailnet = f"{suffix} ({name})" if name and suffix and name != suffix else (name or suffix)
    peers = []
    for p in (root.get("Peer") or {}).values():
        ips = p.get("TailscaleIPs") or []
        ip = next((i for i in ips if "." in i), ips[0] if ips else "")
        peers.append((p.get("HostName", ""), ip, (p.get("DNSName") or "").rstrip("."), p.get("OS", ""), "Yes" if p.get("Online") else "No"))
    peers.sort(key=lambda t: (t[4] != "Yes", t[0].lower()))
    return TailscaleStatus(
        installed=True,
        state=root.get("BackendState") or "Unknown",
        hostname=me.get("HostName", ""),
        dns_name=(me.get("DNSName") or "").rstrip("."),
        ips=list(me.get("TailscaleIPs") or []),
        tailnet=tailnet,
        peers=peers,
    )


def tailscale_status() -> TailscaleStatus:
    if shutil.which("tailscale") is None:
        return TailscaleStatus(installed=False, state="Not installed")
    r = run(["tailscale", "status", "--json"], 20)
    if not r.out.strip().startswith("{"):
        return TailscaleStatus(installed=True, state="Unavailable", error=r.combined)
    return parse_tailscale_status(r.out)


def _wait_state(timeout: float, get: Callable[[], str] | None = None) -> str:
    get = get or (lambda: tailscale_status().state)
    deadline = time.monotonic() + timeout
    while True:
        state = get()
        if state.lower() == "running" or state.lower().startswith("needs") or time.monotonic() >= deadline:
            return state
        time.sleep(1.5)


def tailscale_up() -> Result:
    if shutil.which("tailscale") is None:
        return Result.fail("tailscale was not found. Install it from https://tailscale.com/download/linux")
    log: list[str] = []
    svc = admin(["systemctl", "enable", "--now", "tailscaled"], 60)
    if svc.combined:
        log.append(svc.combined)
    up = admin(["tailscale", "up"], 30)
    if up.combined:
        log.append(up.combined)
    state = _wait_state(20)
    log.append(f"Tailscale state: {state}")
    text = "\n".join(log)
    if state.lower() == "running":
        return Result(0, text)
    if state.lower().startswith("needs"):
        return Result(1, text, "Login required: open the URL shown above in a browser, then click Refresh.")
    return Result(1, text)


def tailscale_down() -> Result:
    return admin(["tailscale", "down"], 60)
