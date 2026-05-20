#!/bin/sh
set -eu

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
IMAGE="${IMAGE:-omniplay-docker:latest}"
PLATFORM_ARG=""

case "${1:-${ARCH:-}}" in
  "")
    ;;
  x64|amd64|x86_64|linux-x64)
    PLATFORM_ARG="--platform=linux/amd64"
    ;;
  arm64|aarch64|armv8|linux-arm64)
    PLATFORM_ARG="--platform=linux/arm64"
    ;;
  *)
    echo "Unsupported architecture: ${1:-${ARCH:-}}" >&2
    echo "Usage: scripts/build-image.sh [x64|arm64]" >&2
    exit 2
    ;;
esac

cd "${ROOT_DIR}"
docker build ${PLATFORM_ARG} -t "${IMAGE}" -f Dockerfile .

echo "Docker image created: ${IMAGE}"
