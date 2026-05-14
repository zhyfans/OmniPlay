FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        ffmpeg \
        tzdata \
    && rm -rf /var/lib/apt/lists/*

COPY publish/ ./

ENV ASPNETCORE_URLS=http://0.0.0.0:8096
ENV OMNIPLAY_URLS=http://0.0.0.0:8096
ENV OMNIPLAY_APP_ROOT=/var/lib/omniplay
ENV OMNIPLAY_FFMPEG_PATH=/usr/bin/ffmpeg
ENV OMNIPLAY_FFPROBE_PATH=/usr/bin/ffprobe

EXPOSE 8096
ENTRYPOINT ["dotnet", "OmniPlay.Api.dll"]
