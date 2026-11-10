# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Snapshots the JIM database at a point in a scale run, then replays the same Run Profiles from it under
    different PostgreSQL settings, for the PostgreSQL tuning experiments (#2834).

.DESCRIPTION
    A full Scale500k25kGroups run takes over an hour, and its heaviest database work is the Source Full
    Synchronisation (about 17 minutes). Phase 1 of engineering/plans/doing/POSTGRESQL_TUNING_EXPERIMENTS.md
    replays just that work from a snapshot, so each setting costs about 20 minutes instead of 65.

    -Action Snapshot runs the -PrepareRunProfile Run Profiles (by default both directories' scoped Full
    Imports) against a stack the runner has set up with -SetupOnly, shuts the database down cleanly, and
    copies its whole volume to -SnapshotVolume.

    The copy is of the volume, not a CREATE DATABASE ... TEMPLATE copy, on purpose. PostgreSQL saves its
    per-table activity counters at a clean shutdown and reloads them at startup; a template copy starts them
    at zero. Those counters are what autovacuum uses to decide when to analyse a table, so without them the
    tables just imported are never re-analysed during the replay, the planner works from different
    statistics than a full run does, and the replay runs different plans (in the first attempt, no JIT on
    the page-load query that a full run JIT-compiles every time).

    -Action Replay, -Repeat times:
      1. stops the Worker, web, Scheduler and database, and recreates the database container with the template's
         runner profile plus -DatabaseSetting (the same JIM_DB_* and JIM_DB_EXTRA_ARGS variables the runner
         sets), then checks PostgreSQL reports every -DatabaseSetting as set on its command line;
      2. before starting it, restores the database volume from the snapshot; after, resets pg_stat_statements
         and the WAL, I/O and checkpointer statistics (never the per-table counters, for the reason above);
      3. starts JIM again, starts Watch-DatabaseActivity.ps1, and runs each -ReplayRunProfile in turn;
      4. appends one row per Run Profile to results.tsv in -OutputDirectory: its time, status, and the
         database's WAL, checkpoints, temporary files and JIT time across the whole replay.
    Each replay's sampler output goes to its own directory beside results.tsv.

    Directory state is restored only for the -DirectoryContainer containers. The default Run Profiles only synchronise, which
    never touches the directories, so those replays depend on the database alone. To replay an Export, take the
    snapshot with -DirectoryContainer as well, so every replay starts from the directory as it was then.

    Run it from the repository root of the checkout that started the stack, with COMPOSE_PROJECT_NAME set
    as it was for the runner. It stops and recreates containers: never point it at a stack another run owns.

.PARAMETER Action
    Snapshot or Replay.

.PARAMETER Template
    The template the stack was set up with; picks the runner's PostgreSQL profile. Defaults to
    Scale500k25kGroups.

.PARAMETER DatabaseSetting
    Replay only: extra PostgreSQL settings, as for the runner's -DatabaseSetting.

.PARAMETER Label
    Replay only: a name for this configuration in results.tsv. Defaults to the settings, or 'baseline'.

.PARAMETER Repeat
    Replay only: how many times to replay. Defaults to 1.

.PARAMETER OutputDirectory
    Where results.tsv and the sampler output go. Defaults to ~/jim-orchestration/2834-phase1.

.PARAMETER SnapshotVolume
    The Docker volume holding the snapshot. Defaults to the lane's database volume name plus -tuning-snapshot.

.PARAMETER PrepareRunProfile
    Snapshot only: Run Profiles to run before taking the snapshot, each 'Connected System|Run Profile'.

.PARAMETER ReplayRunProfile
    Replay only: Run Profiles to time, each 'Connected System|Run Profile', in order.

.PARAMETER PrewarmWalGB
    Replay only: before the statistics are reset, write this many GB of throwaway WAL (logical messages,
    which touch no table) and checkpoint, so PostgreSQL holds recycled WAL files as a server that has been
    running a while does. The snapshot holds few, so without this a setting that keeps more WAL (a larger
    max_wal_size) pays for creating its files inside the timed replay. 0, the default, skips it.

.PARAMETER DirectoryContainer
    Directory containers whose data volumes go with the snapshot, such as openldap-primary. Snapshot: each is
    stopped and its volumes copied into volumes named after -SnapshotVolume. Replay: they are restored before
    each replay. Needed for any Run Profile that writes to a directory, such as an Export. The connector files
    volume is left alone.

.PARAMETER ConnectedSystemSetting
    Replay only: Connected System settings to apply after the snapshot is restored and before the Run
    Profiles run, keyed by Connected System name. Each value is a hashtable of setting name to value, where
    MaxExportParallelism sets that property and any other name is a Connector setting (such as
    'Export Concurrency'), given as an integer, a boolean or a string to match its type.

.EXAMPLE
    pwsh ./test/integration/utils/Invoke-DatabaseTuningReplay.ps1 -Action Replay -DatabaseSetting @{ jit = 'off' } -Repeat 2

.EXAMPLE
    ./test/integration/utils/Invoke-DatabaseTuningReplay.ps1 -Action Replay -DirectoryContainer openldap-primary -SnapshotVolume jim-db-volume-export-snapshot -ReplayRunProfile 'Glitterband EMEA|Export' -ConnectedSystemSetting @{ 'Glitterband EMEA' = @{ MaxExportParallelism = 4; 'Export Concurrency' = 8 } }

    Replays the Target Export from a snapshot taken with the same -DirectoryContainer, with four export workers of eight
    operations each.
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Snapshot', 'Replay')]
    [string]$Action,

    [string]$Template = 'Scale500k25kGroups',

    [hashtable]$DatabaseSetting = @{},

    [string]$Label,

    [ValidateRange(1, 20)]
    [int]$Repeat = 1,

    [string]$OutputDirectory = (Join-Path $HOME 'jim-orchestration/2834-phase1'),

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$SnapshotVolume,

    [string[]]$PrepareRunProfile = @('Yellowstone APAC|Full Import (Scoped)', 'Glitterband EMEA|Full Import (Scoped)'),

    [string[]]$ReplayRunProfile = @('Yellowstone APAC|Full Sync', 'Glitterband EMEA|Full Sync'),

    [ValidateRange(0, 128)]
    [int]$PrewarmWalGB = 0,

    [string[]]$DirectoryContainer = @(),

    [hashtable]$ConnectedSystemSetting = @{}
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Location).Path
if (-not (Test-Path (Join-Path $repoRoot 'docker-compose.override.yml'))) {
    throw 'Run this from the root of the repository checkout that started the stack.'
}
. (Join-Path $PSScriptRoot 'Test-Helpers.ps1')
. (Join-Path $PSScriptRoot 'IntegrationLane.ps1')

$lane = Get-IntegrationLane
$composeArgs = Get-JimComposeArgs
$databaseContainer = $lane.DatabaseContainer
$databaseVolume = $lane.DbVolume
if (-not $SnapshotVolume) { $SnapshotVolume = "$databaseVolume-tuning-snapshot" }
$applicationServices = @('jim.worker', 'jim.web', 'jim.scheduler')
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory

function Invoke-Sql([string]$Sql, [string]$Database = 'jim') {
    $output = $Sql | docker exec -i $databaseContainer psql -U jim -d $Database -X -At -v ON_ERROR_STOP=1 -f - 2>&1
    if ($LASTEXITCODE -ne 0) { throw "SQL failed on $Database`: $output" }
    $output
}

function Wait-Database {
    $deadline = (Get-Date).AddMinutes(5)
    while ($true) {
        docker exec $databaseContainer pg_isready -U jim -d postgres 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { return }
        if ((Get-Date) -gt $deadline) { throw "$databaseContainer did not become ready within 5 minutes." }
        Start-Sleep -Seconds 2
    }
}

function Stop-Database {
    # A clean shutdown, or PostgreSQL discards its activity counters on the next start (crash recovery). Fast
    # shutdown checkpoints first, which can take a while with a large shared_buffers, hence the long timeout.
    docker compose @composeArgs stop -t 600 jim.database 2>&1 | Out-Null
    $exitCode = docker inspect $databaseContainer --format '{{.State.ExitCode}}'
    if ($exitCode -ne '0') { throw "$databaseContainer did not shut down cleanly (exit code $exitCode)." }
}

function Copy-Volume([string]$From, [string]$To) {
    # The database's own image does the copy: it is always present, unlike the harness's busybox.
    $image = docker inspect $databaseContainer --format '{{.Config.Image}}'
    docker volume create $To | Out-Null
    $output = docker run --rm --user 0 --entrypoint sh -v "${From}:/from:ro" -v "${To}:/to" $image -c 'find /to -mindepth 1 -delete && cp -a /from/. /to/' 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Copying volume $From to $To failed: $output" }
}

function Get-DirectoryVolume {
    # The named data volumes of the -DirectoryContainer containers, each with its snapshot volume's name.
    foreach ($container in $DirectoryContainer) {
        $names = docker inspect $container --format '{{if .State.Running}}{{range .Mounts}}{{if eq .Type "volume"}}{{println .Name}}{{end}}{{end}}{{end}}' 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $names) { throw "Directory container $container is not running." }
        foreach ($volume in @($names -split "`n" | Where-Object { $_ -and $_ -ne $lane.ConnectorFilesVolume })) {
            [pscustomobject]@{ Container = $container; Volume = $volume; Snapshot = "$SnapshotVolume.$volume" }
        }
    }
}

function Copy-DirectoryVolume([switch]$Restore) {
    $volumes = @(Get-DirectoryVolume)
    if (-not $volumes) { throw "The directory containers $($DirectoryContainer -join ', ') have no data volume to snapshot or restore." }
    foreach ($group in ($volumes | Group-Object Container)) {
        docker stop -t 300 $group.Name | Out-Null
        foreach ($entry in $group.Group) {
            if ($Restore) {
                docker volume inspect $entry.Snapshot 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "There is no directory snapshot volume $($entry.Snapshot); take the snapshot with -DirectoryContainer." }
                Copy-Volume -From $entry.Snapshot -To $entry.Volume
            }
            else { Copy-Volume -From $entry.Volume -To $entry.Snapshot }
            Write-Host "  Directory volume $($entry.Volume) $(if ($Restore) { 'restored from' } else { 'copied to' }) $($entry.Snapshot)"
        }
        docker start $group.Name | Out-Null
        $deadline = (Get-Date).AddMinutes(10)
        while ((docker inspect $group.Name --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}healthy{{end}}') -ne 'healthy') {
            if ((Get-Date) -gt $deadline) { throw "$($group.Name) did not become healthy within 10 minutes." }
            Start-Sleep -Seconds 3
        }
    }
}

function Set-ReplayConnectedSystemSetting {
    foreach ($systemName in $ConnectedSystemSetting.Keys) {
        $system = Get-JIMConnectedSystem | Where-Object { $_.name -eq $systemName }
        if (-not $system) { throw "No Connected System named '$systemName'." }
        $detail = Get-JIMConnectedSystem -Id $system.id
        $definition = Get-JIMConnectorDefinition -Id $detail.connector.id
        $settingValues = @{}
        foreach ($name in $ConnectedSystemSetting[$systemName].Keys) {
            $value = $ConnectedSystemSetting[$systemName][$name]
            if ($name -eq 'MaxExportParallelism') {
                Set-JIMConnectedSystem -Id $system.id -MaxExportParallelism ([int]$value) | Out-Null
            }
            else {
                $setting = $definition.settings | Where-Object { $_.name -eq $name }
                if (-not $setting) { throw "Connector '$($detail.connector.name)' has no setting named '$name'." }
                $settingValues[$setting.id] = switch ($value) {
                    { $_ -is [bool] } { @{ checkboxValue = $value }; break }
                    { $_ -is [int] } { @{ intValue = $value }; break }
                    default { @{ stringValue = "$value" } }
                }
            }
            Write-Host "  $systemName`: $name = $value"
        }
        if ($settingValues.Count -gt 0) { Set-JIMConnectedSystem -Id $system.id -SettingValues $settingValues | Out-Null }
    }
}

function Connect-JimForReplay {
    $apiKey = (Get-Content (Join-Path $repoRoot 'test/integration/.api-key') -Raw).Trim()
    Import-Module (Join-Path $repoRoot 'src/JIM.PowerShell/JIM.psd1') -Force
    $deadline = (Get-Date).AddMinutes(10)
    while ($true) {
        try { Connect-JIM -Url $lane.JimUrl -ApiKey $apiKey | Out-Null; return }
        catch {
            if ((Get-Date) -gt $deadline) { throw "JIM at $($lane.JimUrl) did not accept the API key within 10 minutes: $_" }
            Start-Sleep -Seconds 5
        }
    }
}

function Resolve-RunProfile([string]$Spec) {
    $systemName, $profileName = $Spec -split '\|', 2
    $system = Get-JIMConnectedSystem | Where-Object { $_.name -eq $systemName }
    if (-not $system) { throw "No Connected System named '$systemName'." }
    $runProfile = Get-JIMRunProfile -ConnectedSystemId $system.id | Where-Object { $_.name -eq $profileName }
    if (-not $runProfile) { throw "Connected System '$systemName' has no Run Profile named '$profileName'." }
    [pscustomobject]@{ Spec = $Spec; SystemId = $system.id; RunProfileId = $runProfile.id }
}

function Invoke-RunProfile($RunProfile) {
    $result = Start-JIMRunProfile -ConnectedSystemId $RunProfile.SystemId -RunProfileId $RunProfile.RunProfileId -Wait -PassThru -Timeout 7200
    $row = Invoke-Sql "SELECT extract(epoch FROM `"ExecutionTime`"), `"Status`", coalesce(`"ObjectsProcessed`", 0) FROM `"Activities`" WHERE `"Id`" = '$($result.activityId)'"
    $seconds, $status, $objects = $row -split '\|'
    [pscustomobject]@{ Seconds = [double]$seconds; Status = [int]$status; Objects = [int]$objects; ActivityId = $result.activityId }
}

function Get-DatabaseCounters {
    $row = Invoke-Sql @'
SELECT (SELECT wal_bytes FROM pg_stat_wal),
       (SELECT num_requested FROM pg_stat_checkpointer),
       (SELECT num_timed FROM pg_stat_checkpointer),
       (SELECT temp_bytes FROM pg_stat_database WHERE datname = 'jim'),
       (SELECT coalesce(sum(jit_generation_time + jit_inlining_time + jit_optimization_time + jit_emission_time), 0) FROM pg_stat_statements)
'@
    $wal, $requested, $timed, $temp, $jit = $row -split '\|'
    [pscustomobject]@{ WalBytes = [double]$wal; Requested = [int]$requested; Timed = [int]$timed; TempBytes = [double]$temp; JitMs = [double]$jit }
}

if ($Action -eq 'Snapshot') {
    Connect-JimForReplay
    foreach ($spec in $PrepareRunProfile) {
        $outcome = Invoke-RunProfile (Resolve-RunProfile $spec)
        Write-Host "  $spec`: $([math]::Round($outcome.Seconds, 1)) s, status $($outcome.Status), $($outcome.Objects) objects"
        if ($outcome.Status -notin 2, 3) { throw "$spec did not complete (status $($outcome.Status)); no snapshot taken." }
    }
    docker compose @composeArgs stop @applicationServices | Out-Null
    Stop-Database
    Copy-Volume -From $databaseVolume -To $SnapshotVolume
    if ($DirectoryContainer) { Copy-DirectoryVolume }
    docker compose @composeArgs start jim.database | Out-Null
    Wait-Database
    docker compose @composeArgs start @applicationServices | Out-Null
    $size = docker run --rm --entrypoint du -v "${SnapshotVolume}:/snap:ro" (docker inspect $databaseContainer --format '{{.Config.Image}}') -sh /snap
    Write-Host "Snapshot taken in volume $SnapshotVolume ($(($size -split '\s+')[0]))."
    return
}

# Replay
$settingArguments = ConvertTo-PostgresSettingArgument -Setting $DatabaseSetting
if (-not $Label) { $Label = if ($settingArguments) { (Get-PostgresSettingTag -Setting $DatabaseSetting).Substring(4) } else { 'baseline' } }
docker volume inspect $SnapshotVolume 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "There is no snapshot volume $SnapshotVolume; take one with -Action Snapshot first." }

$resultsPath = Join-Path $OutputDirectory 'results.tsv'
if (-not (Test-Path $resultsPath)) {
    "started_utc`tlabel`tsettings`trepeat`trun_profile`tseconds`tstatus`tobjects`treplay_wal_gb`treplay_checkpoints_requested`treplay_checkpoints_timed`treplay_temp_gb`treplay_jit_s`tsampler_directory" |
        Set-Content -Path $resultsPath
}

$databaseProfile = Get-JimDatabaseProfile -Template $Template
for ($iteration = 1; $iteration -le $Repeat; $iteration++) {
    $started = (Get-Date).ToUniversalTime()
    Write-Host "Replay '$Label' $iteration of $Repeat ($started UTC)"

    # 1. The database, restored from the snapshot, on the runner's profile for the template plus the experiment's settings.
    docker compose @composeArgs stop @applicationServices | Out-Null
    Stop-Database
    Copy-Volume -From $SnapshotVolume -To $databaseVolume
    if ($DirectoryContainer) { Copy-DirectoryVolume -Restore }
    $env:JIM_DB_SHARED_BUFFERS = $databaseProfile.SharedBuffers
    $env:JIM_DB_EFFECTIVE_CACHE_SIZE = $databaseProfile.EffectiveCache
    $env:JIM_DB_SHM_SIZE = $databaseProfile.ShmSize
    $env:JIM_DB_MAX_WAL_SIZE = $databaseProfile.MaxWal
    $env:JIM_DB_MIN_WAL_SIZE = $databaseProfile.MinWal
    $env:JIM_DB_MAINTENANCE_WORK_MEM = $databaseProfile.MaintenanceWorkMem
    $env:JIM_DB_WORK_MEM = $databaseProfile.WorkMem
    $env:JIM_DB_WAL_BUFFERS = $databaseProfile.WalBuffers
    $env:JIM_DB_CHECKPOINT_TIMEOUT = $databaseProfile.CheckpointTimeout
    $env:JIM_DB_WAL_COMPRESSION = $databaseProfile.WalCompression
    $env:JIM_DB_EXTRA_ARGS = if ($settingArguments) { $settingArguments } else { $null }
    # --force-recreate: a container whose command did not change would otherwise keep running, warm.
    $output = docker compose @composeArgs --profile with-db up -d --no-deps --force-recreate jim.database 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not recreate $databaseContainer`: $output" }
    Wait-Database
    foreach ($name in $DatabaseSetting.Keys) {
        $row = Invoke-Sql "SELECT source || '|' || current_setting('$name') FROM pg_settings WHERE name = '$name'" -Database postgres
        $source, $value = "$row" -split '\|', 2
        if ($source -ne 'command line') { throw "PostgreSQL did not take $name from the command line (source '$source', value '$value')." }
        Write-Host "  $name = $value"
    }

    # 1b. Optionally, recycled WAL files as on a long-running server; the checkpoint recycles what was written.
    if ($PrewarmWalGB -gt 0) {
        Invoke-Sql "SELECT count(*) FROM (SELECT pg_logical_emit_message(false, 'jim-tuning-prewarm', repeat('x', 1048576)) FROM generate_series(1, $($PrewarmWalGB * 1024))) AS m; CHECKPOINT;" -Database postgres | Out-Null
        $walFiles = Invoke-Sql "SELECT count(*) || ' files, ' || pg_size_pretty(sum(size)) FROM pg_ls_waldir()" -Database postgres
        Write-Host "  WAL prewarmed with $PrewarmWalGB GB: $walFiles"
    }

    # 2. Clean statement, WAL, I/O and checkpointer statistics. Not pg_stat_reset(): it would clear the
    #    per-table counters autovacuum relies on, which is what the volume snapshot exists to keep.
    Invoke-Sql "CREATE EXTENSION IF NOT EXISTS pg_stat_statements; SELECT pg_stat_statements_reset(); SELECT pg_stat_reset_shared('wal'); SELECT pg_stat_reset_shared('checkpointer'); SELECT pg_stat_reset_shared('io');" | Out-Null

    # 3. JIM, the sampler, and the timed Run Profiles.
    docker compose @composeArgs start @applicationServices | Out-Null
    Connect-JimForReplay
    Set-ReplayConnectedSystemSetting
    $runProfiles = @($ReplayRunProfile | ForEach-Object { Resolve-RunProfile $_ })
    $samplerDirectory = Join-Path $OutputDirectory ("{0}-{1}-{2}" -f $started.ToString('yyyyMMdd_HHmmss'), $Label, $iteration)
    $stopFile = "$samplerDirectory.stop"
    $sampler = Start-Process -FilePath 'pwsh' -PassThru -RedirectStandardOutput "$samplerDirectory.sampler.log" -RedirectStandardError "$samplerDirectory.sampler.err" -ArgumentList @(
        '-NoProfile', '-File', (Join-Path $PSScriptRoot 'Watch-DatabaseActivity.ps1'),
        '-OutputDirectory', $samplerDirectory, '-DatabaseContainer', $databaseContainer, '-StopFile', $stopFile)
    $before = Get-DatabaseCounters
    $outcomes = @()
    try {
        foreach ($runProfile in $runProfiles) {
            $outcome = Invoke-RunProfile $runProfile
            Write-Host ("  {0}: {1:N1} s, status {2}, {3} objects" -f $runProfile.Spec, $outcome.Seconds, $outcome.Status, $outcome.Objects)
            $outcomes += [pscustomobject]@{ Spec = $runProfile.Spec; Outcome = $outcome }
            if ($outcome.Status -notin 2, 3) { Write-Warning "$($runProfile.Spec) did not complete; the rest of this replay is skipped."; break }
        }
    }
    finally {
        $after = Get-DatabaseCounters
        New-Item -ItemType File -Path $stopFile -Force | Out-Null
        $sampler | Wait-Process -Timeout 120 -ErrorAction SilentlyContinue
        Remove-Item $stopFile -ErrorAction SilentlyContinue
    }

    # 4. Results.
    $wal = [math]::Round(($after.WalBytes - $before.WalBytes) / 1GB, 2)
    $temp = [math]::Round(($after.TempBytes - $before.TempBytes) / 1GB, 2)
    $jit = [math]::Round(($after.JitMs - $before.JitMs) / 1000, 1)
    foreach ($entry in $outcomes) {
        $o = $entry.Outcome
        "$($started.ToString('s'))`t$Label`t$settingArguments`t$iteration`t$($entry.Spec)`t$([math]::Round($o.Seconds, 1))`t$($o.Status)`t$($o.Objects)`t$wal`t$($after.Requested - $before.Requested)`t$($after.Timed - $before.Timed)`t$temp`t$jit`t$samplerDirectory" |
            Add-Content -Path $resultsPath
    }
    Write-Host "  replay: WAL $wal GB, checkpoints $($after.Requested - $before.Requested) requested and $($after.Timed - $before.Timed) timed, temporary files $temp GB, JIT $jit s"
}
