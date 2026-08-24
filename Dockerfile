# ThePlayer, as one container: the API, the Angular player, FFmpeg and MediaMTX.
#
# Linux only, and deliberately. Docker Desktop for Windows NATs container traffic, which makes
# WebRTC ICE painful - the container's view of its own address is not one a client can reach - so
# the documented Windows path stays native. See docs/ARCHITECTURE.md.

# ---------------------------------------------------------------------------------------------
# The player. Built first because it changes most often, and its output is just static files.
# ---------------------------------------------------------------------------------------------
FROM node:22-alpine AS player

WORKDIR /player

# Manifests before sources, so a source-only edit does not reinstall the world.
COPY src/ThePlayer.Player/package.json src/ThePlayer.Player/package-lock.json ./
RUN npm ci

COPY src/ThePlayer.Player/ ./
RUN npm run build

# ---------------------------------------------------------------------------------------------
# The API.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

# Only src/. The solution file pulls in the test projects, and publishing does not need them.
COPY Directory.Build.props ./
COPY src/ThePlayer.Domain/ src/ThePlayer.Domain/
COPY src/ThePlayer.Application/ src/ThePlayer.Application/
COPY src/ThePlayer.Infrastructure/ src/ThePlayer.Infrastructure/
COPY src/ThePlayer.Api/ src/ThePlayer.Api/

RUN dotnet publish src/ThePlayer.Api/ThePlayer.Api.csproj \
    --configuration Release \
    --output /app/publish

# ---------------------------------------------------------------------------------------------
# Runtime.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# ffmpeg does every codec operation; curl and tar fetch MediaMTX below.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ffmpeg ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

# Pinned rather than "latest": an image that quietly changes its media server between builds is
# not one you can debug. Bump deliberately.
ARG MEDIAMTX_VERSION=v1.20.1
ARG TARGETARCH=amd64

RUN curl -fsSL \
      "https://github.com/bluenviron/mediamtx/releases/download/${MEDIAMTX_VERSION}/mediamtx_${MEDIAMTX_VERSION}_linux_${TARGETARCH}.tar.gz" \
      -o /tmp/mediamtx.tar.gz \
    && mkdir -p /app/tools/bin \
    && tar -xzf /tmp/mediamtx.tar.gz -C /app/tools/bin mediamtx \
    && chmod +x /app/tools/bin/mediamtx \
    && rm -f /tmp/mediamtx.tar.gz
# The supervisor walks up from the app directory looking for tools/bin/mediamtx, so /app/tools/bin
# is where it will find it without any configuration.

WORKDIR /app

COPY --from=build /app/publish ./

# The Angular player becomes the site. The WHEP harness keeps its place at a fixed URL, because it
# is the dependency-free fallback when something about the player itself is in question.
RUN mv wwwroot/index.html wwwroot/harness.html
COPY --from=player /player/dist/ThePlayer.Player/browser/ ./wwwroot/

# Something to play. Generated rather than committed, for the same reason the test media is: the
# clips always match the encoder actually installed, and no binary assets live in the repository.
RUN mkdir -p /app/samples \
    && ffmpeg -hide_banner -loglevel error -f lavfi \
        -i "testsrc2=size=1280x720:rate=25:duration=30" \
        -c:v libx264 -preset veryfast -pix_fmt yuv420p -g 50 /app/samples/h264-sample.mp4 \
    && ffmpeg -hide_banner -loglevel error -f lavfi \
        -i "testsrc2=size=1280x720:rate=25:duration=30" \
        -c:v libx265 -preset veryfast -pix_fmt yuv420p \
        -x265-params "keyint=50:log-level=none" -tag:v hvc1 /app/samples/h265-sample.mp4

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080
EXPOSE 8189/udp

# Shell form, because $PORT is only known at run time - the host assigns it. Falling back to 8080
# keeps `docker run` without one working.
CMD ASPNETCORE_URLS="http://0.0.0.0:${PORT:-8080}" dotnet ThePlayer.Api.dll
