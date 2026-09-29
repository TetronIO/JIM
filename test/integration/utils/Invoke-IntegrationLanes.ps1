# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Runs the directory passes of a -DirectoryType All run side by side, one isolated stack ("lane") each.

.DESCRIPTION
    The -Parallel orchestrator (#636). Run-IntegrationTests.ps1 calls Invoke-IntegrationLanes from its
    -DirectoryType All block when -Parallel is set. It:

      1. Checks the host looks big enough, reaps stale monitors once, builds JIM's images once and
         applies -LogLevel to .env once. A lane never builds, never sweeps and never writes .env.
      2. Starts one runner process per directory type with JIM_INTEGRATION_LANE set, so the lane
         resolves its own names, ports and Compose projects (utils/IntegrationLane.ps1). Each lane's
         full output goes to its own log file.
      3. Prints one short, prefixed line per lane event while they run, and the tail of a failing
         lane's log when it finishes. A failing lane does not stop the others.
      4. Writes a combined summary, tears the lanes down when every lane passed (a failed lane is left
         up for diagnosis, with the commands to remove it), and restores .env.

    Ctrl+C stops every lane process and removes every lane's containers, volumes and network; Scenario
    016's shared database containers are never touched.
#>

. (Join-Path $PSScriptRoot 'IntegrationLane.ps1')

function Remove-IntegrationAnsiCodes {
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return '' }
    return ($Text -replace "\x1b\[[0-9;]*[A-Za-z]", '')
}

function Format-IntegrationLaneDuration {
    param([TimeSpan]$Duration)
    if ($Duration.TotalHours -ge 1) { return "{0}h {1:D2}m" -f [int][Math]::Floor($Duration.TotalHours), $Duration.Minutes }
    if ($Duration.TotalMinutes -ge 1) { return "{0}m {1:D2}s" -f [int][Math]::Floor($Duration.TotalMinutes), $Duration.Seconds }
    return "{0}s" -f [int][Math]::Round($Duration.TotalSeconds)
}

function Invoke-WithIntegrationLane {
    <#
    .SYNOPSIS
        Runs a script block as though this process were the named lane, then restores the environment.
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Name,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][scriptblock]$ScriptBlock
    )

    $saved = @{}
    foreach ($variable in @('JIM_INTEGRATION_LANE', 'JIM_LANE_SUFFIX', 'JIM_LANE_WEB_PORT', 'JIM_LANE_DB_PORT', 'JIM_LANE_WORKER_LOG_DIR', 'JIM_INTEGRATION_URL')) {
        $saved[$variable] = [Environment]::GetEnvironmentVariable($variable)
    }
    try {
        $env:JIM_INTEGRATION_LANE = if ($Name) { $Name } else { $null }
        Set-IntegrationLaneComposeEnvironment -RepoRoot $RepoRoot
        & $ScriptBlock
    }
    finally {
        foreach ($variable in $saved.Keys) {
            [Environment]::SetEnvironmentVariable($variable, $saved[$variable])
        }
    }
}

function Remove-IntegrationLaneStack {
    <#
    .SYNOPSIS
        Removes one lane's containers, volumes and network. Scenario 016's database containers and
        their volumes are left alone, as every reset leaves them.

    .PARAMETER Name
        The lane's directory type.

    .PARAMETER RepoRoot
        The repository root; the Compose file paths are relative to it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    Push-Location $RepoRoot
    try {
        Invoke-WithIntegrationLane -Name $Name -RepoRoot $RepoRoot -ScriptBlock {
            $lane = Get-IntegrationLane
            $jimArgs = @(Get-JimComposeArgs)
            $integrationArgs = @(Get-IntegrationComposeArgs)
            # Throwaway containers outside the Compose projects (the volume-audit sidecar, a CSV seeding
            # helper caught mid-copy) would otherwise pin the lane's connector-files volume.
            $pinning = @(docker ps -aq --filter "volume=$($lane.ConnectorFilesVolume)" 2>$null)
            foreach ($containerId in $pinning) {
                $project = docker inspect $containerId --format '{{index .Config.Labels "com.docker.compose.project"}}' 2>$null
                if (-not $project) {
                    docker rm -f $containerId 2>&1 | Out-Null
                }
            }
            # The integration systems first: they sit on the JIM stack's network (declared external in the
            # integration Compose file), which the JIM stack's own `down` can only remove once they are gone.
            docker compose @integrationArgs --profile scenario-002 --profile scenario-008 --profile openldap --profile dirsrv --profile scim down -v --remove-orphans 2>&1 | Out-Null
            docker compose @jimArgs --profile with-db down -v --remove-orphans 2>&1 | Out-Null
            foreach ($container in $lane.DirectoryContainers) {
                # Only a container this lane's integration project created; a fixed name alone could
                # belong to a serial run's stack.
                $project = docker inspect $container --format '{{index .Config.Labels "com.docker.compose.project"}}' 2>$null
                if ($LASTEXITCODE -eq 0 -and "$project" -eq $lane.IntegrationProject) {
                    docker rm -f $container 2>&1 | Out-Null
                }
            }
            $laneVolumes = @(docker volume ls --format '{{.Name}}' | Where-Object { Test-VolumeBelongsToIntegrationLane -VolumeName $_ })
            foreach ($volume in $laneVolumes + @($lane.DbVolume, $lane.ConnectorFilesVolume)) {
                docker volume rm $volume 2>&1 | Out-Null
            }
            # A suffixed lane's network is its own. The unsuffixed jim-network is left alone: Scenario 016's
            # shared database containers stay attached to it between runs.
            if ($lane.Suffix) {
                docker network rm $lane.Network 2>&1 | Out-Null
            }
        }
    }
    finally {
        Pop-Location
    }
}

function Remove-SuffixedIntegrationLaneStacks {
    <#
    .SYNOPSIS
        Removes the OpenLDAP and 389 Directory Server lanes' stacks, if a -Parallel run left any.

    .DESCRIPTION
        Called by a serial run's reset: those stacks have their own names and ports, so nothing else
        would ever remove them. Cheap when there is nothing to remove. The Samba AD lane shares the
        serial names and is reset by the serial reset itself.
    #>
    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..' '..'))
    foreach ($name in Get-IntegrationLaneNames) {
        $laneForName = Get-IntegrationLane -Name $name
        if (-not $laneForName.Suffix) { continue }
        $leftovers = docker ps -aq --filter "label=com.docker.compose.project=$($laneForName.JimProject)" 2>$null
        $leftovers += docker ps -aq --filter "label=com.docker.compose.project=$($laneForName.IntegrationProject)" 2>$null
        $leftoverVolumes = @(docker volume ls -q 2>$null | Where-Object { $_.EndsWith($laneForName.Suffix) })
        if ($leftovers -or $leftoverVolumes) {
            Write-Host "  Removing the $name lane's stack left by a previous -Parallel run..." -ForegroundColor Gray
            Remove-IntegrationLaneStack -Name $name -RepoRoot $repoRoot
        }
    }
}

function Stop-IntegrationLaneProcesses {
    <#
    .SYNOPSIS
        Kills every process that belongs to a lane: the lane's runner and everything it started
        (scenario scripts, docker CLI calls, monitors), found by the JIM_INTEGRATION_LANE value they
        inherited. Killing the runner process alone is not enough: pwsh is a .NET global-tool shim here,
        which does not forward signals to the process that actually runs the script (#918).
    #>
    param([Parameter(Mandatory = $true)][string]$Name)

    $marker = "JIM_INTEGRATION_LANE=$Name"
    $killed = 0
    foreach ($procDir in Get-ChildItem -Path '/proc' -Directory -ErrorAction SilentlyContinue) {
        if ($procDir.Name -notmatch '^\d+$' -or [int]$procDir.Name -eq $PID) { continue }
        try {
            $environ = [System.IO.File]::ReadAllText((Join-Path $procDir.FullName 'environ'))
        }
        catch {
            continue
        }
        if (($environ -split "`0") -contains $marker) {
            Stop-Process -Id ([int]$procDir.Name) -Force -ErrorAction SilentlyContinue
            $killed++
        }
    }
    return $killed
}

function Test-ParallelHostCapacity {
    <#
    .SYNOPSIS
        Warns when the host looks too small for the lanes. Measured on the 2026-09-29 Pre-Release at
        Large: one lane peaks at about 5 GB and 4 cores, and Scenario 016's shared databases add about
        3.5 GB, hence 20 GB available memory and 12 cores for three lanes.
    #>
    param([int]$LaneCount)

    $requiredGb = [Math]::Ceiling(5 * $LaneCount + 3.5)
    $requiredCores = 4 * $LaneCount
    $availableGb = $null
    if (Test-Path '/proc/meminfo') {
        $line = Select-String -Path '/proc/meminfo' -Pattern '^MemAvailable:\s+(\d+)\s+kB' | Select-Object -First 1
        if ($line) { $availableGb = [Math]::Round([double]$line.Matches[0].Groups[1].Value / 1MB, 1) }
    }
    $cores = [Environment]::ProcessorCount

    $memoryText = if ($null -ne $availableGb) { "$availableGb GB available" } else { "available memory unknown" }
    Write-Host "Host: $memoryText, $cores cores (lanes need about $requiredGb GB and $requiredCores cores at Large)" -ForegroundColor Gray
    if (($null -ne $availableGb -and $availableGb -lt $requiredGb) -or $cores -lt $requiredCores) {
        Write-Host "WARNING: this host looks too small to run $LaneCount lanes side by side without them slowing each other down. Consider running without -Parallel." -ForegroundColor Yellow
    }
}

function ConvertTo-IntegrationLaneArguments {
    <#
    .SYNOPSIS
        Turns the runner's pass-through parameters into a pwsh -File argument list.
    #>
    param([hashtable]$Parameters)

    $arguments = @()
    foreach ($key in ($Parameters.Keys | Sort-Object)) {
        $value = $Parameters[$key]
        if ($value -is [bool] -or $value -is [System.Management.Automation.SwitchParameter]) {
            if ([bool]$value) { $arguments += "-$key" }
        }
        else {
            $arguments += @("-$key", "$value")
        }
    }
    return $arguments
}

function Read-IntegrationLaneLog {
    <#
    .SYNOPSIS
        Returns the complete lines appended to a lane's log since the last call, keeping any partial
        trailing line for next time.
    #>
    param([Parameter(Mandatory = $true)]$Lane)

    if (-not (Test-Path $Lane.LogPath)) { return @() }
    $stream = [System.IO.File]::Open($Lane.LogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        if ($stream.Length -le $Lane.Position) { return @() }
        [void]$stream.Seek($Lane.Position, [System.IO.SeekOrigin]::Begin)
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8)
        $text = $reader.ReadToEnd()
        $Lane.Position = $stream.Position
    }
    finally {
        $stream.Dispose()
    }

    $combined = $Lane.Partial + $text
    $parts = $combined -split "`n"
    $Lane.Partial = $parts[-1]
    if ($parts.Count -le 1) { return @() }
    return @($parts[0..($parts.Count - 2)] | ForEach-Object { Remove-IntegrationAnsiCodes $_.TrimEnd("`r") })
}

function Write-IntegrationLaneStatus {
    param($Lane, [string]$Message, [string]$Colour = 'Gray')
    $label = "[$($Lane.Name)]".PadRight(22)
    Write-Host "$label $Message" -ForegroundColor $Colour
}

function Update-IntegrationLaneFromLog {
    <#
    .SYNOPSIS
        Turns new lane log lines into status events: a scenario starting, and a scenario's result.
    #>
    param($Lane)

    foreach ($line in (Read-IntegrationLaneLog -Lane $Lane)) {
        if ($line -match '^\s*\[(\d+)/(\d+)\]\s+(Scenario-\S+)\s*$') {
            $Lane.CurrentScenario = $Matches[3]
            Write-IntegrationLaneStatus -Lane $Lane -Message "$($Matches[3]) started ($($Matches[1])/$($Matches[2]))"
        }
        elseif ($line -match '^\s*Result:\s+(PASSED|FAILED.*?)\s+Duration:\s+(\d+):(\d+):(\d+)') {
            $duration = [TimeSpan]::new([int]$Matches[2], [int]$Matches[3], [int]$Matches[4])
            $scenarioName = if ($Lane.CurrentScenario) { $Lane.CurrentScenario } else { 'Scenario' }
            if ($Matches[1] -eq 'PASSED') {
                $Lane.Passed++
                Write-IntegrationLaneStatus -Lane $Lane -Message "$scenarioName passed ($(Format-IntegrationLaneDuration $duration))" -Colour Green
            }
            else {
                $Lane.Failed++
                Write-IntegrationLaneStatus -Lane $Lane -Message "$scenarioName FAILED ($(Format-IntegrationLaneDuration $duration))" -Colour Red
            }
        }
    }
}

function Get-IntegrationLaneLogTail {
    param([string]$Path, [int]$Lines)
    if (-not (Test-Path $Path)) { return @() }
    return @(Get-Content -Path $Path -Tail $Lines -ErrorAction SilentlyContinue | ForEach-Object { Remove-IntegrationAnsiCodes $_ })
}

function Invoke-IntegrationLanes {
    <#
    .SYNOPSIS
        Runs one lane per directory type side by side and reports on them. See the file header.

    .OUTPUTS
        PSCustomObject with ExitCode (0 when every lane passed) and Lanes (one result per lane).
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RunnerScript,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ScriptRoot,
        [Parameter(Mandatory = $true)][string[]]$DirectoryTypes,
        [Parameter(Mandatory = $true)][hashtable]$TemplateForDirectoryType,
        [Parameter(Mandatory = $true)][hashtable]$PassThruParams,
        [string]$Scenario,
        [string]$LogLevel,
        [int]$FailureTailLines = 50,
        [int]$PollSeconds = 5
    )

    Push-Location $RepoRoot
    $runStart = Get-Date
    $runStamp = $runStart.ToString('yyyy-MM-dd_HHmmss')
    $resultsDir = Join-Path $ScriptRoot 'results'
    $logDir = Join-Path $resultsDir 'logs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null

    Write-Host ""
    Write-Host ("=" * 65) -ForegroundColor Cyan
    Write-Host "  JIM Integration Tests - Parallel Directory Lanes" -ForegroundColor Cyan
    Write-Host ("=" * 65) -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Scenario: $(if ($Scenario) { $Scenario } else { 'All' })" -ForegroundColor Gray
    foreach ($directoryType in $DirectoryTypes) {
        $laneInfo = Get-IntegrationLane -Name $directoryType
        Write-Host ("  {0,-20} template {1,-12} JIM {2}" -f $directoryType, $TemplateForDirectoryType[$directoryType], $laneInfo.JimUrl) -ForegroundColor Gray
    }
    Write-Host ""
    Test-ParallelHostCapacity -LaneCount $DirectoryTypes.Count
    if ($env:JIM_BENCH_API_URL -and $env:JIM_BENCH_API_KEY) {
        Write-Host "JIM-Bench streaming is off for this run: lanes share the host, so their timings are not nominal. Run serially for performance data." -ForegroundColor Gray
    }

    $lanes = @()
    $originalLogLevel = $null
    $envFilePath = Join-Path $RepoRoot '.env'
    $exitCode = 1
    $completed = $false

    try {
        # --- Prepare once -------------------------------------------------------------------------
        $prepareStart = Get-Date
        Write-Host ""
        Write-Host "Prepare: reaping monitors left by earlier runs..." -ForegroundColor Gray
        Clear-StaleIntegrationMonitors -ResultsPath $resultsDir

        # Lanes resolve their own API keys, but every JIM container reads .env through env_file, so the
        # run's log level is applied there once, here, and restored at the end.
        if ($LogLevel -and (Test-Path $envFilePath)) {
            $envContent = Get-Content $envFilePath -Raw
            if ($envContent -match "(?m)^JIM_LOG_LEVEL=(.+)$") {
                $originalLogLevel = $Matches[1].Trim()
            }
            $envContent = $envContent -replace "(?m)^JIM_LOG_LEVEL=.*", "JIM_LOG_LEVEL=$LogLevel"
            $envContent | Set-Content $envFilePath -NoNewline
            Write-Host "Prepare: set JIM_LOG_LEVEL=$LogLevel in .env for the run" -ForegroundColor Gray
        }

        Write-Host "Prepare: building JIM's images once for every lane..." -ForegroundColor Gray
        $buildLog = Join-Path $logDir "lane-prepare-$runStamp.log"
        $now = (Get-Date).ToUniversalTime()
        $env:VERSION_SUFFIX = "dev.$($now.ToString('yyyyMMdd')).$($now.Hour * 60 + $now.Minute)"
        $serialJimArgs = Invoke-WithIntegrationLane -Name '' -RepoRoot $RepoRoot -ScriptBlock { @(Get-JimComposeArgs) }
        docker compose @serialJimArgs build *> $buildLog
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Prepare failed: the JIM image build did not succeed. No lane was started." -ForegroundColor Red
            Get-IntegrationLaneLogTail -Path $buildLog -Lines 30 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            return [PSCustomObject]@{ ExitCode = 1; Lanes = @() }
        }
        Write-Host "Prepare phase complete ($(Format-IntegrationLaneDuration ((Get-Date) - $prepareStart)))" -ForegroundColor Green
        Write-Host ""

        # --- Launch -------------------------------------------------------------------------------
        for ($i = 0; $i -lt $DirectoryTypes.Count; $i++) {
            $directoryType = $DirectoryTypes[$i]
            $laneArguments = @('-NoProfile', '-File', $RunnerScript) +
                (ConvertTo-IntegrationLaneArguments -Parameters $PassThruParams) +
                @('-DirectoryType', $directoryType, '-Template', $TemplateForDirectoryType[$directoryType], '-SkipBuild')
            # The directory-agnostic scenarios (011, 015, 016) run in the first lane only, as in a serial
            # run. The first lane is the Samba AD one, whose unsuffixed names Scenario 016's shared
            # database containers are attached to.
            if ($Scenario -eq 'All' -and $i -gt 0) {
                $laneArguments += '-SkipDirectoryAgnosticScenarios'
            }

            $laneLog = Join-Path $logDir "lane-$directoryType-$runStamp.log"
            $process = Start-Process -FilePath 'pwsh' -ArgumentList $laneArguments -WorkingDirectory $RepoRoot `
                -Environment @{ JIM_INTEGRATION_LANE = $directoryType } `
                -RedirectStandardOutput $laneLog -RedirectStandardError "$laneLog.stderr" -PassThru

            $lane = [PSCustomObject]@{
                Name            = $directoryType
                Template        = $TemplateForDirectoryType[$directoryType]
                LogPath         = $laneLog
                Process         = $process
                Start           = Get-Date
                End             = $null
                Position        = [long]0
                Partial         = ''
                CurrentScenario = $null
                Passed          = 0
                Failed          = 0
                Finished        = $false
                ExitCode        = $null
            }
            $lanes += $lane
            Write-IntegrationLaneStatus -Lane $lane -Message "started -> $laneLog"
        }

        # --- Watch --------------------------------------------------------------------------------
        while (@($lanes | Where-Object { -not $_.Finished }).Count -gt 0) {
            Start-Sleep -Seconds $PollSeconds
            foreach ($lane in @($lanes | Where-Object { -not $_.Finished })) {
                Update-IntegrationLaneFromLog -Lane $lane
                if ($lane.Process.HasExited) {
                    $lane.Process.WaitForExit()
                    Update-IntegrationLaneFromLog -Lane $lane
                    $lane.Finished = $true
                    $lane.End = Get-Date
                    $lane.ExitCode = $lane.Process.ExitCode
                    # A single-scenario lane prints no per-scenario result lines; its exit code is the result.
                    if ($Scenario -ne 'All' -and ($lane.Passed + $lane.Failed) -eq 0) {
                        if ($lane.ExitCode -eq 0) { $lane.Passed = 1 } else { $lane.Failed = 1 }
                    }
                    $laneDuration = Format-IntegrationLaneDuration ($lane.End - $lane.Start)
                    if ($lane.ExitCode -eq 0) {
                        Write-IntegrationLaneStatus -Lane $lane -Message "finished: $($lane.Passed) passed, $($lane.Failed) failed ($laneDuration)" -Colour Green
                    }
                    else {
                        Write-IntegrationLaneStatus -Lane $lane -Message "FAILED (exit $($lane.ExitCode)) after ${laneDuration}: $($lane.Passed) passed, $($lane.Failed) failed. Last $FailureTailLines lines of its log:" -Colour Red
                        Get-IntegrationLaneLogTail -Path $lane.LogPath -Lines $FailureTailLines | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
                    }
                }
            }
        }
        $completed = $true
    }
    finally {
        if (-not $completed -and $lanes.Count -gt 0) {
            # Interrupted (Ctrl+C) or failed mid-run: stop every lane and remove what it created.
            Write-Host ""
            Write-Host "Stopping the lanes and removing their stacks..." -ForegroundColor Yellow
            foreach ($lane in $lanes) {
                if (-not $lane.Finished) {
                    $stopped = Stop-IntegrationLaneProcesses -Name $lane.Name
                    Write-IntegrationLaneStatus -Lane $lane -Message "stopped ($stopped process(es))" -Colour Yellow
                }
            }
            foreach ($lane in $lanes) {
                try {
                    Remove-IntegrationLaneStack -Name $lane.Name -RepoRoot $RepoRoot
                    Write-IntegrationLaneStatus -Lane $lane -Message "stack removed" -Colour Yellow
                }
                catch {
                    Write-IntegrationLaneStatus -Lane $lane -Message "could not remove its stack: $($_.Exception.Message)" -Colour Red
                }
            }
            $interruptedMarker = Join-Path $resultsDir "parallel-lanes-$runStamp-interrupted.json"
            @{ Interrupted = $true; StartTime = $runStart.ToString('yyyy-MM-dd HH:mm:ss'); Lanes = @($lanes | ForEach-Object { @{ DirectoryType = $_.Name; Finished = $_.Finished; ExitCode = $_.ExitCode; LogPath = $_.LogPath } }) } |
                ConvertTo-Json -Depth 5 | Set-Content $interruptedMarker
            Write-Host "Interrupted run recorded in $interruptedMarker" -ForegroundColor Yellow
        }

        if ($originalLogLevel -and (Test-Path $envFilePath)) {
            $envContent = Get-Content $envFilePath -Raw
            $envContent = $envContent -replace "(?m)^JIM_LOG_LEVEL=.*", "JIM_LOG_LEVEL=$originalLogLevel"
            $envContent | Set-Content $envFilePath -NoNewline
            Write-Host "Restored JIM_LOG_LEVEL=$originalLogLevel in .env" -ForegroundColor Gray
        }
        Pop-Location
    }

    # --- Summarise ----------------------------------------------------------------------------------
    $runDuration = (Get-Date) - $runStart
    $laneResults = @()
    foreach ($lane in $lanes) {
        # A -Scenario All lane writes its own full-regression-<DirectoryType>-<timestamp>.json.
        $regressionFile = Get-ChildItem -Path $resultsDir -Filter "full-regression-$($lane.Name)-*.json" -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $runStart } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $laneResults += [PSCustomObject]@{
            DirectoryType  = $lane.Name
            Template       = $lane.Template
            Success        = ($lane.ExitCode -eq 0)
            ExitCode       = $lane.ExitCode
            Duration       = ($lane.End - $lane.Start).ToString('hh\:mm\:ss')
            ScenariosPassed = $lane.Passed
            ScenariosFailed = $lane.Failed
            LogPath        = $lane.LogPath
            RegressionFile = if ($regressionFile) { $regressionFile.FullName } else { $null }
        }
    }

    $passedLanes = @($laneResults | Where-Object { $_.Success }).Count
    Write-Host ""
    Write-Host ("=" * 65) -ForegroundColor Cyan
    Write-Host "  Parallel Directory Lanes - Summary" -ForegroundColor Cyan
    Write-Host ("=" * 65) -ForegroundColor Cyan
    Write-Host ""
    foreach ($result in $laneResults) {
        $status = if ($result.Success) { 'PASS' } else { 'FAIL' }
        $colour = if ($result.Success) { 'Green' } else { 'Red' }
        Write-Host ("  [{0}]  {1,-20} {2,-12} {3}   {4} passed, {5} failed" -f $status, $result.DirectoryType, $result.Template, $result.Duration, $result.ScenariosPassed, $result.ScenariosFailed) -ForegroundColor $colour
    }
    Write-Host ""
    Write-Host "$passedLanes of $($laneResults.Count) lanes passed, $(Format-IntegrationLaneDuration $runDuration) total" -ForegroundColor $(if ($passedLanes -eq $laneResults.Count) { 'Green' } else { 'Red' })

    $summaryFile = Join-Path $resultsDir "parallel-lanes-$runStamp.json"
    [ordered]@{
        Mode           = 'ParallelLanes'
        Scenario       = if ($Scenario) { $Scenario } else { 'All' }
        StartTime      = $runStart.ToString('yyyy-MM-dd HH:mm:ss')
        Duration       = $runDuration.ToString('hh\:mm\:ss')
        OverallSuccess = ($passedLanes -eq $laneResults.Count)
        Lanes          = $laneResults
    } | ConvertTo-Json -Depth 6 | Set-Content $summaryFile
    Write-Host "Summary: $summaryFile" -ForegroundColor Gray

    # --- Tear down ----------------------------------------------------------------------------------
    # Every lane passed: remove them all, so the run leaves nothing behind. A failed lane stays up for
    # diagnosis (its database and logs are the evidence), with the commands to remove it afterwards.
    Write-Host ""
    foreach ($result in $laneResults) {
        if ($result.Success) {
            Remove-IntegrationLaneStack -Name $result.DirectoryType -RepoRoot $RepoRoot
            Write-Host "[$($result.DirectoryType)] stack removed" -ForegroundColor Gray
        }
        else {
            $laneInfo = Get-IntegrationLane -Name $result.DirectoryType
            Write-Host "[$($result.DirectoryType)] left running for diagnosis (JIM on $($laneInfo.JimUrl), database container $($laneInfo.DatabaseContainer)). The next run removes it, or remove it now with:" -ForegroundColor Yellow
            Write-Host "    pwsh -NoProfile -Command `". ./test/integration/utils/Invoke-IntegrationLanes.ps1; Remove-IntegrationLaneStack -Name $($result.DirectoryType) -RepoRoot (Get-Location)`"" -ForegroundColor Yellow
        }
    }

    $exitCode = if ($passedLanes -eq $laneResults.Count -and $laneResults.Count -gt 0) { 0 } else { 1 }
    return [PSCustomObject]@{ ExitCode = $exitCode; Lanes = $laneResults }
}
