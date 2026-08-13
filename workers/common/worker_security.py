import hmac
import os
from pathlib import Path

from fastapi import HTTPException, Request, status


def read_worker_key() -> bytes:
    key_file = Path(os.environ.get("GO_AI_WORKER_KEY_FILE", "/run/secrets/worker_key"))
    value = key_file.read_text(encoding="utf-8").strip()
    if len(value) < 32:
        raise RuntimeError("worker key is missing or too short")
    return value.encode("utf-8")


def require_worker_key(request: Request, expected: bytes) -> bool:
    supplied = request.headers.get("x-go-ai-worker-key", "").encode("utf-8")
    return hmac.compare_digest(supplied, expected)


def resolve_data_path(value: str, allowed_root: str) -> Path:
    root = Path(allowed_root).resolve(strict=True)
    candidate = Path(value).resolve(strict=True)
    if candidate != root and root not in candidate.parents:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={"errorCode": "worker.path_outside_scope"},
        )
    return candidate
