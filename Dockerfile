FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS builder

WORKDIR /src

COPY . .

ARG TARGETARCH
ARG BUILD_BLOCK_INDEX=0
ARG BUILD_NATIVE_ONLY_INDEX=1
RUN case "$TARGETARCH" in \
      amd64) RID=linux-x64 ;; \
      arm64) RID=linux-arm64 ;; \
      *) echo "unsupported TARGETARCH=$TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet publish src/RinhaFraud/RinhaFraud.csproj -c Release -r "$RID" -o /out/app
RUN mkdir -p /out/lb \
    && clang -O3 -DNDEBUG -march=haswell -mtune=haswell -pthread -o /out/lb/rinha-lb src/lb/rinha-lb.c
RUN mkdir -p /out/native \
    && clang -O3 -DNDEBUG -march=haswell -mtune=haswell -fPIC -shared -o /out/native/librinha_native.so src/native/rinha_native.c
RUN mkdir -p /out/native-api \
    && clang -O3 -DNDEBUG -march=haswell -mtune=haswell -pthread -o /out/native-api/rinha-native-api src/native/rinha_native_api.c src/native/rinha_native.c
RUN mkdir -p /out/data \
    && if [ -f resources/references.json.gz ]; then \
         gzip -dc resources/references.json.gz | BUILD_BLOCK_INDEX="$BUILD_BLOCK_INDEX" BUILD_NATIVE_ONLY_INDEX="$BUILD_NATIVE_ONLY_INDEX" /out/app/RinhaFraud build-index /out/data/references.idx ; \
       elif [ -f data/references.idx ]; then \
         cp data/references.idx /out/data/references.idx ; \
       else \
         curl -fsSL https://raw.githubusercontent.com/zanfranceschi/rinha-de-backend-2026/main/resources/references.json.gz \
           | gzip -dc \
           | BUILD_BLOCK_INDEX="$BUILD_BLOCK_INDEX" BUILD_NATIVE_ONLY_INDEX="$BUILD_NATIVE_ONLY_INDEX" /out/app/RinhaFraud build-index /out/data/references.idx ; \
       fi \
    && test -s /out/data/references.idx

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble

WORKDIR /app
COPY --from=builder /out/app/RinhaFraud /usr/local/bin/rinha-fraud
COPY --from=builder /out/lb/rinha-lb /usr/local/bin/rinha-lb
COPY --from=builder /out/native-api/rinha-native-api /usr/local/bin/rinha-native-api
COPY --from=builder /out/native/librinha_native.so /usr/local/lib/librinha_native.so
COPY --from=builder /out/data /app/data

ENV LD_LIBRARY_PATH=/usr/local/lib
ENV BIND_ADDR=0.0.0.0:8080
ENV INDEX_PATH=/app/data/references.idx
ENV SERVER_MODE=raw
ENV TP_MIN_THREADS=64
ENV WORKERS=2
ENV EARLY_CANDIDATES=9800
ENV MIN_CANDIDATES=9800
ENV MAX_CANDIDATES=11000
ENV PROFILE_FASTPATH=1
ENV PROFILE_MIN_COUNT=15
ENV PROFILE_LEGIT_MIN_COUNT=5
ENV PROFILE_FRAUD_MIN_COUNT=15
ENV EXACT_FALLBACK=risky

EXPOSE 8080
ENTRYPOINT ["rinha-fraud", "serve"]
