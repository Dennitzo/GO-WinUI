from __future__ import annotations

import json
import math
import subprocess
import threading
import uuid
import warnings
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from PIL import Image
from pydantic import BaseModel, ConfigDict, Field, model_validator

from common.worker_security import read_worker_key, require_worker_key, resolve_data_path


DATA_ROOT = Path("/data")
UPLOAD_ROOT = DATA_ROOT / "Uploads"
OUTPUT_ROOT = DATA_ROOT / "Artifacts" / "worker"
WORKER_KEY = read_worker_key()
Image.MAX_IMAGE_PIXELS = 50_000_000
warnings.simplefilter("error", Image.DecompressionBombWarning)
PROCESS_GATE = threading.Semaphore(1)
MAX_VIDEO_SECONDS = 60 * 60
MAX_OVERVIEW_FRAMES = 48
MAX_DETAIL_WINDOWS = 3
MAX_DETAIL_FRAMES = 32


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class TimeWindow(StrictModel):
    start: float = Field(ge=0)
    end: float = Field(gt=0)

    @model_validator(mode="after")
    def validate_order(self):
        if self.end <= self.start:
            raise ValueError("end must be greater than start")
        return self


class InspectRequest(StrictModel):
    uploadId: str = Field(pattern=r"^upload-[a-f0-9]{32}$")
    mediaType: str = Field(min_length=3, max_length=128)
    detailWindows: list[TimeWindow] = Field(default_factory=list, max_length=MAX_DETAIL_WINDOWS)


app = FastAPI(title="GO AI Media Worker", docs_url=None, redoc_url=None, openapi_url=None)


@app.get("/health")
def health() -> dict:
    return {"status": "live", "worker": "media"}


@app.middleware("http")
async def authenticate(request: Request, call_next):
    if request.url.path != "/health" and not require_worker_key(request, WORKER_KEY):
        return JSONResponse(status_code=401, content={"errorCode": "worker.authentication_failed"})
    return await call_next(request)


@app.get("/status")
def status() -> dict:
    return {
        "status": "ready",
        "ffmpeg": _tool_version("ffmpeg"),
        "ffprobe": _tool_version("ffprobe"),
        "networkAccess": False,
    }


@app.post("/load")
def load() -> dict:
    return status()


@app.post("/release")
def release() -> dict:
    return status()


@app.post("/inspect")
def inspect(request: InspectRequest) -> dict:
    if not PROCESS_GATE.acquire(blocking=False):
        raise HTTPException(409, detail={"errorCode": "media.worker_busy"})
    try:
        source = resolve_data_path(
            str(UPLOAD_ROOT / request.uploadId / "payload.bin"),
            str(UPLOAD_ROOT),
        )
        prefix = request.mediaType.split("/", 1)[0].lower()
        if prefix == "image":
            return _inspect_image(source)
        if prefix in {"video", "audio"}:
            return _inspect_av(source, include_frames=prefix == "video", windows=request.detailWindows)
        raise HTTPException(400, detail={"errorCode": "media.unsupported_type"})
    finally:
        PROCESS_GATE.release()


def _inspect_image(source: Path) -> dict:
    job_dir = _new_job_directory()
    thumbnail = job_dir / "thumbnail.jpg"
    try:
        with Image.open(source) as image:
            image.verify()
        with Image.open(source) as image:
            width, height = image.size
            image_format = image.format or "unknown"
            image.thumbnail((512, 512), Image.Resampling.LANCZOS)
            if image.mode not in {"RGB", "L"}:
                image = image.convert("RGB")
            image.save(thumbnail, "JPEG", quality=88, optimize=True)
    except (OSError, ValueError, Image.DecompressionBombError, Image.DecompressionBombWarning) as exception:
        raise HTTPException(400, detail={"errorCode": "media.invalid_image"}) from exception
    return {
        "kind": "image",
        "metadata": {"width": width, "height": height, "format": image_format},
        "artifacts": [_artifact(thumbnail, "image/jpeg", "thumbnail")],
        "frames": [],
    }


def _inspect_av(source: Path, include_frames: bool, windows: list[TimeWindow]) -> dict:
    probe = _run_json(
        [
            "ffprobe", "-v", "error", "-show_format", "-show_streams",
            "-of", "json", str(source),
        ],
        timeout=60,
    )
    duration = _read_duration(probe)
    if include_frames and duration > MAX_VIDEO_SECONDS:
        raise HTTPException(400, detail={"errorCode": "media.video_too_long"})

    job_dir = _new_job_directory()
    artifacts: list[dict] = []
    frames: list[dict] = []
    has_audio = any(stream.get("codec_type") == "audio" for stream in probe.get("streams", []))
    if has_audio:
        audio_path = job_dir / "audio.wav"
        _run(
            [
                "ffmpeg", "-nostdin", "-v", "error", "-y", "-i", str(source),
                "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", str(audio_path),
            ],
            timeout=900,
        )
        artifacts.append(_artifact(audio_path, "audio/wav", "audio"))

    if include_frames:
        count = max(1, min(MAX_OVERVIEW_FRAMES, int(math.ceil(duration / 30.0)) if duration else 1))
        frames.extend(_extract_frames(source, job_dir / "overview", 0.0, duration, count, "overview"))
        for index, window in enumerate(windows):
            clipped_end = min(window.end, duration) if duration else window.end
            if clipped_end <= window.start:
                continue
            frames.extend(
                _extract_frames(
                    source,
                    job_dir / f"detail-{index + 1}",
                    window.start,
                    clipped_end,
                    MAX_DETAIL_FRAMES,
                    f"detail-{index + 1}",
                )
            )

    format_info = probe.get("format", {})
    return {
        "kind": "video" if include_frames else "audio",
        "metadata": {
            "durationSeconds": round(duration, 3),
            "formatName": format_info.get("format_name", "unknown"),
            "bitRate": _safe_int(format_info.get("bit_rate")),
            "size": _safe_int(format_info.get("size")),
            "streams": [_sanitize_stream(stream) for stream in probe.get("streams", [])],
        },
        "artifacts": artifacts,
        "frames": frames,
    }


def _extract_frames(
    source: Path,
    directory: Path,
    start: float,
    end: float,
    maximum: int,
    group: str,
) -> list[dict]:
    directory.mkdir(parents=True, exist_ok=True)
    span = max(0.001, end - start)
    count = max(1, min(maximum, int(math.ceil(span / 2.0))))
    fps = count / span
    output_pattern = directory / "frame-%03d.jpg"
    _run(
        [
            "ffmpeg", "-nostdin", "-v", "error", "-y", "-ss", f"{start:.3f}",
            "-i", str(source), "-t", f"{span:.3f}", "-vf",
            f"fps={fps:.8f},scale='min(1280,iw)':-2", "-frames:v", str(count),
            "-q:v", "3", str(output_pattern),
        ],
        timeout=900,
    )
    result: list[dict] = []
    paths = sorted(directory.glob("frame-*.jpg"))[:maximum]
    for index, path in enumerate(paths):
        timestamp = min(end, start + ((index + 0.5) / max(1, len(paths))) * span)
        item = _artifact(path, "image/jpeg", "frame")
        item["timecodeSeconds"] = round(timestamp, 3)
        item["group"] = group
        result.append(item)
    return result


def _artifact(path: Path, media_type: str, role: str) -> dict:
    return {
        "relativePath": str(path.relative_to(DATA_ROOT)).replace("\\", "/"),
        "fileName": path.name,
        "mediaType": media_type,
        "role": role,
    }


def _new_job_directory() -> Path:
    destination = OUTPUT_ROOT / f"media-{uuid.uuid4().hex}"
    destination.mkdir(parents=True, exist_ok=False)
    return destination


def _run_json(arguments: list[str], timeout: int) -> dict:
    completed = _run(arguments, timeout=timeout, capture=True)
    try:
        return json.loads(completed.stdout)
    except json.JSONDecodeError as exception:
        raise HTTPException(422, detail={"errorCode": "media.invalid_metadata"}) from exception


def _run(arguments: list[str], timeout: int, capture: bool = False):
    try:
        return subprocess.run(
            arguments,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE if capture else subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            text=True,
            check=True,
            timeout=timeout,
            env={"PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"},
        )
    except subprocess.TimeoutExpired as exception:
        raise HTTPException(504, detail={"errorCode": "media.processing_timeout"}) from exception
    except subprocess.CalledProcessError as exception:
        raise HTTPException(422, detail={"errorCode": "media.processing_failed"}) from exception


def _read_duration(probe: dict) -> float:
    try:
        return max(0.0, float(probe.get("format", {}).get("duration", 0)))
    except (TypeError, ValueError):
        return 0.0


def _safe_int(value) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _sanitize_stream(stream: dict) -> dict:
    return {
        "index": stream.get("index"),
        "codecType": stream.get("codec_type"),
        "codecName": stream.get("codec_name"),
        "width": stream.get("width"),
        "height": stream.get("height"),
        "sampleRate": _safe_int(stream.get("sample_rate")),
        "channels": stream.get("channels"),
    }


def _tool_version(name: str) -> str:
    try:
        completed = subprocess.run(
            [name, "-version"],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            check=True,
            timeout=5,
        )
        return completed.stdout.splitlines()[0][:160]
    except (OSError, subprocess.SubprocessError):
        return "unavailable"
