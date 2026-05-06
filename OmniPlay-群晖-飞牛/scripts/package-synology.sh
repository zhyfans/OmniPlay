#!/bin/sh
set -eu

SCRIPT_DIR="$(dirname "$0")"
exec node "${SCRIPT_DIR}/package-synology.mjs" "$@"
