from __future__ import annotations

import gc
import os
import re
import threading
import time
import uuid
from pathlib import Path
from typing import Literal

import soundfile as sf
import torch
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from starlette.concurrency import run_in_threadpool

from common.worker_security import read_worker_key, require_worker_key, resolve_data_path


DATA_ROOT = Path("/data")
MODEL_ROOT = Path("/models")
OUTPUT_ROOT = DATA_ROOT / "Artifacts" / "worker"
WHISPER_MODEL = Path(os.environ.get("GO_AI_WHISPER_MODEL", MODEL_ROOT / "faster-whisper-large-v3-turbo"))
TTS_MODEL = Path(os.environ.get("GO_AI_TTS_MODEL", MODEL_ROOT / "Qwen3-TTS-12Hz-0.6B-Base"))
TTS_REFERENCE = Path(os.environ.get("GO_AI_TTS_REFERENCE", DATA_ROOT / "Voices" / "hedda-reference.wav"))
TTS_REFERENCE_TEXT = os.environ.get(
    "GO_AI_TTS_REFERENCE_TEXT",
    "Willkommen beim GO AI Server. Ich unterstütze die technische Gebäudeausrüstung klar und zuverlässig.",
)
TTS_DTYPE_NAME = os.environ.get("GO_AI_TTS_DTYPE", "bfloat16").lower()
TTS_DTYPE = {
    "bfloat16": torch.bfloat16,
    "float16": torch.float16,
    "float32": torch.float32,
}.get(TTS_DTYPE_NAME)
if TTS_DTYPE is None:
    raise RuntimeError("GO_AI_TTS_DTYPE must be bfloat16, float16, or float32")
WORKER_KEY = read_worker_key()
MAX_PARALLEL_SPEECH_OPERATIONS = 2
PROCESS_GATE = threading.BoundedSemaphore(MAX_PARALLEL_SPEECH_OPERATIONS)
MAXIMUM_LIVE_CAPTION_BYTES = 512 * 1024


def _acquire_process_slots(count: int = 1) -> int:
    acquired = 0
    try:
        while acquired < count:
            if not PROCESS_GATE.acquire(timeout=120):
                raise HTTPException(503, detail={"errorCode": "speech.worker_queue_timeout"})
            acquired += 1
        return acquired
    except BaseException:
        for _ in range(acquired):
            PROCESS_GATE.release()
        raise


def _release_process_slots(count: int) -> None:
    for _ in range(count):
        PROCESS_GATE.release()
LANGUAGE_PATTERN = re.compile(r"^[A-Za-z0-9_-]{1,16}$")


def _whisper_available() -> bool:
    return all(
        (WHISPER_MODEL / name).is_file()
        for name in ("model.bin", "config.json", "tokenizer.json", "vocabulary.json")
    )


def _tts_available() -> bool:
    return all(
        (TTS_MODEL / name).is_file()
        for name in ("model.safetensors", "config.json", "tokenizer_config.json", "vocab.json")
    )


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class LoadRequest(StrictModel):
    component: Literal["stt", "tts"]


class TranscriptionRequest(StrictModel):
    uploadId: str = Field(pattern=r"^upload-[a-f0-9]{32}$")
    language: str | None = None


class SpeechRequest(StrictModel):
    text: str = Field(min_length=1, max_length=10000)
    voice: str = Field(default="de-DE-Hedda", max_length=64)
    format: Literal["wav"] = "wav"
    speed: float = Field(default=1.0, ge=0.5, le=2.0)


class ModelRegistry:
    def __init__(self) -> None:
        self._gate = threading.RLock()
        self._stt = None
        self._tts = None
        self._voice_prompt = None
        self._last_used: float | None = None

    def status(self) -> dict:
        with self._gate:
            whisper_available = _whisper_available()
            tts_available = _tts_available()
            files_available = whisper_available and tts_available and TTS_REFERENCE.is_file()
            return {
                "status": "ready" if files_available else "model-missing",
                "sttLoaded": self._stt is not None,
                "ttsLoaded": self._tts is not None,
                "whisperModelAvailable": whisper_available,
                "ttsModelAvailable": tts_available,
                "referenceVoiceAvailable": TTS_REFERENCE.is_file(),
                "ttsDtype": TTS_DTYPE_NAME,
                "lastUsedUnix": self._last_used,
            }

    def load_stt(self):
        with self._gate:
            if self._stt is None:
                if not _whisper_available():
                    raise HTTPException(503, detail={"errorCode": "speech.stt_model_missing"})
                from faster_whisper import WhisperModel

                self._stt = WhisperModel(
                    str(WHISPER_MODEL),
                    device="cuda",
                    compute_type="float16",
                    local_files_only=True,
                    num_workers=MAX_PARALLEL_SPEECH_OPERATIONS,
                )
            self._last_used = time.time()
            return self._stt

    def load_tts(self):
        with self._gate:
            if self._tts is None:
                if not _tts_available():
                    raise HTTPException(503, detail={"errorCode": "speech.tts_model_missing"})
                if not TTS_REFERENCE.is_file():
                    raise HTTPException(503, detail={"errorCode": "speech.reference_voice_missing"})
                from qwen_tts import Qwen3TTSModel

                self._tts = Qwen3TTSModel.from_pretrained(
                    str(TTS_MODEL),
                    device_map="cuda:0",
                    dtype=TTS_DTYPE,
                    attn_implementation="sdpa",
                    local_files_only=True,
                )
                self._voice_prompt = self._tts.create_voice_clone_prompt(
                    ref_audio=str(TTS_REFERENCE),
                    ref_text=TTS_REFERENCE_TEXT,
                    x_vector_only_mode=False,
                )
            self._last_used = time.time()
            return self._tts

    def release(self) -> None:
        with self._gate:
            self._stt = None
            self._tts = None
            self._voice_prompt = None
            self._last_used = None
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    @property
    def voice_prompt(self):
        return self._voice_prompt


models = ModelRegistry()
app = FastAPI(title="GO AI Speech Worker", docs_url=None, redoc_url=None, openapi_url=None)


@app.get("/health")
def health() -> dict:
    return {"status": "live", "worker": "speech"}


@app.middleware("http")
async def authenticate(request: Request, call_next):
    if request.url.path != "/health" and not require_worker_key(request, WORKER_KEY):
        return JSONResponse(status_code=401, content={"errorCode": "worker.authentication_failed"})
    return await call_next(request)


@app.get("/status")
def status() -> dict:
    return models.status()


@app.post("/load")
def load(request: LoadRequest) -> dict:
    slots = _acquire_process_slots()
    try:
        models.load_stt() if request.component == "stt" else models.load_tts()
        return models.status()
    finally:
        _release_process_slots(slots)


@app.post("/release")
def release() -> dict:
    slots = _acquire_process_slots(MAX_PARALLEL_SPEECH_OPERATIONS)
    try:
        models.release()
        return models.status()
    finally:
        _release_process_slots(slots)


@app.post("/transcriptions")
def transcribe(request: TranscriptionRequest) -> dict:
    slots = _acquire_process_slots()
    try:
        source = resolve_data_path(
            str(DATA_ROOT / "Uploads" / request.uploadId / "payload.bin"),
            str(DATA_ROOT / "Uploads"),
        )
        model = models.load_stt()
        segments, info = model.transcribe(
            str(source),
            language=request.language,
            beam_size=5,
            word_timestamps=False,
            vad_filter=True,
            condition_on_previous_text=True,
        )
        rows = [
            {"start": round(segment.start, 3), "end": round(segment.end, 3), "text": segment.text.strip()}
            for segment in segments
        ]
        return {
            "text": " ".join(row["text"] for row in rows).strip(),
            "language": info.language,
            "languageProbability": round(info.language_probability, 6),
            "segments": rows,
            "provider": "whisper-large-v3-turbo",
        }
    finally:
        _release_process_slots(slots)


def _transcribe_live_caption(audio: bytes, language: str | None, task: str) -> dict:
    slots = _acquire_process_slots()
    temporary = Path("/tmp") / f"caption-{uuid.uuid4().hex}.wav"
    try:
        temporary.write_bytes(audio)
        model = models.load_stt()
        segments, info = model.transcribe(
            str(temporary),
            language=language,
            task=task,
            beam_size=1,
            best_of=1,
            word_timestamps=False,
            vad_filter=True,
            condition_on_previous_text=False,
            temperature=0.0,
        )
        rows = [
            {"start": round(segment.start, 3), "end": round(segment.end, 3), "text": segment.text.strip()}
            for segment in segments
            if segment.text.strip()
        ]
        return {
            "text": " ".join(row["text"] for row in rows).strip(),
            "language": info.language,
            "languageProbability": round(info.language_probability, 6),
            "segments": rows,
            "provider": "whisper-large-v3-turbo-live",
        }
    finally:
        temporary.unlink(missing_ok=True)
        _release_process_slots(slots)


@app.post("/live-captions")
async def live_captions(request: Request) -> dict:
    content_type = request.headers.get("content-type", "").split(";", 1)[0].strip().lower()
    if content_type != "audio/wav":
        raise HTTPException(415, detail={"errorCode": "speech.caption_media_type_invalid"})
    content_length = request.headers.get("content-length")
    if content_length is not None:
        try:
            if int(content_length) > MAXIMUM_LIVE_CAPTION_BYTES:
                raise HTTPException(413, detail={"errorCode": "speech.caption_chunk_too_large"})
        except ValueError as exception:
            raise HTTPException(400, detail={"errorCode": "speech.caption_length_invalid"}) from exception

    language = request.headers.get("x-go-ai-caption-language")
    if language is not None and not LANGUAGE_PATTERN.fullmatch(language):
        raise HTTPException(400, detail={"errorCode": "speech.caption_language_invalid"})
    task = request.headers.get("x-go-ai-caption-task", "transcribe")
    if task not in ("transcribe", "translate"):
        raise HTTPException(400, detail={"errorCode": "speech.caption_task_invalid"})

    audio = await request.body()
    if len(audio) < 44 or len(audio) > MAXIMUM_LIVE_CAPTION_BYTES:
        raise HTTPException(413, detail={"errorCode": "speech.caption_chunk_size_invalid"})
    return await run_in_threadpool(_transcribe_live_caption, audio, language, task)


@app.post("/speech")
def synthesize(request: SpeechRequest) -> dict:
    slots = _acquire_process_slots()
    try:
        model = models.load_tts()
        wavs, sample_rate = model.generate_voice_clone(
            text=request.text,
            language="German",
            voice_clone_prompt=models.voice_prompt,
            non_streaming_mode=True,
        )
        waveform = wavs[0]
        if abs(request.speed - 1.0) > 0.001:
            import librosa
            import numpy as np

            waveform = librosa.effects.time_stretch(
                np.asarray(waveform, dtype=np.float32).squeeze(),
                rate=request.speed,
            )
        OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
        file_name = f"speech-{uuid.uuid4().hex}.wav"
        destination = OUTPUT_ROOT / file_name
        sf.write(str(destination), waveform, sample_rate)
        return {
            "relativePath": str(destination.relative_to(DATA_ROOT)).replace("\\", "/"),
            "fileName": file_name,
            "mediaType": "audio/wav",
            "provider": "qwen3-tts-0.6b-base",
            "isFallback": False,
            "metadata": {
                "sampleRate": str(sample_rate),
                "language": "German",
                "voice": "Microsoft Hedda reference",
                "speed": str(request.speed),
            },
        }
    finally:
        _release_process_slots(slots)
