param(
    [string]$Address,
    [string]$ApiKey = $env:ZERION_API_KEY,
    [string]$BaseUrl = "https://api.zerion.io/v1",
    [Alias("Limit")]
    [int]$PageSize = 100,
    [switch]$OnlyNonTrash,
    [string[]]$TransactionOperationTypes,
    [string]$XEnv,
    [ValidateSet("only_simple", "only_complex", "no_filter")]
    [string]$PositionsFilter,
    [string]$PositionsSort,
    [string]$SubscriptionId,
    [string[]]$ChainIds,
    [switch]$IncludeSubscriptions,
    [switch]$IncludeChains,
    [switch]$CreateSubscription,
    [string]$CallbackUrl,
    [string[]]$SubscriptionAddresses,
    [string[]]$SubscriptionChainIds,
    [ValidateSet("auto", "raw", "base64", "bearer")]
    [string]$AuthMode = "auto"
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
        "bearer" {
            return "Bearer $Key"
        }
        default {
            return "Basic $Key"
        }
    }
}

function Get-PropertyNames {
    param([object]$Obj)
    if ($null -eq $Obj) {
        return @()
    }
    return @($Obj.PSObject.Properties.Name)
}

function Get-PropValue {
    param(
        [object]$Obj,
        [string]$Name
    )
    if ($null -eq $Obj) {
        return $null
    }
    if ((Get-PropertyNames -Obj $Obj) -contains $Name) {
        return $Obj.$Name
    }
    return $null
}

$modes = @()
if ($AuthMode -eq "auto") {
    $modes = @("raw", "base64", "bearer")
} else {
    $modes = @($AuthMode)
}

$transactionsQuery = "?currency=usd&page[size]=$PageSize"
if ($OnlyNonTrash.IsPresent) {
    $transactionsQuery += "&filter[trash]=only_non_trash"
}
if ($TransactionOperationTypes -and $TransactionOperationTypes.Count -gt 0) {
    $transactionsQuery += "&filter[operation_types]=$([string]::Join(',', $TransactionOperationTypes))"
}

$positionsQuery = "?currency=usd"
if (-not [string]::IsNullOrWhiteSpace($PositionsFilter)) {
    $positionsQuery += "&filter[positions]=$PositionsFilter"
}
if (-not [string]::IsNullOrWhiteSpace($PositionsSort)) {
    $positionsQuery += "&sort=$PositionsSort"
}
if ($OnlyNonTrash.IsPresent) {
    $positionsQuery += "&filter[trash]=only_non_trash"
}

$targets = @(
    @{ Name = "transactions"; Path = "/wallets/$Address/transactions/"; Query = $transactionsQuery },
    @{ Name = "portfolio"; Path = "/wallets/$Address/portfolio"; Query = "?currency=usd" },
    @{ Name = "positions"; Path = "/wallets/$Address/positions/"; Query = $positionsQuery }
)

if ($IncludeChains.IsPresent) {
    $targets += @{ Name = "chains"; Path = "/chains/"; Query = "" }
}

if ($IncludeSubscriptions.IsPresent -or -not [string]::IsNullOrWhiteSpace($SubscriptionId)) {
    $subQuery = ""
    if ($ChainIds -and $ChainIds.Count -gt 0) {
        $subQuery = "?filter[chain_ids]=$([string]::Join(',', $ChainIds))"
    }
    $targets += @{ Name = "tx-subscriptions"; Path = "/tx-subscriptions/"; Query = $subQuery }

    if (-not [string]::IsNullOrWhiteSpace($SubscriptionId)) {
        $targets += @{ Name = "tx-subscription"; Path = "/tx-subscriptions/$SubscriptionId"; Query = "" }
        $targets += @{ Name = "tx-subscription-wallets"; Path = "/tx-subscriptions/$SubscriptionId/wallets"; Query = "" }
        $targets += @{ Name = "tx-subscription-wallets-count"; Path = "/tx-subscriptions/$SubscriptionId/wallets/count"; Query = "" }
    }
}

$createAttempted = $false

foreach ($mode in $modes) {
    $headers = @{
        Authorization = (New-AuthHeader -Mode $mode -Key $ApiKey)
        Accept = "application/json"
    }
    if (-not [string]::IsNullOrWhiteSpace($XEnv)) {
        $headers["X-Env"] = $XEnv
    }

    Write-Host ""
    Write-Host "=== Auth mode: $mode ==="

    foreach ($target in $targets) {
        $url = "$BaseUrl$($target.Path)$($target.Query)"
        Write-Host ""
        Write-Host "=== $($target.Name) ==="
        Write-Host $url

        try {
            $resp = Invoke-RestMethod -Method Get -Headers $headers -Uri $url -ErrorAction Stop
            $keys = (Get-PropertyNames -Obj $resp) -join ", "
            Write-Host "Top-level keys: $keys"

            $respProps = Get-PropertyNames -Obj $resp
            if ($respProps -contains "data" -and $null -ne $resp.data) {
                $items = @($resp.data)
                if ($items.Count -gt 0) {
                    $first = $items[0]
                    $firstProps = Get-PropertyNames -Obj $first

                    if ($firstProps -contains "type") {
                        $type = $first.type
                    } else {
                        $type = ""
                    }
                    if ($firstProps -contains "id") {
                        $id = $first.id
                    } else {
                        $id = ""
                    }
                    if (-not [string]::IsNullOrWhiteSpace($type) -or -not [string]::IsNullOrWhiteSpace($id)) {
                        Write-Host "First item: type=$type id=$id"
                    }

                    if ($firstProps -contains "attributes" -and $null -ne $first.attributes) {
                        $attrKeys = (Get-PropertyNames -Obj $first.attributes) -join ", "
                        if (-not [string]::IsNullOrWhiteSpace($attrKeys)) {
                            Write-Host "Attributes: $attrKeys"
                        }
                    }

                    if ($firstProps -contains "relationships" -and $null -ne $first.relationships) {
                        $relKeys = (Get-PropertyNames -Obj $first.relationships) -join ", "
                        if (-not [string]::IsNullOrWhiteSpace($relKeys)) {
                            Write-Host "Relationships: $relKeys"
                        }
                    }

                    if ($target.Name -eq "transactions") {
                        $attr = Get-PropValue -Obj $first -Name "attributes"
                        if ($attr) {
                            $opType = Get-PropValue -Obj $attr -Name "operation_type"
                            $hash = Get-PropValue -Obj $attr -Name "hash"
                            $minedAt = Get-PropValue -Obj $attr -Name "mined_at"
                            $status = Get-PropValue -Obj $attr -Name "status"
                            $sentFrom = Get-PropValue -Obj $attr -Name "sent_from"
                            $sentTo = Get-PropValue -Obj $attr -Name "sent_to"
                            Write-Host "Tx: operation_type=$opType status=$status mined_at=$minedAt"
                            Write-Host "Tx: hash=$hash from=$sentFrom to=$sentTo"
                        }

                        $chainId = $null
                        $dappId = $null
                        $rels = Get-PropValue -Obj $first -Name "relationships"
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

                        if (-not [string]::IsNullOrWhiteSpace($chainId) -or -not [string]::IsNullOrWhiteSpace($dappId)) {
                            Write-Host "Tx: chain=$chainId dapp=$dappId"
                        }

                        $transfers = $null
                        $attr = Get-PropValue -Obj $first -Name "attributes"
                        if ($attr) {
                            $transfers = Get-PropValue -Obj $attr -Name "transfers"
                        }
                        if ($transfers) {
                            $items = @($transfers)
                            $max = [Math]::Min($items.Count, 2)
                            for ($i = 0; $i -lt $max; $i++) {
                                $t = $items[$i]
                                $dir = Get-PropValue -Obj $t -Name "direction"
                                $value = Get-PropValue -Obj $t -Name "value"
                                $qty = $null
                                $symbol = $null
                                $quantity = Get-PropValue -Obj $t -Name "quantity"
                                if ($quantity) {
                                    $qty = Get-PropValue -Obj $quantity -Name "float"
                                }
                                $fi = Get-PropValue -Obj $t -Name "fungible_info"
                                if ($fi) {
                                    $symbol = Get-PropValue -Obj $fi -Name "symbol"
                                }
                                Write-Host "Transfer[$i]: dir=$dir qty=$qty symbol=$symbol value=$value"
                            }
                        }
                    } elseif ($target.Name -eq "positions") {
                        $attr = Get-PropValue -Obj $first -Name "attributes"
                        if ($attr) {
                            $fi = Get-PropValue -Obj $attr -Name "fungible_info"
                            $symbol = $null
                            if ($fi) {
                                $symbol = Get-PropValue -Obj $fi -Name "symbol"
                            }
                            $qty = $null
                            $quantity = Get-PropValue -Obj $attr -Name "quantity"
                            if ($quantity) {
                                $qty = Get-PropValue -Obj $quantity -Name "float"
                            }
                            $value = Get-PropValue -Obj $attr -Name "value"
                            Write-Host "Position: symbol=$symbol qty=$qty value=$value"
                        }
                    } elseif ($target.Name -eq "portfolio") {
                        $attr = Get-PropValue -Obj $first -Name "attributes"
                        if ($attr) {
                            $total = Get-PropValue -Obj $attr -Name "total"
                            if ($total) {
                                $positions = Get-PropValue -Obj $total -Name "positions"
                                if ($null -ne $positions) {
                                    Write-Host "Portfolio total.positions=$positions"
                                } else {
                                    $value = Get-PropValue -Obj $total -Name "value"
                                    $currency = Get-PropValue -Obj $total -Name "currency"
                                    if ($null -ne $value) {
                                        Write-Host "Portfolio total.value=$value currency=$currency"
                                    }
                                }
                            }
                        }
                    }
                } else {
                    Write-Host "Data: empty"
                }
            }
        } catch {
            Write-Host "Request failed: $($_.Exception.Message)"
            $ex = $_.Exception
            $resp = $null
            if ($ex -and ($ex -is [System.Net.WebException]) -and $ex.Response) {
                $resp = $ex.Response
            } elseif ($ex -and (Get-PropertyNames -Obj $ex) -contains "Response") {
                $resp = $ex.Response
            }
            if ($resp -and $resp.GetResponseStream()) {
                try {
                    $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
                    $body = $reader.ReadToEnd()
                    if ($body) {
                        Write-Host "Error body: $body"
                    }
                } catch {
                }
            }
        }
    }

    if ($CreateSubscription.IsPresent -and -not $createAttempted) {
        if ([string]::IsNullOrWhiteSpace($CallbackUrl)) {
            throw "CallbackUrl is required when -CreateSubscription is set."
        }

        $createAttempted = $true

        $subAddresses = @()
        if ($SubscriptionAddresses -and $SubscriptionAddresses.Count -gt 0) {
            $subAddresses = $SubscriptionAddresses
        } else {
            $subAddresses = @($Address)
        }

        $subBody = @{
            callback_url = $CallbackUrl
            addresses = $subAddresses
        }
        if ($SubscriptionChainIds -and $SubscriptionChainIds.Count -gt 0) {
            $subBody.chain_ids = $SubscriptionChainIds
        }

        $subUrl = "$BaseUrl/tx-subscriptions/"
        Write-Host ""
        Write-Host "=== tx-subscriptions (create) ==="
        Write-Host $subUrl

        try {
            $jsonBody = ($subBody | ConvertTo-Json -Depth 5)
            $resp = Invoke-RestMethod -Method Post -Headers $headers -ContentType "application/json" -Uri $subUrl -Body $jsonBody -ErrorAction Stop
            $keys = (Get-PropertyNames -Obj $resp) -join ", "
            Write-Host "Top-level keys: $keys"
        } catch {
            Write-Host "Request failed: $($_.Exception.Message)"
            $ex = $_.Exception
            $resp = $null
            if ($ex -and ($ex -is [System.Net.WebException]) -and $ex.Response) {
                $resp = $ex.Response
            } elseif ($ex -and (Get-PropertyNames -Obj $ex) -contains "Response") {
                $resp = $ex.Response
            }
            if ($resp -and $resp.GetResponseStream()) {
                try {
                    $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
                    $body = $reader.ReadToEnd()
                    if ($body) {
                        Write-Host "Error body: $body"
                    }
                } catch {
                }
            }
        }
    }
}
