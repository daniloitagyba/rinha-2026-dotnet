# Rinha de Backend 2026 - .NET

Solucao em .NET Native AOT para a Rinha de Backend 2026.

## Arquitetura

- load balancer TCP proprio em C com `epoll`
- o load balancer apenas distribui conexoes, sem logica de fraude
- duas APIs .NET Native AOT com HTTP raw
- comunicacao interna por Unix socket
- indice binario incluido na imagem Docker

## Estrutura

- `src/RinhaFraud/`: API e motor de classificacao
- `src/lb/`: load balancer TCP
- `submission/`: compose usado na submissao
- `scripts/`: scripts locais de build e benchmark
- `test/`: harness k6 e massa publica de teste

## Rodar localmente

```powershell
.\scripts\k6-local.ps1 -Mode submission
.\scripts\k6-local.ps1 -Mode build
```

`submission` testa a imagem/tag publicada. `build` recompila a imagem local
antes do benchmark.

## Submissao

A branch `submission` aponta para uma imagem GHCR `linux/amd64` com tag
imutavel do commit.

## Notas

Detalhes de calibracao e operacao local ficam em `AGENTS.md`, ignorado pelo git.
