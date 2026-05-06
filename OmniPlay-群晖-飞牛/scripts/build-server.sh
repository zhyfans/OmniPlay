#!/bin/sh
set -eu

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
dotnet build "${ROOT_DIR}/server/OmniPlay.Server.slnx" -c Debug

