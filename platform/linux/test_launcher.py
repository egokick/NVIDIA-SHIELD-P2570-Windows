import importlib.util
import os
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("shield_launcher", Path(__file__).with_name("nvidia-shield-p2570.py"))
app = importlib.util.module_from_spec(spec)
spec.loader.exec_module(app)


class LauncherTests(unittest.TestCase):
    def test_adapter_scope(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name, vendor, product in [("supported", "0bda", "0811"), ("different", "0bda", "8852")]:
                node = root / name
                node.mkdir()
                for field, value in [("idVendor", vendor), ("idProduct", product), ("busnum", "1"), ("devnum", "7")]:
                    (node / field).write_text(value)
            self.assertEqual(app.adapters(root), [Path("/dev/bus/usb/001/007")])

    def test_preflight_explains_recovery(self):
        device = Path("/dev/bus/usb/001/007")
        self.assertIn("not supported", app.preflight([]))
        self.assertIn("one supported", app.preflight([device, device]))
        self.assertIn("Unplug", app.preflight([device], access=lambda *_: False))
        self.assertIn("Restart", app.preflight([device], access=lambda path, _: path == device))
        self.assertIsNone(app.preflight([device], access=lambda *_: True))

    def test_pair_cancel_does_not_launch(self):
        calls = []
        def runner(args, **kwargs):
            calls.append(args)
            return type("Result", (), {"returncode": 1})()
        self.assertFalse(app.pair_prompt(runner))
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0][0:2], ["zenity", "--question"])
        self.assertIn("It's flashing", calls[0])

    def test_host_has_exact_arguments(self):
        calls = []
        def runner(args, **kwargs):
            calls.append((args, kwargs))
            return type("Result", (), {"returncode": 0})()
        self.assertEqual(app.host(True, runner=runner), 0)
        self.assertEqual(calls[0][0], [str(app.BINARY), "--pair"])
        self.assertNotIn("shell", calls[0][1])
        app.host(False, runner=runner)
        self.assertEqual(calls[1][0], [str(app.BINARY)])

    def test_group_without_controller_still_pairs(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "p2p.conf").write_text("group")
            self.assertFalse(app.has_saved_controller(root))
            (root / "known-clients").write_text("# comment\n")
            self.assertFalse(app.has_saved_controller(root))
            (root / "known-clients").write_text("synthetic-controller\n")
            self.assertTrue(app.has_saved_controller(root))


if __name__ == "__main__":
    unittest.main()
