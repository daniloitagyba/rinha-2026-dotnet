FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS builder

WORKDIR /src

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
       else \
         curl -fsSL https://raw.githubusercontent.com/zanfranceschi/rinha-de-backend-2026/main/resources/references.json.gz \
           | gzip -dc \
           | /out/app/RinhaFraud build-index /out/data/references.idx ; \
       fi \
    && test -s /out/data/references.idx

FROM debian:bookworm-slim

WORKDIR /app
COPY --from=builder /out/app/RinhaFraud /usr/local/bin/rinha-fraud
COPY --from=builder /out/data /app/data

ENV BIND_ADDR=0.0.0.0:8080
ENV INDEX_PATH=/app/data/references.idx
ENV WORKERS=1
ENV EARLY_CANDIDATES=14000
ENV MIN_CANDIDATES=14000
ENV MAX_CANDIDATES=28000
ENV PROFILE_FASTPATH=1
ENV PROFILE_MIN_COUNT=20
ENV EXACT_FALLBACK=risky

EXPOSE 8080
ENTRYPOINT ["rinha-fraud", "serve"]
