param(
    [string]$Address,
    [string]$ApiKey = $env:ZERION_API_KEY,
    [string]$BaseUrl = "https://api.zerion.io/v1",
    [int]$PageSize = 5,
    [switch]$IncludeSubscriptions,
    [switch]$IncludeChains,
    [ValidateSet("base64", "raw")]
    [string]$AuthMode = "base64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Net.Http

function Read-DotEnvValue {
    param(
        [string]$Path,
        [string]$KeyName
    )

    if (-not (Test-Path -Path $Path)) {
        return $null
    }

    foreach ($line in Get-Content -Path $Path) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) {
            continue
        }

        $parts = $trimmed.Split("=", 2)
        if ($parts.Count -ne 2) {
            continue
        }

        $key = $parts[0].Trim()
        if ($key -ne $KeyName) {
            continue
        }

        $value = $parts[1].Trim()
        if ($value.StartsWith('"') -and $value.EndsWith('"')) {
            $value = $value.Trim('"')
        } elseif ($value.StartsWith("'") -and $value.EndsWith("'")) {
            $value = $value.Trim("'")
        }

        return $value
    }

    return $null
}

if ([string]::IsNullOrWhiteSpace($Address)) {
    throw "Address is required. Use -Address 0x..."
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $envPath = Join-Path $scriptRoot "..\.env"
    $ApiKey = Read-DotEnvValue -Path $envPath -KeyName "ZERION_API_KEY"
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw "ApiKey is required. Pass -ApiKey or set ZERION_API_KEY env var."
}

function New-AuthHeader {
    param([string]$Mode, [string]$Key)
    switch ($Mode) {
        "raw" {
            return "Basic $Key"
        }
        "base64" {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes("${Key}:")
            $b64 = [Convert]::ToBase64String($bytes)
            return "Basic $b64"
        }
        default {
            return "Basic $Key"
        }
    }
}

$authHeader = New-AuthHeader -Mode $AuthMode -Key $ApiKey

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.DefaultRequestHeaders.Add("Accept", "application/json")
$client.DefaultRequestHeaders.Add("Authorization", $authHeader)

function Test-Url {
    param([string]$Url)
    try {
        $resp = $client.GetAsync($Url).GetAwaiter().GetResult()
        $status = [int]$resp.StatusCode
        $location = $null
        if ($resp.Headers.Location) {
            $location = $resp.Headers.Location.ToString()
        }
        Write-Host "status=$status location=$location"
    } catch {
        Write-Host "status=ERROR message=$($_.Exception.Message)"
    }
}

$txQuery = "?currency=usd&page[size]=$PageSize"
$targets = @(
    @{ Name = "transactions"; WithSlash = "/wallets/$Address/transactions/$txQuery"; NoSlash = "/wallets/$Address/transactions$txQuery" },
    @{ Name = "positions"; WithSlash = "/wallets/$Address/positions/?currency=usd"; NoSlash = "/wallets/$Address/positions?currency=usd" },
    @{ Name = "portfolio"; WithSlash = "/wallets/$Address/portfolio?currency=usd"; NoSlash = "/wallets/$Address/portfolio?currency=usd" }
)

if ($IncludeChains.IsPresent) {
    $targets += @{ Name = "chains"; WithSlash = "/chains/"; NoSlash = "/chains" }
}

if ($IncludeSubscriptions.IsPresent) {
    $targets += @{ Name = "tx-subscriptions"; WithSlash = "/tx-subscriptions/"; NoSlash = "/tx-subscriptions" }
}

foreach ($t in $targets) {
    Write-Host ""
    Write-Host "=== $($t.Name) ==="
    $urlSlash = "$BaseUrl$($t.WithSlash)"
    $urlNoSlash = "$BaseUrl$($t.NoSlash)"
    Write-Host "with slash: $urlSlash"
    Test-Url -Url $urlSlash
    Write-Host "no slash:  $urlNoSlash"
    Test-Url -Url $urlNoSlash
}
