param(
    [switch]$RefreshData,
    [switch]$SkipK6,
    [switch]$SkipProfileCheck
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$drive = $root.Path.Substring(0, 1).ToLowerInvariant()
$rest = $root.Path.Substring(2).Replace("\", "/")
$wslRoot = "/mnt/$drive$rest"

$envParts = @()
if ($RefreshData) {
    $envParts += "REFRESH_DATA=1"
}

if ($SkipK6) {
    $envParts += "RUN_K6=0"
}

if ($SkipProfileCheck) {
    $envParts += "RUN_PROFILE_CHECK=0"
}

$prefix = ""
if ($envParts.Count -gt 0) {
    $prefix = ($envParts -join " ") + " "
}

wsl -d Ubuntu -- bash -lc "cd '$wslRoot' && ${prefix}sh scripts/validate-local.sh"
