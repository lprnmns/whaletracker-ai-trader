param(
    [string]$Address,
    [string]$ApiKey = $env:ZERION_API_KEY,
    [string]$BaseUrl = "https://api.zerion.io/v1",
    [string]$ApiBaseUrl = "http://localhost:5090",
    [int]$PageSize = 5,
    [int]$IntervalSeconds = 30,
    [string]$StateFile = "data/zerion_last_tx.json",
    [switch]$OnlyNonTrash,
    [string[]]$OperationTypes,
    [string[]]$ChainIds,
    [switch]$SendToAi,
    [switch]$Interactive,
    [switch]$Execute,
    [switch]$UseSnapshot,
    [decimal]$OurBalanceUSDT,
    [string]$OurPositionsFile,
    [switch]$IncludePositions,
    [switch]$DisableOkxBalanceLog,
    [string]$OkxBalanceCsv,
    [ValidateSet("only_simple", "only_complex", "no_filter")]
    [string]$PositionsFilter = "only_simple",
    [string]$PositionsSort = "value",
    [int]$PositionsLimit = 10,
    [switch]$StartFromLatest,
    [switch]$Once,
    [string]$HeartbeatFile,
    [string]$LatestEventFile,
    [string]$LogFile
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

if (-not $PSBoundParameters.ContainsKey("StateFile")) {
    $safeAddress = $Address.ToLowerInvariant().Replace("0x", "")
    $safeAddress = $safeAddress -replace "[^a-z0-9]", ""
    $StateFile = "data/zerion_last_tx_$safeAddress.json"
}

if (-not $PSBoundParameters.ContainsKey("HeartbeatFile")) {
    $safeAddress = $Address.ToLowerInvariant().Replace("0x", "")
    $safeAddress = $safeAddress -replace "[^a-z0-9]", ""
    $HeartbeatFile = "data/zerion_watch_status_$safeAddress.json"
}

if (-not $PSBoundParameters.ContainsKey("LatestEventFile")) {
    $safeAddress = $Address.ToLowerInvariant().Replace("0x", "")
    $safeAddress = $safeAddress -replace "[^a-z0-9]", ""
    $LatestEventFile = "data/zerion_latest_event_$safeAddress.json"
}

if (-not $PSBoundParameters.ContainsKey("LogFile")) {
    $safeAddress = $Address.ToLowerInvariant().Replace("0x", "")
    $safeAddress = $safeAddress -replace "[^a-z0-9]", ""
    $LogFile = "data/zerion_watch_log_$safeAddress.jsonl"
}

if (-not $PSBoundParameters.ContainsKey("OkxBalanceCsv")) {
    $OkxBalanceCsv = "data/benchmarks/okx_balance.csv"
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
function Try-ParseTimestamp {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }
    try {
        return [DateTime]::Parse($Value, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
    } catch {
    }
    try {
        return [DateTime]::Parse($Value).ToUniversalTime()
    } catch {
        return $null
    }
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

function Load-State {
    param(
        [string]$Path,
        [string]$Address
    )
    if (-not (Test-Path -Path $Path)) {
        return $null
    }
    try {
        $raw = Get-Content -Path $Path -Raw
        $state = (ConvertFrom-Json -InputObject $raw)
        if (-not $state) {
            return $null
        }
        $stateAddress = Get-PropValue -Obj $state -Name "address"
        if ([string]::IsNullOrWhiteSpace($stateAddress)) {
            Write-Host "State file has no address; ignoring."
            return $null
        }
        if ($stateAddress -ne $Address) {
            Write-Host "State file belongs to different address; ignoring."
            return $null
        }
        return $state
    } catch {
        return $null
    }
}

function Save-State {
    param(
        [string]$Path,
        [string]$Address,
        [string]$LastId,
        [string]$LastHash,
        [string]$LastTimestamp
    )
    $payload = [ordered]@{
        address = $Address
        last_id = $LastId
        last_hash = $LastHash
        last_timestamp = $LastTimestamp
        saved_at = (Get-Date).ToString("o")
    }
    $dir = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($dir) -and -not (Test-Path -Path $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }
    $payload | ConvertTo-Json -Depth 4 | Set-Content -Path $Path
}

function Ensure-Directory {
    param([string]$Path)
    $dir = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($dir) -and -not (Test-Path -Path $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }
}

function Write-LogLine {
    param(
        [string]$Path,
        [string]$Level,
        [string]$Message,
        [hashtable]$Context
    )
    Ensure-Directory -Path $Path
    $entry = [ordered]@{
        timestamp = (Get-Date).ToUniversalTime().ToString("o")
        level = $Level
        message = $Message
        context = $Context
    }
    ($entry | ConvertTo-Json -Depth 6 -Compress) | Add-Content -Path $Path
}

function Write-Heartbeat {
    param(
        [string]$Path,
        [hashtable]$Payload
    )
    Ensure-Directory -Path $Path
    $Payload | ConvertTo-Json -Depth 6 | Set-Content -Path $Path
}

function Load-Heartbeat {
    param(
        [string]$Path,
        [string]$Address
    )
    if (-not (Test-Path -Path $Path)) {
        return $null
    }
    try {
        $raw = Get-Content -Path $Path -Raw
        $heartbeat = ConvertFrom-Json -InputObject $raw
        if (-not $heartbeat) {
            return $null
        }
        $hbAddress = Get-PropValue -Obj $heartbeat -Name "address"
        if ([string]::IsNullOrWhiteSpace($hbAddress) -or $hbAddress -ne $Address) {
            return $null
        }
        return $heartbeat
    } catch {
        return $null
    }
}

function Write-LatestEvent {
    param(
        [string]$Path,
        [object]$Payload
    )
    Ensure-Directory -Path $Path
    $Payload | ConvertTo-Json -Depth 8 | Set-Content -Path $Path
}

function Try-FetchOkxBalance {
    param(
        [string]$ApiBaseUrl
    )
    try {
        $resp = Invoke-RestMethod -Method Get -Uri "$ApiBaseUrl/api/test/okx-account"
        if ($resp.Success -eq $true -and $resp.Data -and $resp.Data.TotalBalanceUSD) {
            return [double]$resp.Data.TotalBalanceUSD
        }
    } catch {
        return $null
    }
    return $null
}

function Try-FetchWhaleSnapshot {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$Address,
        [switch]$IncludePositions,
        [string]$PositionsFilter,
        [string]$PositionsSort,
        [int]$PositionsLimit
    )

    $snapshot = @{
        Balance = $null
        Positions = @()
    }

    try {
        $portfolioResp = Invoke-RestMethod -Method Get -Headers $Headers -Uri "$BaseUrl/wallets/$Address/portfolio?currency=usd"
        $portfolioData = $portfolioResp.data
        if ($portfolioData -and $portfolioData.attributes -and $portfolioData.attributes.total) {
            $total = $portfolioData.attributes.total
            $positionsTotal = Get-PropValue -Obj $total -Name "positions"
            if ($null -ne $positionsTotal) {
                $snapshot.Balance = [double]$positionsTotal
            } else {
                $totalValue = Get-PropValue -Obj $total -Name "value"
                if ($null -ne $totalValue) {
                    $snapshot.Balance = [double]$totalValue
                }
            }
        }

        if ($IncludePositions.IsPresent) {
            $posQuery = "?currency=usd"
            if (-not [string]::IsNullOrWhiteSpace($PositionsFilter)) {
                $posQuery += "&filter[positions]=$PositionsFilter"
            }
            if (-not [string]::IsNullOrWhiteSpace($PositionsSort)) {
                $posQuery += "&sort=$PositionsSort"
            }
            $posResp = Invoke-RestMethod -Method Get -Headers $Headers -Uri "$BaseUrl/wallets/$Address/positions/$posQuery"
            $posItems = @($posResp.data)
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
                    $snapshot.Positions += [ordered]@{
                        symbol = $symbol
                        amount = $qty
                        valueUsdt = $value
                    }
                }
            }
        }
    } catch {
        return $null
    }

    return $snapshot
}

function Update-LatestSnapshot {
    param(
        [string]$Path,
        [string]$Address,
        [double]$Balance,
        [object[]]$Positions
    )

    $payload = $null
    if (Test-Path -Path $Path) {
        try {
            $payload = Get-Content -Path $Path -Raw | ConvertFrom-Json
        } catch {
            $payload = $null
        }
    }

    if (-not $payload) {
        $payload = [ordered]@{
            wallet = $Address
            rawEvent = "Snapshot"
        }
    }

    if ($Balance -gt 0) {
        $payload | Add-Member -NotePropertyName "whaleBalanceUsdt" -NotePropertyValue $Balance -Force
    }
    if ($Positions -and $Positions.Count -gt 0) {
        $payload | Add-Member -NotePropertyName "whalePositions" -NotePropertyValue $Positions -Force
    }

    Write-LatestEvent -Path $Path -Payload $payload
}

function Append-OkxBalance {
    param(
        [string]$Path,
        [double]$Balance
    )
    Ensure-Directory -Path $Path
    if (-not (Test-Path -Path $Path)) {
        "date,value" | Set-Content -Path $Path
    }
    $line = "{0},{1}" -f (Get-Date).ToUniversalTime().ToString("o"), $Balance.ToString("F4", [System.Globalization.CultureInfo]::InvariantCulture)
    Add-Content -Path $Path -Value $line
}

function Build-LatestPayload {
    param(
        [string]$Wallet,
        [double]$WhaleBalance,
        [object]$Attributes,
        [string]$ChainId,
        [string]$RawEvent,
        [object[]]$Transfers,
        [object[]]$WhalePositions
    )
    $opType = Get-PropValue -Obj $Attributes -Name "operation_type"
    $payload = [ordered]@{
        wallet = $Wallet
        whaleBalanceUsdt = $WhaleBalance
        operationType = $opType
        chainId = $ChainId
        timestamp = (Get-PropValue -Obj $Attributes -Name "mined_at")
        txHash = (Get-PropValue -Obj $Attributes -Name "hash")
        status = (Get-PropValue -Obj $Attributes -Name "status")
        rawEvent = $RawEvent
        transfers = @()
    }

    $tradeValue = 0.0
    foreach ($t in $Transfers) {
        $value = Get-TransferValue -Transfer $t
        if ($value -gt $tradeValue) {
            $tradeValue = $value
        }
        $payload.transfers += [ordered]@{
            direction = (Get-PropValue -Obj $t -Name "direction")
            symbol = (Get-TransferSymbol -Transfer $t)
            amount = (Get-TransferAmount -Transfer $t)
            valueUsdt = $value
        }
    }

    $payload["tradeValueUsdt"] = $tradeValue

    if ($WhalePositions) {
        $payload["whalePositions"] = $WhalePositions
    }

    return $payload
}

$headers = @{
    Authorization = (New-AuthHeader -Key $ApiKey)
    Accept = "application/json"
}

$txQuery = "?currency=usd&page[size]=$PageSize"
if ($OnlyNonTrash.IsPresent) {
    $txQuery += "&filter[trash]=only_non_trash"
}
if ($OperationTypes -and $OperationTypes.Count -gt 0) {
    $txQuery += "&filter[operation_types]=$([string]::Join(',', $OperationTypes))"
}
if ($ChainIds -and $ChainIds.Count -gt 0) {
    $txQuery += "&filter[chain_ids]=$([string]::Join(',', $ChainIds))"
}

$state = Load-State -Path $StateFile -Address $Address
$lastId = if ($state) { $state.last_id } else { $null }
$lastHash = if ($state) { $state.last_hash } else { $null }
$lastTimestamp = if ($state) { $state.last_timestamp } else { $null }
$startedAtUtc = (Get-Date).ToUniversalTime()
$startedAt = $startedAtUtc.ToString("o")
$lastTimestampUtc = Try-ParseTimestamp -Value $lastTimestamp
$startOkxBalance = $null
$startWhaleBalance = $null
$startWhalePositions = @()

$heartbeat = Load-Heartbeat -Path $HeartbeatFile -Address $Address
$coldStart = $true
if ($heartbeat) {
    $savedStartedAt = Get-PropValue -Obj $heartbeat -Name "startedAt"
    if (-not [string]::IsNullOrWhiteSpace($savedStartedAt)) {
        $startedAt = $savedStartedAt
        $startedAtUtc = Try-ParseTimestamp -Value $savedStartedAt
        if ($null -eq $startedAtUtc) {
            $startedAtUtc = (Get-Date).ToUniversalTime()
        }
        $coldStart = $false
    }

    $savedOkx = Get-PropValue -Obj $heartbeat -Name "startOkxBalanceUsdt"
    if ($null -ne $savedOkx) {
        $startOkxBalance = [double]$savedOkx
    }

    $savedWhale = Get-PropValue -Obj $heartbeat -Name "startWhaleBalanceUsdt"
    if ($null -ne $savedWhale) {
        $startWhaleBalance = [double]$savedWhale
    }

    $savedPositions = Get-PropValue -Obj $heartbeat -Name "startWhalePositions"
    if ($null -ne $savedPositions) {
        $startWhalePositions = @($savedPositions)
    }
}

if ($null -eq $startOkxBalance) {
    $startOkxBalance = Try-FetchOkxBalance -ApiBaseUrl $ApiBaseUrl
    if ($null -eq $startOkxBalance) {
        Write-LogLine -Path $LogFile -Level "warn" -Message "OKX start balance fetch failed" -Context @{
            address = $Address
            error = "Start OKX balance not available."
        }
    }
}

if ($null -eq $startWhaleBalance -or ($IncludePositions.IsPresent -and $startWhalePositions.Count -eq 0)) {
    try {
        $portfolioStart = Invoke-RestMethod -Method Get -Headers $headers -Uri "$BaseUrl/wallets/$Address/portfolio?currency=usd"
        $portfolioData = $portfolioStart.data
        if ($portfolioData -and $portfolioData.attributes -and $portfolioData.attributes.total) {
            $total = $portfolioData.attributes.total
            $positionsTotal = Get-PropValue -Obj $total -Name "positions"
            if ($null -ne $positionsTotal) {
                $startWhaleBalance = [double]$positionsTotal
            } else {
                $totalValue = Get-PropValue -Obj $total -Name "value"
                if ($null -ne $totalValue) {
                    $startWhaleBalance = [double]$totalValue
                }
            }
        }

        if ($IncludePositions.IsPresent) {
            $posResp = Invoke-RestMethod -Method Get -Headers $headers -Uri "$BaseUrl/wallets/$Address/positions/?currency=usd"
            $posItems = @($posResp.data)
            foreach ($pos in $posItems) {
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
                $startWhalePositions += [ordered]@{
                    symbol = $symbol
                    amount = $qty
                    valueUsdt = $value
                }
            }
        }
    } catch {
        Write-LogLine -Path $LogFile -Level "warn" -Message "Zerion start snapshot failed" -Context @{
            address = $Address
            error = $_.Exception.Message
        }
    }
}

try {
    Write-Heartbeat -Path $HeartbeatFile -Payload ([ordered]@{
        address = $Address
        startedAt = $startedAt
        lastHeartbeat = $startedAt
        lastPollStatus = "init"
        lastError = $null
        lastTxId = $lastId
        lastTxHash = $null
        lastTxTime = $null
        startOkxBalanceUsdt = $startOkxBalance
        startWhaleBalanceUsdt = $startWhaleBalance
        startWhalePositions = $startWhalePositions
    })
} catch {
    Write-LogLine -Path $LogFile -Level "warn" -Message "Failed to write heartbeat" -Context @{
        address = $Address
        error = $_.Exception.Message
    }
}

$watcherMessage = if ($coldStart) { "Watcher started" } else { "Watcher restarted" }
Write-LogLine -Path $LogFile -Level "info" -Message $watcherMessage -Context @{
    address = $Address
    startedAt = $startedAt
}

if ($StartFromLatest.IsPresent) {
    $txResp = Invoke-RestMethod -Method Get -Headers $headers -Uri "$BaseUrl/wallets/$Address/transactions/$txQuery"
    $latest = @($txResp.data) | Select-Object -First 1
    if ($latest) {
        Save-State -Path $StateFile -Address $Address -LastId $latest.id -LastHash $latest.attributes.hash -LastTimestamp $latest.attributes.mined_at
        $lastId = $latest.id
        $lastHash = $latest.attributes.hash
        $lastTimestamp = $latest.attributes.mined_at
        $lastTimestampUtc = Try-ParseTimestamp -Value $lastTimestamp

        $attrs = $latest.attributes
        $rels = $latest.relationships
        $chainId = $null
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
        }

        $appName = $null
        $appMeta = Get-PropValue -Obj $attrs -Name "application_metadata"
        if ($appMeta) {
            $appName = Get-PropValue -Obj $appMeta -Name "name"
        }

        $transfers = @($attrs.transfers)
        $rawEvent = Build-RawEvent -Attributes $attrs -ChainId $chainId -AppName $appName -SentFrom $attrs.sent_from -SentTo $attrs.sent_to -Transfers $transfers
        $whaleBalanceSeed = if ($null -ne $startWhaleBalance) { [double]$startWhaleBalance } else { 0 }
        $latestPayload = Build-LatestPayload `
            -Wallet $Address `
            -WhaleBalance $whaleBalanceSeed `
            -Attributes $attrs `
            -ChainId $chainId `
            -RawEvent $rawEvent `
            -Transfers $transfers `
            -WhalePositions $startWhalePositions
        Write-LatestEvent -Path $LatestEventFile -Payload $latestPayload

        Write-Host "Initialized state with latest transaction id=$lastId"
    }
}

do {
    $pollError = $null
    try {
        $txResp = Invoke-RestMethod -Method Get -Headers $headers -Uri "$BaseUrl/wallets/$Address/transactions/$txQuery"
        $items = @($txResp.data)
        if ($items.Count -eq 0) {
            Write-Host "No transactions returned."
        } else {
            $cutoffUtc = $lastTimestampUtc
            if (-not $cutoffUtc -and $StartFromLatest.IsPresent) {
                $cutoffUtc = $startedAtUtc
            }

            $newItems = New-Object System.Collections.Generic.List[object]
            foreach ($item in $items) {
                if ($lastId -and $item.id -eq $lastId) {
                    break
                }
                if ($cutoffUtc) {
                    $itemTimestamp = Try-ParseTimestamp -Value $item.attributes.mined_at
                    if ($itemTimestamp -and $itemTimestamp -le $cutoffUtc) {
                        continue
                    }
                }
                $newItems.Add($item)
            }

            if ($newItems.Count -gt 0) {
                $newOrdered = $newItems | Sort-Object { $_.attributes.mined_at }

                $portfolioResp = Invoke-RestMethod -Method Get -Headers $headers -Uri "$BaseUrl/wallets/$Address/portfolio?currency=usd"
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

                foreach ($tx in $newOrdered) {
                    $attrs = $tx.attributes
                    $rels = $tx.relationships
                    $chainId = $null
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
                    }

                    $appName = $null
                    $appMeta = Get-PropValue -Obj $attrs -Name "application_metadata"
                    if ($appMeta) {
                        $appName = Get-PropValue -Obj $appMeta -Name "name"
                    }

                    $transfers = @($attrs.transfers)
                    $rawEvent = Build-RawEvent -Attributes $attrs -ChainId $chainId -AppName $appName -SentFrom $attrs.sent_from -SentTo $attrs.sent_to -Transfers $transfers

                    $positionsOut = @()
                    if ($IncludePositions.IsPresent) {
                        $posQuery = "?currency=usd"
                        if (-not [string]::IsNullOrWhiteSpace($PositionsFilter)) {
                            $posQuery += "&filter[positions]=$PositionsFilter"
                        }
                        if (-not [string]::IsNullOrWhiteSpace($PositionsSort)) {
                            $posQuery += "&sort=$PositionsSort"
                        }
                        $posResp = Invoke-RestMethod -Method Get -Headers $headers -Uri "$BaseUrl/wallets/$Address/positions/$posQuery"
                        $posItems = @($posResp.data)
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
                                    valueUsdt = $value
                                }
                            }
                        }
                    }

                    $latestPayload = Build-LatestPayload `
                        -Wallet $Address `
                        -WhaleBalance $whaleBalance `
                        -Attributes $attrs `
                        -ChainId $chainId `
                        -RawEvent $rawEvent `
                        -Transfers $transfers `
                        -WhalePositions $positionsOut

                    Write-LatestEvent -Path $LatestEventFile -Payload $latestPayload
                    Write-LogLine -Path $LogFile -Level "info" -Message "New transaction" -Context @{
                        address = $Address
                        tx_hash = $attrs.hash
                        operation_type = $attrs.operation_type
                        chain_id = $chainId
                    }

                    Write-Host ""
                    Write-Host "New transaction detected:"
                    Write-Host $rawEvent

                    if ($SendToAi.IsPresent) {
                        $aiRequest = [ordered]@{
                            RawEvent = $rawEvent
                            WhaleBalanceUSDT = $whaleBalance
                            TxHash = $attrs.hash
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

                        if ($positionsOut.Count -gt 0) {
                            $aiRequest["WhalePositions"] = $positionsOut
                        }

                        $aiUrl = "$ApiBaseUrl/api/test/whale-history/ai"
                        $aiResp = Invoke-RestMethod -Method Post -ContentType "application/json" -Uri $aiUrl -Body ($aiRequest | ConvertTo-Json -Depth 8)

                        $aiError = Get-PropValue -Obj $aiResp -Name "Error"
                        if ($aiError) {
                            Write-Host "AI error: $aiError"
                        } else {
                            $decision = Get-PropValue -Obj $aiResp -Name "Decision"
                            if ($decision) {
                                Write-Host ""
                                Write-Host "AI decision:"
                                $decision | ConvertTo-Json -Depth 6

                                $shouldTrade = $decision.ShouldTrade
                                if ($shouldTrade -eq $true -or $shouldTrade -eq "true") {
                                    $doExecute = $Execute.IsPresent -or $Interactive.IsPresent
                                    if ($doExecute) {
                                        $execUrl = "$ApiBaseUrl/api/test/whale-history/execute"
                                        $execBody = [ordered]@{
                                            Action = $decision.Action
                                            Symbol = $decision.Symbol
                                            AmountUSDT = $decision.AmountUSDT
                                            Leverage = $decision.Leverage
                                            Reasoning = $decision.Reasoning
                                            SourceTxHash = $attrs.hash
                                        }
                                        $execResp = Invoke-RestMethod -Method Post -ContentType "application/json" -Uri $execUrl -Body ($execBody | ConvertTo-Json -Depth 6)
                                        Write-Host "OKX result:"
                                        $execResp | ConvertTo-Json -Depth 6
                                    } else {
                                        Write-Host "Execution skipped (auto-execute disabled)."
                                    }
                                }
                            }
                        }
                    }
                }

                $last = $items[0]
                $lastId = $last.id
                Save-State -Path $StateFile -Address $Address -LastId $lastId -LastHash $last.attributes.hash -LastTimestamp $last.attributes.mined_at
                $lastHash = $last.attributes.hash
                $lastTimestamp = $last.attributes.mined_at
                $lastTimestampUtc = Try-ParseTimestamp -Value $lastTimestamp
                Write-Host "State updated: last_id=$lastId"
            } else {
                Write-Host "No new transactions."
            }
        }
    } catch {
        $pollError = $_.Exception.Message
        Write-Host "Polling error: $pollError"
        Write-LogLine -Path $LogFile -Level "error" -Message "Polling error" -Context @{
            address = $Address
            error = $pollError
        }
    }

    if ($null -eq $startOkxBalance) {
        $startOkxBalance = Try-FetchOkxBalance -ApiBaseUrl $ApiBaseUrl
        if ($null -ne $startOkxBalance) {
            Write-LogLine -Path $LogFile -Level "info" -Message "OKX start balance captured after startup" -Context @{
                address = $Address
                balance = $startOkxBalance
            }
        }
    }

    if ($null -eq $startWhaleBalance -or ($IncludePositions.IsPresent -and $startWhalePositions.Count -eq 0)) {
        $snapshot = Try-FetchWhaleSnapshot `
            -BaseUrl $BaseUrl `
            -Headers $headers `
            -Address $Address `
            -IncludePositions:$IncludePositions `
            -PositionsFilter $PositionsFilter `
            -PositionsSort $PositionsSort `
            -PositionsLimit $PositionsLimit

        if ($snapshot -and $snapshot.Balance) {
            $startWhaleBalance = [double]$snapshot.Balance
            if ($snapshot.Positions.Count -gt 0) {
                $startWhalePositions = $snapshot.Positions
            }

            Write-LogLine -Path $LogFile -Level "info" -Message "Whale snapshot captured after startup" -Context @{
                address = $Address
                balance = $startWhaleBalance
            }

            Update-LatestSnapshot -Path $LatestEventFile -Address $Address -Balance $startWhaleBalance -Positions $startWhalePositions
        }
    }

    if (-not $DisableOkxBalanceLog.IsPresent) {
        try {
            $currentOkxBalance = Try-FetchOkxBalance -ApiBaseUrl $ApiBaseUrl
            if ($null -ne $currentOkxBalance) {
                Append-OkxBalance -Path $OkxBalanceCsv -Balance $currentOkxBalance
            }
        } catch {
            Write-LogLine -Path $LogFile -Level "warn" -Message "OKX balance log failed" -Context @{
                address = $Address
                error = $_.Exception.Message
            }
        }
    }

    try {
        Write-Heartbeat -Path $HeartbeatFile -Payload ([ordered]@{
            address = $Address
            startedAt = $startedAt
            lastHeartbeat = (Get-Date).ToUniversalTime().ToString("o")
            lastPollStatus = if ($pollError) { "error" } else { "ok" }
            lastError = $pollError
            lastTxId = $lastId
            lastTxHash = $lastHash
            lastTxTime = $lastTimestamp
            startOkxBalanceUsdt = $startOkxBalance
            startWhaleBalanceUsdt = $startWhaleBalance
            startWhalePositions = $startWhalePositions
        })
    } catch {
        Write-LogLine -Path $LogFile -Level "warn" -Message "Failed to write heartbeat" -Context @{
            address = $Address
            error = $_.Exception.Message
        }
    }

    if ($Once.IsPresent) {
        break
    }
    Start-Sleep -Seconds $IntervalSeconds
} while ($true)

