FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app
COPY publish/ ./

ENV ASPNETCORE_URLS=http://0.0.0.0:8096
ENV OMNIPLAY_URLS=http://0.0.0.0:8096
ENV OMNIPLAY_APP_ROOT=/var/lib/omniplay

EXPOSE 8096
ENTRYPOINT ["dotnet", "OmniPlay.Api.dll"]
