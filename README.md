# Rinha de Backend 2026 - .NET

Implementacao em .NET 10 Native AOT com HAProxy e indice vetorial customizado.

## Plano de implementacao

1. Base competitiva: .NET 10 LTS, C# sem dependencias externas, Native AOT e imagem final sem runtime .NET.
2. Entrada HTTP: servidor TCP/HTTP proprio com `/ready` e `/fraud-score`, retornando JSON valido mesmo em erro de parse.
3. Vetorizacao: parser especifico do payload oficial, normalizacao das 14 dimensoes e quantizacao `int16` em escala 10000.
4. Indice: build offline/startup de `references.json.gz` para `references.idx` com vetores, labels e buckets em arquivo binario.
5. Busca: candidatos por buckets vizinhos e top-5 exato por distancia euclidiana quadratica dentro dos candidatos.
6. Infra: HAProxy apenas com round-robin e health check, duas APIs, limite total de 1 CPU e 350 MB.
7. Validacao: `self-test`, smoke com exemplos, `eval` offline com `test-data.json` e k6 via compose.

## Comandos

```sh
dotnet run -c Release --project src/RinhaFraud/RinhaFraud.csproj -- self-test
scripts/smoke-local.sh
scripts/build-index.sh resources/references.json.gz data/references.idx
INDEX_PATH=data/references.idx dotnet run -c Release --project src/RinhaFraud/RinhaFraud.csproj -- eval test/test-data.json
```

Para submissao, publique a imagem `linux/amd64` com `/app/data/references.idx` incluido e use os arquivos da pasta `submission`.

## Publicacao da imagem

A imagem `ghcr.io/daniloitagyba/rinha-2026-dotnet:latest` e publicada pela workflow
`Publish image`. Depois da primeira publicacao no GHCR, marque o pacote como publico em
Package settings > Danger Zone > Change visibility > Public. O teste oficial precisa
baixar essa imagem sem autenticacao.
