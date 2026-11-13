# ModBot for Linux (x64). Built by .github/workflows/docker.yml and published to ghcr.io; docker-compose.yml runs it
# with Lavalink and Ollama. See "Running on a Linux server (Docker)" in README.md.

# Build: the patched Discord.Net, the Linux voice files, then the bot. The SDK image has git and pwsh, which the two
# setup scripts use (the same scripts as on Windows, so every download stays pinned to its SHA-256 in one place).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The patched Discord.Net changes only with its script and patch, so it's its own layer and is rebuilt rarely.
COPY discord-net-patch/build.ps1 discord-net-patch/pr3288.patch discord-net-patch/
RUN pwsh -File discord-net-patch/build.ps1

COPY voice/setup.ps1 voice/
RUN pwsh -File voice/setup.ps1

COPY ModBot.csproj nuget.config ./
RUN dotnet restore ModBot.csproj -r linux-x64

COPY . .
RUN dotnet publish ModBot.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /out \
    && mkdir -p /out/voice \
    && cp -r voice/models voice/piper /out/voice/

# Run: just the .NET runtime, plus OpenMP for Whisper's speech-to-text.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 tzdata \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out .
# data/ and logs/ are mounted from the host (docker-compose.yml); made here so they also work unmounted.
RUN mkdir -p data logs && chown app:app data logs

# The non-root user the .NET images come with (UID 1654).
USER app
ENV DOTNET_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "ModBot.dll"]
