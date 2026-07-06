# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Periodically samples `docker stats` and appends one CSV row per container per interval.

.DESCRIPTION
    Runs in a PowerShell background job for the duration of a scenario run so we can
    correlate per-container memory and CPU with sync operations. The CSV is line-flushed
    (one Out-File per row) so a mid-run crash still leaves interpretable data on disk.

    Columns:
        timestamp_utc, container, cpu_perc, mem_usage_bytes, mem_limit_bytes, mem_perc,
        net_rx_bytes, net_tx_bytes, block_read_bytes, block_write_bytes, pids

.PARAMETER OutputPath
    Target CSV path. Created with a header row if missing.

.PARAMETER IntervalSeconds
    Sample interval in seconds. Defaults to 2.

.PARAMETER ParentPid
    PID of the launching process. When supplied, the sampler exits on its own once that
    process no longer exists, so a crashed or hard-killed runner cannot leak a sampler
    that appends to the CSV forever (#918). 0 (the default) disables the check.
#>

param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [int]$IntervalSeconds = 2,
    [int]$ParentPid = 0
)

$ErrorActionPreference = 'Continue'

function ConvertTo-Bytes {
    param([string]$Value)
    # docker stats emits values like "512MiB", "1.5GiB", "128kB", "2.3MB / 16GiB"
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -eq '--') {
        return 0
    }
    $m = [regex]::Match($Value.Trim(), '^\s*([0-9.]+)\s*([KMGT]?i?B)\s*$', 'IgnoreCase')
    if (-not $m.Success) { return 0 }
    $num = [double]$m.Groups[1].Value
    switch ($m.Groups[2].Value.ToLowerInvariant()) {
        'b'   { return [long]$num }
        'kb'  { return [long]($num * 1000) }
        'kib' { return [long]($num * 1024) }
        'mb'  { return [long]($num * 1000 * 1000) }
        'mib' { return [long]($num * 1024 * 1024) }
        'gb'  { return [long]($num * 1000 * 1000 * 1000) }
        'gib' { return [long]($num * 1024 * 1024 * 1024) }
        'tb'  { return [long]($num * 1000 * 1000 * 1000 * 1000) }
        'tib' { return [long]($num * 1024 * 1024 * 1024 * 1024) }
        default { return 0 }
    }
}

$dir = Split-Path -Parent $OutputPath
if ($dir -and -not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

if (-not (Test-Path $OutputPath)) {
    'timestamp_utc,container,cpu_perc,mem_usage_bytes,mem_limit_bytes,mem_perc,net_rx_bytes,net_tx_bytes,block_read_bytes,block_write_bytes,pids' |
        Out-File -FilePath $OutputPath -Encoding UTF8 -Append
}

$format = '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}|{{.MemPerc}}|{{.NetIO}}|{{.BlockIO}}|{{.PIDs}}'

# The runner signals a graceful stop by creating this file next to the CSV; it is the
# reliable stop primitive because the Start-Process handle the runner holds only tracks
# the .NET global-tool shim, not the dotnet child that actually runs this script (#918).
$stopFilePath = "$OutputPath.stop"

while ($true) {
    # Self-termination checks run first so exit latency is at most one interval.
    if (Test-Path -LiteralPath $stopFilePath) {
        break
    }
    if ($ParentPid -gt 0 -and -not (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue)) {
        # The launching runner has died (crash, hard kill, host shutdown); stop sampling
        # rather than appending to a historical CSV forever.
        break
    }

    try {
        $ts = (Get-Date).ToUniversalTime().ToString('o')
        $output = docker stats --no-stream --format $format 2>$null

        $rows = foreach ($line in ($output -split "`n")) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $parts = $line.Trim() -split '\|'
            if ($parts.Length -lt 7) { continue }

            $container = $parts[0]
            $cpuPerc = ($parts[1] -replace '%', '').Trim()
            $memParts = $parts[2] -split '/'
            $memUsage = if ($memParts.Length -ge 1) { ConvertTo-Bytes $memParts[0] } else { 0 }
            $memLimit = if ($memParts.Length -ge 2) { ConvertTo-Bytes $memParts[1] } else { 0 }
            $memPerc = ($parts[3] -replace '%', '').Trim()
            $netParts = $parts[4] -split '/'
            $netRx = if ($netParts.Length -ge 1) { ConvertTo-Bytes $netParts[0] } else { 0 }
            $netTx = if ($netParts.Length -ge 2) { ConvertTo-Bytes $netParts[1] } else { 0 }
            $blockParts = $parts[5] -split '/'
            $blockRead = if ($blockParts.Length -ge 1) { ConvertTo-Bytes $blockParts[0] } else { 0 }
            $blockWrite = if ($blockParts.Length -ge 2) { ConvertTo-Bytes $blockParts[1] } else { 0 }
            $pids = $parts[6].Trim()

            "${ts},${container},${cpuPerc},${memUsage},${memLimit},${memPerc},${netRx},${netTx},${blockRead},${blockWrite},${pids}"
        }

        if ($rows) {
            # Single append per sample so each tick is durable on disk.
            $rows | Out-File -FilePath $OutputPath -Encoding UTF8 -Append
        }
    }
    catch {
        # Never fail the integration run because of a stats hiccup.
        "${ts},__capture_error__,,,,,,,,,${_}" | Out-File -FilePath $OutputPath -Encoding UTF8 -Append
    }

    Start-Sleep -Seconds $IntervalSeconds
}
