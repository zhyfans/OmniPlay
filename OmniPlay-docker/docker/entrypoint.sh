#!/bin/sh
set -eu

: "${OMNIPLAY_APP_ROOT:=/var/lib/omniplay}"
: "${OMNIPLAY_CACHE_ROOT:=/var/cache/omniplay}"
: "${OMNIPLAY_URLS:=http://0.0.0.0:45722}"
: "${ASPNETCORE_URLS:=${OMNIPLAY_URLS}}"
: "${OMNIPLAY_FFMPEG_PATH:=/usr/bin/ffmpeg}"
: "${OMNIPLAY_FFPROBE_PATH:=/usr/bin/ffprobe}"
: "${OMNIPLAY_LOCAL_SHARE_ROOTS:=/media}"
: "${OMNIPLAY_VAAPI_DEVICE:=/dev/dri/renderD128}"
: "${OMNIPLAY_ENABLE_QSV:=0}"

export OMNIPLAY_APP_ROOT
export OMNIPLAY_CACHE_ROOT
export OMNIPLAY_URLS
export ASPNETCORE_URLS
export OMNIPLAY_FFMPEG_PATH
export OMNIPLAY_FFPROBE_PATH
export OMNIPLAY_LOCAL_SHARE_ROOTS
export OMNIPLAY_VAAPI_DEVICE
export OMNIPLAY_ENABLE_QSV

mkdir -p \
  "${OMNIPLAY_APP_ROOT}/data" \
  "${OMNIPLAY_APP_ROOT}/logs" \
  "${OMNIPLAY_APP_ROOT}/settings" \
  "${OMNIPLAY_CACHE_ROOT}"

exec dotnet OmniPlay.Api.dll "$@"
