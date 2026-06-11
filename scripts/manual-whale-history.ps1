param(
    [string]$ApiBaseUrl = "http://localhost:5090",
    [string]$HistoryFile = "data/whale_history_raw.txt",
    [decimal]$WhaleBalanceUSDT = 100000,
    [int]$DelayMs = 0,
    [int]$StartStep = 1,
    [bool]$UseSnapshot = $true,
    [string]$OnlySteps = "",
    [int]$MaxRawLength = 4000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-HistoryPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path) -and (Test-Path $Path)) {
        return (Resolve-Path $Path).Path
    }

    if (Test-Path $Path) {
        return (Resolve-Path $Path).Path
    }

    if ($PSScriptRoot) {
        $candidate = Join-Path $PSScriptRoot "..\\data\\whale_history_raw.txt"
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw "History file not found: $Path"
}

function Read-Key {
    param([string]$Prompt)
    Write-Host $Prompt -NoNewline
    $key = [System.Console]::ReadKey($true)
    Write-Host $key.KeyChar
    return $key.KeyChar.ToString().ToUpperInvariant()
}

function Normalize-Token {
    param([string]$Token)
    if ([string]::IsNullOrWhiteSpace($Token)) {
        return $Token
    }

    $t = $Token.Trim().ToUpperInvariant()
    switch ($t) {
        "WETH" { return "ETH" }
        "USDC" { return "USDT" }
        default { return $t }
    }
}

function Is-Stable {
    param([string]$Token)
    return ($Token -eq "USDT" -or $Token -eq "USDC")
}

function Parse-Number {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) {
        return [decimal]0
    }

    $clean = $Text.Trim().Replace(",", "").Replace("$", "")
    $value = [decimal]0
    if ([decimal]::TryParse($clean, [System.Globalization.NumberStyles]::AllowLeadingSign -bor [System.Globalization.NumberStyles]::AllowDecimalPoint, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
        return $value
    }

    return [decimal]0
}

function Parse-EventDetails {
    param([string]$Raw)

    $lines = $Raw -split "\r?\n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
    $types = @("Trade", "Deposit", "Receive", "Send", "Approve", "Execute", "Mint")
    $eventType = $null
    $eventIndex = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($types -contains $lines[$i]) {
            $eventType = $lines[$i]
            $eventIndex = $i
            break
        }
    }

    $chain = ""
    if ($eventIndex -gt 0) {
        $chain = $lines[$eventIndex - 1]
    }

    $time = ($lines | Where-Object { $_ -match "^\d{1,2}:\d{2}\s+[AP]M$" } | Select-Object -First 1)

    $legs = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "icon$") {
            if ($i + 3 -lt $lines.Count) {
                $amountRaw = $lines[$i + 1]
                $token = $lines[$i + 2]
                $usdRaw = $lines[$i + 3]

                $legs += [pscustomobject]@{
                    Token = $token
                    Amount = (Parse-Number $amountRaw)
                    Usd = (Parse-Number $usdRaw)
                }
            }
        }
    }

    return [pscustomobject]@{
        EventType = $eventType
        Chain = $chain
        Time = $time
        Legs = $legs
        CleanRaw = ($lines -join "`n")
    }
}

function Get-EventTimestamp {
    param([string]$DateLine, [string]$Block)

    if ([string]::IsNullOrWhiteSpace($DateLine)) {
        return $null
    }

    $match = [regex]::Match($Block, "\b\d{1,2}:\d{2}\s+[AP]M\b")
    if (-not $match.Success) {
        return $null
    }

    $combined = "$DateLine $($match.Value)"
    $formats = @("MMMM d, yyyy h:mm tt", "MMMM dd, yyyy h:mm tt")
    foreach ($fmt in $formats) {
        try {
            return [datetime]::ParseExact($combined, $fmt, [System.Globalization.CultureInfo]::InvariantCulture)
        } catch {
        }
    }

    return $null
}

function Add-Position {
    param([hashtable]$Positions, [string]$Symbol, [decimal]$Delta)
    if ([string]::IsNullOrWhiteSpace($Symbol)) {
        return
    }

    if (-not $Positions.ContainsKey($Symbol)) {
        $Positions[$Symbol] = [decimal]0
    }

    $Positions[$Symbol] = [decimal]$Positions[$Symbol] + $Delta
    if ([math]::Abs([double]$Positions[$Symbol]) -lt 0.00000001) {
        $Positions.Remove($Symbol) | Out-Null
    }
}

function Update-WhaleState {
    param(
        [pscustomobject]$Details,
        [ref]$Balance,
        [hashtable]$Positions
    )

    if (-not $Details -or -not $Details.EventType) {
        return
    }

    $legs = $Details.Legs
    if (-not $legs -or $legs.Count -eq 0) {
        return
    }

    switch ($Details.EventType) {
        "Trade" {
            if ($legs.Count -lt 2) {
                return
            }

            $a = $legs[0]
            $b = $legs[1]
            $tokenA = Normalize-Token $a.Token
            $tokenB = Normalize-Token $b.Token

            $stableA = Is-Stable $tokenA
            $stableB = Is-Stable $tokenB

            if ($stableA -and -not $stableB) {
                $amount = [decimal]$b.Amount
                if ($amount -lt 0) { $amount = -$amount }
                Add-Position -Positions $Positions -Symbol $tokenB -Delta $amount
            } elseif ($stableB -and -not $stableA) {
                $amount = [decimal]$a.Amount
                if ($amount -gt 0) { $amount = -$amount }
                Add-Position -Positions $Positions -Symbol $tokenA -Delta $amount
            }
        }
        "Deposit" {
            $token = Normalize-Token $legs[0].Token
            if (Is-Stable $token) {
                $Balance.Value = [decimal]$Balance.Value + $legs[0].Amount
            } else {
                Add-Position -Positions $Positions -Symbol $token -Delta $legs[0].Amount
            }
        }
        "Receive" {
            $token = Normalize-Token $legs[0].Token
            if (Is-Stable $token) {
                $Balance.Value = [decimal]$Balance.Value + $legs[0].Amount
            } else {
                Add-Position -Positions $Positions -Symbol $token -Delta $legs[0].Amount
            }
        }
        "Send" {
            $token = Normalize-Token $legs[0].Token
            if (Is-Stable $token) {
                $Balance.Value = [decimal]$Balance.Value - [decimal]$legs[0].Amount
            } else {
                Add-Position -Positions $Positions -Symbol $token -Delta (-[decimal]$legs[0].Amount)
            }
        }
    }
}

function Show-WhalePositions {
    param([hashtable]$Positions)
    if ($Positions.Count -eq 0) {
        return "(none)"
    }

    $items = $Positions.Keys | Sort-Object | ForEach-Object {
        "$_=$([math]::Round([decimal]$Positions[$_], 6))"
    }
    return ($items -join ", ")
}

function Get-OkxAccount {
    param([string]$BaseUrl)
    try {
        return Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/test/okx-account"
    } catch {
        return $null
    }
}

function Build-OurPositionsFromSnapshot {
    param($Snapshot)
    $positions = @()
    if (-not $Snapshot -or -not $Snapshot.Data -or -not $Snapshot.Data.OpenPositions) {
        return $positions
    }

    foreach ($pos in $Snapshot.Data.OpenPositions) {
        $margin = [decimal]$pos.MarginUSD
        $entry = [decimal]$pos.EntryPrice
        $size = [decimal]$pos.Size
        $lev = 3
        if ($margin -gt 0 -and $entry -gt 0 -and $size -gt 0) {
            $lev = [math]::Ceiling($size * $entry / $margin)
        }

        $positions += @{
            Symbol = $pos.Symbol
            Direction = $pos.Direction
            MarginUSDT = $margin
            EntryPrice = $entry
            UnrealizedPnL = [decimal]$pos.UnrealizedPnL
            Leverage = [int]$lev
        }
    }

    return $positions
}

$resolvedPath = Resolve-HistoryPath $HistoryFile
$rawText = Get-Content -Raw -Path $resolvedPath
$rawText = $rawText.TrimStart([char]0xFEFF)

$blocks = [regex]::Split($rawText, "\r?\n\r?\n") | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
$entries = @()
$currentDate = $null
$index = 0

foreach ($block in $blocks) {
    if ($block -match "^[A-Za-z]+\s+\d{1,2},\s+\d{4}$") {
        $currentDate = $block
        continue
    }

    if ($block -notmatch "\b(Trade|Deposit|Receive|Send|Approve|Execute|Mint)\b") {
        continue
    }

    $index++
    $raw = if ($currentDate) { "$currentDate`n`n$block" } else { $block }
    $timestamp = Get-EventTimestamp -DateLine $currentDate -Block $block

    $entries += [pscustomobject]@{
        Index = $index
        RawText = $raw
        Timestamp = $timestamp
    }
}

$ordered = $entries | Sort-Object `
    @{ Expression = { if ($_.Timestamp) { $_.Timestamp } else { [datetime]::MaxValue } } }, `
    @{ Expression = { $_.Index } }

$whaleBalance = [decimal]$WhaleBalanceUSDT
$whalePositions = @{}
$lastOkxSnapshot = $null
$onlyStepList = @()
if (-not [string]::IsNullOrWhiteSpace($OnlySteps)) {
    $onlyStepList = $OnlySteps -split "[,; ]+" | Where-Object { $_ -match "^[0-9]+$" } | ForEach-Object { [int]$_ } | Sort-Object -Unique
}

if ($StartStep -lt 1) { $StartStep = 1 }
if ($StartStep -gt $ordered.Count) {
    Write-Host "StartStep is beyond total events."
    return
}

Write-Host "Manual whale history replay"
Write-Host "History file: $resolvedPath"
Write-Host "Total events: $($ordered.Count)"
Write-Host "Api base: $ApiBaseUrl"
Write-Host ""

$step = 0
foreach ($entry in $ordered) {
    $step++
    $details = Parse-EventDetails $entry.RawText
    $timestampText = if ($entry.Timestamp) { $entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") } else { "n/a" }
    $txHash = ("0x_manual_{0:d4}" -f $step)

    if ($step -lt $StartStep) {
        Update-WhaleState -Details $details -Balance ([ref]$whaleBalance) -Positions $whalePositions
        continue
    }

    if ($onlyStepList.Count -gt 0 -and -not ($onlyStepList -contains $step)) {
        Update-WhaleState -Details $details -Balance ([ref]$whaleBalance) -Positions $whalePositions
        continue
    }

    Write-Host "============================================================"
    Write-Host ("STEP {0}/{1} | {2} | {3} | {4}" -f $step, $ordered.Count, $details.EventType, $details.Chain, $timestampText)
    Write-Host "Whale balance (mock, before): $([math]::Round($whaleBalance, 2))"
    Write-Host ("Whale positions (before): {0}" -f (Show-WhalePositions $whalePositions))

    $okx = Get-OkxAccount -BaseUrl $ApiBaseUrl
    if ($okx -and $okx.Success) {
        $lastOkxSnapshot = $okx
        Write-Host ("OKX balance: {0}" -f [math]::Round([decimal]$okx.Data.TotalBalanceUSD, 2))
        Write-Host ("OKX open positions: {0}" -f $okx.Data.OpenPositionsCount)
        if ($okx.Data.OpenPositionsCount -gt 0 -and $okx.Data.OpenPositions) {
            foreach ($pos in $okx.Data.OpenPositions) {
                Write-Host ("  {0} {1} margin={2} size={3} entry={4} upl={5}" -f $pos.Symbol, $pos.Direction, $pos.MarginUSD, $pos.Size, $pos.EntryPrice, $pos.UnrealizedPnL)
            }
        }
    } else {
        Write-Host "OKX account: not available"
    }

    Write-Host ""
    Write-Host "Raw event:"
    Write-Host $entry.RawText
    Write-Host ""

    $decision = $null
    $aiDone = $false
    $done = $false

    while (-not $done) {
        if (-not $aiDone) {
            $key = Read-Key "[E] AI analyze, [S] skip, [Q] quit: "
            if ($key -eq "Q") { return }
            if ($key -eq "S") { break }
            if ($key -ne "E") { continue }

            $snapshotBalance = $null
            $snapshotPositions = @()
            if ($UseSnapshot -and $lastOkxSnapshot) {
                $snapshotBalance = [decimal]$lastOkxSnapshot.Data.TotalBalanceUSD
                $snapshotPositions = Build-OurPositionsFromSnapshot -Snapshot $lastOkxSnapshot
            }

            $rawPayload = $entry.RawText
            if ($MaxRawLength -gt 0 -and $rawPayload.Length -gt $MaxRawLength) {
                $rawPayload = $details.CleanRaw
            }
            if ($MaxRawLength -gt 0 -and $rawPayload.Length -gt $MaxRawLength) {
                $rawPayload = $rawPayload.Substring(0, $MaxRawLength)
            }

            $aiBody = @{
                RawEvent = $rawPayload
                WhaleBalanceUSDT = $WhaleBalanceUSDT
                TxHash = $txHash
                UseSnapshot = $UseSnapshot
                OurBalanceUSDT = $snapshotBalance
                OurPositions = $snapshotPositions
            } | ConvertTo-Json -Depth 6

            try {
                $decision = Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/api/test/whale-history/ai" -ContentType "application/json" -Body $aiBody -ErrorAction Stop
            } catch {
                Write-Host ""
                Write-Host ("AI call failed: {0}" -f $_.Exception.Message)
                $resp = $_.Exception.Response
                if ($resp -and $resp.GetResponseStream()) {
                    try {
                        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
                        $body = $reader.ReadToEnd()
                        if ($body) {
                            Write-Host ("AI error body: {0}" -f $body)
                        }
                    } catch {
                    }
                }
                Write-Host ""
                continue
            }
            Write-Host ""
            $errorProp = $decision.PSObject.Properties["Error"]
            if ($errorProp -and $errorProp.Value) {
                Write-Host ("AI error: {0}" -f $errorProp.Value)
            }
            Write-Host "AI decision:"
            if (-not $decision.Decision) {
                Write-Host "AI response missing Decision payload."
            } else {
                Write-Host ("Action={0} Symbol={1} AmountUSDT={2} ShouldTrade={3}" -f $decision.Decision.Action, $decision.Decision.Symbol, $decision.Decision.AmountUSDT, $decision.Decision.ShouldTrade)
                Write-Host ("Reason={0}" -f $decision.Decision.Reasoning)
            }
            Write-Host ""

            $aiDone = $true
            continue
        }

        $key = Read-Key "[E] execute on OKX, [N] next, [Q] quit: "
        if ($key -eq "Q") { return }
        if ($key -eq "N") { break }
        if ($key -ne "E") { continue }

        if (-not $decision) {
            Write-Host "No AI decision available."
            break
        }

        $execBody = @{
            Action = $decision.Decision.Action
            Symbol = $decision.Decision.Symbol
            AmountUSDT = [decimal]$decision.Decision.AmountUSDT
            Leverage = [int]$decision.Decision.Leverage
            Reasoning = $decision.Decision.Reasoning
            SourceTxHash = $txHash
        } | ConvertTo-Json -Depth 6

        $exec = Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/api/test/whale-history/execute" -ContentType "application/json" -Body $execBody
        Write-Host ""
        Write-Host "OKX result:"
        Write-Host ($exec | ConvertTo-Json -Depth 6)
        Write-Host ""

        break
    }

    Update-WhaleState -Details $details -Balance ([ref]$whaleBalance) -Positions $whalePositions
    Write-Host ("Whale balance (after): {0}" -f [math]::Round($whaleBalance, 2))
    Write-Host ("Whale positions (after): {0}" -f (Show-WhalePositions $whalePositions))
    Write-Host ""

    if ($DelayMs -gt 0) {
        Start-Sleep -Milliseconds $DelayMs
    }
}
