FROM nvidia/cuda@sha256:24c8e3581ea6330038b0d374920721983312627f8adbfcf390bdb4b399d280ed AS builder

ARG STABLE_DIFFUSION_CPP_COMMIT=de298c225bed97c3f9026b73cd7b71e7879bd41b
ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install --yes --no-install-recommends build-essential cmake git ninja-build \
    && rm -rf /var/lib/apt/lists/*

RUN git clone https://github.com/leejet/stable-diffusion.cpp.git /src \
    && cd /src \
    && git checkout "${STABLE_DIFFUSION_CPP_COMMIT}" \
    && git submodule update --init --recursive --depth 1 \
    && cmake -S /src -B /src/build -G Ninja \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_CUDA_ARCHITECTURES=75 \
        -DSD_CUDA=ON \
        -DSD_WEBP=OFF \
        -DSD_WEBM=OFF \
    && cmake --build /src/build --config Release --target sd-cli --parallel 4

FROM nvidia/cuda@sha256:ac55d124da4882b497f732d8dfd9a702d5447a5f29d08d56da6f64f0a1eb34bc

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

RUN apt-get update \
    && apt-get install --yes --no-install-recommends python3 python3-pip python3-venv libgomp1 \
    && rm -rf /var/lib/apt/lists/* \
    && python3 -m venv /opt/venv

ENV PATH=/opt/venv/bin:$PATH

COPY image/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt \
    && rm /tmp/requirements.txt

COPY --from=builder /src/build/bin /opt/stable-diffusion

RUN groupadd --gid 10001 goai \
    && useradd --uid 10001 --gid 10001 --create-home --shell /usr/sbin/nologin goai \
    && mkdir -p /app /data/Artifacts/worker \
    && chown -R 10001:10001 /app /data

COPY common /app/common
COPY image/app.py /app/app.py

USER 10001:10001
WORKDIR /app
EXPOSE 8080
ENTRYPOINT ["python", "-m", "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080", "--no-access-log", "--log-level", "warning"]
