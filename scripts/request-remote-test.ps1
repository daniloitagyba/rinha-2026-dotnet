param(
    [string]$Submission = "itagyba-dotnet",
    [string]$Participant = "daniloitagyba",
    [string]$Title,
    [string]$Body,
    [string]$Repo = "zanfranceschi/rinha-de-backend-2026",
    [string]$ResultsUrl = "https://raw.githubusercontent.com/arinhadebackend/arinhadebackend.github.io/2026-preview/results-preview.json",
    [string]$OutputPath = "test/remote-result.json",
    [int]$TimeoutMinutes = 45,
    [int]$PollSeconds = 20,
    [string]$IssueUrl,
    [string]$Token,
    [string]$LocalTokenDir,
    [switch]$NoCreate,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Title)) {
    $Title = "rinha/test $Submission"
}

if ([string]::IsNullOrWhiteSpace($Body)) {
    $Body = $Title
}

function ConvertFrom-SecureStringToText {
    param([securestring]$SecureText)

    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureText)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

function Get-LocalTokenDir {
    param([string]$ExplicitDir)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitDir)) {
        return $ExplicitDir
    }

    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        return $null
    }

    return Join-Path $env:LOCALAPPDATA "rinha-2026-dotnet\secrets"
}

function Get-LocalGitHubToken {
    param([string]$TokenDirectory)

    $dir = Get-LocalTokenDir -ExplicitDir $TokenDirectory
    if ([string]::IsNullOrWhiteSpace($dir)) {
        return $null
    }

    $keyPath = Join-Path $dir "github-token.key"
    $tokenPath = Join-Path $dir "github-token.enc"
    if (-not (Test-Path $keyPath) -or -not (Test-Path $tokenPath)) {
        return $null
    }

    try {
        $key = [IO.File]::ReadAllBytes($keyPath)
        $encrypted = (Get-Content -Raw -Path $tokenPath).Trim()
        if ([string]::IsNullOrWhiteSpace($encrypted)) {
            return $null
        }

        $secure = ConvertTo-SecureString $encrypted -Key $key
        return ConvertFrom-SecureStringToText -SecureText $secure
    } catch {
        return $null
    }
}

function Get-GitHubToken {
    param(
        [string]$ExplicitToken,
        [string]$TokenDirectory
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitToken)) {
        return $ExplicitToken
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        return $env:GITHUB_TOKEN
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        return $env:GH_TOKEN
    }

    $localToken = Get-LocalGitHubToken -TokenDirectory $TokenDirectory
    if (-not [string]::IsNullOrWhiteSpace($localToken)) {
        return $localToken
    }

    return $null
}

function New-GitHubHeaders {
    param([string]$AuthToken)

    $headers = @{
        "Accept" = "application/vnd.github+json"
        "User-Agent" = "rinha-dotnet-remote-test"
        "X-GitHub-Api-Version" = "2022-11-28"
    }

    if (-not [string]::IsNullOrWhiteSpace($AuthToken)) {
        $headers["Authorization"] = "Bearer $AuthToken"
    }

    return $headers
}

function ConvertTo-PrefilledIssueUrl {
    param(
        [string]$Repository,
        [string]$IssueTitle,
        [string]$IssueBody
    )

    $encodedTitle = [System.Uri]::EscapeDataString($IssueTitle)
    $encodedBody = [System.Uri]::EscapeDataString($IssueBody)
    return "https://github.com/$Repository/issues/new?title=$encodedTitle&body=$encodedBody"
}

function Get-IssueNumberFromUrl {
    param([string]$Url)

    if ($Url -notmatch "/issues/(\d+)$") {
        throw "IssueUrl invalida: $Url"
    }

    return [int]$Matches[1]
}

function Get-ResultForIssue {
    param(
        [string]$SourceUrl,
        [string]$ExpectedIssueUrl,
        [string]$ExpectedParticipant,
        [string]$ExpectedSubmission
    )

    $cacheBuster = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $separator = if ($SourceUrl.Contains("?")) { "&" } else { "?" }
    $results = Invoke-RestMethod -Method Get -Uri "$SourceUrl${separator}t=$cacheBuster" -Headers @{
        "User-Agent" = "rinha-dotnet-remote-test"
        "Cache-Control" = "no-cache"
    }

    $participantNode = $results.PSObject.Properties[$ExpectedParticipant]
    if ($null -eq $participantNode) {
        return $null
    }

    $submissionNode = $participantNode.Value.PSObject.Properties[$ExpectedSubmission]
    if ($null -eq $submissionNode) {
        return $null
    }

    $result = $submissionNode.Value
    if ($result.issue_url -ne $ExpectedIssueUrl) {
        return $null
    }

    return $result
}

$authToken = Get-GitHubToken -ExplicitToken $Token -TokenDirectory $LocalTokenDir
$headers = New-GitHubHeaders -AuthToken $authToken

if ($DryRun) {
    ConvertTo-PrefilledIssueUrl -Repository $Repo -IssueTitle $Title -IssueBody $Body
    exit 0
}

if ($NoCreate -and [string]::IsNullOrWhiteSpace($IssueUrl)) {
    throw "Use -IssueUrl junto com -NoCreate para aguardar uma issue ja criada."
}

if (-not $NoCreate) {
    if ([string]::IsNullOrWhiteSpace($authToken)) {
        $url = ConvertTo-PrefilledIssueUrl -Repository $Repo -IssueTitle $Title -IssueBody $Body
        throw "Token GitHub ausente. Defina GITHUB_TOKEN/GH_TOKEN ou use o link manual: $url"
    }

    $payload = @{
        title = $Title
        body = $Body
    } | ConvertTo-Json

    $issue = Invoke-RestMethod `
        -Method Post `
        -Uri "https://api.github.com/repos/$Repo/issues" `
        -Headers $headers `
        -ContentType "application/json" `
        -Body $payload

    $IssueUrl = $issue.html_url
    Write-Host "Issue criada: $IssueUrl"
}

$issueNumber = Get-IssueNumberFromUrl -Url $IssueUrl
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$lastState = "unknown"

while ((Get-Date) -lt $deadline) {
    $issue = Invoke-RestMethod `
        -Method Get `
        -Uri "https://api.github.com/repos/$Repo/issues/$issueNumber" `
        -Headers $headers

    $lastState = $issue.state
    $result = Get-ResultForIssue `
        -SourceUrl $ResultsUrl `
        -ExpectedIssueUrl $IssueUrl `
        -ExpectedParticipant $Participant `
        -ExpectedSubmission $Submission

    if ($null -ne $result) {
        $out = [ordered]@{
            "repo-url" = $result.repo_url
            "issue-url" = $result.issue_url
            "timestamp" = $result.timestamp
            "test-results" = [ordered]@{
                expected = $result.expected
                p99 = $result.p99
                scoring = $result.scoring
            }
        }

        $parent = Split-Path -Parent $OutputPath
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }

        $out | ConvertTo-Json -Depth 32 | Set-Content -Path $OutputPath -Encoding utf8
        Write-Host "Resultado capturado em $OutputPath"
        $out | ConvertTo-Json -Depth 32
        exit 0
    }

    $updated = if ($issue.updated_at) { $issue.updated_at } else { "-" }
    Write-Host "Aguardando resultado remoto... issue=#$issueNumber state=$lastState updated=$updated"
    Start-Sleep -Seconds $PollSeconds
}

throw "Timeout aguardando resultado remoto da issue $IssueUrl. Ultimo estado: $lastState"
