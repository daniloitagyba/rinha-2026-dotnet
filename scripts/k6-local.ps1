param(
    [ValidateSet("submission", "build")]
    [string]$Mode = "submission",
    [ValidateSet("default", "remote-ryzen", "remote-ryzen-hard")]
    [string]$RunnerPreset = "default",
    [string]$ProjectName = "rinha-local",
    [string]$K6Image = $env:K6_IMAGE,
    [string]$EarlyCandidates = $env:EARLY_CANDIDATES,
    [string]$MinCandidates = $env:MIN_CANDIDATES,
    [string]$MaxCandidates = $env:MAX_CANDIDATES,
    [string]$ProfileFastPath = $env:PROFILE_FASTPATH,
    [string]$ProfileMinCount = $env:PROFILE_MIN_COUNT,
    [string]$ProfileLegitMinCount = $env:PROFILE_LEGIT_MIN_COUNT,
    [string]$ProfileFraudMinCount = $env:PROFILE_FRAUD_MIN_COUNT,
    [string]$ExactFallback = $env:EXACT_FALLBACK,
    [string]$EarlyEdgeFallback = $env:EARLY_EDGE_FALLBACK,
    [string]$RiskyAmountMin = $env:RISKY_AMOUNT_MIN,
    [string]$RiskyAmountMax = $env:RISKY_AMOUNT_MAX,
    [string]$RiskyInstallmentsMin = $env:RISKY_INSTALLMENTS_MIN,
    [string]$RiskyInstallmentsMax = $env:RISKY_INSTALLMENTS_MAX,
    [string]$RiskyRatioMin = $env:RISKY_RATIO_MIN,
    [string]$RiskyKmHomeMin = $env:RISKY_KM_HOME_MIN,
    [string]$RiskyKmHomeMax = $env:RISKY_KM_HOME_MAX,
    [string]$RiskyTx24hMin = $env:RISKY_TX24H_MIN,
    [string]$RiskyTx24hMax = $env:RISKY_TX24H_MAX,
    [string]$RiskyMerchantAvgMin = $env:RISKY_MERCHANT_AVG_MIN,
    [string]$RiskyMerchantAvgMax = $env:RISKY_MERCHANT_AVG_MAX,
    [string]$RiskyCompact = $env:RISKY_COMPACT,
    [string]$RiskyFineBuckets = $env:RISKY_FINE_BUCKETS,
    [string]$RiskySimd = $env:RISKY_SIMD,
    [string]$RiskyNativeFine = $env:RISKY_NATIVE_FINE,
    [string]$BlockScan = $env:BLOCK_SCAN,
    [string]$SocketsMount = $env:SOCKETS_MOUNT,
    [string]$Workers = $env:WORKERS,
    [string]$ServerMode = $env:SERVER_MODE,
    [string]$IndexHugePages = $env:INDEX_HUGEPAGES,
    [string]$DotnetProcessorCount = $env:DOTNET_PROCESSOR_COUNT,
    [string]$DotnetGCHeapCount = $env:DOTNET_GCHeapCount,
    [string]$DotnetGCConserveMemory = $env:DOTNET_GCConserveMemory,
    [string]$DotnetEnableDiagnostics = $env:DOTNET_EnableDiagnostics,
    [string]$GcLatencyMode = $env:GC_LATENCY_MODE,
    [string]$ThreadPoolMinThreads = $env:TP_MIN_THREADS,
    [string]$ThreadPoolMinIoThreads = $env:TP_MIN_IO_THREADS,
    [string]$ThreadPoolMaxThreads = $env:TP_MAX_THREADS,
    [string]$ThreadPoolMaxIoThreads = $env:TP_MAX_IO_THREADS,
    [string]$KeepAliveRequests = $env:KEEP_ALIVE_REQUESTS,
    [string]$KeepAliveIdleMs = $env:KEEP_ALIVE_IDLE_MS,
    [string]$ApiCpu = $env:API_CPU,
    [string]$ApiMemory = $env:API_MEMORY,
    [string]$ApiCpuset = $env:API_CPUSET,
    [string]$Api1Cpuset = $env:API1_CPUSET,
    [string]$Api2Cpuset = $env:API2_CPUSET,
    [string]$LbCpu = $env:LB_CPU,
    [string]$LbMemory = $env:LB_MEMORY,
    [string]$LbCpuset = $env:LB_CPUSET,
    [string]$SubmissionComposeFile = $env:SUBMISSION_COMPOSE_FILE,
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
        if ([string]::IsNullOrWhiteSpace($ApiCpu)) {
            $ApiCpu = "0.300"
        }

        if ([string]::IsNullOrWhiteSpace($LbCpu)) {
            $LbCpu = "0.110"
        }
    }
    "remote-ryzen-hard" {
        if ([string]::IsNullOrWhiteSpace($ApiCpu)) {
            $ApiCpu = "0.300"
        }

        if ([string]::IsNullOrWhiteSpace($LbCpu)) {
            $LbCpu = "0.108"
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
    if ([string]::IsNullOrWhiteSpace($SubmissionComposeFile)) {
        $worktreeCompose = "C:\tmp\rinha-2026-submission\docker-compose.yml"
        if (Test-Path $worktreeCompose) {
            $SubmissionComposeFile = $worktreeCompose
        }
    }

    if ([string]::IsNullOrWhiteSpace($SubmissionComposeFile)) {
        $composeFile = Join-Path $root "submission/docker-compose.yml"
    } else {
        $composeFile = $SubmissionComposeFile
    }
} else {
    $composeFile = Join-Path $root "docker-compose.yml"
}

$originalComposeParallelLimit = $env:COMPOSE_PARALLEL_LIMIT
if ($Mode -eq "build" -and [string]::IsNullOrWhiteSpace($env:COMPOSE_PARALLEL_LIMIT)) {
    $env:COMPOSE_PARALLEL_LIMIT = "1"
}

$overrideFile = $null
$apiOverrides = [ordered]@{
    "EARLY_CANDIDATES" = $EarlyCandidates
    "MIN_CANDIDATES" = $MinCandidates
    "MAX_CANDIDATES" = $MaxCandidates
    "PROFILE_FASTPATH" = $ProfileFastPath
    "PROFILE_MIN_COUNT" = $ProfileMinCount
    "PROFILE_LEGIT_MIN_COUNT" = $ProfileLegitMinCount
    "PROFILE_FRAUD_MIN_COUNT" = $ProfileFraudMinCount
    "EXACT_FALLBACK" = $ExactFallback
    "EARLY_EDGE_FALLBACK" = $EarlyEdgeFallback
    "RISKY_AMOUNT_MIN" = $RiskyAmountMin
    "RISKY_AMOUNT_MAX" = $RiskyAmountMax
    "RISKY_INSTALLMENTS_MIN" = $RiskyInstallmentsMin
    "RISKY_INSTALLMENTS_MAX" = $RiskyInstallmentsMax
    "RISKY_RATIO_MIN" = $RiskyRatioMin
    "RISKY_KM_HOME_MIN" = $RiskyKmHomeMin
    "RISKY_KM_HOME_MAX" = $RiskyKmHomeMax
    "RISKY_TX24H_MIN" = $RiskyTx24hMin
    "RISKY_TX24H_MAX" = $RiskyTx24hMax
    "RISKY_MERCHANT_AVG_MIN" = $RiskyMerchantAvgMin
    "RISKY_MERCHANT_AVG_MAX" = $RiskyMerchantAvgMax
    "RISKY_COMPACT" = $RiskyCompact
    "RISKY_FINE_BUCKETS" = $RiskyFineBuckets
    "RISKY_SIMD" = $RiskySimd
    "RISKY_NATIVE_FINE" = $RiskyNativeFine
    "BLOCK_SCAN" = $BlockScan
    "WORKERS" = $Workers
    "SERVER_MODE" = $ServerMode
    "INDEX_HUGEPAGES" = $IndexHugePages
    "DOTNET_PROCESSOR_COUNT" = $DotnetProcessorCount
    "DOTNET_GCHeapCount" = $DotnetGCHeapCount
    "DOTNET_GCConserveMemory" = $DotnetGCConserveMemory
    "DOTNET_EnableDiagnostics" = $DotnetEnableDiagnostics
    "GC_LATENCY_MODE" = $GcLatencyMode
    "TP_MIN_THREADS" = $ThreadPoolMinThreads
    "TP_MIN_IO_THREADS" = $ThreadPoolMinIoThreads
    "TP_MAX_THREADS" = $ThreadPoolMaxThreads
    "TP_MAX_IO_THREADS" = $ThreadPoolMaxIoThreads
    "KEEP_ALIVE_REQUESTS" = $KeepAliveRequests
    "KEEP_ALIVE_IDLE_MS" = $KeepAliveIdleMs
}

$activeApiOverrides = @()
foreach ($item in $apiOverrides.GetEnumerator()) {
    if (-not [string]::IsNullOrWhiteSpace($item.Value)) {
        $activeApiOverrides += $item
    }
}

$originalSocketsMount = $env:SOCKETS_MOUNT
if ([string]::IsNullOrWhiteSpace($SocketsMount)) {
    Remove-Item Env:SOCKETS_MOUNT -ErrorAction SilentlyContinue
} else {
    $env:SOCKETS_MOUNT = $SocketsMount
}

$hasResourceOverrides =
    -not [string]::IsNullOrWhiteSpace($ApiCpu) -or
    -not [string]::IsNullOrWhiteSpace($ApiMemory) -or
    -not [string]::IsNullOrWhiteSpace($LbCpu) -or
    -not [string]::IsNullOrWhiteSpace($LbMemory)

$hasCpusetOverrides =
    -not [string]::IsNullOrWhiteSpace($ApiCpuset) -or
    -not [string]::IsNullOrWhiteSpace($Api1Cpuset) -or
    -not [string]::IsNullOrWhiteSpace($Api2Cpuset) -or
    -not [string]::IsNullOrWhiteSpace($LbCpuset)

if ($activeApiOverrides.Count -gt 0 -or $hasResourceOverrides -or $hasCpusetOverrides) {
    $overrideFile = Join-Path ([System.IO.Path]::GetTempPath()) "$ProjectName.override.yml"
    $lines = @("services:")
    if (-not [string]::IsNullOrWhiteSpace($LbCpu) -or -not [string]::IsNullOrWhiteSpace($LbMemory) -or -not [string]::IsNullOrWhiteSpace($LbCpuset)) {
        $lines += "  lb:"
        if (-not [string]::IsNullOrWhiteSpace($LbCpuset)) {
            $lines += "    cpuset: `"$LbCpuset`""
        }

        if (-not [string]::IsNullOrWhiteSpace($LbCpu) -or -not [string]::IsNullOrWhiteSpace($LbMemory)) {
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
    }

    if (-not [string]::IsNullOrWhiteSpace($SocketsMount)) {
        foreach ($service in @("lb", "api1", "api2")) {
            $lines += "  ${service}:"
            $lines += "    volumes:"
            $lines += "      - ${SocketsMount}"
        }
    }

    foreach ($service in @("api1", "api2")) {
        $serviceCpuset = $ApiCpuset
        if ($service -eq "api1" -and -not [string]::IsNullOrWhiteSpace($Api1Cpuset)) {
            $serviceCpuset = $Api1Cpuset
        }

        if ($service -eq "api2" -and -not [string]::IsNullOrWhiteSpace($Api2Cpuset)) {
            $serviceCpuset = $Api2Cpuset
        }

        $lines += "  ${service}:"
        if (-not [string]::IsNullOrWhiteSpace($serviceCpuset)) {
            $lines += "    cpuset: `"$serviceCpuset`""
        }

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
    try {
        Compose "down" "--remove-orphans" "-v"
    } catch {
        Write-Warning $_
    }

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
            Compose "down" "--remove-orphans" "-v"
        } catch {
            Write-Warning $_
        }
    }

    if ($overrideFile -and (Test-Path $overrideFile)) {
        Remove-Item -Path $overrideFile -Force
    }

    if ($null -eq $originalSocketsMount) {
        Remove-Item Env:SOCKETS_MOUNT -ErrorAction SilentlyContinue
    } else {
        $env:SOCKETS_MOUNT = $originalSocketsMount
    }

    if ($null -eq $originalComposeParallelLimit) {
        Remove-Item Env:COMPOSE_PARALLEL_LIMIT -ErrorAction SilentlyContinue
    } else {
        $env:COMPOSE_PARALLEL_LIMIT = $originalComposeParallelLimit
    }
}
