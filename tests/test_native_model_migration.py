"""Real Windows file-migration checks; no model downloads or user files are used."""
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "windows" / "migrate-local-models-to-unsloth.ps1"


class NativeModelMigrationTests(unittest.TestCase):
    def test_verified_move_preserves_bytes_identity_and_hf_reference(self):
        with tempfile.TemporaryDirectory(prefix="go-model-migration-") as temporary:
            root = Path(temporary)
            source = root / "old" / "publisher" / "repo"
            source.mkdir(parents=True)
            content = b"local-model-fixture"
            model = source / "model.gguf"
            model.write_bytes(content)
            revision = "a" * 40
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps({"models": [{
                "repository": "publisher/repo", "revision": revision,
                "path": "publisher/repo/model.gguf", "length": len(content),
                "sha256": hashlib.sha256(content).hexdigest(),
            }]}))
            report = root / "report.json"
            arguments = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT),
                         "-SourceRoot", str(root / "old"), "-DestinationRoot", str(root / "hub"),
                         "-ManifestPath", str(manifest), "-ReportPath", str(report), "-Apply"]
            result = subprocess.run(arguments, capture_output=True, text=True, timeout=30)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            destination = root / "hub" / "models--publisher--repo" / "snapshots" / revision / "model.gguf"
            self.assertFalse(model.exists())
            self.assertEqual(content, destination.read_bytes())
            self.assertEqual(revision.encode(), (destination.parents[2] / "refs" / "main").read_bytes())
            data = json.loads(report.read_text(encoding="utf-8-sig"))
            self.assertEqual("completed", data["state"])
            self.assertTrue(data["files"][0]["moved"])
            self.assertTrue(data["files"][0]["fileId"].startswith("0x"))

            # A collision must fail before moving or modifying either file.
            model.write_bytes(content)
            collision = subprocess.run(arguments, capture_output=True, text=True, timeout=30)
            self.assertNotEqual(0, collision.returncode)
            self.assertIn("Destination already exists", collision.stdout + collision.stderr)
            self.assertEqual(content, model.read_bytes())
            self.assertEqual(content, destination.read_bytes())

    def test_hash_mismatch_does_not_move_any_source(self):
        with tempfile.TemporaryDirectory(prefix="go-model-migration-") as temporary:
            root = Path(temporary)
            model = root / "old" / "publisher" / "repo" / "model.gguf"
            model.parent.mkdir(parents=True)
            model.write_bytes(b"corrupt")
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps({"models": [{
                "repository": "publisher/repo", "revision": "b" * 40,
                "path": "publisher/repo/model.gguf", "length": 7, "sha256": "0" * 64,
            }]}))
            result = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT),
                                     "-SourceRoot", str(root / "old"), "-DestinationRoot", str(root / "hub"),
                                     "-ManifestPath", str(manifest), "-ReportPath", str(root / "report.json"), "-Apply"],
                                    capture_output=True, text=True, timeout=30)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("SHA-256 mismatch", result.stdout + result.stderr)
            self.assertEqual(b"corrupt", model.read_bytes())
            self.assertFalse((root / "hub").exists())


if __name__ == "__main__":
    unittest.main()
