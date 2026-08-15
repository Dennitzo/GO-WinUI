from __future__ import annotations

import base64
import binascii
import json
import os
import secrets
import subprocess
import threading
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from PIL import Image
from pydantic import BaseModel, ConfigDict, Field, model_validator

from common.worker_security import read_worker_key, require_worker_key


DATA_ROOT = Path("/data")
OUTPUT_ROOT = DATA_ROOT / "Artifacts" / "worker"
SD_SERVER = Path("/opt/stable-diffusion/sd-server")
MODEL = Path(os.environ.get("GO_AI_Z_IMAGE_MODEL", "/models/z-image/z_image_turbo-Q4_K.gguf"))
VAE = Path(os.environ.get("GO_AI_Z_IMAGE_VAE", "/models/z-image/ae.safetensors"))
ENCODER = Path(os.environ.get("GO_AI_Z_IMAGE_ENCODER", "/models/z-image/Qwen3-4B-Instruct-2507-Q4_K_M.gguf"))
MODEL_TTL_SECONDS = max(60, int(os.environ.get("GO_AI_MODEL_TTL_SECONDS", "600")))
SERVER_LOAD_TIMEOUT_SECONDS = max(60, int(os.environ.get("GO_AI_IMAGE_LOAD_TIMEOUT_SECONDS", "1200")))
SD_SERVER_PORT = int(os.environ.get("GO_AI_SD_SERVER_PORT", "8090"))
SD_SERVER_URL = f"http://127.0.0.1:{SD_SERVER_PORT}"
WORKER_KEY = read_worker_key()
PROCESS_GATE = threading.Lock()


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class GenerateRequest(StrictModel):
    prompt: str = Field(min_length=1, max_length=10000)
    width: int = Field(default=1024, ge=256, le=1536)
    height: int = Field(default=1024, ge=256, le=1536)
    seed: int | None = Field(default=None, ge=0, le=2147483647)
    count: int = Field(default=1, ge=1, le=4)

    @model_validator(mode="after")
    def validate_dimensions(self):
        if self.width % 64 or self.height % 64:
            raise ValueError("width and height must be multiples of 64")
        return self


class SdServerRegistry:
    def __init__(self) -> None:
        self._gate = threading.RLock()
        self._process: subprocess.Popen | None = None
        self._last_used: float | None = None

    def status(self) -> dict:
        with self._gate:
            loaded = self._is_loaded_unlocked()
            return {
                "status": "ready" if _all_files_available() else "model-missing",
                "runtimeAvailable": SD_SERVER.is_file(),
                "modelAvailable": MODEL.is_file(),
                "vaeAvailable": VAE.is_file(),
                "encoderAvailable": ENCODER.is_file(),
                "modelLoaded": loaded,
                "busy": PROCESS_GATE.locked(),
                "model": "Z-Image-Turbo Q4_K",
                "lastUsedUnix": self._last_used,
                "idleTtlSeconds": MODEL_TTL_SECONDS,
            }

    def load(self) -> None:
        if not _all_files_available():
            raise HTTPException(503, detail={"errorCode": "image.model_missing"})
        with self._gate:
            if self._is_loaded_unlocked():
                self._last_used = time.time()
                return

            environment = os.environ.copy()
            environment["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID"
            self._process = subprocess.Popen(
                [
                    str(SD_SERVER),
                    "--listen-ip", "127.0.0.1",
                    "--listen-port", str(SD_SERVER_PORT),
                    "--diffusion-model", str(MODEL),
                    "--vae", str(VAE),
                    "--llm", str(ENCODER),
                    "--cfg-scale", "1.0",
                    "--diffusion-fa",
                ],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                env=environment,
            )
            process = self._process

        deadline = time.monotonic() + SERVER_LOAD_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            if process.poll() is not None:
                self.release()
                raise HTTPException(502, detail={"errorCode": "image.runtime_start_failed"})
            try:
                _request_json("/v1/models", timeout=2)
                with self._gate:
                    self._last_used = time.time()
                return
            except (urllib.error.URLError, TimeoutError, json.JSONDecodeError):
                time.sleep(1)

        self.release()
        raise HTTPException(504, detail={"errorCode": "image.runtime_start_timeout"})

    def touch(self) -> None:
        with self._gate:
            if self._is_loaded_unlocked():
                self._last_used = time.time()

    def release(self) -> None:
        with self._gate:
            process = self._process
            self._process = None
            self._last_used = None
        if process is None or process.poll() is not None:
            return
        process.terminate()
        try:
            process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)

    def release_if_idle(self) -> bool:
        with self._gate:
            if (
                not self._is_loaded_unlocked()
                or self._last_used is None
                or time.time() - self._last_used < MODEL_TTL_SECONDS
                or PROCESS_GATE.locked()
            ):
                return False
        self.release()
        return True

    def _is_loaded_unlocked(self) -> bool:
        if self._process is not None and self._process.poll() is None:
            return True
        self._process = None
        self._last_used = None
        return False


runtime = SdServerRegistry()
app = FastAPI(title="GO AI Image Worker", docs_url=None, redoc_url=None, openapi_url=None)


@app.on_event("startup")
def start_background_services() -> None:
    threading.Thread(target=_reap_idle_model, name="go-ai-image-idle-reaper", daemon=True).start()
    if os.environ.get("GO_AI_PRELOAD_IMAGE", "0").strip().lower() in ("1", "true", "yes", "on"):
        threading.Thread(target=_try_preload, name="go-ai-image-preload", daemon=True).start()


@app.on_event("shutdown")
def stop_runtime() -> None:
    runtime.release()


@app.get("/health")
def health() -> dict:
    return {"status": "live", "worker": "image"}


@app.middleware("http")
async def authenticate(request: Request, call_next):
    if request.url.path != "/health" and not require_worker_key(request, WORKER_KEY):
        return JSONResponse(status_code=401, content={"errorCode": "worker.authentication_failed"})
    return await call_next(request)


@app.get("/status")
def status() -> dict:
    return runtime.status()


@app.post("/load")
def load() -> dict:
    runtime.load()
    return runtime.status()


@app.post("/release")
def release() -> dict:
    runtime.release()
    return runtime.status()


@app.post("/generate")
def generate(request: GenerateRequest) -> dict:
    if not PROCESS_GATE.acquire(blocking=False):
        raise HTTPException(409, detail={"errorCode": "image.worker_busy"})
    started = time.monotonic()
    created_paths: list[Path] = []
    try:
        runtime.load()
        OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
        results = []
        base_seed = request.seed if request.seed is not None else secrets.randbelow(2147483647)
        for index in range(request.count):
            seed = base_seed + index
            response = _request_json(
                "/sdapi/v1/txt2img",
                {
                    "prompt": request.prompt,
                    "negative_prompt": "",
                    "width": request.width,
                    "height": request.height,
                    "steps": 8,
                    "cfg_scale": 1.0,
                    "seed": seed,
                    "batch_size": 1,
                    "sampler_name": "euler",
                    "scheduler": "simple",
                },
                timeout=30 * 60,
            )
            encoded_images = response.get("images")
            if not isinstance(encoded_images, list) or not encoded_images:
                raise HTTPException(502, detail={"errorCode": "image.empty_output"})
            try:
                image_bytes = base64.b64decode(str(encoded_images[0]), validate=True)
            except (ValueError, binascii.Error) as exception:
                raise HTTPException(502, detail={"errorCode": "image.invalid_output"}) from exception

            file_name = f"image-{uuid.uuid4().hex}.png"
            destination = OUTPUT_ROOT / file_name
            destination.write_bytes(image_bytes)
            created_paths.append(destination)
            try:
                with Image.open(destination) as image:
                    image.verify()
                    actual_width, actual_height = image.size
            except (OSError, ValueError) as exception:
                raise HTTPException(502, detail={"errorCode": "image.invalid_output"}) from exception
            results.append(
                {
                    "relativePath": str(destination.relative_to(DATA_ROOT)).replace("\\", "/"),
                    "fileName": file_name,
                    "mediaType": "image/png",
                    "metadata": {
                        "model": "Z-Image-Turbo Q4_K",
                        "prompt": request.prompt,
                        "seed": str(seed),
                        "width": str(actual_width),
                        "height": str(actual_height),
                        "steps": "8",
                        "cfgScale": "1.0",
                    },
                }
            )
        runtime.touch()
        return {
            "provider": "stable-diffusion.cpp server",
            "model": "Z-Image-Turbo Q4_K",
            "durationMilliseconds": round((time.monotonic() - started) * 1000),
            "artifacts": results,
        }
    except HTTPException:
        for path in created_paths:
            path.unlink(missing_ok=True)
        raise
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exception:
        for path in created_paths:
            path.unlink(missing_ok=True)
        raise HTTPException(502, detail={"errorCode": "image.generation_failed"}) from exception
    finally:
        PROCESS_GATE.release()


def _request_json(path: str, body: dict | None = None, timeout: int = 30) -> dict:
    data = None if body is None else json.dumps(body, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        f"{SD_SERVER_URL}{path}",
        data=data,
        method="GET" if body is None else "POST",
        headers={"Content-Type": "application/json"} if body is not None else {},
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        payload = json.loads(response.read().decode("utf-8"))
    if not isinstance(payload, dict):
        raise json.JSONDecodeError("Expected an object", "", 0)
    return payload


def _try_preload() -> None:
    try:
        runtime.load()
    except Exception:
        pass


def _reap_idle_model() -> None:
    interval = min(30, max(5, MODEL_TTL_SECONDS // 4))
    while True:
        time.sleep(interval)
        runtime.release_if_idle()


def _all_files_available() -> bool:
    return all(path.is_file() for path in (SD_SERVER, MODEL, VAE, ENCODER))
