# Fraud Score API

[English version](README.md)

Submissao da Rinha de Backend 2026 para score de fraude.

Este repositorio e uma implementacao hibrida .NET/C. O runtime submetido mantem
o processo da API em .NET 10 Native AOT e chama um classificador KD-tree nativo
em C via P/Invoke para a busca critica de vizinhos mais proximos.

## Arquitetura

A imagem competitiva contem tres componentes principais de runtime:

- `rinha-fraud`: CLI e servidor HTTP em .NET 10 Native AOT usado pelos servicos
  `api1` e `api2` submetidos
- `rinha-lb`: load balancer TCP em C
- `librinha_native.so`: classificador nativo em C carregado pelo servidor .NET
  via P/Invoke

Topologia em runtime:

- `lb` escuta na porta `9999`
- `lb` usa fd handoff por Unix sockets para distribuir conexoes TCP aceitas
- `api1` e `api2` executam `rinha-fraud serve`
- o indice binario de referencias fica embutido na imagem Docker
- `KDTREE_NATIVE=1` ativa a busca exata por KD-tree nativa dentro do processo
  da API .NET

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
- API .NET Native AOT para o hot path submetido
- busca KD-tree nativa em C chamada via P/Invoke
- load balancer TCP em C com fd handoff
- respostas JSON pre-montadas para todos os valores possiveis de `fraud_score`
- busca KD-tree usada como caminho de acuracia para requisicoes submetidas
- logica de profile e fallback risky mantida para experimentos controlados
- sem tabela de lookup por payload e sem logica de fraude no load balancer

## Estrutura

- `src/RinhaFraud/`: API .NET, CLI, builder do indice, eval, self-test e integracao do classificador
- `src/native/`: runtime nativo de classificacao/busca
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

Verificar se as referencias oficiais mudaram e rodar o gate de refresh apenas
quando necessario:

```sh
sh scripts/reference-refresh.sh
```
