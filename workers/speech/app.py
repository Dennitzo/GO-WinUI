from __future__ import annotations

import gc
import base64
import binascii
import os
import re
import threading
import time
import uuid
import wave
from pathlib import Path
from typing import Literal

import soundfile as sf
import numpy as np
import torch
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from starlette.concurrency import run_in_threadpool

from common.worker_security import read_worker_key, require_worker_key, resolve_data_path


DATA_ROOT = Path("/data")
MODEL_ROOT = Path("/models")
OUTPUT_ROOT = DATA_ROOT / "Artifacts" / "worker"
WHISPER_MODEL = Path(os.environ.get("GO_AI_WHISPER_MODEL", MODEL_ROOT / "faster-whisper-large-v3"))
SPEAKER_MODEL = Path(os.environ.get("GO_AI_SPEAKER_MODEL", MODEL_ROOT / "spkrec-ecapa-voxceleb"))
WHISPER_HOTWORDS = os.environ.get(
    "GO_AI_WHISPER_HOTWORDS",
    "TGA, technische Gebäudeausrüstung, Heizung, Lüftung, Sanitär, Klima, Kälte, "
    "Elektro, Brandschutz, Volumenstrom, Heizlast, Kühllast, RLT, VDI, DIN, BricsCAD",
).strip()
TTS_MODEL = Path(os.environ.get("GO_AI_TTS_MODEL", MODEL_ROOT / "piper" / "de_DE-kerstin-low" / "de_DE-kerstin-low.onnx"))
TTS_CONFIG = Path(os.environ.get("GO_AI_TTS_CONFIG", f"{TTS_MODEL}.json"))
TTS_PROVIDER = "piper-de_DE-kerstin-low"
MODEL_TTL_SECONDS = max(60, int(os.environ.get("GO_AI_MODEL_TTL_SECONDS", "600")))
WORKER_KEY = read_worker_key()
MAX_PARALLEL_SPEECH_OPERATIONS = 2
PROCESS_GATE = threading.BoundedSemaphore(MAX_PARALLEL_SPEECH_OPERATIONS)
ACTIVE_OPERATIONS_GATE = threading.Lock()
ACTIVE_OPERATIONS = 0
MAXIMUM_LIVE_CAPTION_BYTES = 512 * 1024


def _acquire_process_slots(count: int = 1) -> int:
    global ACTIVE_OPERATIONS
    acquired = 0
    try:
        while acquired < count:
            if not PROCESS_GATE.acquire(timeout=120):
                raise HTTPException(503, detail={"errorCode": "speech.worker_queue_timeout"})
            acquired += 1
        with ACTIVE_OPERATIONS_GATE:
            ACTIVE_OPERATIONS += acquired
        return acquired
    except BaseException:
        for _ in range(acquired):
            PROCESS_GATE.release()
        raise


def _release_process_slots(count: int) -> None:
    global ACTIVE_OPERATIONS
    with ACTIVE_OPERATIONS_GATE:
        ACTIVE_OPERATIONS = max(0, ACTIVE_OPERATIONS - count)
    for _ in range(count):
        PROCESS_GATE.release()


def _has_active_operations() -> bool:
    with ACTIVE_OPERATIONS_GATE:
        return ACTIVE_OPERATIONS > 0
LANGUAGE_PATTERN = re.compile(r"^[A-Za-z0-9_-]{1,16}$")
CAPTION_SESSION_PATTERN = re.compile(r"^caption-[a-f0-9]{32}$")


def _whisper_available() -> bool:
    return all(
        (WHISPER_MODEL / name).is_file()
        for name in ("model.bin", "config.json", "tokenizer.json", "vocabulary.json")
    )


def _tts_available() -> bool:
    return TTS_MODEL.is_file() and TTS_CONFIG.is_file()


def _speaker_available() -> bool:
    return all(
        (SPEAKER_MODEL / name).is_file()
        for name in ("hyperparams.yaml", "embedding_model.ckpt", "mean_var_norm_emb.ckpt")
    )


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class LoadRequest(StrictModel):
    component: Literal["stt", "tts", "speaker"]


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
        self._speaker = None
        self._last_used: float | None = None

    def status(self) -> dict:
        with self._gate:
            whisper_available = _whisper_available()
            tts_available = _tts_available()
            speaker_available = _speaker_available()
            files_available = whisper_available and tts_available and speaker_available
            return {
                "status": "ready" if files_available else "model-missing",
                "sttLoaded": self._stt is not None,
                "ttsLoaded": self._tts is not None,
                "speakerLoaded": self._speaker is not None,
                "whisperModelAvailable": whisper_available,
                "ttsModelAvailable": tts_available,
                "speakerModelAvailable": speaker_available,
                "ttsProvider": TTS_PROVIDER,
                "lastUsedUnix": self._last_used,
                "idleTtlSeconds": MODEL_TTL_SECONDS,
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
                from piper import PiperVoice

                self._tts = PiperVoice.load(
                    str(TTS_MODEL),
                    config_path=str(TTS_CONFIG),
                    use_cuda=False,
                )
            self._last_used = time.time()
            return self._tts

    def load_speaker(self):
        with self._gate:
            if self._speaker is None:
                if not _speaker_available():
                    raise HTTPException(503, detail={"errorCode": "speech.speaker_model_missing"})
                import torchaudio

                # SpeechBrain 1.0 checks this legacy helper during import. It was
                # removed in torchaudio 2.9, while the load/feature APIs used by
                # ECAPA remain compatible.
                if not hasattr(torchaudio, "list_audio_backends"):
                    torchaudio.list_audio_backends = lambda: ["soundfile"]
                from speechbrain.inference.speaker import EncoderClassifier

                self._speaker = EncoderClassifier.from_hparams(
                    source=str(SPEAKER_MODEL),
                    savedir="/tmp/go-ai-speaker-model",
                    run_opts={"device": "cuda"},
                    overrides={"pretrained_path": str(SPEAKER_MODEL)},
                )
            self._last_used = time.time()
            return self._speaker

    def release(self) -> None:
        with self._gate:
            self._stt = None
            self._tts = None
            self._speaker = None
            self._last_used = None
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    def release_if_idle(self) -> bool:
        with self._gate:
            if self._last_used is None or time.time() - self._last_used < MODEL_TTL_SECONDS:
                return False
            self._stt = None
            self._tts = None
            self._speaker = None
            self._last_used = None
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
        return True

models = ModelRegistry()
app = FastAPI(title="GO AI Speech Worker", docs_url=None, redoc_url=None, openapi_url=None)


class SpeakerTracker:
    def __init__(self) -> None:
        self.centroids: list[np.ndarray] = []
        self.counts: list[int] = []
        self.last_speaker = 0
        self.updated_at = time.monotonic()

    def assign(self, embedding: np.ndarray) -> int:
        vector = embedding.astype(np.float32, copy=False).reshape(-1)
        norm = float(np.linalg.norm(vector))
        if norm <= 1e-8:
            return self.last_speaker
        vector = vector / norm
        if not self.centroids:
            self.centroids.append(vector)
            self.counts.append(1)
            self.last_speaker = 0
            return 0

        similarities = [float(np.dot(vector, centroid)) for centroid in self.centroids]
        best = int(np.argmax(similarities))
        if similarities[best] < 0.35 and len(self.centroids) < 8:
            best = len(self.centroids)
            self.centroids.append(vector)
            self.counts.append(1)
        else:
            count = self.counts[best]
            centroid = self.centroids[best] * count + vector
            centroid_norm = float(np.linalg.norm(centroid))
            self.centroids[best] = centroid / max(centroid_norm, 1e-8)
            self.counts[best] = count + 1
        self.last_speaker = best
        self.updated_at = time.monotonic()
        return best


SPEAKER_TRACKERS: dict[str, SpeakerTracker] = {}
SPEAKER_TRACKER_GATE = threading.Lock()


def _speaker_tracker(session_id: str) -> SpeakerTracker:
    now = time.monotonic()
    with SPEAKER_TRACKER_GATE:
        for key in [key for key, value in SPEAKER_TRACKERS.items() if now - value.updated_at > 900]:
            SPEAKER_TRACKERS.pop(key, None)
        tracker = SPEAKER_TRACKERS.setdefault(session_id, SpeakerTracker())
        tracker.updated_at = now
        return tracker


def _preload_models() -> None:
    try:
        models.load_stt()
        models.load_tts()
        models.load_speaker()
    except Exception:
        # Readiness/status expose a missing model. Startup remains available so the
        # gateway can report the concrete worker error instead of a dead container.
        pass


def _reap_idle_models() -> None:
    interval = min(30, max(5, MODEL_TTL_SECONDS // 4))
    while True:
        time.sleep(interval)
        if not _has_active_operations():
            models.release_if_idle()


@app.on_event("startup")
def preload_configured_models() -> None:
    if os.environ.get("GO_AI_PRELOAD_STT", "1").strip().lower() in ("1", "true", "yes", "on"):
        threading.Thread(target=_preload_models, name="go-ai-speech-preload", daemon=True).start()
    threading.Thread(target=_reap_idle_models, name="go-ai-speech-idle-reaper", daemon=True).start()


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
        if request.component == "stt":
            models.load_stt()
        elif request.component == "speaker":
            models.load_speaker()
        else:
            models.load_tts()
        return models.status()
    finally:
        _release_process_slots(slots)


@app.post("/release")
def release() -> dict:
    slots = _acquire_process_slots(MAX_PARALLEL_SPEECH_OPERATIONS)
    try:
        models.release()
        with SPEAKER_TRACKER_GATE:
            SPEAKER_TRACKERS.clear()
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
            "provider": "whisper-large-v3",
        }
    finally:
        _release_process_slots(slots)


def _speaker_labels(temporary: Path, rows: list[dict], session_id: str) -> None:
    if not rows:
        return
    waveform, sample_rate = sf.read(str(temporary), dtype="float32", always_2d=False)
    if sample_rate != 16000:
        return
    if waveform.ndim > 1:
        waveform = waveform.mean(axis=1)
    speaker_model = models.load_speaker()
    tracker = _speaker_tracker(session_id)
    for row in rows:
        start = max(0, int(float(row["start"]) * sample_rate))
        end = min(len(waveform), int(float(row["end"]) * sample_rate))
        clip = waveform[start:end]
        if clip.size < sample_rate // 2:
            row["speaker"] = f"Person {tracker.last_speaker + 1}"
            continue
        signal = torch.from_numpy(np.ascontiguousarray(clip)).unsqueeze(0)
        with torch.inference_mode():
            embedding = speaker_model.encode_batch(signal).squeeze().detach().cpu().numpy()
        row["speaker"] = f"Person {tracker.assign(embedding) + 1}"


def _transcribe_live_caption(
    audio: bytes,
    language: str | None,
    task: str,
    context: str | None,
    session_id: str,
) -> dict:
    slots = _acquire_process_slots()
    temporary = Path("/tmp") / f"caption-{uuid.uuid4().hex}.wav"
    try:
        temporary.write_bytes(audio)
        model = models.load_stt()
        segments, info = model.transcribe(
            str(temporary),
            language=language,
            task=task,
            beam_size=5,
            best_of=5,
            patience=1.0,
            word_timestamps=False,
            vad_filter=True,
            condition_on_previous_text=True,
            initial_prompt=context or None,
            hotwords=WHISPER_HOTWORDS or None,
            temperature=0.0,
        )
        rows = [
            {"start": round(segment.start, 3), "end": round(segment.end, 3), "text": segment.text.strip()}
            for segment in segments
            if segment.text.strip()
        ]
        _speaker_labels(temporary, rows, session_id)
        return {
            "text": " ".join(row["text"] for row in rows).strip(),
            "language": info.language,
            "languageProbability": round(info.language_probability, 6),
            "segments": rows,
            "provider": "whisper-large-v3-live + ECAPA",
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
    session_id = request.headers.get("x-go-ai-caption-session", "")
    if not CAPTION_SESSION_PATTERN.fullmatch(session_id):
        raise HTTPException(400, detail={"errorCode": "speech.caption_session_invalid"})
    context = None
    encoded_context = request.headers.get("x-go-ai-caption-context-b64")
    if encoded_context:
        if len(encoded_context) > 8192:
            raise HTTPException(400, detail={"errorCode": "speech.caption_context_invalid"})
        try:
            context = base64.b64decode(encoded_context, validate=True).decode("utf-8")[-1000:]
        except (binascii.Error, ValueError, UnicodeDecodeError) as exception:
            raise HTTPException(400, detail={"errorCode": "speech.caption_context_invalid"}) from exception

    audio = await request.body()
    if len(audio) < 44 or len(audio) > MAXIMUM_LIVE_CAPTION_BYTES:
        raise HTTPException(413, detail={"errorCode": "speech.caption_chunk_size_invalid"})
    return await run_in_threadpool(
        _transcribe_live_caption,
        audio,
        language,
        task,
        context,
        session_id,
    )


@app.post("/speech")
def synthesize(request: SpeechRequest) -> dict:
    slots = _acquire_process_slots()
    try:
        model = models.load_tts()
        OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
        file_name = f"speech-{uuid.uuid4().hex}.wav"
        destination = OUTPUT_ROOT / file_name
        from piper import SynthesisConfig

        synthesis = SynthesisConfig(
            length_scale=1.0 / request.speed,
            normalize_audio=True,
        )
        with wave.open(str(destination), "wb") as wav_file:
            model.synthesize_wav(request.text, wav_file, syn_config=synthesis)
        with wave.open(str(destination), "rb") as wav_file:
            frames = wav_file.readframes(wav_file.getnframes())
            duration = wav_file.getnframes() / max(1, wav_file.getframerate())
            if wav_file.getsampwidth() != 2 or not frames:
                destination.unlink(missing_ok=True)
                raise HTTPException(502, detail={"errorCode": "speech.tts_invalid_pcm"})
            pcm = np.frombuffer(frames, dtype=np.int16).astype(np.float32) / 32768.0
            rms = float(np.sqrt(np.mean(np.square(pcm)))) if pcm.size else 0.0
            if duration < 0.1 or rms < 0.0005:
                destination.unlink(missing_ok=True)
                raise HTTPException(502, detail={"errorCode": "speech.tts_silent_output"})
        return {
            "relativePath": str(destination.relative_to(DATA_ROOT)).replace("\\", "/"),
            "fileName": file_name,
            "mediaType": "audio/wav",
            "provider": TTS_PROVIDER,
            "isFallback": False,
            "metadata": {
                "sampleRate": "22050",
                "language": "German",
                "voice": "Kerstin",
                "speed": str(request.speed),
                "durationSeconds": f"{duration:.3f}",
                "rms": f"{rms:.6f}",
            },
        }
    finally:
        _release_process_slots(slots)
