#!/bin/sh
set -eu

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
ARCH="${1:-${ARCH:-x64}}"
IMAGE="${IMAGE:-omniplay-nas:dev}"
BUILD_DIR="${ROOT_DIR}/build/docker"
PUBLISH_DIR="${BUILD_DIR}/publish"

case "${ARCH}" in
  x64|amd64|x86_64|linux-x64)
    RID="linux-x64"
    PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"
    ;;
  arm64|aarch64|armv8|linux-arm64)
    RID="linux-arm64"
    PLATFORM="${DOCKER_PLATFORM:-linux/arm64}"
    ;;
  *)
    echo "Unsupported architecture: ${ARCH}" >&2
    echo "Usage: scripts/package-docker.sh [x64|arm64]" >&2
    exit 2
    ;;
esac

echo "==> Building Web UI"
npm --prefix "${ROOT_DIR}/web" run build

echo "==> Publishing server payload (${RID})"
rm -rf "${PUBLISH_DIR}"
mkdir -p "${PUBLISH_DIR}"
dotnet publish "${ROOT_DIR}/server/src/OmniPlay.Api/OmniPlay.Api.csproj" \
  -c Release \
  -r "${RID}" \
  --self-contained false \
  -o "${PUBLISH_DIR}" \
  /p:PublishSingleFile=false

echo "==> Staging Web UI into payload"
rm -rf "${PUBLISH_DIR}/wwwroot"
cp -R "${ROOT_DIR}/web/dist" "${PUBLISH_DIR}/wwwroot"

echo "==> Building Docker image ${IMAGE} (${PLATFORM})"
docker buildx build \
  --platform "${PLATFORM}" \
  -f "${ROOT_DIR}/packaging/docker/Dockerfile" \
  -t "${IMAGE}" \
  --load \
  "${BUILD_DIR}"

echo "Docker image created: ${IMAGE}"
