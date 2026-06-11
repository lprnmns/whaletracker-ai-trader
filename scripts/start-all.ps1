param(
    [string]$Address = "0xc82b2e484b161d20eae386877d57c4e5807b5581",
    [int]$DelaySeconds = 2
)

$root = Resolve-Path (Join-Path $PSScriptRoot "..")

Start-Process powershell -WorkingDirectory $root -ArgumentList @(
    "-NoExit",
    "-Command",
    "cd `"$root`"; dotnet run --project src\WhaleTracker.API"
)

Start-Sleep -Seconds $DelaySeconds

Start-Process powershell -WorkingDirectory $root -ArgumentList @(
    "-NoExit",
    "-Command",
    "cd `"$root`"; python scripts\market_comparison_service.py"
)

Start-Sleep -Seconds $DelaySeconds

Start-Process powershell -WorkingDirectory $root -ArgumentList @(
    "-NoExit",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    "$root\\scripts\\zerion-watch.ps1",
    "-Address",
    $Address,
    "-StartFromLatest",
    "-OnlyNonTrash",
    "-OperationTypes",
    "trade",
    "-SendToAi",
    "-IncludePositions",
    "-Execute"
)
