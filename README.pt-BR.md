# Fraud Score API

[English version](README.md)

API HTTP para calcular risco de fraude a partir de um payload transacional.

## Arquitetura

- load balancer TCP proprio em C com `epoll`
- duas instancias da API em .NET Native AOT
- servidor HTTP raw, sem Kestrel no caminho principal
- comunicacao interna por Unix socket
- indice binario de referencias incluido na imagem Docker

O load balancer apenas aceita conexoes, escolhe uma API e copia bytes entre
cliente e backend. A classificacao acontece somente nas APIs.

## Classificacao

O payload e convertido em um vetor quantizado de 14 dimensoes. A API busca os
5 vizinhos mais proximos no indice de referencias e calcula:

- `fraud_score = vizinhos_fraudulentos / 5`
- `approved = fraud_score < 0.6`

O indice fica pre-processado em formato binario para reduzir custo de startup e
evitar parsing de JSON em runtime.

## Endpoints

- `GET /ready`: indica que a instancia esta pronta
- `POST /fraud-score`: recebe o payload transacional e retorna a decisao

Resposta de classificacao:

```json
{"approved":true,"fraud_score":0.0}
```

## Decisoes De Implementacao

- vetorizacao sem alocacoes no caminho quente
- indice agrupado por buckets para reduzir a busca inicial
- fast path por perfil quando a decisao local e estavel
- fallback exato restrito ao subconjunto de referencias de maior risco
- fallback compacto com SIMD (`AVX2` quando disponivel, `SSE2` como reserva)
- respostas HTTP pre-montadas para todos os valores possiveis de `fraud_score`

## Estrutura

- `src/RinhaFraud/`: API, parser, vetorizacao e indice de classificacao
- `src/lb/`: load balancer TCP
- `scripts/`: scripts locais de build, validacao e carga
- `resources/`: referencias usadas para montar o indice binario
- `test/`: harness local de validacao

## Configuracao

Variaveis principais das APIs:

- `BIND_ADDR`: endereco de escuta, normalmente `unix:/sockets/api1.sock`
- `INDEX_PATH`: caminho do indice binario
- `SERVER_MODE`: modo do servidor HTTP raw
- `WORKERS`: quantidade de workers por instancia
- `TP_MIN_THREADS`: minimo do ThreadPool
- `EARLY_CANDIDATES`, `MIN_CANDIDATES`, `MAX_CANDIDATES`: limites da busca
- `PROFILE_FASTPATH`: habilita ou desabilita o fast path por perfil
- `EXACT_FALLBACK`: modo do fallback exato
- `RISKY_SIMD`: habilita ou desabilita SIMD no fallback de risco

## Comandos

Gerar o indice:

```powershell
scripts/build-index.sh resources/references.json.gz data/references.idx
```

Rodar self-test:

```powershell
dotnet run -c Release --project src/RinhaFraud/RinhaFraud.csproj -- self-test
```

Build da imagem local:

```powershell
docker compose build
```
