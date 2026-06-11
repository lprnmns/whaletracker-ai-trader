param(
    [string]$Address,
    [string]$ApiKey = $env:ZERION_API_KEY,
    [string]$BaseUrl = "https://api.zerion.io/v1",
    [string]$ApiBaseUrl = "http://localhost:5090",
    [int]$PageSize = 1,
    [switch]$OnlyNonTrash,
    [string[]]$OperationTypes,
    [string]$XEnv,
    [switch]$IncludePositions,
    [switch]$RawOnly,
    [switch]$SendToAi,
    [switch]$Execute,
    [switch]$Interactive,
    [switch]$UseSnapshot,
    [decimal]$OurBalanceUSDT,
    [string]$OurPositionsFile,
    [ValidateSet("only_simple", "only_complex", "no_filter")]
    [string]$PositionsFilter = "only_simple",
    [string]$PositionsSort = "value",
    [int]$PositionsLimit = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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
    param([string]$Key)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("${Key}:")
    $b64 = [Convert]::ToBase64String($bytes)
    return "Basic $b64"
}

function Get-PropValue {
    param(
        [object]$Obj,
        [string]$Name
    )
    if ($null -eq $Obj) {
        return $null
    }
    if ($Obj.PSObject.Properties.Name -contains $Name) {
        return $Obj.$Name
    }
    return $null
}

function Get-TransferValue {
    param([object]$Transfer)
    $value = Get-PropValue -Obj $Transfer -Name "value"
    if ($null -ne $value) {
        return [double]$value
    }
    return 0.0
}

function Get-TransferSymbol {
    param([object]$Transfer)
    $fi = Get-PropValue -Obj $Transfer -Name "fungible_info"
    if ($fi) {
        return (Get-PropValue -Obj $fi -Name "symbol")
    }
    return $null
}

function Get-TransferAmount {
    param([object]$Transfer)
    $quantity = Get-PropValue -Obj $Transfer -Name "quantity"
    if ($quantity) {
        $float = Get-PropValue -Obj $quantity -Name "float"
        if ($null -ne $float) {
            return [double]$float
        }
    }
    return $null
}

function Get-TopTransfer {
    param([object[]]$Transfers)
    if (-not $Transfers -or $Transfers.Count -eq 0) {
        return $null
    }
    $top = $Transfers | Sort-Object -Property @{ Expression = { Get-TransferValue $_ } } -Descending | Select-Object -First 1
    return $top
}

function To-TitleCase {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $Text
    }
    $culture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")
    return $culture.TextInfo.ToTitleCase($Text.ToLowerInvariant())
}

function Format-Amount {
    param([double]$Value)
    $culture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")
    $rounded = [math]::Round($Value, 3)
    if ([math]::Abs($rounded - [math]::Round($rounded)) -lt 0.0000001) {
        return $rounded.ToString("#,0", $culture)
    }
    return $rounded.ToString("#,0.###", $culture)
}

function Format-Usd {
    param([double]$Value)
    $culture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")
    return $Value.ToString("#,0.00", $culture)
}

function Build-RawEvent {
    param(
        [object]$Attributes,
        [string]$ChainId,
        [string]$AppName,
        [string]$SentFrom,
        [string]$SentTo,
        [object[]]$Transfers
    )

    $culture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")
    $operation = Get-PropValue -Obj $Attributes -Name "operation_type"
    $opTitle = To-TitleCase $operation
    $chainTitle = To-TitleCase $ChainId

    $minedAt = Get-PropValue -Obj $Attributes -Name "mined_at"
    $dt = $null
    if ($minedAt) {
        try {
            $dt = [DateTime]::Parse($minedAt, $culture, [System.Globalization.DateTimeStyles]::AssumeUniversal).ToLocalTime()
        } catch {
            $dt = [DateTime]::UtcNow
        }
    } else {
        $dt = [DateTime]::UtcNow
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add($dt.ToString("MMMM d, yyyy", $culture))
    $lines.Add("")
    $lines.Add($chainTitle)
    $lines.Add($opTitle)
    $lines.Add($dt.ToString("hh:mm tt", $culture))

    $outTransfers = @($Transfers | Where-Object { $_.direction -eq "out" })
    $inTransfers = @($Transfers | Where-Object { $_.direction -eq "in" })
    $ordered = @()
    if ($operation -eq "trade") {
        $ordered += $outTransfers
        $ordered += $inTransfers
    } else {
        $ordered = @($Transfers)
    }

    foreach ($t in $ordered) {
        $symbol = Get-TransferSymbol -Transfer $t
        if ([string]::IsNullOrWhiteSpace($symbol)) {
            continue
        }
        $amount = Get-TransferAmount -Transfer $t
        $value = Get-TransferValue -Transfer $t
        $sign = if ($t.direction -eq "out") { "-" } else { "+" }
        $lines.Add("$symbol icon")
        if ($null -ne $amount) {
            $lines.Add("$sign$(Format-Amount -Value $amount)")
        } else {
            $lines.Add("$sign" + "0")
        }
        $lines.Add($symbol)
        if ($value -gt 0) {
            $lines.Add("$" + (Format-Usd -Value $value))
        }
    }

    if ($operation -eq "receive") {
        $lines.Add("From")
        if (-not [string]::IsNullOrWhiteSpace($SentFrom)) {
            $lines.Add($SentFrom)
        }
    } elseif ($operation -eq "send") {
        $lines.Add("To")
        if (-not [string]::IsNullOrWhiteSpace($SentTo)) {
            $lines.Add($SentTo)
        }
    } else {
        $lines.Add("Application")
        if (-not [string]::IsNullOrWhiteSpace($AppName)) {
            $lines.Add($AppName)
        }
    }

    return ($lines -join "`r`n")
}

$headers = @{
    Authorization = (New-AuthHeader -Key $ApiKey)
    Accept = "application/json"
}
if (-not [string]::IsNullOrWhiteSpace($XEnv)) {
    $headers["X-Env"] = $XEnv
}

$txQuery = "?currency=usd&page[size]=$PageSize"
if ($OnlyNonTrash.IsPresent) {
    $txQuery += "&filter[trash]=only_non_trash"
}
if ($OperationTypes -and $OperationTypes.Count -gt 0) {
    $txQuery += "&filter[operation_types]=$([string]::Join(',', $OperationTypes))"
}

$txUrl = "$BaseUrl/wallets/$Address/transactions/$txQuery"
$portfolioUrl = "$BaseUrl/wallets/$Address/portfolio?currency=usd"

$txResp = Invoke-RestMethod -Method Get -Headers $headers -Uri $txUrl
$portfolioResp = Invoke-RestMethod -Method Get -Headers $headers -Uri $portfolioUrl

$tx = @($txResp.data) | Select-Object -First 1
if (-not $tx) {
    throw "No transactions returned."
}

$attrs = $tx.attributes
$rels = $tx.relationships

$chainId = $null
$dappId = $null
if ($rels) {
    $chain = Get-PropValue -Obj $rels -Name "chain"
    if ($chain) {
        $chainData = Get-PropValue -Obj $chain -Name "data"
        if ($chainData) {
            $chainId = Get-PropValue -Obj $chainData -Name "id"
        } else {
            $chainId = Get-PropValue -Obj $chain -Name "id"
        }
    }
    $dapp = Get-PropValue -Obj $rels -Name "dapp"
    if ($dapp) {
        $dappData = Get-PropValue -Obj $dapp -Name "data"
        if ($dappData) {
            $dappId = Get-PropValue -Obj $dappData -Name "id"
        } else {
            $dappId = Get-PropValue -Obj $dapp -Name "id"
        }
    }
}

$appName = $null
$appContract = $null
$appMeta = Get-PropValue -Obj $attrs -Name "application_metadata"
if ($appMeta) {
    $appName = Get-PropValue -Obj $appMeta -Name "name"
    $appContract = Get-PropValue -Obj $appMeta -Name "contract_address"
}

$transfers = @($attrs.transfers)
$outTransfers = @($transfers | Where-Object { $_.direction -eq "out" })
$inTransfers = @($transfers | Where-Object { $_.direction -eq "in" })

$rawEvent = Build-RawEvent -Attributes $attrs -ChainId $chainId -AppName $appName -SentFrom $attrs.sent_from -SentTo $attrs.sent_to -Transfers $transfers

$fromTransfer = Get-TopTransfer -Transfers $outTransfers
$toTransfer = Get-TopTransfer -Transfers $inTransfers

$fromSymbol = if ($fromTransfer) { Get-TransferSymbol -Transfer $fromTransfer } else { $null }
$fromAmount = if ($fromTransfer) { Get-TransferAmount -Transfer $fromTransfer } else { $null }
$fromValue = if ($fromTransfer) { Get-TransferValue -Transfer $fromTransfer } else { 0.0 }

$toSymbol = if ($toTransfer) { Get-TransferSymbol -Transfer $toTransfer } else { $null }
$toAmount = if ($toTransfer) { Get-TransferAmount -Transfer $toTransfer } else { $null }
$toValue = if ($toTransfer) { Get-TransferValue -Transfer $toTransfer } else { 0.0 }

$stableSymbols = @("USDT", "USDC")
$stableTransfers = @($transfers | Where-Object { $stableSymbols -contains (Get-TransferSymbol -Transfer $_) })
$tradeValue = $null
if ($stableTransfers.Count -gt 0) {
    $tradeValue = ($stableTransfers | ForEach-Object { Get-TransferValue $_ } | Measure-Object -Maximum).Maximum
} elseif ($transfers.Count -gt 0) {
    $tradeValue = ($transfers | ForEach-Object { Get-TransferValue $_ } | Measure-Object -Maximum).Maximum
}
if ($null -eq $tradeValue) {
    $tradeValue = 0.0
}

$whaleBalance = 0.0
$portfolioData = $portfolioResp.data
if ($portfolioData -and $portfolioData.attributes -and $portfolioData.attributes.total) {
    $total = $portfolioData.attributes.total
    $positionsTotal = Get-PropValue -Obj $total -Name "positions"
    if ($null -ne $positionsTotal) {
        $whaleBalance = [double]$positionsTotal
    } else {
        $totalValue = Get-PropValue -Obj $total -Name "value"
        if ($null -ne $totalValue) {
            $whaleBalance = [double]$totalValue
        }
    }
}

$transferList = @()
foreach ($t in $transfers) {
    $transferList += [ordered]@{
        direction = Get-PropValue -Obj $t -Name "direction"
        symbol = Get-TransferSymbol -Transfer $t
        amount = Get-TransferAmount -Transfer $t
        value_usdt = Get-TransferValue -Transfer $t
    }
}

$payload = [ordered]@{
    wallet = $Address
    whale_balance_usdt = $whaleBalance
    operation_type = Get-PropValue -Obj $attrs -Name "operation_type"
    chain_id = $chainId
    timestamp = Get-PropValue -Obj $attrs -Name "mined_at"
    tx_hash = Get-PropValue -Obj $attrs -Name "hash"
    status = Get-PropValue -Obj $attrs -Name "status"
    from_token = $fromSymbol
    from_amount = $fromAmount
    from_value_usdt = $fromValue
    to_token = $toSymbol
    to_amount = $toAmount
    to_value_usdt = $toValue
    trade_value_usdt = $tradeValue
    app = [ordered]@{
        name = $appName
        dapp_id = $dappId
        contract_address = $appContract
    }
    transfers = $transferList
    raw_event = $rawEvent
}

if ($IncludePositions.IsPresent) {
    $posQuery = "?currency=usd"
    if (-not [string]::IsNullOrWhiteSpace($PositionsFilter)) {
        $posQuery += "&filter[positions]=$PositionsFilter"
    }
    if (-not [string]::IsNullOrWhiteSpace($PositionsSort)) {
        $posQuery += "&sort=$PositionsSort"
    }
    $posUrl = "$BaseUrl/wallets/$Address/positions/$posQuery"
    $posResp = Invoke-RestMethod -Method Get -Headers $headers -Uri $posUrl
    $posItems = @($posResp.data)

    $positionsOut = @()
    if ($posItems.Count -gt 0) {
        $limit = [Math]::Min($PositionsLimit, $posItems.Count)
        for ($i = 0; $i -lt $limit; $i++) {
            $pos = $posItems[$i]
            $posAttr = $pos.attributes
            $fi = Get-PropValue -Obj $posAttr -Name "fungible_info"
            $symbol = $null
            if ($fi) {
                $symbol = Get-PropValue -Obj $fi -Name "symbol"
            }
            $qty = $null
            $quantity = Get-PropValue -Obj $posAttr -Name "quantity"
            if ($quantity) {
                $qty = Get-PropValue -Obj $quantity -Name "float"
            }
            $value = Get-PropValue -Obj $posAttr -Name "value"

            $positionsOut += [ordered]@{
                symbol = $symbol
                amount = $qty
                value_usdt = $value
            }
        }
    }
    $payload["whale_positions"] = $positionsOut
}

Write-Host ""
Write-Host "RawEvent (mock):"
Write-Host $rawEvent

if (-not $RawOnly.IsPresent) {
    Write-Host ""
    Write-Host "Latest transaction (AI payload):"
    $payload | ConvertTo-Json -Depth 8
}

if ($SendToAi.IsPresent) {
    $aiRequest = [ordered]@{
        RawEvent = $rawEvent
        WhaleBalanceUSDT = $whaleBalance
        TxHash = $payload.tx_hash
        UseSnapshot = $UseSnapshot.IsPresent
    }

    if ($UseSnapshot.IsPresent) {
        $aiRequest["OurBalanceUSDT"] = $OurBalanceUSDT
        if (-not [string]::IsNullOrWhiteSpace($OurPositionsFile)) {
            if (-not (Test-Path -Path $OurPositionsFile)) {
                throw "OurPositionsFile not found: $OurPositionsFile"
            }
            $positionsJson = Get-Content -Path $OurPositionsFile -Raw
            $aiRequest["OurPositions"] = (ConvertFrom-Json -InputObject $positionsJson)
        } else {
            $aiRequest["OurPositions"] = @()
        }
    }

    Write-Host ""
    Write-Host "Sending to AI..."
    $aiUrl = "$ApiBaseUrl/api/test/whale-history/ai"
    $aiResp = Invoke-RestMethod -Method Post -ContentType "application/json" -Uri $aiUrl -Body ($aiRequest | ConvertTo-Json -Depth 8)

    $aiError = Get-PropValue -Obj $aiResp -Name "Error"
    if ($aiError) {
        Write-Host "AI error: $aiError"
        return
    }

    $decision = Get-PropValue -Obj $aiResp -Name "Decision"
    if (-not $decision) {
        Write-Host "AI response missing decision."
        return
    }

    Write-Host ""
    Write-Host "AI decision:"
    $decision | ConvertTo-Json -Depth 6

    $shouldTrade = $decision.ShouldTrade
    if ($shouldTrade -ne $true -and $shouldTrade -ne "true") {
        Write-Host "AI decided not to trade."
        return
    }

    $doExecute = $Execute.IsPresent
    if (-not $doExecute -and $Interactive.IsPresent) {
        $choice = Read-Host "[E] execute on OKX, [S] skip"
        if ($choice -match "^[eE]$") {
            $doExecute = $true
        }
    }

    if ($doExecute) {
        $execUrl = "$ApiBaseUrl/api/test/whale-history/execute"
        $execBody = [ordered]@{
            Action = $decision.Action
            Symbol = $decision.Symbol
            AmountUSDT = $decision.AmountUSDT
            Leverage = $decision.Leverage
            Reasoning = $decision.Reasoning
            SourceTxHash = $payload.tx_hash
        }

        Write-Host ""
        Write-Host "Executing on OKX..."
        $execResp = Invoke-RestMethod -Method Post -ContentType "application/json" -Uri $execUrl -Body ($execBody | ConvertTo-Json -Depth 6)
        Write-Host "OKX result:"
        $execResp | ConvertTo-Json -Depth 6
    }
}
