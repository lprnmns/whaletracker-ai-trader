param(
    [string[]]$Symbols = @("WBTC", "BTC", "MNT", "ZRO", "UNI", "ETH"),
    [string]$BaseUrl = "https://www.okx.com"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-Instrument {
    param([string]$Symbol)
    $instId = "$Symbol-USDT-SWAP"
    $url = "$BaseUrl/api/v5/public/instruments?instType=SWAP&instId=$instId"
    try {
        return Invoke-RestMethod -Method Get -Uri $url
    } catch {
        return $null
    }
}

function Get-Ticker {
    param([string]$InstId)
    $url = "$BaseUrl/api/v5/market/ticker?instId=$InstId"
    try {
        return Invoke-RestMethod -Method Get -Uri $url
    } catch {
        return $null
    }
}

foreach ($symbol in $Symbols) {
    $upper = $symbol.Trim().ToUpperInvariant()
    $instrument = Get-Instrument -Symbol $upper

    if (-not $instrument -or $instrument.code -ne "0" -or -not $instrument.data -or $instrument.data.Count -eq 0) {
        Write-Host "${upper}: NOT FOUND"
        continue
    }

    $inst = $instrument.data[0]
    $instId = $inst.instId
    $ticker = Get-Ticker -InstId $instId

    $last = 0
    if ($ticker -and $ticker.code -eq "0" -and $ticker.data -and $ticker.data.Count -gt 0) {
        $last = [decimal]$ticker.data[0].last
    }

    $ctVal = [decimal]$inst.ctVal
    $minSz = [decimal]$inst.minSz
    $lotSz = [decimal]$inst.lotSz
    $oneContractUsd = $ctVal * $last
    $minContractUsd = $minSz * $oneContractUsd
    $minMargin3x = if ($last -gt 0) { $minContractUsd / 3 } else { 0 }

    Write-Host "${upper}: instId=$instId ctVal=$ctVal minSz=$minSz lotSz=$lotSz last=$last minMargin3x=$([math]::Round($minMargin3x, 6))"
}
