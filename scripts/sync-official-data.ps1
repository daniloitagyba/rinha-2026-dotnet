param(
    [string]$Ref = $env:RINHA_REF,
    [switch]$Force,
    [switch]$References
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Ref)) {
    $Ref = "main"
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$testDir = Join-Path $root "test"
$resourcesDir = Join-Path $root "resources"
New-Item -ItemType Directory -Force -Path $testDir, $resourcesDir | Out-Null

function Download-OfficialFile {
    param(
        [string]$Path,
        [string]$OutputPath
    )

    if ((Test-Path $OutputPath) -and -not $Force) {
        Write-Host "exists $OutputPath"
        return
    }

    $url = "https://raw.githubusercontent.com/zanfranceschi/rinha-de-backend-2026/$Ref/$Path"
    Write-Host "download $url"
    Invoke-WebRequest -Uri $url -OutFile $OutputPath
}

Download-OfficialFile "test/test-data.json" (Join-Path $testDir "test-data.json")

if ($References) {
    Download-OfficialFile "resources/references.json.gz" (Join-Path $resourcesDir "references.json.gz")
}
