# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Samples what the JIM database is doing, and the host's CPU and disk, while an integration run executes.

.DESCRIPTION
    Phase 0 of the PostgreSQL tuning experiments (#2834,
    engineering/plans/doing/POSTGRESQL_TUNING_EXPERIMENTS.md) asks, for each Run Profile step, for what share of the
    step the database was busy and what it was waiting on. This script records the raw material; the step windows
    come from the run's own logs afterwards.

    Into -OutputDirectory it writes:

    - activity.tsv: once a second, one row per JIM connection: sample time, pid, state, wait event type, wait event,
      seconds the current query has run, and the first 80 characters of the query. Taken by one long-lived psql
      session (\watch), which leaves itself out, so sampling costs one cheap query a second.
    - host.tsv: once a second, host CPU (user, system, idle, iowait, steal) and blocks read and written, from vmstat.
    - disks.tsv: every -SnapshotSeconds, each block device's cumulative reads, writes and milliseconds spent doing
      I/O, from /proc/diskstats. Successive rows give each disk's utilisation.
    - stats/<time>.json: every -SnapshotSeconds, the cumulative pg_stat_statements (top 300 statements by total
      time), pg_stat_wal, pg_stat_io and checkpointer figures. Differences between snapshots give each window's
      statements, WAL and checkpoints.

    It runs until -StopFile exists, or until no process matching -WhileProcess is running. Start it detached once
    the database is up; it creates pg_stat_statements if the extension is missing (the development compose override
    preloads the library).

.PARAMETER OutputDirectory
    Where to write the samples. Created if missing.

.PARAMETER DatabaseContainer
    The database container. Defaults to jim.database.

.PARAMETER SnapshotSeconds
    How often to take the cumulative statistics snapshots. Defaults to 30.

.PARAMETER StopFile
    Stop when this file exists.

.PARAMETER WhileProcess
    Stop when no process's command line matches this pattern, for example 'Run-IntegrationTests'.

.EXAMPLE
    pwsh ./test/integration/utils/Watch-DatabaseActivity.ps1 -OutputDirectory ~/jim-orchestration/2834-phase0 -WhileProcess 'Run-IntegrationTests'
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$DatabaseContainer = 'jim.database',

    [int]$SnapshotSeconds = 30,

    [string]$StopFile,

    [string]$WhileProcess
)

$ErrorActionPreference = 'Stop'

if (-not $StopFile -and -not $WhileProcess) {
    throw 'Give -StopFile or -WhileProcess, so the sampler knows when to stop.'
}

$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$statsDirectory = Join-Path $OutputDirectory 'stats'
$null = New-Item -ItemType Directory -Force -Path $statsDirectory

function Invoke-DatabaseQuery([string]$Sql) {
    $Sql | docker exec -i -e PGAPPNAME=jim-activity-sampler $DatabaseContainer psql -U jim -d jim -X -At -v ON_ERROR_STOP=1 -f - 2>&1
}

function Test-ShouldContinue {
    if ($StopFile -and (Test-Path $StopFile)) { return $false }
    if ($WhileProcess) {
        $running = Get-Process | Where-Object { $_.CommandLine -and $_.CommandLine -match $WhileProcess -and $_.Id -ne $PID }
        if (-not $running) { return $false }
    }
    return $true
}

# Wait for the database to accept connections; the runner may still be starting it.
$deadline = (Get-Date).AddMinutes(30)
while ((Invoke-DatabaseQuery 'SELECT 1' | Select-Object -First 1) -ne '1') {
    if ((Get-Date) -gt $deadline) { throw "The database in $DatabaseContainer did not accept connections within 30 minutes." }
    if (-not (Test-ShouldContinue)) { return }
    Start-Sleep -Seconds 5
}
Invoke-DatabaseQuery 'CREATE EXTENSION IF NOT EXISTS pg_stat_statements' | Out-Null

# Activity: one psql session, re-running the query every second.
$activitySql = @'
\pset footer off
SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS'),
       pid, state, coalesce(wait_event_type, ''), coalesce(wait_event, ''),
       round(extract(epoch FROM clock_timestamp() - query_start)::numeric, 1),
       left(regexp_replace(query, '\s+', ' ', 'g'), 80)
FROM pg_stat_activity
WHERE datname = current_database() AND backend_type = 'client backend' AND application_name <> 'jim-activity-sampler'
  AND state IS DISTINCT FROM 'idle'
\watch 1
'@
function Start-ShellPipeline([string]$Command) {
    # ArgumentList passes the command to bash as one argument, untouched by PowerShell's own quoting.
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new('bash')
    $startInfo.ArgumentList.Add('-c')
    $startInfo.ArgumentList.Add($Command)
    $startInfo.UseShellExecute = $false
    [System.Diagnostics.Process]::Start($startInfo)
}

$activityPath = Join-Path $OutputDirectory 'activity.tsv'
$activitySqlPath = Join-Path $OutputDirectory 'activity.sql'
Set-Content -Path $activitySqlPath -Value $activitySql
"time`tpid`tstate`twait_type`twait_event`tquery_seconds`tquery" | Set-Content -Path $activityPath
$activityProcess = Start-ShellPipeline (
    'docker exec -i -e PGAPPNAME=jim-activity-sampler {0} psql -U jim -d jim -X -At -q -F "$(printf ''\t'')" < "{1}" >> "{2}" 2>/dev/null' -f $DatabaseContainer, $activitySqlPath, $activityPath)

# Host CPU and block I/O, once a second.
$hostPath = Join-Path $OutputDirectory 'host.tsv'
"time`trun`tblocked`tblocks_in`tblocks_out`tuser`tsystem`tidle`tiowait`tsteal" | Set-Content -Path $hostPath
$vmstatProcess = Start-ShellPipeline (
    'stdbuf -oL vmstat -n -t -w 1 | awk -v OFS="\t" ''NR==2 { for (i = 1; i <= NF; i++) col[$i] = i } NR>2 { print $(NF-1)"T"$NF, $col["r"], $col["b"], $col["bi"], $col["bo"], $col["us"], $col["sy"], $col["id"], $col["wa"], $col["st"]; fflush() }'' >> "HOST_PATH"'.Replace('HOST_PATH', $hostPath))

$disksPath = Join-Path $OutputDirectory 'disks.tsv'
"time`tdevice`treads_completed`tsectors_read`twrites_completed`tsectors_written`tio_ms`tweighted_io_ms" | Set-Content -Path $disksPath

$snapshotSql = @'
SELECT json_build_object(
  'time', to_char(clock_timestamp() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS'),
  'statements', (SELECT coalesce(json_agg(s), '[]') FROM (
      SELECT queryid, calls, round(total_exec_time::numeric, 1) AS total_ms, round(mean_exec_time::numeric, 2) AS mean_ms,
             rows, shared_blks_hit, shared_blks_read, shared_blks_written, temp_blks_written, wal_bytes,
             left(regexp_replace(query, '\s+', ' ', 'g'), 300) AS query
      FROM pg_stat_statements ORDER BY total_exec_time DESC LIMIT 300) s),
  'wal', (SELECT row_to_json(w) FROM pg_stat_wal w),
  'checkpointer', (SELECT row_to_json(c) FROM pg_stat_checkpointer c),
  'io', (SELECT json_agg(i) FROM pg_stat_io i WHERE reads > 0 OR writes > 0 OR extends > 0 OR fsyncs > 0))
'@

try {
    while (Test-ShouldContinue) {
        $stamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss')
        foreach ($line in Get-Content '/proc/diskstats') {
            $f = $line.Trim() -split '\s+'
            if ($f[2] -match '^(nvme\d+n\d+|sd[a-z]+|vd[a-z]+|xvd[a-z]+)$') {
                "$stamp`t$($f[2])`t$($f[3])`t$($f[5])`t$($f[7])`t$($f[9])`t$($f[12])`t$($f[13])" | Add-Content -Path $disksPath
            }
        }
        $snapshot = Invoke-DatabaseQuery $snapshotSql
        if ($LASTEXITCODE -eq 0) {
            Set-Content -Path (Join-Path $statsDirectory "$($stamp -replace ':', '').json") -Value ($snapshot -join "`n")
        }
        $waited = 0
        while ($waited -lt $SnapshotSeconds -and (Test-ShouldContinue)) { Start-Sleep -Seconds 1; $waited++ }
    }
}
finally {
    foreach ($process in @($activityProcess, $vmstatProcess)) {
        if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    # The shells' children (docker exec, vmstat) outlive Stop-Process on the shell; end them by their own command lines.
    Get-Process | Where-Object { $_.CommandLine -and ($_.CommandLine -match [regex]::Escape($activitySqlPath) -or $_.CommandLine -match [regex]::Escape($hostPath) -or $_.CommandLine -match "^vmstat -n -t -w 1") } |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    # Ending the sampler's own sessions ends the in-container psql, which a stopped docker exec client can leave behind.
    Invoke-DatabaseQuery "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = 'jim-activity-sampler' AND pid <> pg_backend_pid()" | Out-Null
}
