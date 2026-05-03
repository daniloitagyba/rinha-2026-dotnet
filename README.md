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

## Teste local proximo da engine

O teste mais comparavel com o resultado remoto roda Docker Compose + k6, usando a
mesma massa oficial e gerando `test/results.json` no mesmo formato da engine.

No Windows/PowerShell:

```powershell
.\scripts\k6-local.ps1
```

No Linux/WSL:

```sh
sh scripts/k6-local.sh
```

Por padrao o script usa `submission/docker-compose.yml`, ou seja, testa a mesma
imagem/tag submetida. Para testar o codigo local com build da imagem:

```powershell
.\scripts\k6-local.ps1 -Mode build
```

```sh
MODE=build sh scripts/k6-local.sh
```

Os defaults do `test/rinha-test.js` seguem o teste publico oficial: rampa de
120s ate 900 req/s, `maxVUs=250`, timeout de `2001ms` e formula de pontuacao da
documentacao.

Para validar o encadeamento sem esperar a rampa completa:

```powershell
$env:TARGET_RATE = "10"; $env:RAMP_DURATION = "5s"; .\scripts\k6-local.ps1
```

Para testar uma combinacao sem alterar a submissao:

```powershell
.\scripts\k6-local.ps1 -EarlyCandidates 18000 -MinCandidates 18000 -MaxCandidates 36000 -Workers 1
```

No Windows com Ryzen, use primeiro o `-Mode submission` puro. Depois da troca
para TCP mode, os overrides de CPU do Docker Desktop ficaram pouco previsiveis
nesta maquina: eles servem para exploracao, mas nao reproduzem o remoto oficial
de forma estavel. O preset abaixo ficou apenas como atalho para esse modo de
comparacao local:

```powershell
.\scripts\k6-local.ps1 -RunnerPreset remote-ryzen
```

No Linux/WSL:

```sh
RUNNER_PRESET=remote-ryzen sh scripts/k6-local.sh
```

O perfil padrao da submissao usa `EXACT_FALLBACK=risky`, `WORKERS=1` e
`EARLY_CANDIDATES/MIN_CANDIDATES/MAX_CANDIDATES=16200/16200/32400`: a busca aproximada
continua no caminho quente, mas apenas os perfis de fronteira conhecidos executam
fallback exato para zerar falso positivo/falso negativo na massa oficial local.
Para comparar:

```powershell
$env:EXACT_FALLBACK = "off"; .\scripts\k6-local.ps1 -Mode build
$env:EXACT_FALLBACK = "profile"; .\scripts\k6-local.ps1 -Mode build
```

`off` prioriza p99 e aceita uma pequena taxa de erro. `profile` roda KNN exato
em todo miss do fast path e e o modo mais conservador, mas reduz bastante o
throughput sob carga.

Para submissao, publique a imagem `linux/amd64` com `/app/data/references.idx` incluido e use os arquivos da pasta `submission`.

## Publicacao da imagem

A imagem `ghcr.io/daniloitagyba/rinha-2026-dotnet-tcp:latest` e publicada pela workflow
`Publish image`. Depois da primeira publicacao no GHCR, marque o pacote como publico em
Package settings > Danger Zone > Change visibility > Public. O teste oficial precisa
baixar essa imagem sem autenticacao.
