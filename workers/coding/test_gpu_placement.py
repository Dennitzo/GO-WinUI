import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from test_catalog import gguf

spec = importlib.util.spec_from_file_location("gpu_catalog", Path(__file__).with_name("catalog.py"))
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)


class GpuPlacementTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        gguf(self.root / "Qwen3.8-27B.gguf")
        self.model = catalog.discover_models(self.root)[0]
        self.gpus = [{"index": 0, "device": "CUDA0", "free": 48 * 1024**3},
                     {"index": 1, "device": "CUDA1", "free": 40 * 1024**3}]

    def test_qwen_prefers_physical_gpu_one_and_respects_actual_free_memory(self):
        self.assertEqual(catalog.choose_single_gpu(self.model, self.gpus), "CUDA1")
        self.gpus[1]["free"] = 0
        self.assertEqual(catalog.choose_single_gpu(self.model, self.gpus), "CUDA0")
        self.gpus[0]["free"] = 0
        self.assertIsNone(catalog.choose_single_gpu(self.model, self.gpus))

    def test_other_models_choose_most_free_memory(self):
        self.model["id"] = "coding/new-future-model"
        self.assertEqual(catalog.choose_single_gpu(self.model, self.gpus), "CUDA0")

    def test_shard_sizes_include_every_file(self):
        first = self.root / "large-00001-of-00002.gguf"
        second = self.root / "large-00002-of-00002.gguf"
        first.write_bytes(b"1" * 100)
        second.write_bytes(b"2" * 200)
        self.assertEqual(catalog.model_file_bytes({"path": first}), 300)

    def scenario(self, error):
        manager = catalog.GpuLoadManager(self.root, self.root / "models.ini", self.root, 8081)
        attempts = []
        def router(path, body=None):
            if path == "models/load":
                attempts.append(manager.placements[self.model["id"]])
                if len(attempts) == 1:
                    (self.root / "llama.stderr.log").write_text(error)
                return {}
            if path.startswith("v1/models"):
                failed = len(attempts) == 1
                return {"data": [{"id": self.model["id"], "status": {
                    "value": "unloaded" if len(attempts) < 2 else "loaded", "failed": failed}}]}
            raise AssertionError(path)
        manager.router = router
        return manager, attempts

    def test_allocation_failure_retries_once_with_multi_gpu_and_preserves_full_single_context(self):
        manager, attempts = self.scenario("CUDA error: out of memory")
        with patch.object(catalog, "gpu_inventory", return_value=self.gpus):
            result = manager.load(self.model["id"])
        self.assertTrue(result["fallback"])
        self.assertEqual(attempts, ["CUDA1", None])
        self.assertNotIn("device =", manager.preset.read_text())
        self.assertNotIn("ctx-size", manager.preset.read_text())
        self.assertIn('"outcome": "failed"', (self.root / "gpu-placement.jsonl").read_text())

    def test_non_memory_failure_does_not_repeat(self):
        manager, attempts = self.scenario("invalid model tensor")
        with patch.object(catalog, "gpu_inventory", return_value=self.gpus):
            with self.assertRaises(RuntimeError):
                manager.load(self.model["id"])
        self.assertEqual(attempts, ["CUDA1"])
        text = manager.preset.read_text()
        self.assertIn("device = CUDA1", text)
        self.assertIn("split-mode = none", text)
        self.assertIn("fit = off", text)
        self.assertIn("ctx-size = 32768", text)
        manager.refresh()
        self.assertEqual(text, manager.preset.read_text())

    def test_gpu_enumeration_uses_pci_order_without_changing_physical_preference(self):
        with patch.object(catalog.subprocess, "run", return_value=type("Result", (), {"stdout": "0, 0000:80:00.0, 10000\n1, 0000:20:00.0, 20000\n"})()):
            devices = catalog.gpu_inventory()
        self.assertEqual(devices[0]["index"], 1)
        self.assertEqual(devices[0]["device"], "CUDA0")


if __name__ == "__main__":
    unittest.main()
