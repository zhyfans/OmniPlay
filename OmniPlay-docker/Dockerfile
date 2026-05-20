# syntax=docker/dockerfile:1

FROM node:22-bookworm-slim AS web-build
WORKDIR /src/web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src
COPY server/ ./server/
COPY --from=web-build /src/web/dist ./web/dist
RUN dotnet publish server/src/OmniPlay.Api/OmniPlay.Api.csproj \
    -c Release \
    --self-contained false \
    -o /app/publish \
    /p:PublishSingleFile=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        ffmpeg \
        fontconfig \
        fonts-noto-cjk \
        intel-media-va-driver \
        libva-drm2 \
        libva2 \
        libvpl2 \
        mesa-va-drivers \
        tzdata \
    && rm -rf /var/lib/apt/lists/*

COPY --from=server-build /app/publish/ ./
COPY docker/entrypoint.sh /usr/local/bin/omniplay-entrypoint
RUN chmod +x /usr/local/bin/omniplay-entrypoint

ENV ASPNETCORE_URLS=http://0.0.0.0:45721
ENV OMNIPLAY_URLS=http://0.0.0.0:45721
ENV OMNIPLAY_APP_ROOT=/var/lib/omniplay
ENV OMNIPLAY_LOCAL_SHARE_ROOTS=/media
ENV OMNIPLAY_FFMPEG_PATH=/usr/bin/ffmpeg
ENV OMNIPLAY_FFPROBE_PATH=/usr/bin/ffprobe
ENV OMNIPLAY_VAAPI_DEVICE=/dev/dri/renderD128
ENV OMNIPLAY_ENABLE_QSV=1
ENV DOTNET_EnableDiagnostics=0

VOLUME ["/var/lib/omniplay", "/media"]
EXPOSE 45721
ENTRYPOINT ["/usr/local/bin/omniplay-entrypoint"]
