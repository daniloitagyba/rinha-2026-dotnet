FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder

WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev gzip ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY . .

ARG TARGETARCH
RUN case "$TARGETARCH" in \
      amd64) RID=linux-x64 ;; \
      arm64) RID=linux-arm64 ;; \
      *) echo "unsupported TARGETARCH=$TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet publish src/RinhaFraud/RinhaFraud.csproj -c Release -r "$RID" -o /out/app
RUN mkdir -p /out/data \
    && if [ -f resources/references.json.gz ]; then \
         gzip -dc resources/references.json.gz | /out/app/RinhaFraud build-index /out/data/references.idx ; \
       elif [ -f data/references.idx ]; then \
         cp data/references.idx /out/data/references.idx ; \
       fi

FROM debian:bookworm-slim

WORKDIR /app
COPY --from=builder /out/app/RinhaFraud /usr/local/bin/rinha-fraud
COPY --from=builder /out/data /app/data
COPY docker/entrypoint.sh /entrypoint.sh

ENV BIND_ADDR=0.0.0.0:8080
ENV INDEX_PATH=/app/data/references.idx
ENV WORKERS=1
ENV MIN_CANDIDATES=16000
ENV MAX_CANDIDATES=32000

EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
