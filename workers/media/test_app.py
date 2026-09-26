from pathlib import Path

import pytest
from fastapi import HTTPException
from PIL import Image

import app


def test_webp_is_decoded_to_llama_compatible_jpeg(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setattr(app, "DATA_ROOT", tmp_path)
    monkeypatch.setattr(app, "OUTPUT_ROOT", tmp_path / "output")
    source = tmp_path / "reference.webp"
    Image.new("RGBA", (640, 360), (32, 96, 160, 180)).save(source, "WEBP")

    result = app._inspect_image(source)

    vision = next(item for item in result["artifacts"] if item["role"] == "vision_input")
    vision_path = app.DATA_ROOT / vision["relativePath"]
    with Image.open(vision_path) as decoded:
        assert decoded.format == "JPEG"
        assert decoded.size == (640, 360)
    assert vision["mediaType"] == "image/jpeg"


def test_corrupt_image_is_rejected_before_native_model_request(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setattr(app, "DATA_ROOT", tmp_path)
    monkeypatch.setattr(app, "OUTPUT_ROOT", tmp_path / "output")
    source = tmp_path / "broken.webp"
    source.write_bytes(b"RIFF-not-an-image-WEBP")

    with pytest.raises(HTTPException) as error:
        app._inspect_image(source)

    assert error.value.status_code == 400
    assert error.value.detail == {"errorCode": "media.invalid_image"}
