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
ARG NATIVE_CFLAGS_EXTRA="-DJSON_FIXED_NUMBERS=1 -DKD_BEST_FIRST=1"
RUN mkdir -p /out/lb \
    && clang -O3 -DNDEBUG -march=haswell -mtune=haswell $NATIVE_CFLAGS_EXTRA -pthread -o /out/lb/rinha-lb src/lb/rinha-lb.c
ARG KDTREE_KEY_PROFILE=0
RUN mkdir -p /out/native \
    && clang -O3 -DNDEBUG -march=haswell -mtune=haswell $NATIVE_CFLAGS_EXTRA -DKDTREE_KEY_PROFILE="$KDTREE_KEY_PROFILE" -fPIC -shared -o /out/native/librinha_native.so src/native/rinha_native.c
RUN clang -O3 -DNDEBUG -march=haswell -mtune=haswell $NATIVE_CFLAGS_EXTRA -DKDTREE_KEY_PROFILE="$KDTREE_KEY_PROFILE" -pthread -o /out/native/rinha-native-api src/native/rinha_native_api.c src/native/rinha_native.c
ARG BUILD_BLOCK_INDEX=0
ARG BUILD_NATIVE_ONLY_INDEX=1
ARG BUILD_KDTREE_INDEX=1
ARG KDTREE_LEAF_SIZE=96
RUN mkdir -p /out/data \
    && if [ -f resources/references.json.gz ]; then \
         refs_gz=/tmp/references.json.gz ; \
         cp resources/references.json.gz "$refs_gz" ; \
       elif [ -f data/references.idx ]; then \
         cp data/references.idx /out/data/references.idx ; \
       else \
         refs_gz=/tmp/references.json.gz ; \
         curl -fsSL https://raw.githubusercontent.com/zanfranceschi/rinha-de-backend-2026/main/resources/references.json.gz -o "$refs_gz" ; \
       fi \
    && if [ ! -f /out/data/references.idx ]; then \
         refs_sha="$(sha256sum "$refs_gz" | awk '{print $1}')" ; \
         gzip -dc "$refs_gz" \
           | REFERENCES_GZIP_SHA256="$refs_sha" BUILD_BLOCK_INDEX="$BUILD_BLOCK_INDEX" BUILD_NATIVE_ONLY_INDEX="$BUILD_NATIVE_ONLY_INDEX" BUILD_KDTREE_INDEX="$BUILD_KDTREE_INDEX" KDTREE_LEAF_SIZE="$KDTREE_LEAF_SIZE" KDTREE_KEY_PROFILE="$KDTREE_KEY_PROFILE" /out/app/RinhaFraud build-index /out/data/references.idx ; \
       fi \
    && test -s /out/data/references.idx

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble

WORKDIR /app
COPY --from=builder /out/app/RinhaFraud /usr/local/bin/rinha-fraud
COPY --from=builder /out/lb/rinha-lb /usr/local/bin/rinha-lb
COPY --from=builder /out/native/librinha_native.so /usr/local/lib/librinha_native.so
COPY --from=builder /out/native/rinha-native-api /usr/local/bin/rinha-native-api
COPY --from=builder /out/data /app/data

ENV LD_LIBRARY_PATH=/usr/local/lib
ENV BIND_ADDR=0.0.0.0:8080
ENV INDEX_PATH=/app/data/references.idx
ENV SERVER_MODE=raw
ENV TP_MIN_THREADS=64
ENV TP_MIN_IO_THREADS=4
ENV WORKERS=2
ENV EARLY_CANDIDATES=9800
ENV MIN_CANDIDATES=9800
ENV MAX_CANDIDATES=11000
ENV PROFILE_FASTPATH=0
ENV PROFILE_MIN_COUNT=15
ENV PROFILE_LEGIT_MIN_COUNT=5
ENV PROFILE_FRAUD_MIN_COUNT=15
ENV EXACT_FALLBACK=risky
ENV KDTREE_NATIVE=1

EXPOSE 8080
ENTRYPOINT ["rinha-fraud", "serve"]
