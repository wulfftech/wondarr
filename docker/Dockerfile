# syntax=docker/dockerfile:1.7
#
# Compilarr — one multi-arch image (linux/amd64, linux/arm64): API + React UI with slskd, ffmpeg,
# ffprobe, fpcalc and Deno bundled, supervised by s6-overlay with PUID/PGID/UMASK/TZ.
#
# Build context is the repository root:
#   docker build -f docker/Dockerfile .

# ---------------------------------------------------------------------------------------------
# 1. frontend — built once on the build platform; the output is platform-independent.
# ---------------------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS frontend

# vite.config.ts resolves build.outDir to ../src/Compilarr.Api/wwwroot, so the repository layout
# has to stay intact: /src is the repository root.
WORKDIR /src/frontend

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./
COPY docs/api/openapi.json /src/docs/api/openapi.json

RUN npm run build

# ---------------------------------------------------------------------------------------------
# 2. build — framework-dependent publish for the target architecture.
#
# EF Core, SignalR and Quartz use reflection that trimming breaks, and the runtime image already
# carries the .NET runtime, so this is a default (framework-dependent) publish.
# ---------------------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

ARG TARGETARCH
ARG VERSION=0.0.0-dev

WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props Compilarr.sln ./
COPY src/Compilarr.Api/Compilarr.Api.csproj src/Compilarr.Api/
COPY src/Compilarr.Core/Compilarr.Core.csproj src/Compilarr.Core/
COPY src/Compilarr.Sources.Slskd/Compilarr.Sources.Slskd.csproj src/Compilarr.Sources.Slskd/
COPY src/Compilarr.Sources.Torznab/Compilarr.Sources.Torznab.csproj src/Compilarr.Sources.Torznab/
COPY src/Compilarr.Sources.YouTube/Compilarr.Sources.YouTube.csproj src/Compilarr.Sources.YouTube/

RUN dotnet restore src/Compilarr.Api/Compilarr.Api.csproj -a "$TARGETARCH"

COPY src/ src/
COPY --from=frontend /src/src/Compilarr.Api/wwwroot/ src/Compilarr.Api/wwwroot/

RUN dotnet publish src/Compilarr.Api/Compilarr.Api.csproj \
        -c Release \
        -a "$TARGETARCH" \
        --no-self-contained \
        -o /app \
        -p:Version="$VERSION"

# ---------------------------------------------------------------------------------------------
# 3. downloads — every third-party artifact, checksum-verified before it is unpacked.
#    Runs on the target platform, so TARGETARCH maps to the arch names each project uses.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS downloads

ARG TARGETARCH

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates unzip wget xz-utils \
 && rm -rf /var/lib/apt/lists/*

COPY scripts/fetch-slskd.sh /usr/local/bin/fetch-slskd.sh

COPY --from=mwader/static-ffmpeg:9.0.2 /ffmpeg /tools/ffmpeg/ffmpeg
COPY --from=mwader/static-ffmpeg:9.0.2 /ffprobe /tools/ffprobe/ffprobe

RUN set -eux; \
    chmod 0755 /usr/local/bin/fetch-slskd.sh; \
    case "$TARGETARCH" in \
        amd64) \
            s6arch=x86_64; denoarch=x86_64; fpcalcarch=x86_64; slskdarch=x64; \
            s6sha=e6befcc96a437a3831386ecfc51808c5d3e939dc5fe3c02ae9284599e8aa2408; \
            denosha=c6527f24f4b16031d3ae4fa9f658d5f11534c8d84ce7dc8502420280919c3490; \
            fpcalcsha=fc16cd37a70168040bc9ceb45f1d4d1216f5a75bc4c9cf8564bea70ac6a45733; \
            ;; \
        arm64) \
            s6arch=aarch64; denoarch=aarch64; fpcalcarch=arm64; slskdarch=arm64; \
            s6sha=b17f17a82e7a515c682a91edaf2ffdabb73f891981b6c1fd712115693a2f8b4c; \
            denosha=c832298b1ad4422481334855f6003e0f54145762c5a134f20a489511d2f65bbf; \
            fpcalcsha=7eaf5d655c4aa172ab28e3c870b8bb61dd2c327ac94de145676f88842cf6215a; \
            ;; \
        *) echo "unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    \
    # s6-overlay 3.2.3.2 (arch + noarch tarballs), extracted ready to be copied over /.
    wget -q -O /tmp/s6-overlay-arch.tar.xz \
        "https://github.com/just-containers/s6-overlay/releases/download/v3.2.3.2/s6-overlay-${s6arch}.tar.xz"; \
    printf '%s  %s\n' "$s6sha" /tmp/s6-overlay-arch.tar.xz | sha256sum -c -; \
    wget -q -O /tmp/s6-overlay-noarch.tar.xz \
        "https://github.com/just-containers/s6-overlay/releases/download/v3.2.3.2/s6-overlay-noarch.tar.xz"; \
    printf '%s  %s\n' 5379750ed30a84bbd2e2dd74847ba6b5bd29cd0b2e3ea2ec58049b57eb2eda12 /tmp/s6-overlay-noarch.tar.xz | sha256sum -c -; \
    mkdir -p /s6; \
    tar -C /s6 -Jxpf /tmp/s6-overlay-arch.tar.xz; \
    tar -C /s6 -Jxpf /tmp/s6-overlay-noarch.tar.xz; \
    \
    # Deno 2.9.7 — the JS runtime yt-dlp needs to solve YouTube's challenges.
    wget -q -O /tmp/deno.zip \
        "https://github.com/denoland/deno/releases/download/v2.9.7/deno-${denoarch}-unknown-linux-gnu.zip"; \
    printf '%s  %s\n' "$denosha" /tmp/deno.zip | sha256sum -c -; \
    mkdir -p /tools/deno; \
    unzip -q -o /tmp/deno.zip -d /tools/deno; \
    chmod 0755 /tools/deno/deno; \
    \
    # fpcalc 1.6.1 (static chromaprint) — AcoustID fingerprinting.
    wget -q -O /tmp/fpcalc.tar.gz \
        "https://github.com/acoustid/chromaprint/releases/download/v1.6.1/chromaprint-fpcalc-1.6.1-linux-${fpcalcarch}.tar.gz"; \
    printf '%s  %s\n' "$fpcalcsha" /tmp/fpcalc.tar.gz | sha256sum -c -; \
    mkdir -p /tools/fpcalc; \
    tar -xzf /tmp/fpcalc.tar.gz -C /tools/fpcalc --strip-components=1; \
    chmod 0755 /tools/fpcalc/fpcalc; \
    \
    # Bundled slskd 0.26.0 (AGPL-3.0, redistributed unmodified with its licence next to it).
    # fetch-slskd.sh verifies the release sha256 itself.
    mkdir -p /opt/slskd; \
    fetch-slskd.sh 0.26.0 "$slskdarch" /opt/slskd; \
    \
    rm -rf /tmp/s6-overlay-arch.tar.xz /tmp/s6-overlay-noarch.tar.xz /tmp/deno.zip /tmp/fpcalc.tar.gz

# ---------------------------------------------------------------------------------------------
# 4. runtime — the shipped image.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

ARG VERSION=0.0.0-dev
# Keeps the tzdata post-install from prompting for a timezone; TZ is applied at runtime.
ARG DEBIAN_FRONTEND=noninteractive

LABEL org.opencontainers.image.source="https://github.com/wulfftech/compilarr" \
      org.opencontainers.image.title="Compilarr" \
      org.opencontainers.image.description="Self-hosted *arr for single songs: Soulseek, YouTube Music, torrents and usenet" \
      org.opencontainers.image.licenses="GPL-3.0-only" \
      org.opencontainers.image.version="$VERSION"

# passwd is what the s6 init oneshot uses to apply PUID/PGID to the compilarr account. Ubuntu
# 24.04 images ship an 'ubuntu' account on uid/gid 1000; it goes, so compilarr can own 1000.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates passwd tzdata wget xz-utils \
 && rm -rf /var/lib/apt/lists/* \
 && (userdel --remove ubuntu 2>/dev/null || true) \
 && groupadd --gid 1000 compilarr \
 && useradd --uid 1000 --gid 1000 --home-dir /config --no-create-home --shell /usr/sbin/nologin compilarr \
 && mkdir -p /config /data \
 && chown compilarr:compilarr /config /data

# s6-overlay, extracted over the filesystem root by the downloads stage.
COPY --from=downloads /s6/ /

COPY --from=build /app /app
COPY --from=downloads /opt/slskd /opt/slskd
COPY --from=downloads /tools/deno/deno /tools/fpcalc/fpcalc /usr/local/bin/
COPY --from=downloads /tools/ffmpeg/ffmpeg /tools/ffprobe/ffprobe /usr/local/bin/

COPY docker/root/ /

# The build context may arrive without the executable bit (Windows checkouts, shared folders);
# s6 runs these scripts by path, so set it explicitly.
RUN chmod 0755 \
      /etc/s6-overlay/scripts/init-compilarr-user \
      /etc/s6-overlay/s6-rc.d/svc-compilarr/run

ENV COMPILARR_CONFIG_DIR=/config \
    PUID=1000 \
    PGID=1000 \
    UMASK=002 \
    TZ=Etc/UTC \
    S6_BEHAVIOUR_IF_STAGE2_FAILS=2 \
    S6_CMD_WAIT_FOR_SERVICES_MAXTIME=0 \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/config/slskd/.net

VOLUME ["/config", "/data"]

EXPOSE 1077 50300

HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD wget -q --spider http://localhost:1077/ping || exit 1

ENTRYPOINT ["/init"]
