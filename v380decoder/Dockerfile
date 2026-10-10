# Build args provided by the Supervisor builder.
ARG BUILD_FROM=mcr.microsoft.com/dotnet/sdk:10.0

# ---- build stage (SDK needed only here) ----
FROM ${BUILD_FROM} AS build
WORKDIR /src
# NOTE: build context is v380decoder/, with the PATCHED decoder source
# vendored under decoder-src/ (per-type reassembly, decrypt-from-16,
# wall-clock RTP, H265 passthrough, AAC audio, codec stickiness).
COPY decoder-src/V380Decoder.csproj decoder-src/NuGet.Config ./
COPY decoder-src/src/ ./src/
ARG TARGETARCH
RUN set -e; \
    case "${TARGETARCH}" in \
      amd64) RID=linux-x64 ;; \
      arm64) RID=linux-arm64 ;; \
      *) echo "unsupported arch ${TARGETARCH}"; exit 1 ;; \
    esac; \
    dotnet publish -c Release -r "${RID}" --self-contained false -o /out

# ---- runtime stage (small, plus ffmpeg for snapshot decode) ----
FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg jq \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /out /app
COPY run.sh /run.sh
RUN chmod +x /run.sh
CMD ["/run.sh"]
