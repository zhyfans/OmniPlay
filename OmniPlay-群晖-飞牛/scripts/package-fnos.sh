#!/bin/sh
set -eu

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
IMAGE="${IMAGE:-omniplay-fnos:dev}"

echo "==> Building fnOS Docker validation image"
IMAGE="${IMAGE}" "${ROOT_DIR}/scripts/package-docker.sh" "${1:-x64}"

echo "fnOS Docker image created: ${IMAGE}"
echo "Native .fpk packaging still depends on the current fnOS app platform manifest/signing requirements."
