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

    def test_pair_has_independent_presets_and_idempotent_configuration(self):
        manager = catalog.GpuLoadManager(self.root, self.root / "models.ini", self.root, 8081)
        active = []
        manager.router = lambda *args: {"data": active}
        with patch.object(catalog, "gpu_inventory", return_value=self.gpus):
            configured = manager.configure_pair(self.model["id"], self.model["id"])
        self.assertNotEqual(configured["mainAlias"], configured["secondaryAlias"])
        text = manager.preset.read_text()
        main_section = text.split("[" + configured["mainAlias"] + "]")[1].split("[")[0]
        secondary_section = text.split("[" + configured["secondaryAlias"] + "]")[1]
        self.assertIn("device = CUDA0", main_section)
        self.assertIn("device = CUDA1", secondary_section)
        for section in (main_section, secondary_section):
            self.assertIn("split-mode = none", section)
            self.assertIn("ctx-size = 32768", section)
            self.assertIn("fit = off", section)
        active.append({"id": configured["mainAlias"], "status": {"value": "loaded"}})
        self.assertTrue(manager.configure_pair(self.model["id"], self.model["id"])["reused"])
        with self.assertRaises(ValueError):
            manager.configure_pair(self.model["id"], None)
        active.clear()
        manager.configure_pair(self.model["id"], None)
        self.assertNotIn("go-agent-instance", manager.preset.read_text())

    def test_secondary_load_keeps_primary_and_never_falls_back_to_shared_gpus(self):
        manager = catalog.GpuLoadManager(self.root, self.root / "models.ini", self.root, 8081)
        active = []
        manager.router = lambda *args: {"data": active}
        with patch.object(catalog, "gpu_inventory", return_value=self.gpus):
            pair = manager.configure_pair(self.model["id"], self.model["id"])
        main, secondary = pair["mainAlias"], pair["secondaryAlias"]
        attempts = []
        active.extend([{"id": main, "status": {"value": "loaded"}},
                       {"id": secondary, "status": {"value": "unloaded"}}])
        def router(path, body=None):
            if path == "models/load":
                attempts.append(body["model"])
                active[1]["status"] = {"value": "failed", "failed": True}
                (self.root / "llama.stderr.log").write_text("CUDA error: out of memory")
            return {"data": active}
        manager.router = router
        with self.assertRaises(RuntimeError):
            manager.load(secondary)
        self.assertEqual(attempts, [secondary])
        self.assertEqual(manager.placements[main], "CUDA0")
        self.assertEqual(manager.placements[secondary], "CUDA1")
        self.assertEqual(active[0]["status"]["value"], "loaded")

    def test_pair_requires_two_physical_gpus(self):
        manager = catalog.GpuLoadManager(self.root, self.root / "models.ini", self.root, 8081)
        manager.router = lambda *args: {"data": []}
        with patch.object(catalog, "gpu_inventory", return_value=self.gpus[:1]):
            with self.assertRaisesRegex(ValueError, "GPU0 and GPU1"):
                manager.configure_pair(self.model["id"], self.model["id"])
        self.assertIsNone(manager.pair)

    def test_failed_catalog_reload_does_not_claim_pair_is_configured(self):
        manager = catalog.GpuLoadManager(self.root, self.root / "models.ini", self.root, 8081)
        def router(path, body=None):
            if "reload" in path:
                raise OSError("router disconnected")
            return {"data": []}
        manager.router = router
        with patch.object(catalog, "gpu_inventory", return_value=self.gpus):
            with self.assertRaises(OSError):
                manager.configure_pair(self.model["id"], self.model["id"])
        self.assertIsNone(manager.pair)
        self.assertNotIn("go-agent-instance", manager.preset.read_text())

    def test_gpu_enumeration_uses_pci_order_without_changing_physical_preference(self):
        with patch.object(catalog.subprocess, "run", return_value=type("Result", (), {"stdout": "0, 0000:80:00.0, 10000\n1, 0000:20:00.0, 20000\n"})()):
            devices = catalog.gpu_inventory()
        self.assertEqual(devices[0]["index"], 1)
        self.assertEqual(devices[0]["device"], "CUDA0")


if __name__ == "__main__":
    unittest.main()
