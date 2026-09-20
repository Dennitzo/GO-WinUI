"""Discover complete local GGUF models and supervise the native llama.cpp router.

Model files are only read. Generated presets and logs use a separate state directory.
No network access, downloads, model conversion, or Unsloth code is used here.
"""
from __future__ import annotations

import argparse
import ctypes
import csv
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import struct
import subprocess
import sys
import threading
import time
import urllib.request
import urllib.error
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from session_cache import NativeSessionCache

SHARD = re.compile(r"^(.*)-([0-9]{5})-of-([0-9]{5})\.gguf$", re.IGNORECASE)
NON_TEXT = re.compile(r"(?:^|[-_./])(?:mmproj|ggml-vocab|embedding|embed|asr|whisper|clip|vae|diffusion)(?:[-_./]|$)", re.IGNORECASE)
NON_TEXT_ARCHITECTURES = ("bert", "nomic-bert", "jina-bert", "embedding", "t5", "clip", "mclip", "wavtokenizer", "whisper")
EMBEDDING_ARCHITECTURES = ("bert", "nomic-bert", "jina-bert", "embedding")
_METADATA_CACHE = {}
SCALAR_SIZES = {0: 1, 1: 1, 2: 2, 3: 2, 4: 4, 5: 4, 6: 4, 7: 1, 10: 8, 11: 8, 12: 8}


def read_exact(stream, length):
    data = stream.read(length)
    if len(data) != length:
        raise ValueError("Truncated GGUF metadata")
    return data


def u64(stream):
    return struct.unpack("<Q", read_exact(stream, 8))[0]


def read_string(stream):
    size = u64(stream)
    if size > 16 * 1024 * 1024:
        raise ValueError("GGUF metadata string exceeds bound")
    return read_exact(stream, size).decode("utf-8", errors="replace")


def skip_value(stream, kind):
    if kind == 8:
        length = u64(stream)
        if length > 16 * 1024 * 1024:
            raise ValueError("GGUF metadata string exceeds bound")
        stream.seek(length, 1)
    elif kind == 9:
        element = struct.unpack("<I", read_exact(stream, 4))[0]
        count = u64(stream)
        if count > 10_000_000 or element == 9:
            raise ValueError("Invalid GGUF array")
        if element in SCALAR_SIZES:
            stream.seek(SCALAR_SIZES[element] * count, 1)
        else:
            for _ in range(count):
                skip_value(stream, element)
    elif kind in SCALAR_SIZES:
        stream.seek(SCALAR_SIZES[kind], 1)
    else:
        raise ValueError("Unknown GGUF value kind")


def model_metadata(path, allow_metadata_only=False):
    stat = path.stat()
    if stat.st_size < (24 if allow_metadata_only else 1024 * 1024):
        return None
    cache_key = (str(path), stat.st_size, stat.st_mtime_ns, allow_metadata_only)
    if cache_key in _METADATA_CACHE:
        return _METADATA_CACHE[cache_key]
    with path.open("rb") as stream:
        magic, version, tensors, count = struct.unpack("<4sIQQ", read_exact(stream, 24))
        if magic != b"GGUF" or version not in (2, 3) or (tensors == 0 and not allow_metadata_only) or count > 100_000:
            return None
        metadata = {"general.type": "model", "tensor_count": tensors}
        for _ in range(count):
            key = read_string(stream)
            kind = struct.unpack("<I", read_exact(stream, 4))[0]
            if key in ("general.architecture", "general.type", "general.name", "tokenizer.chat_template") and kind == 8:
                metadata[key] = read_string(stream)
            elif key.endswith((".context_length", ".pooling_type", ".block_count", ".embedding_length",
                               ".attention.head_count", ".attention.head_count_kv", ".attention.key_length",
                               ".attention.value_length", ".attention.indexer.key_length", ".ssm.state_size", ".ssm.inner_size",
                               ".ssm.group_count", ".ssm.conv_kernel")) and kind == 4:
                metadata[key] = struct.unpack("<I", read_exact(stream, 4))[0]
            else:
                skip_value(stream, kind)
        # A zero-tensor first shard can end exactly at its metadata boundary.
        # Seeking beyond EOF still indicates truncated metadata.
        end = stream.tell()
        if end > stat.st_size or (end == stat.st_size and not (allow_metadata_only and tensors == 0)):
            return None
        if len(_METADATA_CACHE) > 512:
            _METADATA_CACHE.clear()
        _METADATA_CACHE[cache_key] = metadata
        return metadata


def is_text_model(path, allow_metadata_only=False):
    if NON_TEXT.search(path.name):
        return False
    metadata = model_metadata(path, allow_metadata_only)
    if metadata is None:
        return False
    architecture = metadata.get("general.architecture", "").lower()
    return bool(architecture) and metadata["general.type"] == "model" and not NON_TEXT.search(architecture) and not architecture.startswith(NON_TEXT_ARCHITECTURES)


def is_tensor_shard(path):
    # Unsloth may place tokenizer/architecture metadata in a zero-tensor first
    # shard. Subsequent shards carry tensors and only split metadata.
    if path.stat().st_size < 1024 * 1024:
        return False
    with path.open("rb") as stream:
        magic, version, tensors, count = struct.unpack("<4sIQQ", read_exact(stream, 24))
        return magic == b"GGUF" and version in (2, 3) and tensors > 0 and count <= 100_000


def matching_projector(path, root):
    # A quantization subfolder may use the projector beside it in the same HF
    # snapshot. Never borrow a projector from another snapshot or model repo.
    stop = path.parent
    for parent in path.parents:
        if parent.parent.name == "snapshots":
            stop = parent
            break
        if parent == root:
            break
    current = path.parent
    while current.is_relative_to(root):
        candidates = []
        for projector in current.glob("mmproj*.gguf"):
            if projector.is_file() and projector.resolve().is_relative_to(root):
                metadata = model_metadata(projector)
                if metadata and metadata.get("general.architecture") in ("clip", "mclip"):
                    candidates.append(projector)
        if candidates:
            return candidates[0] if len(candidates) == 1 else None
        if current == stop:
            break
        current = current.parent
    return None


def reasoning_profile(metadata):
    """Only select effort values supported by this model's actual template."""
    template = metadata.get("tokenizer.chat_template", "")
    architecture = metadata.get("general.architecture", "")
    levels = []
    for match in re.finditer(r"(?:resolved_)?reasoning_(?:effort|strength)\s+(?:not\s+)?in\s*[\[(]([^\])]{1,512})[\])]", template):
        levels.extend(re.findall(r"['\"]([a-z][a-z0-9_-]{0,31})['\"]", match.group(1)))
    levels = list(dict.fromkeys(levels))
    if not levels and (architecture == "gpt-oss" or "gpt-oss" in metadata.get("general.name", "").lower()) and "reasoning_effort" in template:
        levels = ["low", "medium", "high"]
    if levels:
        if "enable_thinking" in template and "none" not in levels:
            levels.insert(0, "none")
        highest = next((level for level in ("max", "ultra", "xhigh", "high", "medium", "low", "minimal") if level in levels), None)
        return {"enabled": True, "effort": highest, "budget": -1, "levels": levels, "mode": "llama-native"}
    if "enable_thinking" in template:
        return {"enabled": True, "effort": None, "budget": -1, "levels": ["none", "on"], "mode": "llama-toggle"}
    if "<think>" in template or "<|channel>analysis" in template:
        return {"enabled": None, "effort": None, "budget": -1, "levels": ["on"], "mode": "llama-fixed"}
    return {"enabled": None, "effort": None, "budget": -1, "levels": [], "mode": "automatic"}


def discover_models(root):
    root = Path(root).resolve()
    found = []
    if not root.is_dir():
        return found
    for path in sorted(root.rglob("*.gguf")):
        try:
            # HF symlinks may target blobs within this mount, never other host paths.
            if not path.resolve().is_relative_to(root):
                continue
            match = SHARD.match(path.name)
            if match:
                stem, index, total = match.groups()
                if int(index) != 1 or not 1 <= int(total) <= 128:
                    continue
                shards = [path.with_name(f"{stem}-{part:05d}-of-{int(total):05d}.gguf") for part in range(1, int(total) + 1)]
                if not all(part.is_file() and part.resolve().is_relative_to(root) for part in shards):
                    continue
                metadata = model_metadata(shards[0], allow_metadata_only=True)
                if not metadata or (metadata["tensor_count"] == 0 and len(shards) == 1) or not all(is_tensor_shard(part) for part in shards[1:]):
                    continue
                name = stem
            else:
                metadata = model_metadata(path)
                if not metadata:
                    continue
                name = path.stem
            safe_name = re.sub(r"[^A-Za-z0-9_.-]", "_", name)[:160]
            digest = hashlib.sha256(path.relative_to(root).as_posix().encode()).hexdigest()[:12]
            architecture = metadata.get("general.architecture", "").lower()
            if not architecture or metadata["general.type"] != "model":
                continue
            context = metadata.get(architecture + ".context_length")
            # Do not invent a supported context for incomplete/unknown metadata.
            if not isinstance(context, int) or not 1 <= context <= 2_147_483_647:
                continue
            is_embedding = architecture.startswith(EMBEDDING_ARCHITECTURES) or re.search(r"(?:^|[-_])(?:embedding|embed)(?:[-_]|$)", name, re.IGNORECASE)
            if is_embedding:
                pooling = {1: "mean", 2: "cls", 3: "last"}.get(metadata.get(architecture + ".pooling_type"), "cls" if architecture == "bert" else "last")
                found.append({"id": f"embedding/{safe_name}~{digest}", "path": path, "role": "embedding",
                              "context": context, "contextPolicy": "model-maximum", "pooling": pooling})
                continue
            if NON_TEXT.search(path.name) or NON_TEXT.search(architecture) or architecture.startswith(NON_TEXT_ARCHITECTURES):
                continue
            reasoning = reasoning_profile(metadata)
            found.append({"id": f"coding/{safe_name}~{digest}", "path": path, "role": "general", "context": context,
                          "contextPolicy": "max-fit", "reasoning": reasoning})
            projector = matching_projector(path, root)
            if projector and architecture.startswith("deepseek"):
                # Keep DeepSeek's text/tools and vision in the same native instance.
                found[-1]["projector"] = projector
            if projector:
                found.append({"id": f"vision/{safe_name}~{digest}", "path": path, "role": "vision",
                              "context": context, "contextPolicy": "max-fit", "reasoning": reasoning, "projector": projector})
        except (OSError, ValueError, struct.error):
            continue
    return found


def discover(root):
    """Compatibility view for callers needing only Coding text models."""
    return [(model["id"], model["path"]) for model in discover_models(root) if model["role"] == "general"]


DEEPSEEK_HISTORY_BY_FLAG = "{%- if keep_reasoning and thinking -%}"
DEEPSEEK_HISTORY_BY_CONTENT = ("{%- if keep_reasoning and message['reasoning_content'] is defined "
                               "and message['reasoning_content'] -%}")


def german_reasoning_template(template):
    """Adapt known templates for German reasoning and durable prompt prefixes.

    The heading is native assistant prefill, not a fabricated reasoning sentence.
    llama returns the prefill in reasoning_content; history must retain it once.
    """
    deepseek_history = "{%- set keep_reasoning = tp.has or (loop.index0 > last_user_idx.value) -%}"
    if "dsml_token" in template and deepseek_history in template:
        # The stock DeepSeek-V4 template drops old reasoning without tools.
        # JSON history can therefore be identical while its rendered prompt
        # changes. In-memory rollback checkpoints mask this until /slots/restore
        # (which restores final KV+tokens, not those checkpoints). Keep the
        # provider's saved reasoning so the next user turn appends to the exact
        # generated prefix, including in General chat after a process restart.
        adapted = template.replace(deepseek_history, "{%- set keep_reasoning = true -%}")
        # Historical assistant turns must replay exactly what was generated. The
        # stock template renders them by the CURRENT thinking flag, so switching
        # reasoning between runs of one session ("none" -> "on") rewrites every
        # earlier turn and discards the whole native KV prefix (observed: 0 of
        # 158k cached tokens after Stop + new prompt). Render by the stored
        # reasoning of each turn instead; only the generation prompt follows the
        # current flag.
        return adapted.replace(DEEPSEEK_HISTORY_BY_FLAG, DEEPSEEK_HISTORY_BY_CONTENT)
    marker = "{%- if add_generation_prompt %}"
    position = template.rfind(marker)
    thinking = "{{- '<think>\\n' }}"
    if position < 0 or thinking not in template[position:]:
        return None
    instruction = "Analysiere auf Deutsch und beginne unmittelbar mit dem fachlichen Inhalt. Kuendige weder die Sprache noch deinen Denkprozess an."
    localized = template.replace("{%- set reasoning_instructions = '' %}",
        "{%- set reasoning_instructions = '" + instruction + "' %}")
    translations = {
        "Reasoning effort is set to xhigh. Please think carefully through the task, validate key assumptions, consider plausible alternatives, and prioritize correctness, consistency, and clarity in the final answer.":
            "Die Denkleistung ist auf xhigh gesetzt. Denke die Aufgabe sorgfaeltig auf Deutsch durch, pruefe wesentliche Annahmen und plausible Alternativen. Priorisiere Korrektheit, Konsistenz und Klarheit der Abschlussantwort.",
        "Reasoning effort is set to low. Keep your thinking brief and focused, moving directly to the conclusion without unnecessary elaboration.":
            "Die Denkleistung ist auf low gesetzt. Denke kurz und zielgerichtet auf Deutsch und gelange ohne unnoetige Ausfuehrungen zum Ergebnis."
    }
    for original, translated in translations.items():
        localized = localized.replace("'" + original + "'", "'" + translated + " " + instruction + "'")
    # System/reminder instructions alone do not reliably change Qwen's learned
    # English thinking language. A neutral German heading primes the actual
    # native continuation without inventing any analysis or modifying GGUFs.
    heading = "Überlegung auf Deutsch:\\n"
    history_thinking = "'\\n<think>\\n' + reasoning_content"
    if history_thinking in template:
        localized = localized.replace(thinking, "{{- '<think>\\n" + heading + "' }}")
        # The native parser returns this prefill with reasoning_content. The
        # original history formatter already reproduces it. Adding it here
        # would duplicate it and invalidate the KV prefix; empty reasoning
        # (thinking disabled) likewise keeps the original empty think block.
    return localized if localized != template else None


def multi_gpu_split_mode():
    """llama split mode for models that need both GPUs; GO_NATIVE_MULTI_GPU_SPLIT overrides."""
    value = os.environ.get("GO_NATIVE_MULTI_GPU_SPLIT", "layer").strip().lower()
    return value if value in ("layer", "row", "tensor") else "layer"


def write_presets(root, target, placements=None, managed_gpu=False, fit_target="2048", gpu_layers="auto"):
    models = discover_models(root)
    session_directory = Path(target).resolve().parent / "session-cache"
    session_directory.mkdir(parents=True, exist_ok=True)
    # Session persistence addresses slot 0. Omitting this internal limit lets
    # llama default to four slots and send chat turns to an unsaved LRU slot.
    # This is a single execution slot, not a user-facing parallel-agent option.
    lines = ["version = 1", "", "[*]", "load-on-startup = false", "stop-timeout = 10", "sleep-idle-seconds = -1", "parallel = 1",
             f"slot-save-path = {session_directory.as_posix()}/",
             "fit = on", f"fit-target = {fit_target}", "fit-ctx = 4096", f"n-gpu-layers = {gpu_layers}", ""]
    for model in models:
        model_id, path = model["id"], model["path"]
        if any(char in str(path) for char in "\r\n"):
            continue
        tags = f"go-context-train:{model['context']},go-context-policy:{model['contextPolicy']}"
        if model.get("projector"):
            tags += ",go-vision:projector"
        if managed_gpu:
            tags += ",go-gpu-policy:single-preferred-v1"
        if model.get("instance"):
            tags += f",go-agent-instance:{model['instance']},go-base-model:{model['baseModel']}"
        if model["role"] != "embedding":
            profile = model["reasoning"]
            tags += f",go-reasoning-mode:{profile['mode']},go-reasoning-levels:{'|'.join(profile['levels'])}"
            default = profile["effort"] or ("on" if "on" in profile["levels"] else "none" if profile["levels"] == ["none"] else "auto")
            tags += f",go-reasoning-default:{default}"
        lines += [f"[{model_id}]", f"model = {path.as_posix()}", f"tags = {tags}"]
        if model["role"] != "embedding":
            metadata = model_metadata(path, allow_metadata_only=True) or {}
            localized = german_reasoning_template(metadata.get("tokenizer.chat_template", ""))
            if localized:
                template_dir = Path(target).parent / "templates"
                template_dir.mkdir(parents=True, exist_ok=True)
                template_path = template_dir / (hashlib.sha256(localized.encode()).hexdigest()[:24] + ".jinja")
                if not template_path.exists():
                    template_path.write_text(localized, encoding="utf-8")
                lines += [f"chat-template-file = {template_path.as_posix()}"]
        placement = (placements or {}).get(model_id)
        if placement:
            lines += [f"device = {placement}", "split-mode = none", "main-gpu = 0", "n-gpu-layers = 999", "fit = off"]
            if model["role"] != "embedding":
                lines += [f"ctx-size = {model['context']}"]
            if model.get("projector"):
                lines += [f"mmproj-device = {placement}"]
        if model["role"] == "embedding":
            lines += [f"ctx-size = {model['context']}", "embedding = true", f"pooling = {model['pooling']}", f"batch-size = {model['context']}",
                      f"ubatch-size = {model['context']}", "cache-type-k = f16", "cache-type-v = f16", "flash-attn = off"]
        else:
            # Omit ctx-size entirely. llama starts at the GGUF training maximum,
            # then --fit can reduce it only when required by device memory.
            # Explicit ctx-size=0 disables that reduction in llama/common/arg.cpp.
            lines += ["cache-type-k = q8_0", "cache-type-v = q8_0", "flash-attn = on", "predict = -1",
                      "reasoning-budget = -1", "reasoning = on" if model["reasoning"]["enabled"] else "reasoning = auto"]
            if not placement and multi_gpu_split_mode() != "layer":
                # A model that does not fit one GPU is split across both. "layer"
                # (llama default) pipelines the GPUs; "tensor"/"row" run every
                # layer on both GPUs at once. Chosen by measurement, see README.
                lines += [f"split-mode = {multi_gpu_split_mode()}"]
            if model["reasoning"]["effort"]:
                lines += [f"reasoning-effort = {model['reasoning']['effort']}"]
            if model.get("projector"):
                lines += [f"mmproj = {model['projector'].as_posix()}"]
        lines.append("")
    text = "\n".join(lines)
    target = Path(target)
    if not target.exists() or target.read_text(encoding="utf-8") != text:
        temporary = target.with_suffix(".new")
        temporary.write_text(text, encoding="utf-8")
        temporary.replace(target)
        print(f"Native catalog: {len(models)} local text, vision and embedding preset(s) in {root}", flush=True)
    return models


def create_windows_job(process):
    """Ensure only this supervisor's native server tree ends with the supervisor."""
    if os.name != "nt":
        return None
    from ctypes import wintypes

    class BasicLimits(ctypes.Structure):
        _fields_ = [("process_time", ctypes.c_longlong), ("job_time", ctypes.c_longlong),
                    ("flags", wintypes.DWORD), ("min_working_set", ctypes.c_size_t),
                    ("max_working_set", ctypes.c_size_t), ("active_limit", wintypes.DWORD),
                    ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD), ("scheduling", wintypes.DWORD)]

    class IoCounters(ctypes.Structure):
        _fields_ = [(name, ctypes.c_ulonglong) for name in ("read_ops", "write_ops", "other_ops", "read_bytes", "write_bytes", "other_bytes")]

    class ExtendedLimits(ctypes.Structure):
        _fields_ = [("basic", BasicLimits), ("io", IoCounters), ("process_memory", ctypes.c_size_t),
                    ("job_memory", ctypes.c_size_t), ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t)]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.CreateJobObjectW.restype = wintypes.HANDLE
    kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    job = kernel.CreateJobObjectW(None, None)
    limits = ExtendedLimits()
    limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    if not job or not kernel.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)) or not kernel.AssignProcessToJobObject(job, process._handle):
        error = ctypes.get_last_error()
        if job:
            kernel.CloseHandle(job)
        process.kill()
        raise OSError(error, "Could not isolate the managed native llama process tree")
    return kernel, job


def gpu_inventory():
    """Match CUDA's PCI_BUS_ID ordering to physical nvidia-smi indices."""
    try:
        result = subprocess.run(["nvidia-smi", "--query-gpu=index,pci.bus_id,memory.free", "--format=csv,noheader,nounits"],
                                capture_output=True, text=True, timeout=10,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0, check=True)
        rows = sorted(csv.reader(result.stdout.splitlines()), key=lambda row: row[1].strip())
        return [{"index": int(row[0]), "device": f"CUDA{position}", "free": int(row[2]) * 1024 * 1024}
                for position, row in enumerate(rows)]
    except (OSError, ValueError, IndexError, subprocess.SubprocessError):
        return []


def model_file_bytes(model):
    path = model["path"]
    match = SHARD.match(path.name)
    paths = [path] if not match else [path.with_name(f"{match[1]}-{part:05d}-of-{int(match[3]):05d}.gguf")
                                    for part in range(1, int(match[3]) + 1)]
    if model.get("projector"):
        paths.append(model["projector"])
    return sum(item.stat().st_size for item in paths)


def choose_single_gpu(model, devices, reserve_mib=2048):
    # This is only admission to a real full-context allocation trial, not a KV
    # memory estimate. Unknown architectures are validated by llama itself.
    minimum = model_file_bytes(model) + int(reserve_mib) * 1024 ** 2
    candidates = [gpu for gpu in devices if gpu["free"] >= minimum]
    prefer_one = "qwen3.8-27b" in model["id"].lower()
    candidates.sort(key=lambda gpu: (prefer_one and gpu["index"] == 1, gpu["free"], gpu["index"] == 1), reverse=True)
    return candidates[0]["device"] if candidates else None


def estimate_q8_session_bytes(metadata, tokens):
    """Conservative q8_0 KV estimate for standard/GQA and DeepSeek-V4 caches.

    Count every layer as attention, including hybrid layers, so this remains an
    upper estimate without guessing an architecture's recurrent-layer mask.
    Unknown/variable dimensions use the cache manager's explicit fallback.
    """
    architecture = metadata.get("general.architecture", "")
    if architecture not in ("llama", "deepseek4") and not architecture.startswith("qwen"):
        return None
    def number(suffix):
        value = metadata.get(architecture + "." + suffix)
        return value if isinstance(value, int) and value > 0 else None
    layers = number("block_count")
    if architecture == "deepseek4":
        key = number("attention.key_length")
        indexer_key = number("attention.indexer.key_length")
        if not all((layers, key, indexer_key)):
            return None
        # llama-kv-cache-dsv4.cpp stores K-only MLA rows, not a dense K+V
        # tensor for all query heads. Include raw K for every token/layer,
        # CSA and lightning-indexer at 1/4, and HCA at 1/128. Counting every
        # layer in both compressed groups deliberately overestimates their
        # disjoint layer masks. Round rows upward and reserve compressor state,
        # slot metadata and 25% margin; real per-session measurements supersede
        # this initial estimate after the first successful snapshot.
        key_row = ((key + 31) // 32) * 34
        indexer_row = ((indexer_key + 31) // 32) * 34
        quarter = (tokens + 3) // 4 + 1
        compressed = (tokens + 127) // 128 + 1
        rows = layers * (tokens * key_row + quarter * (key_row + indexer_row) + compressed * key_row)
        return int((rows + tokens * 64) * 1.25) + 128 * 1024**2
    heads = number("attention.head_count")
    embedding = number("embedding_length")
    kv_heads = number("attention.head_count_kv") or heads
    inferred = embedding // heads if embedding and heads and embedding % heads == 0 else None
    key = number("attention.key_length") or inferred
    value = number("attention.value_length") or key
    if not all((layers, kv_heads, key, value)):
        return None
    # GGML q8_0 stores 32 values and a 2-byte scale in each 34-byte block.
    per_token = layers * (((key * kv_heads + 31) // 32) * 34 + ((value * kv_heads + 31) // 32) * 34)
    recurrent = 0
    if number("ssm.state_size") and number("ssm.inner_size"):
        state, inner = number("ssm.state_size"), number("ssm.inner_size")
        convolution = max(0, (number("ssm.conv_kernel") or 1) - 1) * (inner + 2 * (number("ssm.group_count") or 1) * state)
        recurrent = layers * (state * inner + convolution) * 4 * 2
    return int((tokens * (per_token + 24) + recurrent) * 1.1) + 16 * 1024**2


class GpuLoadManager:
    """Only changes presets while their model is unloaded; never on live refresh."""
    def __init__(self, root, preset, state, port, fit_target="2048", gpu_layers="auto", binary=None):
        self.root, self.preset, self.state, self.port = root, preset, state, port
        self.fit_target, self.gpu_layers = fit_target, gpu_layers
        self.placements = {}
        self.lock = threading.RLock()
        self.binary = Path(binary) if binary else None
        self.sessions = NativeSessionCache(Path(state) / "session-cache", self.router, self.session_fingerprint,
                                           estimate_bytes=self.session_size_estimate)

    def session_model(self, model_id):
        # This lookup is read-only: fingerprint checks do not rewrite/reload
        # active presets on each inference round.
        base_id = model_id
        model = next((item for item in discover_models(self.root) if item["id"] == base_id), None)
        if model is None:
            raise ValueError("Model is not in the local catalog")
        return model

    def session_size_estimate(self, model_id, tokens):
        metadata = model_metadata(Path(self.session_model(model_id)["path"]), allow_metadata_only=True) or {}
        return estimate_q8_session_bytes(metadata, tokens)

    def session_fingerprint(self, model_id):
        model = self.session_model(model_id)
        path = Path(model["path"])
        shard = SHARD.match(path.name)
        paths = sorted(path.parent.glob(shard.group(1) + "-*.gguf")) if shard else [path]
        if self.binary:
            paths += [self.binary] + sorted(self.binary.parent.glob("*.dll"))
        identity = [(str(item.resolve()), item.stat().st_size, item.stat().st_mtime_ns) for item in paths]
        metadata = model_metadata(path, allow_metadata_only=True) or {}
        return dict(version=1, files=identity, context=model["context"], cache="q8_0", slots=1,
                    template=german_reasoning_template(metadata.get("tokenizer.chat_template", "")),
                    placement=self.placements.get(model_id), fit=self.fit_target, gpuLayers=self.gpu_layers,
                    split=multi_gpu_split_mode())

    def session_prepare(self, model, key):
        with self.lock:
            return self.sessions.prepare(model, key)

    def session_save(self, model, key, prompt_tokens=None):
        with self.lock:
            return self.sessions.save(model, key, prompt_tokens)

    def refresh(self):
        with self.lock:
            return write_presets(self.root, self.preset, self.placements, managed_gpu=True,
                                 fit_target=self.fit_target, gpu_layers=self.gpu_layers)

    def router(self, path, body=None, timeout=15):
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}/{path}",
                  data=json.dumps(body).encode() if body is not None else None,
                  headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.load(response)

    def record(self, model, device, outcome, detail=None):
        with (self.state / "gpu-placement.jsonl").open("a", encoding="utf-8") as stream:
            stream.write(json.dumps({"time": time.time(), "model": model, "device": device or "multi-gpu-auto",
                                     "outcome": outcome, "detail": detail}) + "\n")

    def load(self, model_id):
        with self.lock:
            models = self.refresh()
            model = next((item for item in models if item["id"] == model_id), None)
            if model is None:
                raise ValueError("Model is not in the local catalog")
            current = self.router("v1/models")["data"]
            active = [item for item in current if item.get("status", {}).get("value") in ("loaded", "loading", "sleeping")]
            if any(item["id"] != model_id for item in active):
                raise ValueError("Unload the previous model before selecting GPU placement")
            if any(item["id"] == model_id for item in active):
                return {"success": True, "reused": True}
            self.sessions.invalidate(model_id)
            device = choose_single_gpu(model, gpu_inventory(), self.fit_target)
            deadline = time.monotonic() + 280
            for attempt in range(2):
                self.placements[model_id] = device
                self.refresh()
                self.router("v1/models?reload=1")
                logs = [self.state / "llama.stderr.log", self.state / "llama.stdout.log"]
                offsets = {log: log.stat().st_size if log.exists() else 0 for log in logs}
                self.record(model_id, device, "loading")
                self.router("models/load", {"model": model_id})
                while time.monotonic() < deadline:
                    item = next(item for item in self.router("v1/models")["data"] if item["id"] == model_id)
                    status = item.get("status", {})
                    if status.get("value") in ("loaded", "sleeping") and not status.get("failed"):
                        self.record(model_id, device, "loaded")
                        return {"success": True, "device": device, "fallback": attempt > 0}
                    if status.get("failed") or status.get("value") == "failed":
                        break
                    time.sleep(0.25)
                else:
                    try:
                        self.router("models/unload", {"model": model_id})
                    finally:
                        self.record(model_id, device, "timeout")
                    raise TimeoutError("Native model load exceeded its time limit")
                failure = ""
                for log, offset in offsets.items():
                    if log.exists():
                        with log.open("rb") as stream:
                            stream.seek(offset)
                            failure += stream.read(2 * 1024 * 1024).decode("utf-8", errors="replace")
                memory_failure = re.search(r"out of memory|cudaMalloc.*failed|failed to allocate|unable to allocate|CUDA error.*memory", failure, re.I)
                self.record(model_id, device, "failed", failure[-4000:])
                if not device or attempt or not memory_failure:
                    raise RuntimeError("Native model load failed; see gpu-placement.jsonl and llama.stderr.log")
                # A failed router child has already exited, releasing its CUDA
                # allocations. Reloading the changed preset joins that child.
                device = None
            raise RuntimeError("GPU load attempts exhausted")


def start_gpu_control(manager, host, port):
    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if self.path not in ("/models/load", "/sessions/prepare", "/sessions/save") or not 0 < length <= 8192:
                    raise ValueError("Invalid GPU control request")
                body = json.loads(self.rfile.read(length))
                if self.path.startswith("/sessions/"):
                    if self.path == "/sessions/prepare":
                        result = manager.session_prepare(body["model"], body.get("sessionKey"))
                    else:
                        result = manager.session_save(body["model"], body.get("sessionKey"), body.get("promptTokens"))
                else:
                    result = manager.load(body["model"])
                status = 200
            except Exception as error:
                result, status = {"error": str(error)}, 500
            encoded = json.dumps(result).encode()
            try:
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(encoded)))
                self.end_headers()
                self.wfile.write(encoded)
            except (BrokenPipeError, ConnectionResetError):
                pass
        def log_message(self, *_):
            pass
    server = ThreadingHTTPServer((host, port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def runtime_environment(source, state_directory):
    environment = dict(source, HF_HUB_OFFLINE="1", LLAMA_CACHE=str(state_directory / "cache"))
    # A parent shell's fixed context would bypass the model-maximum fitting policy.
    # No unrelated user environment or process is changed.
    for key in ("LLAMA_ARG_CTX_SIZE", "LLAMA_ARG_KV_UNIFIED_PER_SLOT", "LLAMA_ARG_FIT_CTX",
                "LLAMA_ARG_N_PREDICT", "LLAMA_ARG_THINK_BUDGET", "LLAMA_ARG_REASONING_EFFORT",
                "LLAMA_ARG_REASONING", "LLAMA_ARG_CHAT_TEMPLATE_KWARGS", "LLAMA_ARG_DEVICE", "LLAMA_ARG_SPLIT_MODE",
                "LLAMA_ARG_MAIN_GPU", "LLAMA_ARG_TENSOR_SPLIT", "LLAMA_ARG_N_GPU_LAYERS", "LLAMA_ARG_FIT",
                "LLAMA_ARG_FIT_TARGET", "CUDA_VISIBLE_DEVICES"):
        environment.pop(key, None)
    environment["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID"
    return environment


def runtime_command(binary, preset, host, port, fit_target, gpu_layers):
    # The installed native build crashed in llama.dll after an idle sleep/wake
    # while input_tokens was reusing pre-wake model pointers. Disable that path;
    # the manager still unloads the owned model on actual workload/model switches.
    return [str(binary), "--host", host, "--port", str(port),
            "--models-preset", str(preset), "--models-max", "2", "--no-models-autoload",
            "--jinja", "--no-webui", "--sleep-idle-seconds", "-1"]


def main():
    parser = argparse.ArgumentParser(description="Native GO llama.cpp supervisor for all model roles")
    parser.add_argument("--model-root", required=True)
    parser.add_argument("--binary")
    parser.add_argument("--state-directory")
    parser.add_argument("--list-models", action="store_true", help="Print the read-only catalog and exit without starting a process")
    parser.add_argument("--port", type=int, default=8081)
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--fit-target", default="2048")
    parser.add_argument("--gpu-layers", default="auto")
    args = parser.parse_args()
    root = Path(args.model_root).resolve(strict=True)
    if args.list_models:
        print(json.dumps(discover_models(root), default=str))
        return 0
    if not args.binary or not args.state_directory:
        parser.error("--binary and --state-directory are required when starting the native runtime")
    binary = Path(args.binary).resolve(strict=True)
    state_directory = Path(args.state_directory).resolve()
    state_directory.mkdir(parents=True, exist_ok=True)
    preset = state_directory / "models.ini"
    stop_file = state_directory / "stop.requested"
    stop_file.unlink(missing_ok=True)
    gpu_manager = GpuLoadManager(root, preset, state_directory, args.port, args.fit_target, args.gpu_layers, binary=binary)
    gpu_manager.refresh()
    control = start_gpu_control(gpu_manager, args.host, args.port + 1)
    stopped = threading.Event()

    def refresh():
        while not stopped.wait(10):
            try:
                gpu_manager.refresh()
            except OSError as error:
                print(f"Native catalog refresh failed: {error}", file=sys.stderr, flush=True)

    threading.Thread(target=refresh, daemon=True).start()
    command = runtime_command(binary, preset, args.host, args.port, args.fit_target, args.gpu_layers)
    environment = runtime_environment(os.environ, state_directory)
    # Windows venv launchers can lose inherited redirected handles. Give the
    # actual native process explicit persistent log handles instead.
    runtime_stdout = (state_directory / "llama.stdout.log").open("a", encoding="utf-8")
    runtime_stderr = (state_directory / "llama.stderr.log").open("a", encoding="utf-8")
    runtime_stderr.write("Native GO model command: " + json.dumps(command) + "\n")
    runtime_stderr.flush()
    process = subprocess.Popen(command, cwd=binary.parent, env=environment,
                               creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                               stdout=runtime_stdout, stderr=runtime_stderr)
    job = create_windows_job(process)
    (state_directory / "runtime.json").write_text(json.dumps({"supervisorPid": os.getpid(), "serverPid": process.pid,
        "binary": str(binary), "modelRoot": str(root), "port": args.port, "contextPolicy": "max-fit",
        "fitTargetMiB": args.fit_target, "gpuLayers": args.gpu_layers,
        "reasoningPolicy": "model-highest", "reasoningBudget": -1, "sleepIdleSeconds": -1}), encoding="utf-8")

    def stop(*_):
        stopped.set()
        if process.poll() is None:
            with gpu_manager.lock:
                gpu_manager.sessions.save_all()
            process.terminate()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    try:
        while process.poll() is None:
            if stop_file.exists():
                stop()
            stopped.wait(0.25)
        return process.wait()
    finally:
        stopped.set()
        control.shutdown()
        control.server_close()
        if job:
            job[0].CloseHandle(job[1])
        runtime_stdout.close()
        runtime_stderr.close()


if __name__ == "__main__":
    raise SystemExit(main())
