import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from session_cache import NativeSessionCache


class SessionCacheTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.tokens = 100
        self.busy = False
        self.calls = []
        self.fail_save = False
        self.fail_restore = False
        self.version = 1
        self.cache = self.create_cache()

    def create_cache(self):
        return NativeSessionCache(self.root, self.router, lambda _: self.version)

    def router(self, path, body=None, timeout=15):
        self.calls.append((path, body))
        if path.startswith("slots?"):
            return [dict(id=0, is_processing=self.busy, n_prompt_tokens=self.tokens)]
        if "action=save" in path:
            (self.root / body["filename"]).write_bytes(b"snapshot" * 128)
            if self.fail_save:
                raise OSError("simulated disk error")
            return dict(n_saved=self.tokens, n_written=1024)
        if "action=restore" in path:
            if self.fail_restore:
                raise ValueError("incompatible state")
            self.tokens = 100
            return dict(n_restored=100, n_read=1024)
        raise AssertionError(path)

    def actions(self, action):
        return [call for call in self.calls if "action=" + action in call[0]]

    def test_contiguous_session_stays_resident_without_disk_io(self):
        self.assertEqual("miss", self.cache.prepare("model", "session")["status"])
        self.assertEqual("resident", self.cache.prepare("model", "session")["status"])
        self.assertFalse(self.actions("restore"))
        self.assertFalse(self.actions("save"))
        events = [json.loads(line) for line in (self.root / "events.jsonl").read_text().splitlines()]
        self.assertEqual(["miss", "resident"], [event["status"] for event in events])

    def test_completed_session_restores_after_manager_restart(self):
        self.cache.prepare("model", "session")
        self.assertEqual("saved", self.cache.save("model", "session")["status"])
        self.tokens = 0
        reopened = self.create_cache()
        self.assertEqual(dict(status="restored", restoredTokens=100), reopened.prepare("model", "session"))

    def test_auxiliary_request_saves_then_resume_restores_original(self):
        self.cache.prepare("model", "session")
        self.assertEqual("bypass", self.cache.prepare("model", None)["status"])
        self.assertEqual(1, len(self.actions("save")))
        self.assertEqual("restored", self.cache.prepare("model", "session")["status"])

    def test_null_key_saves_tracked_session_before_unload(self):
        self.cache.prepare("model", "session")
        self.assertEqual("saved", self.cache.save("model", None)["status"])
        self.cache.invalidate("model")
        self.assertEqual("restored", self.cache.prepare("model", "session")["status"])

    def test_busy_slot_is_not_saved_or_restored(self):
        self.cache.prepare("model", "session")
        self.busy = True
        self.assertEqual("unavailable", self.cache.save("model", "session")["status"])
        self.assertEqual("unavailable", self.cache.prepare("model", "another")["status"])
        self.assertFalse(self.actions("save"))
        self.assertFalse(self.actions("restore"))

    def test_failed_save_does_not_replace_valid_snapshot(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        target = next(self.root.glob("*.bin"))
        original = target.read_bytes()
        self.cache.prepare("model", "session")
        self.fail_save = True
        self.assertEqual("unavailable", self.cache.save("model", "session")["status"])
        self.assertEqual(original, target.read_bytes())
        self.assertFalse(list(self.root.glob("*.pending")))

    def test_model_version_change_does_not_restore_incompatible_snapshot(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.version = 2
        self.tokens = 0
        self.assertEqual("miss", self.create_cache().prepare("model", "session")["status"])
        self.assertFalse(self.actions("restore"))

    def test_failed_restore_returns_unavailable_and_can_save_new_state(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.cache.invalidate("model")
        self.fail_restore = True
        self.assertEqual("unavailable", self.cache.prepare("model", "session")["status"])
        self.assertEqual("saved", self.cache.save("model", "session")["status"])

    def test_file_names_never_contain_external_session_key(self):
        self.cache.prepare("model", "../../outside")
        self.cache.save("model", "../../outside")
        self.assertEqual(1, len(list(self.root.glob("*.bin"))))
        self.assertEqual(64, len(next(self.root.glob("*.bin")).stem))

    def test_disk_reserve_prevents_native_write(self):
        self.cache.prepare("model", "session")
        with patch("session_cache.shutil.disk_usage", return_value=type("Usage", (), {"free": 1})()):
            self.assertEqual("unavailable", self.cache.save("model", "session")["status"])
        self.assertFalse(self.actions("save"))

    def test_snapshot_replacement_counts_only_final_size_toward_budget(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.cache.maximum_bytes = 1536
        self.cache.prepare("model", "session")
        self.assertEqual("saved", self.cache.save("model", "session")["status"])
        self.assertEqual(1, len(list(self.root.glob("*.bin"))))

    def test_atomic_replacement_still_requires_free_space_for_both_files(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.cache.prepare("model", "session")
        saves = len(self.actions("save"))
        with patch("session_cache.shutil.disk_usage", return_value=type("Usage", (), {"free": self.cache.free_reserve + 1000})()):
            self.assertEqual("unavailable", self.cache.save("model", "session")["status"])
        self.assertEqual(saves, len(self.actions("save")))

    def test_first_snapshot_uses_model_estimate_when_available(self):
        self.cache.estimate_bytes = lambda model, tokens: 1200
        self.cache.maximum_bytes = 1536
        self.cache.prepare("model", "session")
        self.assertEqual("saved", self.cache.save("model", "session")["status"])

    def test_eviction_removes_only_inactive_owned_snapshots(self):
        self.cache.prepare("model", "session")
        stale = self.root / ("a" * 64 + ".bin")
        stale.write_bytes(b"old" * 512)
        (self.root / "do-not-delete.txt").write_text("user")
        self.cache.maximum_bytes = 100 * 256 * 1024
        self.assertEqual("saved", self.cache.save("model", "session")["status"])
        self.assertFalse(stale.exists())
        self.assertTrue((self.root / "do-not-delete.txt").exists())

    def test_shutdown_saves_each_dirty_session_only_once(self):
        self.cache.prepare("one", "session")
        self.cache.prepare("two", "session")
        self.cache.save_all()
        self.cache.save_all()
        self.assertEqual(2, len(self.actions("save")))

    def test_general_and_coding_session_switches_restore_only_their_own_snapshot(self):
        for scope in ("general/session-a", "coding/session-a", "general/session-b"):
            self.assertEqual("miss", self.cache.prepare("model", scope)["status"])
            self.cache.save("model", scope)
        for scope in ("general/session-a", "coding/session-a", "general/session-b"):
            self.assertEqual("restored", self.cache.prepare("model", scope)["status"])
        self.assertEqual(3, len(list(self.root.glob("*.bin"))))

    def test_model_switch_never_restores_another_models_kv_but_return_restores_its_own(self):
        self.cache.prepare("model-one", "session")
        self.cache.save("model-one", "session")
        self.cache.invalidate("model-one")
        self.assertEqual("miss", self.cache.prepare("model-two", "session")["status"])
        self.cache.save("model-two", "session")
        self.cache.invalidate("model-two")
        self.assertEqual("restored", self.cache.prepare("model-one", "session")["status"])
        self.assertEqual(2, len(list(self.root.glob("*.bin"))))

    def test_reopened_app_and_native_empty_slot_restore_saved_snapshot(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.tokens = 0
        self.assertEqual("restored", self.cache.prepare("model", "session")["status"])
        self.assertEqual("restored", self.create_cache().prepare("model", "session")["status"])

    def test_slot_restore_without_positive_token_measurement_is_not_reported_as_restored(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.cache.invalidate("model")
        original = self.cache.router
        self.cache.router = lambda path, body=None, timeout=15: dict(n_restored=0) if "action=restore" in path else original(path, body, timeout)
        self.assertEqual("unavailable", self.cache.prepare("model", "session")["status"])

    def test_foreign_session_save_cannot_overwrite_current_session_snapshot(self):
        self.cache.prepare("model", "session-a")
        self.assertEqual("unchanged", self.cache.save("model", "session-b")["status"])
        self.assertFalse(self.actions("save"))


if __name__ == "__main__":
    unittest.main()
