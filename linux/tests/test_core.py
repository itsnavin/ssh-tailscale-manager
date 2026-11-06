import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from sshtsm import core  # noqa: E402

PUB = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl test@pc"


class CoreTests(unittest.TestCase):
    def test_file_name_validation(self):
        self.assertIsNone(core.validate_file_name("id_ed25519"))
        for bad in ("", "  ", "a/b", "x.pub", "trail.", ".."):
            self.assertIsNotNone(core.validate_file_name(bad), bad)

    def test_passphrase_rules(self):
        self.assertIsNone(core.validate_passphrase("", ""))
        self.assertIsNone(core.validate_passphrase("hunter22", "hunter22"))
        self.assertIsNotNone(core.validate_passphrase("abc", "abc"))
        self.assertIsNotNone(core.validate_passphrase("hunter22", "hunter23"))

    def test_keygen_args(self):
        self.assertEqual(core.keygen_args("ed25519", "/k", "c", ""), ["ssh-keygen", "-q", "-t", "ed25519", "-f", "/k", "-C", "c", "-N", ""])
        rsa = core.keygen_args("rsa", "/k", "c", "pw123")
        self.assertIn("4096", rsa)

    def test_remote_validation_and_command(self):
        self.assertIsNone(core.validate_remote("pi", "10.0.0.5", 22))
        self.assertIsNotNone(core.validate_remote("a b", "h", 22))
        self.assertIsNotNone(core.validate_remote("u", "h; rm -rf /", 22))
        self.assertIsNotNone(core.validate_remote("u", "h", 70000))
        self.assertEqual(core.copy_to_host_command("/k.pub", "pi", "h", 2222), ["ssh-copy-id", "-i", "/k.pub", "-p", "2222", "pi@h"])

    def test_snippets(self):
        self.assertEqual(core.ssh_command("h", 22, "me", "/k"), "ssh -i /k me@h")
        self.assertIn("-p 2222", core.ssh_command("h", 2222, "me", "/k"))
        self.assertNotIn("Port", core.config_snippet("pc", "h", 22, "me", "/k"))
        self.assertIn("Port 2222", core.config_snippet("pc", "h", 2222, "me", "/k"))
        self.assertEqual(core.sanitize_alias(" My PC 1 "), "my-pc-1")

    def test_authorized_keys_add_remove_dedupe(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, ".ssh", "authorized_keys")
            self.assertTrue(core.add_keys(PUB, path).ok)
            again = core.add_keys(PUB + "\n", path)
            self.assertIn("already present", again.out)
            self.assertEqual(core.read_keys(path), [PUB])
            self.assertFalse(core.add_keys("-----BEGIN OPENSSH PRIVATE KEY-----", path).ok)
            if os.name == "posix":
                self.assertEqual(os.stat(path).st_mode & 0o777, 0o600)
            self.assertTrue(core.remove_key(PUB, path).ok)
            self.assertEqual(core.read_keys(path), [])

    def test_configured_port(self):
        with tempfile.TemporaryDirectory() as d:
            os.makedirs(os.path.join(d, "sshd_config.d"))
            with open(os.path.join(d, "sshd_config"), "w") as f:
                f.write("#Port 22\nPort 2200\n")
            self.assertEqual(core.configured_port(d), 2200)
            with open(os.path.join(d, "sshd_config.d", "10.conf"), "w") as f:
                f.write("Port 2222\n")
            self.assertEqual(core.configured_port(d), 2222)
            self.assertEqual(core.configured_port(os.path.join(d, "missing")), 22)

    def test_parse_tailscale_status(self):
        raw = json.dumps({
            "BackendState": "Running", "MagicDNSSuffix": "tail.ts.net",
            "CurrentTailnet": {"Name": "me@x.com", "MagicDNSSuffix": "tail.ts.net"},
            "Self": {"HostName": "box", "DNSName": "box.tail.ts.net.", "TailscaleIPs": ["100.1.2.3", "fd7a::1"]},
            "Peer": {"a": {"HostName": "z", "DNSName": "z.tail.ts.net.", "TailscaleIPs": ["100.9.9.9"], "OS": "linux", "Online": False},
                     "b": {"HostName": "a", "DNSName": "a.tail.ts.net.", "TailscaleIPs": ["100.8.8.8"], "OS": "windows", "Online": True}},
        })
        s = core.parse_tailscale_status(raw)
        self.assertTrue(s.running)
        self.assertEqual(s.ipv4, "100.1.2.3")
        self.assertEqual(s.dns_name, "box.tail.ts.net")
        self.assertEqual(s.peers[0][0], "a")  # online first
        self.assertEqual(core.parse_tailscale_status("nope").state, "Unavailable")

    def test_wait_state(self):
        states = iter(["Starting", "Starting", "Running"])
        self.assertEqual(core._wait_state(30, lambda: next(states)), "Running")
        self.assertEqual(core._wait_state(30, lambda: "NeedsLogin"), "NeedsLogin")

    def test_export(self):
        with tempfile.TemporaryDirectory() as d:
            priv = os.path.join(d, "k")
            open(priv, "w").write("PRIVATE")
            open(priv + ".pub", "w").write(PUB)
            out = os.path.join(d, "out")
            self.assertTrue(core.export_key(priv, out, False).ok)
            self.assertFalse(os.path.exists(os.path.join(out, "k")))
            self.assertTrue(core.export_key(priv, out, True).ok)
            self.assertEqual(open(os.path.join(out, "k")).read(), "PRIVATE")

    @unittest.skipIf(shutil.which("ssh-keygen") is None, "ssh-keygen not available")
    def test_generate_refuses_overwrite_then_generates(self):
        with tempfile.TemporaryDirectory() as d:
            open(os.path.join(d, "k"), "w").write("existing")
            self.assertFalse(core.generate_key("ed25519", d, "k", "c", "", False).ok)
            self.assertEqual(open(os.path.join(d, "k")).read(), "existing")
            r = core.generate_key("ed25519", d, "k", "test@pc", "", True)
            self.assertTrue(r.ok, r.message)
            self.assertTrue(r.public_key.startswith("ssh-ed25519 "))
            self.assertTrue(core.is_valid_key(r.public_key))
            self.assertIn("SHA256:", r.fingerprint)


if __name__ == "__main__":
    unittest.main()
