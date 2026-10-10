param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'Diagnostics'),
    [switch]$SkipDump
)
$ErrorActionPreference = 'Stop'
$collectionRoot = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('Crash-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $collectionRoot) { throw 'This crash collection already exists.' }
New-Item -ItemType Directory -Path $collectionRoot | Out-Null

# Read existing records only. No process attach, game calls, configuration
# changes, forced crashes, or process termination are performed.
$events = @(Get-WinEvent -FilterHashtable @{ LogName='Application'; Id=1000,1001; StartTime=(Get-Date).AddDays(-2) } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'forzahorizon6.exe|Microsoft.ForteBaseGame' } |
    Select-Object -First 12 TimeCreated,Id,RecordId,Message)
ConvertTo-Json -InputObject $events -Depth 4 | Set-Content -LiteralPath (Join-Path $collectionRoot 'Windows-Forza-events.json') -Encoding utf8

$logs = @(Get-ChildItem -LiteralPath ([IO.Path]::GetFullPath($OutputRoot)) -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'session-*.jsonl*' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 6)
foreach ($logFile in $logs) { Copy-Item -LiteralPath $logFile.FullName -Destination $collectionRoot }

$dumpFile = Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'CrashDumps') -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'forzahorizon6.exe.*.dmp' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (!$SkipDump -and $null -ne $dumpFile) { Copy-Item -LiteralPath $dumpFile.FullName -Destination $collectionRoot }

[ordered]@{
    CollectedAt = (Get-Date).ToString('o')
    EventCount = $events.Count
    CopiedSessionLogs = @($logs.Name)
    LatestDumpPath = if ($null -ne $dumpFile) { $dumpFile.FullName } else { $null }
    LatestDumpTime = if ($null -ne $dumpFile) { $dumpFile.LastWriteTime.ToString('o') } else { $null }
    DumpCopied = (!$SkipDump -and $null -ne $dumpFile)
    GameModified = $false
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $collectionRoot 'collection.json') -Encoding utf8
Write-Host "Crash records saved to: $collectionRoot"
if ($null -eq $dumpFile) { Write-Host 'No existing Windows crash dump was available; events and action logs are still saved.' }
