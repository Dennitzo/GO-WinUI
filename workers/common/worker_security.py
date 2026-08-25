from pathlib import Path

from fastapi import HTTPException, status


def resolve_data_path(value: str, allowed_root: str) -> Path:
    root = Path(allowed_root).resolve(strict=True)
    candidate = Path(value).resolve(strict=True)
    if candidate != root and root not in candidate.parents:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={"errorCode": "worker.path_outside_scope"},
        )
    return candidate
