from __future__ import annotations

import os
import secrets
import subprocess
import threading
import time
import uuid
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from PIL import Image
from pydantic import BaseModel, ConfigDict, Field, model_validator

from common.worker_security import read_worker_key, require_worker_key


DATA_ROOT = Path("/data")
OUTPUT_ROOT = DATA_ROOT / "Artifacts" / "worker"
SD_CLI = Path("/opt/stable-diffusion/sd-cli")
MODEL = Path(os.environ.get("GO_AI_Z_IMAGE_MODEL", "/models/z-image/z_image_turbo-Q4_K.gguf"))
VAE = Path(os.environ.get("GO_AI_Z_IMAGE_VAE", "/models/z-image/ae.safetensors"))
ENCODER = Path(os.environ.get("GO_AI_Z_IMAGE_ENCODER", "/models/z-image/Qwen3-4B-Instruct-2507-Q4_K_M.gguf"))
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


app = FastAPI(title="GO AI Image Worker", docs_url=None, redoc_url=None, openapi_url=None)


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
    return {
        "status": "ready" if _all_files_available() else "model-missing",
        "runtimeAvailable": SD_CLI.is_file(),
        "modelAvailable": MODEL.is_file(),
        "vaeAvailable": VAE.is_file(),
        "encoderAvailable": ENCODER.is_file(),
        "busy": PROCESS_GATE.locked(),
        "model": "Z-Image-Turbo Q4_K",
    }


@app.post("/load")
def load() -> dict:
    if not _all_files_available():
        raise HTTPException(503, detail={"errorCode": "image.model_missing"})
    return status()


@app.post("/release")
def release() -> dict:
    return status()


@app.post("/generate")
def generate(request: GenerateRequest) -> dict:
    if not _all_files_available():
        raise HTTPException(503, detail={"errorCode": "image.model_missing"})
    if not PROCESS_GATE.acquire(blocking=False):
        raise HTTPException(409, detail={"errorCode": "image.worker_busy"})
    started = time.monotonic()
    created_paths: list[Path] = []
    try:
        OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
        results = []
        base_seed = request.seed if request.seed is not None else secrets.randbelow(2147483647)
        for index in range(request.count):
            seed = base_seed + index
            file_name = f"image-{uuid.uuid4().hex}.png"
            destination = OUTPUT_ROOT / file_name
            created_paths.append(destination)
            _run_generation(request, seed, destination)
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
        return {
            "provider": "stable-diffusion.cpp",
            "model": "Z-Image-Turbo Q4_K",
            "durationMilliseconds": round((time.monotonic() - started) * 1000),
            "artifacts": results,
        }
    except Exception:
        for path in created_paths:
            path.unlink(missing_ok=True)
        raise
    finally:
        PROCESS_GATE.release()


def _run_generation(request: GenerateRequest, seed: int, destination: Path) -> None:
    arguments = [
        str(SD_CLI),
        "--diffusion-model", str(MODEL),
        "--vae", str(VAE),
        "--llm", str(ENCODER),
        "-p", request.prompt,
        "--cfg-scale", "1.0",
        "--steps", "8",
        "--seed", str(seed),
        "--rng", "cpu",
        "--diffusion-fa",
        "-W", str(request.width),
        "-H", str(request.height),
        "-o", str(destination),
    ]
    try:
        subprocess.run(
            arguments,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            check=True,
            timeout=30 * 60,
            env={
                "PATH": "/usr/local/cuda/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                "LD_LIBRARY_PATH": "/usr/local/cuda/lib64",
                "CUDA_DEVICE_ORDER": "PCI_BUS_ID",
                "CUDA_VISIBLE_DEVICES": os.environ.get("CUDA_VISIBLE_DEVICES", "0"),
            },
        )
    except subprocess.TimeoutExpired as exception:
        raise HTTPException(504, detail={"errorCode": "image.generation_timeout"}) from exception
    except subprocess.CalledProcessError as exception:
        raise HTTPException(502, detail={"errorCode": "image.generation_failed"}) from exception


def _all_files_available() -> bool:
    return all(path.is_file() for path in (SD_CLI, MODEL, VAE, ENCODER))
