FROM nvidia/cuda@sha256:ac55d124da4882b497f732d8dfd9a702d5447a5f29d08d56da6f64f0a1eb34bc

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1

RUN apt-get update \
    && apt-get install --yes --no-install-recommends python3 python3-pip python3-venv libsndfile1 ffmpeg sox \
    && rm -rf /var/lib/apt/lists/* \
    && python3 -m venv /opt/venv

ENV PATH=/opt/venv/bin:$PATH

COPY speech/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir --extra-index-url https://download.pytorch.org/whl/cu128 -r /tmp/requirements.txt \
    && pip install --no-cache-dir --no-deps qwen-tts==0.1.1 \
    && rm /tmp/requirements.txt

ENV NUMBA_CACHE_DIR=/tmp/numba-cache \
    XDG_CACHE_HOME=/tmp/cache \
    TORCH_HOME=/tmp/torch

RUN groupadd --gid 10001 goai \
    && useradd --uid 10001 --gid 10001 --create-home --shell /usr/sbin/nologin goai \
    && mkdir -p /app /data/Artifacts/worker \
    && chown -R 10001:10001 /app /data

COPY common /app/common
COPY speech/app.py /app/app.py

USER 10001:10001
WORKDIR /app
EXPOSE 8080
ENTRYPOINT ["python", "-m", "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080", "--no-access-log", "--log-level", "warning"]
