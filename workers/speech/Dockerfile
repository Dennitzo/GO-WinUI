FROM nvidia/cuda@sha256:ac55d124da4882b497f732d8dfd9a702d5447a5f29d08d56da6f64f0a1eb34bc

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1

RUN apt-get update \
    && apt-get install --yes --no-install-recommends python3 python3-pip python3-venv libsndfile1 ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && python3 -m venv /opt/venv

ENV PATH=/opt/venv/bin:$PATH

COPY speech/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir --extra-index-url https://download.pytorch.org/whl/cu128 -r /tmp/requirements.txt \
    && pip install --no-cache-dir --no-deps supertonic==1.3.1 \
    && rm /tmp/requirements.txt

# Supertonic is the resident CUDA voice model. SpeechBrain provides live-caption
# diarization. No secondary speech provider is installed in this image.
RUN pip install --no-cache-dir speechbrain==1.0.3 \
    && pip uninstall --yes onnxruntime \
    && pip install --no-cache-dir --no-deps --force-reinstall onnxruntime-gpu==1.23.2 \
    && python -c "import importlib.metadata as m, onnxruntime as ort; distributions=[d.metadata['Name'].lower() for d in m.distributions() if d.metadata.get('Name', '').lower().startswith('onnxruntime')]; assert distributions == ['onnxruntime-gpu'], distributions; assert 'CUDAExecutionProvider' in ort.get_available_providers(), ort.get_available_providers()"

ENV NUMBA_CACHE_DIR=/tmp/numba-cache \
    XDG_CACHE_HOME=/tmp/cache \
    TORCH_HOME=/tmp/torch \
    OMP_NUM_THREADS=4 \
    OPENBLAS_NUM_THREADS=1 \
    MKL_NUM_THREADS=1 \
    NUMEXPR_NUM_THREADS=1

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
