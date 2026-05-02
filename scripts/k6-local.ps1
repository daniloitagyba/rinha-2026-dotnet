param(
    [ValidateSet("submission", "build")]
    [string]$Mode = "submission",
    [ValidateSet("default", "remote-ryzen")]
    [string]$RunnerPreset = "default",
    [string]$ProjectName = "rinha-local",
    [string]$K6Image = $env:K6_IMAGE,
    [string]$EarlyCandidates = $env:EARLY_CANDIDATES,
    [string]$MinCandidates = $env:MIN_CANDIDATES,
    [string]$MaxCandidates = $env:MAX_CANDIDATES,
    [string]$ProfileFastPath = $env:PROFILE_FASTPATH,
    [string]$ProfileMinCount = $env:PROFILE_MIN_COUNT,
    [string]$ExactFallback = $env:EXACT_FALLBACK,
    [string]$Workers = $env:WORKERS,
    [string]$ServerMode = $env:SERVER_MODE,
    [string]$ThreadPoolMinThreads = $env:TP_MIN_THREADS,
    [string]$KeepAliveRequests = $env:KEEP_ALIVE_REQUESTS,
    [string]$KeepAliveIdleMs = $env:KEEP_ALIVE_IDLE_MS,
    [string]$ApiCpu = $env:API_CPU,
    [string]$ApiMemory = $env:API_MEMORY,
    [string]$LbCpu = $env:LB_CPU,
    [string]$LbMemory = $env:LB_MEMORY,
    [switch]$KeepServices,
    [switch]$RefreshData,
    [switch]$Pull
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($K6Image)) {
    $K6Image = "grafana/k6:latest"
}

switch ($RunnerPreset) {
    "remote-ryzen" {
        if ([string]::IsNullOrWhiteSpace($LbCpu)) {
            $LbCpu = "0.12"
        }
    }
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$testDir = Join-Path $root "test"
$testData = Join-Path $testDir "test-data.json"

if ($RefreshData -or -not (Test-Path $testData)) {
    & (Join-Path $PSScriptRoot "sync-official-data.ps1") -Force:$RefreshData
}

if ($Mode -eq "submission") {
    $composeFile = Join-Path $root "submission/docker-compose.yml"
} else {
    $composeFile = Join-Path $root "docker-compose.yml"
}

$overrideFile = $null
$apiOverrides = [ordered]@{
    "EARLY_CANDIDATES" = $EarlyCandidates
    "MIN_CANDIDATES" = $MinCandidates
    "MAX_CANDIDATES" = $MaxCandidates
    "PROFILE_FASTPATH" = $ProfileFastPath
    "PROFILE_MIN_COUNT" = $ProfileMinCount
    "EXACT_FALLBACK" = $ExactFallback
    "WORKERS" = $Workers
    "SERVER_MODE" = $ServerMode
    "TP_MIN_THREADS" = $ThreadPoolMinThreads
    "KEEP_ALIVE_REQUESTS" = $KeepAliveRequests
    "KEEP_ALIVE_IDLE_MS" = $KeepAliveIdleMs
}

$activeApiOverrides = @()
foreach ($item in $apiOverrides.GetEnumerator()) {
    if (-not [string]::IsNullOrWhiteSpace($item.Value)) {
        $activeApiOverrides += $item
    }
}

$hasResourceOverrides =
    -not [string]::IsNullOrWhiteSpace($ApiCpu) -or
    -not [string]::IsNullOrWhiteSpace($ApiMemory) -or
    -not [string]::IsNullOrWhiteSpace($LbCpu) -or
    -not [string]::IsNullOrWhiteSpace($LbMemory)

if ($activeApiOverrides.Count -gt 0 -or $hasResourceOverrides) {
    $overrideFile = Join-Path ([System.IO.Path]::GetTempPath()) "$ProjectName.override.yml"
    $lines = @("services:")
    if (-not [string]::IsNullOrWhiteSpace($LbCpu) -or -not [string]::IsNullOrWhiteSpace($LbMemory)) {
        $lines += "  lb:"
        $lines += "    deploy:"
        $lines += "      resources:"
        $lines += "        limits:"
        if (-not [string]::IsNullOrWhiteSpace($LbCpu)) {
            $lines += "          cpus: `"$LbCpu`""
        }

        if (-not [string]::IsNullOrWhiteSpace($LbMemory)) {
            $lines += "          memory: `"$LbMemory`""
        }
    }

    foreach ($service in @("api1", "api2")) {
        $lines += "  ${service}:"
        $lines += "    environment:"
        foreach ($item in $activeApiOverrides) {
            $lines += "      $($item.Key): `"$($item.Value)`""
        }

        if (-not [string]::IsNullOrWhiteSpace($ApiCpu) -or -not [string]::IsNullOrWhiteSpace($ApiMemory)) {
            $lines += "    deploy:"
            $lines += "      resources:"
            $lines += "        limits:"
            if (-not [string]::IsNullOrWhiteSpace($ApiCpu)) {
                $lines += "          cpus: `"$ApiCpu`""
            }

            if (-not [string]::IsNullOrWhiteSpace($ApiMemory)) {
                $lines += "          memory: `"$ApiMemory`""
            }
        }
    }

    Set-Content -Path $overrideFile -Value ($lines -join [Environment]::NewLine) -Encoding ascii
}

function Compose {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$ComposeArgs
    )

    $args = @("compose", "-p", $ProjectName, "-f", $composeFile)
    if ($overrideFile) {
        $args += @("-f", $overrideFile)
    }

    $args += $ComposeArgs
    & docker @args
}

try {
    if ($Pull -or $Mode -eq "submission") {
        Compose "pull"
    }

    if ($Mode -eq "build") {
        Compose "up" "-d" "--build" "--remove-orphans"
    } else {
        Compose "up" "-d" "--remove-orphans"
    }

    $ready = $false
    for ($i = 0; $i -lt 90; $i++) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:9999/ready" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                $ready = $true
                break
            }
        } catch {
            Start-Sleep -Seconds 1
        }
    }

    if (-not $ready) {
        throw "backend did not become ready on http://127.0.0.1:9999/ready"
    }

    $mount = "${testDir}:/scripts"
    $dockerArgs = @(
        "run", "--rm",
        "--network", "${ProjectName}_default",
        "-e", "BASE_URL=http://lb:9999",
        "-e", "RESULTS_PATH=/scripts/results.json",
        "-e", "TARGET_RATE",
        "-e", "RAMP_DURATION",
        "-e", "START_RATE",
        "-e", "PRE_ALLOCATED_VUS",
        "-e", "MAX_VUS",
        "-e", "REQUEST_TIMEOUT",
        "-v", $mount,
        $K6Image,
        "run", "/scripts/rinha-test.js"
    )

    & docker @dockerArgs
} finally {
    if (-not $KeepServices) {
        try {
            Compose "down" "--remove-orphans"
        } catch {
            Write-Warning $_
        }
    }

    if ($overrideFile -and (Test-Path $overrideFile)) {
        Remove-Item -Path $overrideFile -Force
    }
}
