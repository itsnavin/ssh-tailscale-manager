"""Tkinter GUI for SSH + Tailscale Manager (Linux)."""
from __future__ import annotations

import json
import os
import subprocess
import threading
import tkinter as tk
import webbrowser
from datetime import datetime
from tkinter import filedialog, messagebox, ttk
from typing import Callable

from . import core

LIGHT = {"bg": "#f3f3f3", "fg": "#1b1b1b", "input": "#ffffff", "accent": "#0b6bcb", "good": "#107c10", "bad": "#c42b1c", "border": "#c8c8c8"}
DARK = {"bg": "#202124", "fg": "#e8eaed", "input": "#2b2d31", "accent": "#4da3ff", "good": "#5dd75d", "bad": "#ff6b5e", "border": "#4a4d52"}
SETTINGS = os.path.join(os.path.expanduser("~"), ".config", "ssh-tailscale-manager", "settings.json")


def system_prefers_dark() -> bool:
    r = core.run(["gsettings", "get", "org.gnome.desktop.interface", "color-scheme"], 3)
    return "dark" in r.out.lower()


def load_theme_pref() -> str:
    try:
        with open(SETTINGS, encoding="utf-8") as f:
            v = json.load(f).get("theme", "System")
            return v if v in ("System", "Light", "Dark") else "System"
    except (OSError, ValueError):
        return "System"


def save_theme_pref(value: str) -> None:
    try:
        os.makedirs(os.path.dirname(SETTINGS), exist_ok=True)
        with open(SETTINGS, "w", encoding="utf-8") as f:
            json.dump({"theme": value}, f)
    except OSError:
        pass


class App(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("SSH + Tailscale Manager")
        self.geometry("1000x760")
        self.minsize(780, 560)
        icon = _find_icon()
        if icon:
            try:
                self.iconphoto(True, tk.PhotoImage(file=icon))
            except tk.TclError:
                pass

        self.style = ttk.Style(self)
        self.style.theme_use("clam")
        self.theme_var = tk.StringVar(value=load_theme_pref())
        self.ssh: core.SshStatus | None = None
        self.ts: core.TailscaleStatus | None = None
        self.gen_priv: str | None = None
        self.busy = False

        self._build_header()
        self.tabs = ttk.Notebook(self)
        self.tabs.pack(fill="both", expand=True, padx=10, pady=(0, 6))
        self._build_server_tab()
        self._build_keygen_tab()
        self._build_tailscale_tab()
        self._build_connect_tab()
        self._build_keys_tab()
        self._build_log()
        self.apply_theme()
        self.after(100, self.refresh_all)

    # ---------- theme ----------

    def apply_theme(self) -> None:
        pref = self.theme_var.get()
        dark = system_prefers_dark() if pref == "System" else pref == "Dark"
        p = DARK if dark else LIGHT
        self.palette = p
        s = self.style
        self.configure(bg=p["bg"])
        s.configure(".", background=p["bg"], foreground=p["fg"], fieldbackground=p["input"], bordercolor=p["border"],
                    lightcolor=p["bg"], darkcolor=p["bg"], troughcolor=p["input"], insertcolor=p["fg"])
        s.configure("TButton", background=p["input"], foreground=p["fg"], padding=(10, 4))
        s.map("TButton", background=[("active", p["accent"])], foreground=[("disabled", p["border"])])
        s.configure("TNotebook", background=p["bg"])
        s.configure("TNotebook.Tab", background=p["input"], foreground=p["fg"], padding=(12, 5))
        s.map("TNotebook.Tab", background=[("selected", p["accent"])], foreground=[("selected", "#ffffff")])
        s.configure("TLabelframe", background=p["bg"], bordercolor=p["border"])
        s.configure("TLabelframe.Label", background=p["bg"], foreground=p["accent"])
        s.configure("TEntry", fieldbackground=p["input"], foreground=p["fg"])
        s.configure("TCombobox", fieldbackground=p["input"], foreground=p["fg"], background=p["input"])
        s.configure("TCheckbutton", background=p["bg"], foreground=p["fg"])
        s.configure("TRadiobutton", background=p["bg"], foreground=p["fg"])
        s.configure("Hint.TLabel", foreground=p["border"] if not dark else "#9aa0a6")
        s.configure("Good.TLabel", foreground=p["good"])
        s.configure("Bad.TLabel", foreground=p["bad"])
        s.configure("Treeview", background=p["input"], foreground=p["fg"], fieldbackground=p["input"])
        s.configure("Treeview.Heading", background=p["bg"], foreground=p["fg"])
        s.map("Treeview", background=[("selected", p["accent"])], foreground=[("selected", "#ffffff")])
        for w in self._text_widgets:
            w.configure(bg=p["input"], fg=p["fg"], insertbackground=p["fg"], highlightbackground=p["border"])
        self.option_add("*TCombobox*Listbox.background", p["input"])
        self.option_add("*TCombobox*Listbox.foreground", p["fg"])

    _text_widgets: list[tk.Text] = []

    def _text(self, parent: tk.Misc, **kw) -> tk.Text:
        t = tk.Text(parent, relief="flat", highlightthickness=1, **kw)
        self._text_widgets.append(t)
        return t

    # ---------- layout helpers ----------

    def _build_header(self) -> None:
        bar = ttk.Frame(self, padding=(14, 10))
        bar.pack(fill="x")
        ttk.Label(bar, text="SSH + Tailscale Manager", font=("TkDefaultFont", 14, "bold")).pack(side="left")
        ttk.Button(bar, text="Refresh all", command=self.refresh_all).pack(side="right")
        combo = ttk.Combobox(bar, textvariable=self.theme_var, values=["System", "Light", "Dark"], width=8, state="readonly")
        combo.pack(side="right", padx=8)
        ttk.Label(bar, text="Theme").pack(side="right")
        combo.bind("<<ComboboxSelected>>", lambda _e: (save_theme_pref(self.theme_var.get()), self.apply_theme()))
        self.header_status = ttk.Label(self, text="", style="Hint.TLabel", padding=(14, 0, 0, 6))
        self.header_status.pack(fill="x")

    def _build_log(self) -> None:
        frame = ttk.LabelFrame(self, text="Activity log")
        frame.pack(fill="x", padx=10, pady=(0, 10))
        self.log_box = self._text(frame, height=8, wrap="word", state="disabled", font=("monospace", 9))
        self.log_box.pack(fill="x", padx=4, pady=4)
        self.busy_label = ttk.Label(self, text="Ready", style="Hint.TLabel", padding=(14, 0, 0, 6))
        self.busy_label.pack(fill="x")

    def log(self, message: str) -> None:
        def write() -> None:
            self.log_box.configure(state="normal")
            self.log_box.insert("end", f"[{datetime.now():%H:%M:%S}] {message.rstrip()}\n")
            self.log_box.see("end")
            self.log_box.configure(state="disabled")
        self.after(0, write)

    def _tab(self, title: str) -> ttk.Frame:
        f = ttk.Frame(self.tabs, padding=10)
        self.tabs.add(f, text=title)
        return f

    def _group(self, parent: tk.Misc, title: str) -> ttk.LabelFrame:
        g = ttk.LabelFrame(parent, text=title, padding=8)
        g.pack(fill="x", pady=(0, 8))
        return g

    def _buttons(self, parent: tk.Misc, items: list[tuple[str, Callable[[], None]]]) -> dict[str, ttk.Button]:
        row = ttk.Frame(parent)
        row.pack(fill="x", pady=(4, 0))
        out = {}
        for text, cmd in items:
            b = ttk.Button(row, text=text, command=cmd)
            b.pack(side="left", padx=(0, 6), pady=2)
            out[text] = b
        return out

    def _hint(self, parent: tk.Misc, text: str) -> ttk.Label:
        lbl = ttk.Label(parent, text=text, style="Hint.TLabel", wraplength=900, justify="left")
        lbl.pack(fill="x", pady=(0, 4))
        return lbl

    def _value_rows(self, parent: tk.Misc, labels: list[str]) -> dict[str, ttk.Label]:
        out = {}
        for i, name in enumerate(labels):
            ttk.Label(parent, text=name, width=18).grid(row=i, column=0, sticky="w", pady=2)
            v = ttk.Label(parent, text="…")
            v.grid(row=i, column=1, sticky="w", pady=2)
            out[name] = v
        return out

    def _copy_rows(self, parent: tk.Misc, labels: list[str]) -> dict[str, tk.StringVar]:
        parent.columnconfigure(1, weight=1)
        out = {}
        for i, name in enumerate(labels):
            var = tk.StringVar()
            ttk.Label(parent, text=name, width=18).grid(row=i, column=0, sticky="w", pady=2)
            ttk.Entry(parent, textvariable=var, state="readonly").grid(row=i, column=1, sticky="ew", pady=2)
            ttk.Button(parent, text="Copy", command=lambda v=var: self.copy(v.get())).grid(row=i, column=2, padx=(6, 0))
            out[name] = var
        return out

    def copy(self, text: str) -> None:
        if not text.strip():
            return
        self.clipboard_clear()
        self.clipboard_append(text)
        self.busy_label.configure(text="Copied: " + (text if len(text) < 80 else text[:77] + "…"))

    def _set(self, label: ttk.Label, text: str, good: bool | None) -> None:
        label.configure(text=text, style="TLabel" if good is None else ("Good.TLabel" if good else "Bad.TLabel"))

    # ---------- background actions ----------

    def action(self, description: str, work: Callable[[], core.Result], then: Callable[[bool], None] | None = None) -> None:
        if self.busy:
            return
        self.busy = True
        self.busy_label.configure(text=description + "…")
        self.log("> " + description)
        self.config(cursor="watch")

        def runner() -> None:
            try:
                r = work()
                if r.combined:
                    self.log(r.combined)
                self.log(f"✔ {description} succeeded." if r.ok else f"✖ {description} failed (exit code {r.code}).")
                ok = r.ok
            except Exception as ex:  # surface unexpected errors in the log instead of dying silently
                self.log(f"✖ {description} failed: {ex}")
                ok = False

            def finish() -> None:
                self.busy = False
                self.config(cursor="")
                self.busy_label.configure(text="Ready")
                if then:
                    then(ok)
            self.after(0, finish)

        threading.Thread(target=runner, daemon=True).start()

    def background(self, work: Callable[[], object], done: Callable[[object], None]) -> None:
        def runner() -> None:
            try:
                res = work()
            except Exception as ex:
                self.log("✖ " + str(ex))
                return
            self.after(0, lambda: done(res))
        threading.Thread(target=runner, daemon=True).start()

    # ---------- refresh ----------

    def refresh_all(self) -> None:
        self.busy_label.configure(text="Refreshing status…")
        self.background(lambda: (core.ssh_status(), core.tailscale_status()), self._apply_all)

    def _apply_all(self, res: object) -> None:
        self.ssh, self.ts = res  # type: ignore[misc]
        self.apply_ssh()
        self.apply_tailscale()
        self.reload_keys()
        self.update_derived()
        self.busy_label.configure(text="Ready")

    def refresh_ssh(self) -> None:
        self.background(core.ssh_status, lambda r: (setattr(self, "ssh", r), self.apply_ssh(), self.update_derived()))

    def refresh_tailscale(self) -> None:
        self.background(core.tailscale_status, lambda r: (setattr(self, "ts", r), self.apply_tailscale(), self.update_derived()))

    # ---------- SSH server tab ----------

    def _build_server_tab(self) -> None:
        tab = self._tab("SSH Server")
        g = self._group(tab, "OpenSSH Server status")
        self.ssh_rows = self._value_rows(g, ["Installed", "Service state", "Start at boot", "Listening port", "Firewall (ufw)"])
        g = self._group(tab, "Service")
        self.ssh_btns = self._buttons(g, [
            ("Install OpenSSH Server", lambda: self.run_ssh(core.install_ssh_server, "Install OpenSSH Server")),
            ("Start", lambda: self.run_ssh(lambda: core.ssh_action("start"), "Start sshd")),
            ("Stop", lambda: self.run_ssh(lambda: core.ssh_action("stop"), "Stop sshd")),
            ("Restart", lambda: self.run_ssh(lambda: core.ssh_action("restart"), "Restart sshd")),
            ("Start at boot", lambda: self.run_ssh(lambda: core.ssh_action("enable"), "Enable sshd at boot")),
            ("Don't start at boot", lambda: self.run_ssh(lambda: core.ssh_action("disable"), "Disable sshd at boot")),
            ("Refresh", self.refresh_ssh),
        ])
        g = self._group(tab, "Firewall (ufw)")
        self._hint(g, "Allows inbound connections to the sshd port. Tailscale traffic also needs this when ufw is enabled.")
        self._buttons(g, [
            ("Allow SSH port", lambda: self.run_ssh(lambda: core.firewall_allow(self._port(), True), "Allow SSH through ufw")),
            ("Deny SSH port", lambda: self.run_ssh(lambda: core.firewall_allow(self._port(), False), "Deny SSH in ufw")),
        ])
        self._hint(tab, "Administrator actions open a system password prompt (pkexec). Edit /etc/ssh/sshd_config yourself and restart sshd for config changes.")

    def _port(self) -> int:
        return self.ssh.port if self.ssh else core.configured_port()

    def run_ssh(self, work: Callable[[], core.Result], description: str) -> None:
        self.action(description, work, lambda _ok: self.refresh_ssh())

    def apply_ssh(self) -> None:
        s = self.ssh
        if s is None:
            return
        self._set(self.ssh_rows["Installed"], "Yes" if s.installed else "No (use Install OpenSSH Server)", s.installed)
        self._set(self.ssh_rows["Service state"], s.active, s.active == "active")
        self._set(self.ssh_rows["Start at boot"], s.enabled, s.enabled == "enabled")
        self._set(self.ssh_rows["Listening port"], str(s.port), None)
        self._set(self.ssh_rows["Firewall (ufw)"], s.firewall, None)

    # ---------- Key generation tab ----------

    def _build_keygen_tab(self) -> None:
        tab = self._tab("SSH Key Generation")
        g = self._group(tab, "Generate a new key pair")
        self.kg_status = self._hint(g, "")
        form = ttk.Frame(g)
        form.pack(fill="x")
        form.columnconfigure(1, weight=1)
        self.key_type = tk.StringVar(value="ed25519")
        self.key_dir = tk.StringVar(value=core.default_ssh_dir())
        self.key_name = tk.StringVar(value="id_ed25519")
        self.key_comment = tk.StringVar(value=core.default_comment())
        self.key_pass = tk.StringVar()
        self.key_pass2 = tk.StringVar()

        ttk.Label(form, text="Key type", width=18).grid(row=0, column=0, sticky="w", pady=3)
        types = ttk.Frame(form)
        types.grid(row=0, column=1, sticky="w")
        for text, val in (("Ed25519 (recommended)", "ed25519"), ("RSA 4096 (older systems)", "rsa")):
            ttk.Radiobutton(types, text=text, value=val, variable=self.key_type, command=self.on_key_type).pack(side="left", padx=(0, 14))
        rows = [("Folder", self.key_dir, ""), ("File name", self.key_name, ""), ("Comment", self.key_comment, ""),
                ("Passphrase", self.key_pass, "*"), ("Repeat passphrase", self.key_pass2, "*")]
        for i, (label, var, show) in enumerate(rows, start=1):
            ttk.Label(form, text=label, width=18).grid(row=i, column=0, sticky="w", pady=3)
            ttk.Entry(form, textvariable=var, show=show).grid(row=i, column=1, sticky="ew", pady=3)
        ttk.Button(form, text="Browse…", command=lambda: self.key_dir.set(filedialog.askdirectory(initialdir=self.key_dir.get()) or self.key_dir.get())).grid(row=1, column=2, padx=(6, 0))
        self._hint(g, "Leave the passphrase empty for a key without one (less safe if the file leaks). Keys are created locally with ssh-keygen; the private key and passphrase "
                      "never leave this PC and are never written to the Activity log. An existing key is never overwritten without confirmation. "
                      "ssh-keygen only accepts a passphrase as a command-line argument, so it is briefly visible to other local processes while the key is generated.")
        self.gen_btn = self._buttons(g, [("Generate key pair", self.generate), ("Open folder", self.open_key_folder)])["Generate key pair"]

        g = self._group(tab, "Generated key")
        box = ttk.Frame(g)
        box.pack(fill="x")
        v = self._copy_rows(box, ["Private key file", "Fingerprint", "Public key"])
        self.gen_priv_var, self.gen_fp_var, self.gen_pub_var = v["Private key file"], v["Fingerprint"], v["Public key"]
        self._buttons(g, [("Use existing key…", self.use_existing)])

        g = self._group(tab, "Export / send the key to another machine")
        self._hint(g, "Copy to host installs the public key on a remote SSH host (ssh-copy-id): a terminal opens and asks for that host's password once.")
        form = ttk.Frame(g)
        form.pack(fill="x")
        self.r_user, self.r_host, self.r_port = tk.StringVar(), tk.StringVar(), tk.StringVar(value="22")
        for i, (label, var, w) in enumerate((("Remote user", self.r_user, 18), ("Remote host", self.r_host, 28), ("Port", self.r_port, 6))):
            ttk.Label(form, text=label).grid(row=0, column=i * 2, sticky="w", padx=(0, 4))
            ttk.Entry(form, textvariable=var, width=w).grid(row=0, column=i * 2 + 1, padx=(0, 14))
        self.export_private = tk.BooleanVar(value=False)
        ttk.Checkbutton(g, text="Include the private key when exporting to a folder (keep it secret)", variable=self.export_private).pack(anchor="w", pady=4)
        self._buttons(g, [("Copy public key to host…", self.copy_to_host), ("Export to folder…", self.export_to_folder)])

        g = self._group(tab, "Use this key to log in to this PC (server side)")
        self._hint(g, "Adds the public key to ~/.ssh/authorized_keys so a client holding the private key can sign in to this PC's SSH server.")
        self.auth_target = self._hint(g, "Target: " + core.authorized_keys_path())
        self._buttons(g, [("Add public key to authorized_keys", self.add_generated), ("Go to Authorized keys", lambda: self.tabs.select(4))])

        g = self._group(tab, "Connecting with this key")
        self._hint(g, "Run on a client that has the private key. Host and port come from the Connect tab.")
        box = ttk.Frame(g)
        box.pack(fill="x")
        v = self._copy_rows(box, ["ssh command", "~/.ssh/config"])
        self.gen_cmd_var, self.gen_cfg_var = v["ssh command"], v["~/.ssh/config"]
        self._hint(g, "Phone (Termius): prefer generating the key inside Termius (Keychain → Generate key) and adding its public key on the Authorized keys tab, so the private key never travels. "
                      "If you use this key instead, move the private key to the phone over a trusted channel (USB cable or local transfer), import it in Termius → Keychain, then delete extra copies. "
                      "Never email it or paste it into chats or cloud notes.")
        self.update_keygen_availability()

    def on_key_type(self) -> None:
        if self.key_name.get().strip() in ("", "id_ed25519", "id_rsa"):
            self.key_name.set(core.default_key_name(self.key_type.get()))
        self.update_derived()

    def update_keygen_availability(self) -> None:
        import shutil
        ok = shutil.which("ssh-keygen") is not None
        self.kg_status.configure(text="Using " + shutil.which("ssh-keygen") if ok else
                                 "ssh-keygen was not found. Install it with: sudo apt install openssh-client")
        self.gen_btn.configure(state="normal" if ok else "disabled")

    def open_key_folder(self) -> None:
        d = self.key_dir.get().strip()
        if os.path.isdir(d):
            subprocess.Popen(["xdg-open", d])
        else:
            self.log(f"! {d} does not exist yet.")

    def generate(self) -> None:
        err = core.validate_file_name(self.key_name.get()) or core.validate_passphrase(self.key_pass.get(), self.key_pass2.get())
        if err:
            self.log("! " + err)
            messagebox.showwarning("SSH Key Generation", err)
            return
        directory, name = self.key_dir.get().strip(), self.key_name.get()
        priv = os.path.abspath(os.path.join(directory, name))
        overwrite = False
        if os.path.exists(priv) or os.path.exists(priv + ".pub"):
            if not messagebox.askyesno("Key already exists",
                                       f"A key already exists at:\n{priv}\n\nOverwriting it permanently destroys the old private key; anything that uses it will stop working. Overwrite?",
                                       default="no", icon="warning"):
                self.log("Key generation cancelled: existing key kept.")
                return
            overwrite = True
        args = (self.key_type.get(), directory, name, self.key_comment.get().strip(), self.key_pass.get(), overwrite)
        holder: dict[str, core.KeyGenResult] = {}

        def work() -> core.Result:
            holder["r"] = core.generate_key(*args)
            r = holder["r"]
            return core.Result.success(r.message) if r.ok else core.Result.fail(r.message)

        def done(ok: bool) -> None:
            if ok:
                self.key_pass.set("")
                self.key_pass2.set("")
                r = holder["r"]
                self.show_generated(r.private_path, r.public_key, r.fingerprint)

        self.action(f"Generate {self.key_type.get()} key", work, done)

    def use_existing(self) -> None:
        path = filedialog.askopenfilename(title="Select a public key (.pub)", initialdir=self.key_dir.get(), filetypes=[("Public keys", "*.pub")])
        if not path:
            return
        try:
            with open(path, encoding="utf-8") as f:
                pub = f.read().strip()
        except OSError as ex:
            self.log("! Could not read key: " + str(ex))
            return
        if not core.is_valid_key(pub):
            self.log("! That file is not a valid OpenSSH public key.")
            return
        self.show_generated(path[:-4] if path.endswith(".pub") else path, pub, core.fingerprint(path))

    def show_generated(self, priv: str, pub: str, fp: str) -> None:
        self.gen_priv = priv
        self.gen_priv_var.set(priv)
        self.gen_pub_var.set(pub)
        self.gen_fp_var.set(fp)
        self.update_derived()

    def add_generated(self) -> None:
        pub = self.gen_pub_var.get()
        if not pub:
            self.log("! Generate or select a key first.")
            return
        self.action("Add generated public key to authorized keys", lambda: core.add_keys(pub), lambda _ok: self.reload_keys())

    def copy_to_host(self) -> None:
        if not self.gen_priv or not os.path.exists(self.gen_priv + ".pub"):
            self.log("! Generate or select a key first.")
            return
        user, host = self.r_user.get().strip(), self.r_host.get().strip()
        try:
            port = int(self.r_port.get().strip())
        except ValueError:
            port = 0
        err = core.validate_remote(user, host, port)
        if err:
            self.log("! " + err)
            messagebox.showwarning("Copy public key to host", err)
            return
        import shutil
        if shutil.which("ssh-copy-id") is None:
            self.log("! ssh-copy-id was not found. Install it with: sudo apt install openssh-client")
            return
        cmd = core.terminal_command(core.copy_to_host_command(self.gen_priv + ".pub", user, host, port))
        if cmd is None:
            self.log("! No terminal emulator found. Run this yourself: " + " ".join(core.copy_to_host_command(self.gen_priv + ".pub", user, host, port)))
            return
        if not messagebox.askokcancel("Copy public key to host",
                                      f"Install this public key on {user}@{host}:{port}?\n\nA terminal opens and ssh asks for that account's password (typed into ssh, never into this app)."):
            return
        subprocess.Popen(cmd)
        self.log(f"> Copy public key to {user}@{host}:{port} (follow the prompts in the terminal window).")

    def export_to_folder(self) -> None:
        if not self.gen_priv:
            return
        include = self.export_private.get()
        if include and not messagebox.askyesno("Export private key",
                                               "The private key gives full access to every machine that trusts this key. Only export it to a place you control (e.g. your own USB drive) and delete other copies afterwards. Continue?",
                                               default="no", icon="warning"):
            return
        dest = filedialog.askdirectory(title="Choose the folder to export the key to")
        if not dest:
            return
        priv = self.gen_priv
        self.action("Export key", lambda: core.export_key(priv, dest, include))

    # ---------- Tailscale tab ----------

    def _build_tailscale_tab(self) -> None:
        tab = self._tab("Tailscale")
        g = self._group(tab, "This device")
        self.ts_rows = self._value_rows(g, ["State", "Hostname", "MagicDNS name", "Tailscale IPs", "Tailnet"])
        self._buttons(tab, [
            ("Connect (tailscale up)", self.ts_up),
            ("Disconnect (tailscale down)", self.ts_down),
            ("Refresh", self.refresh_tailscale),
            ("Open admin console", lambda: webbrowser.open("https://login.tailscale.com/admin/machines")),
        ])
        g = ttk.LabelFrame(tab, text="Devices on your tailnet", padding=6)
        g.pack(fill="both", expand=True, pady=(8, 0))
        cols = ("Host", "Tailscale IP", "MagicDNS name", "OS", "Online")
        self.peers = ttk.Treeview(g, columns=cols, show="headings", height=6)
        for c, w in zip(cols, (140, 120, 280, 80, 60)):
            self.peers.heading(c, text=c)
            self.peers.column(c, width=w, stretch=c in ("Host", "MagicDNS name"))
        self.peers.pack(fill="both", expand=True)

    def ts_up(self) -> None:
        self.log("If a login URL appears below, open it in a browser to authenticate this device.")
        self.action("tailscale up", core.tailscale_up, lambda _ok: self.refresh_tailscale())

    def ts_down(self) -> None:
        if not messagebox.askyesno("Disconnect Tailscale", "Disconnecting Tailscale will drop any SSH session that is using it (including from your phone). Continue?", icon="warning"):
            return
        self.action("tailscale down", core.tailscale_down, lambda _ok: self.refresh_tailscale())

    def apply_tailscale(self) -> None:
        t = self.ts
        if t is None:
            return
        if t.error:
            self.log("! Tailscale: " + t.error)
        self._set(self.ts_rows["State"], t.state if t.installed else "Not installed (https://tailscale.com/download/linux)", t.running)
        self._set(self.ts_rows["Hostname"], t.hostname or "—", None)
        self._set(self.ts_rows["MagicDNS name"], t.dns_name or "—", None)
        self._set(self.ts_rows["Tailscale IPs"], ", ".join(t.ips) or "—", None)
        self._set(self.ts_rows["Tailnet"], t.tailnet or "—", None)
        self.peers.delete(*self.peers.get_children())
        for row in t.peers:
            self.peers.insert("", "end", values=(row[0], row[1], row[2], row[3], row[4]))

    # ---------- Connect tab ----------

    def _build_connect_tab(self) -> None:
        tab = self._tab("Connect (Termius)")
        g = self._group(tab, "Connection details")
        v = self._copy_rows(g, ["Host (Tailscale IP)", "Host (MagicDNS)", "Host (LAN IP)", "Port", "Username", "ssh command"])
        self.conn = v
        g = self._group(tab, "Setting up Termius on Android")
        self._hint(g, "1. Install Tailscale on the phone and sign in to the same tailnet.")
        self._hint(g, "2. In Termius: Hosts → + → New Host. Use the Tailscale IP or MagicDNS name above as Hostname (works from anywhere), or the LAN IP on the same Wi-Fi.")
        self._hint(g, "3. Enter the port and username above. Use your Linux account password, or (recommended) generate a key in Termius → Keychain and add its public key on the Authorized keys tab.")

    def update_derived(self) -> None:
        port = self.ssh.port if self.ssh else core.configured_port()
        user = os.environ.get("USER", "user")
        lan = core.lan_addresses()
        ts_ip = self.ts.ipv4 if self.ts and self.ts.running else None
        ts_dns = self.ts.dns_name if self.ts and self.ts.running else None
        host = ts_ip or (lan[0] if lan else "HOST")
        self.conn["Host (Tailscale IP)"].set(ts_ip or "(Tailscale not connected)")
        self.conn["Host (MagicDNS)"].set(ts_dns or "(MagicDNS name unavailable)")
        self.conn["Host (LAN IP)"].set(lan[0] if lan else "(no LAN address)")
        self.conn["Port"].set(str(port))
        self.conn["Username"].set(user)
        self.conn["ssh command"].set(f"ssh -p {port} {user}@{host}" if port != 22 else f"ssh {user}@{host}")

        priv = self.gen_priv or os.path.join(core.default_ssh_dir(), core.default_key_name(self.key_type.get()))
        self.gen_cmd_var.set(core.ssh_command(host, port, user, priv))
        self.gen_cfg_var.set(core.config_snippet(core.sanitize_alias(core.socket.gethostname()), host, port, user, priv).replace("\n", " | "))
        sshs = "unknown" if self.ssh is None else self.ssh.active
        tss = "unknown" if self.ts is None else self.ts.state + (f" ({ts_ip})" if ts_ip else "")
        self.header_status.configure(text=f"sshd: {sshs}  ·  port {port}  ·  Tailscale: {tss}  ·  user: {user}")

    # ---------- Authorized keys tab ----------

    def _build_keys_tab(self) -> None:
        tab = self._tab("Authorized keys")
        g = self._group(tab, "Key file")
        ttk.Label(g, text=core.authorized_keys_path()).pack(anchor="w")
        self._hint(g, "Keys here let you log in as the current user. The file is kept with permissions 600 and its folder with 700, as sshd requires.")
        g = self._group(tab, "Add public key")
        self._hint(g, "Paste one or more public keys (e.g. from Termius → Keychain → key → Copy public key). Never paste a private key.")
        self.new_key = self._text(g, height=4, wrap="word", font=("monospace", 9))
        self.new_key.pack(fill="x", pady=4)
        self._buttons(g, [("Add key", self.add_key), ("Load .pub file…", self.load_pub)])
        g = ttk.LabelFrame(tab, text="Installed keys", padding=6)
        g.pack(fill="both", expand=True)
        self.keys_list = tk.Listbox(g, height=6, relief="flat", font=("monospace", 9), selectmode="single", exportselection=False)
        self.keys_list.pack(fill="both", expand=True)
        self._buttons(g, [("Remove selected", self.remove_selected), ("Reload", self.reload_keys)])

    def reload_keys(self) -> None:
        p = self.palette
        self.keys_list.configure(bg=p["input"], fg=p["fg"], selectbackground=p["accent"], highlightbackground=p["border"])
        self.keys_list.delete(0, "end")
        try:
            for k in core.read_keys():
                self.keys_list.insert("end", k)
        except OSError as ex:
            self.log(f"! Could not read authorized keys: {ex}")

    def add_key(self) -> None:
        text = self.new_key.get("1.0", "end")
        self.action("Add public key", lambda: core.add_keys(text), lambda ok: (self.new_key.delete("1.0", "end") if ok else None, self.reload_keys()))

    def load_pub(self) -> None:
        path = filedialog.askopenfilename(title="Select a public key", initialdir=core.default_ssh_dir(), filetypes=[("Public keys", "*.pub"), ("All files", "*")])
        if path:
            try:
                with open(path, encoding="utf-8") as f:
                    self.new_key.delete("1.0", "end")
                    self.new_key.insert("1.0", f.read().strip())
            except OSError as ex:
                self.log("! Could not read file: " + str(ex))

    def remove_selected(self) -> None:
        sel = self.keys_list.curselection()
        if not sel:
            return
        key = self.keys_list.get(sel[0])
        if not messagebox.askyesno("Remove key", f"Remove this key?\n\n{key[:80]}"):
            return
        self.action("Remove public key", lambda: core.remove_key(key), lambda _ok: self.reload_keys())


def _find_icon() -> str | None:
    here = os.path.dirname(os.path.abspath(__file__))
    for p in ("/usr/share/icons/hicolor/512x512/apps/ssh-tailscale-manager.png", os.path.join(here, "icon.png")):
        if os.path.exists(p):
            return p
    return None


def main() -> None:
    App().mainloop()
