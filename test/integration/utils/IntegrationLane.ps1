# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Resolves the names, ports and Compose arguments of the integration stack this process drives.

.DESCRIPTION
    A Pre-Release run with -Parallel runs its three directory passes side by side, each in its own
    process and its own copy of the stack: a "lane" (#636). Everything a lane owns that would clash
    with another lane on the same Docker host (container names, host ports, volumes, the network and
    the Compose project names) is resolved here, from one environment variable, so the runner, the
    helpers and the scenarios all agree without passing a lane through every call.

    JIM_INTEGRATION_LANE carries the lane's directory type (SambaAD, OpenLDAP or DirectoryServer389).
    It is set only by the -Parallel parent on the lane processes it starts. When it is absent the run
    is serial and every value below is exactly what the harness has always used, so a serial run's
    container names, ports and Compose invocations are unchanged.

    The Samba AD lane keeps the serial names and ports too: it is the lane a developer can browse and
    sign in to, and it runs the directory-agnostic scenarios, including Scenario 016, whose shared
    database containers join the unsuffixed jim-network. What makes it a lane is only that its reset
    and clean-up are scoped to itself (see Active). The OpenLDAP and 389 Directory Server lanes get a
    suffix on every name, their own host ports, and a pair of Compose override files that apply them.

    The directory containers themselves (samba-ad-*, openldap-primary, dirsrv-primary) keep their
    names in every lane: each belongs to exactly one directory type, and so to exactly one lane.
#>

$script:IntegrationLaneDefinitions = @{
    SambaAD = @{
        Suffix             = ''
        JimProject         = 'jim'
        IntegrationProject = 'jim-integration'
        WebPort            = 5200
        DbPort             = 5432
        DirectoryContainers = @('samba-ad-primary', 'samba-ad-source', 'samba-ad-target', 'postgres-target', 'mysql-test')
    }
    OpenLDAP = @{
        Suffix             = '-openldap'
        JimProject         = 'jim-openldap'
        IntegrationProject = 'jim-integration-openldap'
        WebPort            = 5300
        DbPort             = 5433
        DirectoryContainers = @('openldap-primary')
    }
    DirectoryServer389 = @{
        Suffix             = '-dirsrv'
        JimProject         = 'jim-dirsrv'
        IntegrationProject = 'jim-integration-dirsrv'
        WebPort            = 5400
        DbPort             = 5434
        DirectoryContainers = @('dirsrv-primary')
    }
}

# Every container the serial reset has always force-removed by name.
$script:SerialDirectoryContainers = @('samba-ad-primary', 'samba-ad-source', 'samba-ad-target', 'openldap-primary', 'dirsrv-primary', 'postgres-target', 'mysql-test')

function Get-IntegrationLaneNames {
    <#
    .SYNOPSIS
        The directory types that can run as a lane, in the order Pre-Release runs them.
    #>
    return @('SambaAD', 'OpenLDAP', 'DirectoryServer389')
}

function Get-IntegrationLane {
    <#
    .SYNOPSIS
        Returns the lane this process belongs to, or the serial defaults when it belongs to none.

    .PARAMETER Name
        The lane's directory type. Defaults to $env:JIM_INTEGRATION_LANE; empty means serial.

    .OUTPUTS
        PSCustomObject with Active, Name, Suffix, projects, container names, ports, volumes, network,
        JimUrl and WorkerLogDirectoryName.
    #>
    param(
        [Parameter(Mandatory = $false)]
        [AllowEmptyString()]
        [string]$Name = $env:JIM_INTEGRATION_LANE
    )

    $active = -not [string]::IsNullOrEmpty($Name)
    if ($active -and -not $script:IntegrationLaneDefinitions.ContainsKey($Name)) {
        throw "Unknown integration lane '$Name'. Valid lanes: $((Get-IntegrationLaneNames) -join ', ')."
    }

    $definition = if ($active) { $script:IntegrationLaneDefinitions[$Name] } else { $script:IntegrationLaneDefinitions['SambaAD'] }
    $suffix = $definition.Suffix

    return [PSCustomObject]@{
        # True only inside a -Parallel lane process: resets and clean-up must then stay inside the lane.
        Active              = $active
        Name                = if ($active) { $Name } else { '' }
        Suffix              = $suffix
        JimProject          = $definition.JimProject
        IntegrationProject  = $definition.IntegrationProject
        WebPort             = $definition.WebPort
        DbPort              = $definition.DbPort
        JimUrl              = "http://localhost:$($definition.WebPort)"
        # Keycloak is published (and bridged to 8181 for browsers) only by the unsuffixed stack: two
        # stacks behind one browser-facing sign-in address is what produces invalid_grant failures.
        PublishesKeycloak   = ($suffix -eq '')
        WebContainer        = "jim.web$suffix"
        WorkerContainer     = "jim.worker$suffix"
        SchedulerContainer  = "jim.scheduler$suffix"
        DatabaseContainer   = "jim.database$suffix"
        KeycloakContainer   = "jim.keycloak$suffix"
        Network             = "jim-network$suffix"
        DbVolume            = "jim-db-volume$suffix"
        ConnectorFilesVolume = "jim-connector-files-volume$suffix"
        WorkerLogDirectoryName = "worker$suffix"
        # The directory containers this process may force-remove. A lane may only touch its own; a
        # serial run keeps its historic list.
        DirectoryContainers = if ($active) { @($definition.DirectoryContainers) } else { @($script:SerialDirectoryContainers) }
    }
}

function Get-JimServiceContainers {
    <#
    .SYNOPSIS
        The JIM service containers whose logs the error watcher and post-scenario scan read.
    #>
    $lane = Get-IntegrationLane
    return @($lane.WebContainer, $lane.WorkerContainer, $lane.SchedulerContainer)
}

function Get-JimComposeArgs {
    <#
    .SYNOPSIS
        The `docker compose` arguments that address this process's JIM stack (docker-compose.yml plus
        the development override), relative to the repository root.

    .DESCRIPTION
        Serial runs and the Samba AD lane get exactly the arguments the runner has always used. The
        suffixed lanes add an explicit project name and the lane override file, which renames every
        container, volume and the network, and moves the host ports.
    #>
    $lane = Get-IntegrationLane
    $composeArgs = @()
    if ($lane.Suffix) {
        $composeArgs += @('-p', $lane.JimProject)
    }
    $composeArgs += @('-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml')
    if ($lane.Suffix) {
        $composeArgs += @('-f', 'test/integration/docker/jim-lane.override.yml')
    }
    return $composeArgs
}

function Get-IntegrationComposeArgs {
    <#
    .SYNOPSIS
        The `docker compose` arguments that address this process's integration systems (the directory
        containers and friends), relative to the repository root.

    .DESCRIPTION
        As Get-JimComposeArgs. The suffixed lanes' override renames every volume the integration
        Compose file declares: `docker compose down -v` removes explicitly named volumes whoever created
        them, so two projects sharing one volume name would delete each other's directory data.
    #>
    $lane = Get-IntegrationLane
    $composeArgs = @()
    if ($lane.Suffix) {
        $composeArgs += @('-p', $lane.IntegrationProject)
    }
    $composeArgs += @('-f', 'test/integration/docker/docker-compose.integration-tests.yml')
    if ($lane.Suffix) {
        $composeArgs += @('-f', 'test/integration/docker/integration-lane.override.yml')
    }
    return $composeArgs
}

function Set-IntegrationLaneComposeEnvironment {
    <#
    .SYNOPSIS
        Sets the process environment variables the lane override files interpolate.

    .PARAMETER RepoRoot
        The repository root, used to give the worker's log bind mount an absolute path.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $lane = Get-IntegrationLane
    $env:JIM_LANE_SUFFIX = if ($lane.Suffix) { $lane.Suffix } else { $null }
    $env:JIM_LANE_WEB_PORT = "$($lane.WebPort)"
    $env:JIM_LANE_DB_PORT = "$($lane.DbPort)"
    $env:JIM_LANE_WORKER_LOG_DIR = Join-Path $RepoRoot 'test' 'integration' 'results' 'logs' $lane.WorkerLogDirectoryName
    # Scenario and setup scripts default their -JIMUrl from this, so a lane's scripts reach its own JIM.
    $env:JIM_INTEGRATION_URL = if ($lane.Active) { $lane.JimUrl } else { $null }
}

function Test-VolumeBelongsToIntegrationLane {
    <#
    .SYNOPSIS
        Whether a Docker volume, by name, is one this process's reset may remove.

    .DESCRIPTION
        Serial runs remove every jim-integration volume except the preserved Scenario 016 database
        volumes, as they always have. A suffixed lane removes only volumes carrying its suffix. The
        Samba AD lane removes unsuffixed jim-integration volumes only, never another lane's.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$VolumeName,

        [Parameter(Mandatory = $false)]
        [string[]]$PreservedVolumes = @('jim-integration-oracle-data', 'jim-integration-sqlserver-data')
    )

    if ($VolumeName -notmatch 'jim-integration') { return $false }
    if ($PreservedVolumes -contains $VolumeName) { return $false }

    $lane = Get-IntegrationLane
    if (-not $lane.Active) { return $true }

    $otherSuffixes = @(Get-IntegrationLaneNames | ForEach-Object { (Get-IntegrationLane -Name $_).Suffix } |
        Where-Object { $_ -and $_ -ne $lane.Suffix })
    if ($lane.Suffix) {
        return $VolumeName.EndsWith($lane.Suffix)
    }
    foreach ($otherSuffix in $otherSuffixes) {
        if ($VolumeName.EndsWith($otherSuffix)) { return $false }
    }
    return $true
}

function Get-IntegrationTestDataPath {
    <#
    .SYNOPSIS
        The directory scenarios write their generated CSVs into before seeding them into the
        connector-files volume.

    .DESCRIPTION
        Serial runs use test/test-data, as they always have. Each -Parallel lane gets its own
        sub-directory, because the lanes run different templates at the same time: the Samba AD lane
        writing its Medium hr-users.csv into the directory the OpenLDAP lane is about to seed from would
        hand the OpenLDAP lane the wrong data. The CSV cache lives beneath this directory too, so each lane
        also has its own cache and no two processes ever write the same cache entry.
    #>
    $testDataRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..' 'test-data'))
    $lane = Get-IntegrationLane
    if (-not $lane.Active) {
        return $testDataRoot
    }

    $lanePath = Join-Path $testDataRoot 'lanes' $lane.Name
    if (-not (Test-Path $lanePath)) {
        New-Item -ItemType Directory -Path $lanePath -Force | Out-Null
    }
    return $lanePath
}

function Get-IntegrationTempPath {
    <#
    .SYNOPSIS
        A lane-unique path under the system temp directory, for scenario scratch files with fixed names.

    .PARAMETER Name
        The file or directory name a serial run has always used, such as "scenario-012-hr-users.csv".
        A -Parallel lane gets the same name inside its own temp sub-directory, so two lanes running the
        same scenario never share a scratch file. The name itself is kept, because some scenarios seed
        the file into the connector-files volume under its own name, which their Connected Systems expect.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $lane = Get-IntegrationLane
    $tempRoot = [System.IO.Path]::GetTempPath()
    if (-not $lane.Active) {
        return Join-Path $tempRoot $Name
    }

    $laneTempDir = Join-Path $tempRoot "jim-integration-lane-$($lane.Name.ToLowerInvariant())"
    if (-not (Test-Path $laneTempDir)) {
        New-Item -ItemType Directory -Path $laneTempDir -Force | Out-Null
    }
    return Join-Path $laneTempDir $Name
}
