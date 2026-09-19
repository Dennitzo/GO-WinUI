"""Best-effort native KV snapshots. Conversation history remains authoritative.

Only opaque, hashed keys become filenames. llama.cpp validates restored state and
still compares prompt tokens before reusing it. No model is loaded by this module.
"""
import hashlib
import json
from pathlib import Path
import re
import shutil
import time
import urllib.parse
import uuid


class NativeSessionCache:
    def __init__(self, directory, router, fingerprint, maximum_bytes=64 * 1024**3,
                 free_reserve=4 * 1024**3, estimate_bytes=None):
        self.directory = Path(directory).resolve()
        self.directory.mkdir(parents=True, exist_ok=True)
        self.router, self.fingerprint = router, fingerprint
        self.maximum_bytes, self.free_reserve = maximum_bytes, free_reserve
        self.estimate_bytes = estimate_bytes
        self.resident = {}

    def invalidate(self, model):
        self.resident.pop(model, None)

    def _identity(self, model, key):
        if key is None:
            return None
        if not isinstance(key, str) or not 1 <= len(key) <= 2048:
            raise ValueError("Invalid native session cache key")
        return hashlib.sha256(json.dumps([model, key, self.fingerprint(model)],
                            sort_keys=True).encode()).hexdigest()

    def _record(self, model, operation, result):
        try:
            log = self.directory / "events.jsonl"
            if log.exists() and log.stat().st_size > 10 * 1024**2:
                log.replace(self.directory / "events.previous.jsonl")
            with log.open("a", encoding="utf-8") as stream:
                stream.write(json.dumps(dict(time=time.time(), model=model,
                                  operation=operation, **result)) + "\n")
        except OSError:
            pass  # Cache diagnostics must not break inference on a full disk.
        return result

    def _slots(self, model):
        return self.router("slots?model=" + urllib.parse.quote(model, safe=""))

    def prepare(self, model, key):
        try:
            identity = self._identity(model, key)
            current = self.resident.get(model)
            # A model can also disappear through an external router unload. A
            # fresh empty slot must restore even when its logical key is equal.
            slots = self._slots(model)
            slot = next((item for item in slots if item.get("id") == 0), None)
            if slot is None or slot.get("is_processing"):
                raise RuntimeError("Native slot is not idle")
            tokens = slot.get("n_prompt_tokens", slot.get("n_past", slot.get("n_tokens", 0)))
            if current and current["identity"] == identity and tokens > 0:
                current["dirty"] = True
                return self._record(model, "prepare", dict(status="resident"))
            if current and tokens > 0:
                self._save(model)
            self.resident.pop(model, None)
            result = dict(status="bypass" if identity is None else "miss")
            path = self.directory / (identity + ".bin") if identity else None
            if path and path.is_file():
                try:
                    response = self.router("slots/0?action=restore",
                                 dict(model=model, filename=path.name), timeout=120)
                    restored = response.get("n_restored")
                    if not isinstance(restored, int) or restored <= 0:
                        raise RuntimeError("Native server did not restore reusable session tokens")
                    path.touch()
                    result = dict(status="restored", restoredTokens=restored)
                except Exception as error:
                    # A partial/incompatible cache is disposable; saved messages
                    # still permit a correct cold prompt, with a visible reason.
                    result = dict(status="unavailable", detail=str(error)[:500])
            if identity:
                self.resident[model] = dict(identity=identity, key=key, dirty=True)
            return self._record(model, "prepare", result)
        except Exception as error:
            self.resident.pop(model, None)
            return self._record(model, "prepare", dict(status="unavailable", detail=str(error)[:500]))

    def _prune(self, required=0, protected=None, replacing=None):
        protected = set(protected or ()) | {
            value["identity"] + ".bin" for value in self.resident.values()}
        files = sorted((path for path in self.directory.glob("*.bin")
                        if re.fullmatch(r"[0-9a-f]{64}\.bin", path.name)),
                       key=lambda path: path.stat().st_mtime)
        pending_bytes = 0
        for pending in self.directory.glob("*.pending"):
            if not re.fullmatch(r"[0-9a-f]{64}\.[0-9a-f]{32}\.pending", pending.name):
                continue
            try:
                if time.time() - pending.stat().st_mtime > 300:
                    pending.unlink()
                else:
                    pending_bytes += pending.stat().st_size
            except OSError:
                pending_bytes += pending.stat().st_size if pending.exists() else 0
        total = pending_bytes + sum(path.stat().st_size for path in files)
        # The old snapshot survives until atomic replacement. It counts toward
        # peak filesystem use, but not toward the eventual snapshot allowance.
        replaced_bytes = replacing.stat().st_size if replacing and replacing.exists() else 0
        final_growth = required - replaced_bytes
        for path in files:
            if total + final_growth <= self.maximum_bytes and shutil.disk_usage(self.directory).free >= self.free_reserve + required:
                break
            if path.name in protected:
                continue
            total -= path.stat().st_size
            path.unlink()
            path.with_suffix(".json").unlink(missing_ok=True)
        if total + final_growth > self.maximum_bytes or shutil.disk_usage(self.directory).free < self.free_reserve + required:
            raise OSError("Insufficient native session cache space; conversation remains saved")

    def _save(self, model):
        current = self.resident.get(model)
        if not current or not current["dirty"]:
            return dict(status="unchanged")
        identity = current["identity"]
        target = self.directory / (identity + ".bin")
        temporary = self.directory / (identity + "." + uuid.uuid4().hex + ".pending")
        try:
            slots = self._slots(model)
            slot = next((item for item in slots if item.get("id") == 0), None)
            if slot is None or slot.get("is_processing"):
                raise RuntimeError("Native slot is not idle")
            token_count = slot.get("n_prompt_tokens", slot.get("n_past", slot.get("n_tokens", 0)))
            if token_count <= 0:
                raise RuntimeError("Native slot has no reusable tokens")
            # Reserve before the native writer starts. Subsequent snapshots use
            # measured bytes/token; the first uses GGUF cache dimensions when
            # available and an explicit conservative fallback otherwise.
            previous_bytes = target.stat().st_size if target.exists() else 0
            metadata = target.with_suffix(".json")
            estimate = self.estimate_bytes(model, token_count) if self.estimate_bytes else None
            estimate = estimate if isinstance(estimate, int) and estimate > 0 else token_count * 256 * 1024
            if metadata.exists():
                try:
                    previous = json.loads(metadata.read_text(encoding="utf-8"))
                    estimate = int(max(1, previous["bytes"]) / max(1, previous["tokens"]) * token_count * 1.1)
                except (ValueError, TypeError, KeyError):
                    pass
            estimate = max(previous_bytes, estimate)
            self._prune(required=estimate, protected=[target.name], replacing=target)
            response = self.router("slots/0?action=save",
                         dict(model=model, filename=temporary.name), timeout=120)
            if not temporary.is_file() or int(response.get("n_saved", 0)) <= 0:
                raise RuntimeError("Native server did not save a usable session cache")
            if temporary.stat().st_size > self.maximum_bytes:
                raise OSError("Native session cache exceeds its disk allowance")
            temporary.replace(target)
            metadata.write_text(json.dumps(dict(bytes=target.stat().st_size, tokens=response["n_saved"])), encoding="utf-8")
            current["dirty"] = False
            self._prune(protected=[target.name])
            return self._record(model, "save", dict(status="saved", savedTokens=response["n_saved"],
                                             bytes=response.get("n_written", target.stat().st_size)))
        except Exception as error:
            return self._record(model, "save", dict(status="unavailable", detail=str(error)[:500]))
        finally:
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass  # A timed-out native write may still hold this unique file.

    def save(self, model, key):
        try:
            current = self.resident.get(model)
            if current is None or (key is not None and current["identity"] != self._identity(model, key)):
                return dict(status="unchanged")
            return self._save(model)
        except Exception as error:
            return self._record(model, "save", dict(status="unavailable", detail=str(error)[:500]))

    def save_all(self):
        for model in list(self.resident):
            self._save(model)
