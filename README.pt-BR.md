# Fraud Score API

[English version](README.md)

Submissao da Rinha de Backend 2026 para score de fraude.

Este repositorio hoje e uma implementacao hibrida .NET/C. O projeto .NET ainda
controla o pipeline do modelo, o pre-processamento das referencias, os
diagnosticos, o self-test e o eval offline. O caminho publicado que atende o
benchmark roda em binarios C nativos.

## Resultado Atual

Execucao oficial publicada:

- issue: `#5616`
- p99: `0.98ms`
- score final: `6000`
- falsos positivos: `0`
- falsos negativos: `0`
- erros HTTP: `0`

## Arquitetura

A imagem competitiva contem tres binarios principais:

- `rinha-fraud`: CLI em .NET 10 Native AOT usado para `build-index`, `eval`,
  `self-test` e como servidor reserva
- `rinha-lb`: load balancer TCP em C
- `rinha-native-api`: API em C usada por `api1` e `api2` no compose submetido

Topologia em runtime:

- `lb` escuta na porta `9999`
- `lb` usa fd handoff por Unix sockets para distribuir conexoes TCP aceitas
- `api1` e `api2` executam `rinha-native-api`
- o indice binario de referencias fica embutido na imagem Docker
- `KDTREE_INDEX=1` ativa o caminho atual de busca exata por KD-tree

Para trafego de fraude, o load balancer apenas aceita e distribui conexoes. Ele
nao classifica transacoes e nao usa dados do payload relacionados a fraude. A
classificacao acontece no processo da API.

## Papel Do .NET

O .NET ainda e o nucleo organizador do repositorio:

- gera o indice a partir de `references.json.gz`
- escreve o binario `references.idx`
- mantem comandos de self-test e eval offline
- preserva o classificador, parser, vetorizador e diagnosticos originais em C#
- conduz o build Docker via `dotnet publish` com Native AOT

O runtime vencedor atual nao e uma API .NET pura. A descricao mais correta e:

```text
pipeline .NET 10 Native AOT + hot path nativo em C
```

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
- API nativa em C para o hot path do benchmark
- load balancer TCP em C com fd handoff
- respostas JSON pre-montadas para todos os valores possiveis de `fraud_score`
- logica de profile e fallback risky mantida como caminhos validados
- sem tabela de lookup por payload e sem logica de fraude no load balancer

## Estrutura

- `src/RinhaFraud/`: CLI .NET, builder do indice, eval, self-test e classificador original
- `src/native/`: API nativa e runtime nativo de classificacao/busca
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
- `KDTREE_INDEX`: habilita as secoes KD-tree no runtime nativo
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
