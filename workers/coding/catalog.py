"""Discover complete local GGUF models and supervise the native llama.cpp router.

Model files are only read. Generated presets and logs use a separate state directory.
No network access, downloads, model conversion, or Unsloth code is used here.
"""
from __future__ import annotations

import argparse
import ctypes
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
    if stat.st_size < 1024 * 1024:
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
            elif (key.endswith(".context_length") or key.endswith(".pooling_type")) and kind == 4:
                metadata[key] = struct.unpack("<I", read_exact(stream, 4))[0]
            else:
                skip_value(stream, kind)
        if stream.tell() >= path.stat().st_size:
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
    if architecture == "gpt-oss" and "reasoning_effort" in template:
        # GPT-OSS was trained with low/medium/high (not xhigh or max).
        return {"enabled": True, "effort": "high", "budget": -1}
    if "enable_thinking" in template:
        # Current Unsloth Qwen3.8 templates explicitly enumerate supported levels.
        supported = re.search(r"resolved_reasoning_effort\s+not\s+in\s*\(([^)]{1,256})\)", template)
        levels = re.findall(r"['\"]([a-z]+)['\"]", supported.group(1)) if supported else []
        highest = next((level for level in ("max", "xhigh", "high", "medium", "low", "minimal") if level in levels), None)
        return {"enabled": True, "effort": highest, "budget": -1}
    return {"enabled": None, "effort": None, "budget": -1}


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
            if projector:
                found.append({"id": f"vision/{safe_name}~{digest}", "path": path, "role": "vision",
                              "context": context, "contextPolicy": "max-fit", "reasoning": reasoning, "projector": projector})
        except (OSError, ValueError, struct.error):
            continue
    return found


def discover(root):
    """Compatibility view for callers needing only Coding text models."""
    return [(model["id"], model["path"]) for model in discover_models(root) if model["role"] == "general"]


def write_presets(root, target):
    models = discover_models(root)
    lines = ["version = 1", "", "[*]", "load-on-startup = false", "stop-timeout = 10", "sleep-idle-seconds = -1", ""]
    for model in models:
        model_id, path = model["id"], model["path"]
        if any(char in str(path) for char in "\r\n"):
            continue
        lines += [f"[{model_id}]", f"model = {path.as_posix()}",
                  f"tags = go-context-train:{model['context']},go-context-policy:{model['contextPolicy']}"]
        if model["role"] == "embedding":
            lines += [f"ctx-size = {model['context']}", "embedding = true", f"pooling = {model['pooling']}", f"batch-size = {model['context']}",
                      f"ubatch-size = {model['context']}", "cache-type-k = f16", "cache-type-v = f16", "flash-attn = off"]
        else:
            # Omit ctx-size entirely. llama starts at the GGUF training maximum,
            # then --fit can reduce it only when required by device memory.
            # Explicit ctx-size=0 disables that reduction in llama/common/arg.cpp.
            lines += ["cache-type-k = q8_0", "cache-type-v = q8_0", "flash-attn = on", "predict = -1",
                      "reasoning-budget = -1", "reasoning = on" if model["reasoning"]["enabled"] else "reasoning = auto"]
            if model["reasoning"]["effort"]:
                lines += [f"reasoning-effort = {model['reasoning']['effort']}"]
            if model["role"] == "vision":
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


def runtime_environment(source, state_directory):
    environment = dict(source, HF_HUB_OFFLINE="1", LLAMA_CACHE=str(state_directory / "cache"))
    # A parent shell's fixed context would bypass the model-maximum fitting policy.
    # No unrelated user environment or process is changed.
    for key in ("LLAMA_ARG_CTX_SIZE", "LLAMA_ARG_KV_UNIFIED_PER_SLOT", "LLAMA_ARG_FIT_CTX",
                "LLAMA_ARG_N_PREDICT", "LLAMA_ARG_THINK_BUDGET", "LLAMA_ARG_REASONING_EFFORT",
                "LLAMA_ARG_REASONING", "LLAMA_ARG_CHAT_TEMPLATE_KWARGS"):
        environment.pop(key, None)
    return environment


def runtime_command(binary, preset, host, port, fit_target, gpu_layers):
    # The installed native build crashed in llama.dll after an idle sleep/wake
    # while input_tokens was reusing pre-wake model pointers. Disable that path;
    # the manager still unloads the owned model on actual workload/model switches.
    return [str(binary), "--host", host, "--port", str(port),
            "--models-preset", str(preset), "--models-max", "1", "--no-models-autoload",
            "--parallel", "1", "--jinja", "--no-webui",
            "--fit", "on", "--fit-target", str(fit_target), "--fit-ctx", "4096",
            "--n-gpu-layers", gpu_layers, "--sleep-idle-seconds", "-1"]


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
    write_presets(root, preset)
    stopped = threading.Event()

    def refresh():
        while not stopped.wait(10):
            try:
                write_presets(root, preset)
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
        if job:
            job[0].CloseHandle(job[1])
        runtime_stdout.close()
        runtime_stderr.close()


if __name__ == "__main__":
    raise SystemExit(main())
