FROM python:3.12.10-slim-bookworm@sha256:fd95fa221297a88e1cf49c55ec1828edd7c5a428187e67b5d1805692d11588db

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1 \
    MPLCONFIGDIR=/tmp/matplotlib

COPY requirements.lock /tmp/requirements.lock

RUN groupadd --gid 10001 research \
    && useradd --uid 10001 --gid 10001 --create-home --shell /usr/sbin/nologin research \
    && python -m pip install --no-cache-dir --require-hashes --requirement /tmp/requirements.lock \
    && rm -f /tmp/requirements.lock \
    && mkdir -p /sandbox \
    && chown research:research /sandbox

USER 10001:10001
WORKDIR /sandbox/work
ENTRYPOINT []
