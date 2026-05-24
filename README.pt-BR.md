# Fraud Score API

[English version](README.md)

Submissao da Rinha de Backend 2026 para score de fraude.

Este repositorio e uma implementacao hibrida .NET/C. A submissao competitiva
mantem o CLI .NET 10 Native AOT como builder do indice, avaliador e API de
fallback, enquanto o processo de API atual orientado a latencia e o runtime
nativo em C.

## Arquitetura

A imagem competitiva contem tres componentes principais de runtime:

- `rinha-fraud`: CLI .NET 10 Native AOT, builder de indice, avaliador e
  servidor HTTP de fallback
- `rinha-native-api`: API HTTP/fd-handoff nativa em C usada por `api1` e
  `api2` na submissao
- `rinha-lb`: load balancer TCP em C
- `librinha_native.so`: classificador nativo compartilhado pelos runtimes

Topologia em runtime:

- `lb` escuta na porta `9999`
- `lb` usa fd handoff por Unix sockets para distribuir conexoes TCP aceitas
- `api1` e `api2` executam `rinha-native-api`
- o indice binario de referencias fica embutido na imagem Docker
- `KDTREE_INDEX=1` ativa a busca exata por KD-tree particionado na API nativa

Para trafego de fraude, o load balancer apenas aceita e distribui conexoes. Ele
nao classifica transacoes e nao usa dados do payload relacionados a fraude. A
classificacao acontece no processo da API.

## Classificacao

O payload e convertido em um vetor quantizado. A API busca os 5 vizinhos mais
proximos no indice de referencias e calcula:

- `fraud_score = vizinhos_fraudulentos / 5`
- `approved = fraud_score < 0.6`

O indice e pre-processado em formato binario antes da imagem ser construida. O
indice publicado atual inclui secoes de KD-tree particionado, entao a busca em
runtime nao precisa varrer todo o conjunto de referencias a cada requisicao.

## Endpoints

- `GET /ready`: healthcheck
- `POST /fraud-score`: classificacao da transacao

Resposta de classificacao:

```json
{"approved":true,"fraud_score":0.0}
```

## Decisoes De Implementacao

- indice binario pre-processado dentro da imagem Docker
- busca exata por KD-tree particionado no baseline publico atual
- API nativa em C para o hot path submetido
- .NET Native AOT mantido para build, eval, self-test e API de fallback
- load balancer TCP em C com fd handoff
- respostas JSON pre-montadas para todos os valores possiveis de `fraud_score`
- busca KD-tree usada como caminho de acuracia para requisicoes submetidas
- logica de profile e fallback risky mantida para experimentos controlados
- sem tabela de lookup por payload e sem logica de fraude no load balancer

## Estrutura

- `src/RinhaFraud/`: API .NET, CLI, builder do indice, eval, self-test e integracao do classificador
- `src/native/`: runtime nativo de classificacao/busca e API nativa submetida
- `src/lb/`: load balancer TCP
- `scripts/`: scripts locais de build, validacao, release e carga
- `resources/`: referencias usadas para montar o indice binario
- `test/`: harness local de validacao e snapshots de resultado remoto
- `docker-compose.yml`: topologia local de build e benchmark
- branch `submission`: compose oficial e metadata usados pelo bot

## Configuracao

Variaveis principais de runtime:

- `BIND_ADDR`: endereco de escuta ou fd handoff
- `INDEX_PATH`: caminho do indice binario
- `KDTREE_INDEX`: habilita as secoes KD-tree no runtime da API nativa
- `KDTREE_NATIVE`: habilita a busca KD-tree pela biblioteca nativa a partir do .NET
- `WORKERS`: quantidade de workers por instancia de API
- `EARLY_CANDIDATES`, `MIN_CANDIDATES`, `MAX_CANDIDATES`: limites de busca para caminhos sem KD
- `PROFILE_FASTPATH`: habilita o fast path por perfil
- `EXACT_FALLBACK`: modo do fallback exato
- `RISKY_NATIVE_FINE`: habilita o caminho nativo de fine fallback
- `LB_MODE`: modo do load balancer, atualmente `fdpass`

## Comandos

Rodar self-test:

```powershell
dotnet run -c Release --project src/RinhaFraud/RinhaFraud.csproj -- self-test
```

Build da imagem local:

```powershell
docker compose build
```

Rodar benchmark local proximo do oficial:

```powershell
.\scripts\k6-local.ps1 -Mode build
```

Rodar o mesmo caminho pelo WSL/Linux:

```sh
MODE=build sh scripts/k6-local.sh
```

Rodar o gate local completo antes de publicar:

```sh
sh scripts/validate-local.sh
```

Pelo PowerShell:

```powershell
.\scripts\validate-local.ps1
```
