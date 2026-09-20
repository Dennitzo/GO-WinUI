"""Best-effort native KV snapshots. Conversation history remains authoritative.

Only opaque, hashed keys become filenames. llama.cpp validates restored state and
still compares prompt tokens before reusing it. No model is loaded by this module.
"""
import hashlib
import json
from pathlib import Path
import re
import shutil
import struct
import time
import urllib.parse
import uuid


# A Stop arrives while llama is inside a prompt batch. Large models process a
# 2048-token batch in tens of seconds, so the slot reports is_processing long
# after the client disconnected. The gateway keeps its turn gate until this save
# returns; waiting here costs nothing the follow-up prompt would not wait for
# anyway (the single slot cannot start it earlier) and keeps the snapshot exact.
INTERRUPTED_SAVE_IDLE_SECONDS = 90.0


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

    def _idle_slot(self, model, timeout=2):
        # Closing a streaming HTTP request is acknowledged before llama.cpp has
        # necessarily finished cancelling its slot. Briefly drain that race;
        # never erase the session identity or restore an older snapshot over it.
        deadline = time.monotonic() + timeout
        while True:
            slot = next((item for item in self._slots(model) if item.get("id") == 0), None)
            if slot is None:
                raise RuntimeError("Native slot is not available")
            if not slot.get("is_processing"):
                return slot
            if time.monotonic() >= deadline:
                raise RuntimeError("Native slot is not idle")
            time.sleep(0.05)

    def prepare(self, model, key):
        try:
            identity = self._identity(model, key)
            current = self.resident.get(model)
            # A model can also disappear through an external router unload. A
            # fresh empty slot must restore even when its logical key is equal.
            slot = self._idle_slot(model)
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
            # An unavailable/busy slot does not invalidate its logical owner.
            # A later call must be allowed to reuse the interrupted prefix once
            # cancellation has drained. Actual unloads call invalidate().
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

    @staticmethod
    def _snapshot_tail(path, prompt_tokens, saved_tokens):
        # llama_state_seq_save_file v3 stores its token vector before the KV
        # bytes. Recent servers wrap it in server_tokens::serialize v1. Read
        # only that bounded vector tail, never the potentially huge KV state.
        with path.open("rb") as stream:
            header = stream.read(12)
            if len(header) != 12:
                raise ValueError("Incomplete native session header")
            magic, version, packed_count = struct.unpack("<III", header)
            if magic != 0x67677371 or version != 3:
                raise ValueError("Unsupported native session format")
            if packed_count < 1 or packed_count > (path.stat().st_size - 12) // 4:
                raise ValueError("Invalid native session token bounds")
            marker_bytes = stream.read(4)
            marker, = struct.unpack("<i", marker_bytes)
            if marker == -1:
                if packed_count < 4:
                    raise ValueError("Incomplete native server token header")
                server_version, token_count = struct.unpack("<II", stream.read(8))
                if server_version != 1 or token_count > packed_count - 4:
                    raise ValueError("Unsupported native server token vector")
                offset = 24
            else:
                if marker < 0:
                    raise ValueError("Invalid native plain token vector")
                token_count, offset = packed_count, 12  # Older plain token list.
            if token_count != saved_tokens or not 0 < prompt_tokens <= token_count:
                raise ValueError("Native snapshot and prompt token counts differ")
            tail_count = token_count - prompt_tokens
            if tail_count > 32768:
                raise ValueError("Native generated tail exceeds the recovery limit")
            stream.seek(offset + prompt_tokens * 4)
            encoded = stream.read(tail_count * 4)
            if len(encoded) != tail_count * 4:
                raise ValueError("Incomplete native generated token tail")
            tokens = list(struct.unpack("<" + "i" * tail_count, encoded))
            if any(token < 0 for token in tokens):
                raise ValueError("Native generated tail contains non-text tokens")
            return tokens

    def _save(self, model, prompt_tokens=None):
        current = self.resident.get(model)
        if not current or not current["dirty"]:
            return dict(status="unchanged")
        identity = current["identity"]
        target = self.directory / (identity + ".bin")
        temporary = self.directory / (identity + "." + uuid.uuid4().hex + ".pending")
        try:
            slot = self._idle_slot(model, timeout=INTERRUPTED_SAVE_IDLE_SECONDS if prompt_tokens is not None else 2)
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
            result = self._record(model, "save", dict(status="saved", savedTokens=response["n_saved"],
                                             bytes=response.get("n_written", target.stat().st_size)))
            if prompt_tokens is not None:
                try:
                    tokens = self._snapshot_tail(target, prompt_tokens, response["n_saved"])
                    decoded = self.router("detokenize", dict(model=model, tokens=tokens)) if tokens else {"content": ""}
                    tail = decoded.get("content")
                    if not isinstance(tail, str):
                        raise ValueError("Native generated tail was not decoded as text")
                    tail.encode("utf-8", errors="strict")
                    result["generatedTail"] = tail
                except Exception as error:
                    # A valid KV snapshot remains saved even if exact text
                    # recovery is unavailable. Never label the SSE tail exact.
                    result["generatedTailDetail"] = str(error)[:500]
            return result
        except Exception as error:
            return self._record(model, "save", dict(status="unavailable", detail=str(error)[:500]))
        finally:
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass  # A timed-out native write may still hold this unique file.

    def save(self, model, key, prompt_tokens=None):
        try:
            if prompt_tokens is not None and (type(prompt_tokens) is not int or prompt_tokens <= 0 or not key):
                raise ValueError("promptTokens requires a positive integer and a session key")
            current = self.resident.get(model)
            if current is None or (key is not None and current["identity"] != self._identity(model, key)):
                return dict(status="unchanged")
            return self._save(model, prompt_tokens)
        except Exception as error:
            return self._record(model, "save", dict(status="unavailable", detail=str(error)[:500]))

    def save_all(self):
        for model in list(self.resident):
            self._save(model)
