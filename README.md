# Rinha de Backend 2026 - .NET

Implementacao em .NET 10 Native AOT para a Rinha de Backend 2026.

## Arquitetura

- HAProxy em `mode tcp`
- duas instancias da API em Kestrel
- comunicacao interna por Unix socket
- indice vetorial binario gerado a partir de `references.json.gz`
- Native AOT, sem dependencias externas de runtime

## Estrutura

- `src/RinhaFraud/`: aplicacao e motor de score
- `submission/`: arquivos usados na submissao oficial
- `scripts/`: automacao local de build, benchmark e utilitarios
- `test/`: massa e harness local

## Comandos essenciais

```powershell
dotnet run -c Release --project src/RinhaFraud/RinhaFraud.csproj -- self-test
.\scripts\k6-local.ps1
.\scripts\k6-local.ps1 -Mode build
```

O modo padrao testa a mesma configuracao da `submission`. O modo `build` monta
a imagem a partir do codigo local antes do benchmark.

## Submissao

Use os arquivos de `submission/` e publique uma imagem `linux/amd64` imutavel,
com o indice incluido na imagem.

## Notas locais

Detalhes operacionais, combinacoes de benchmark e observacoes de calibracao
ficam em `AGENTS.md`, mantido localmente e ignorado pelo git.
