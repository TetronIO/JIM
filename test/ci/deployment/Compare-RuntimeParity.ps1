# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Compares what JIM's containers actually run on Docker with what they run on Podman.

.DESCRIPTION
    JIM ships two hand-written deployment definitions, the Docker Compose files and the Podman pod files, and
    nothing else keeps them in step. This compares the running result instead of the files, from each runtime's
    inspect output (saved by Invoke-DeploymentBoot.ps1), for each of JIM's four services:

      - the names of its environment variables (not their values, which rightly differ);
      - where things are mounted in it, tmpfs included;
      - whether its root file system is read-only;
      - the capabilities it keeps: Docker's defaults less those dropped, against what Podman reports;
      - whether no-new-privileges is set;
      - the user it runs as.

    A difference the $IntendedDifferences list below does not explain fails the comparison, naming the
    service, what differs and which runtime has it. The intended ones are reported, with their reasons, so a
    reader can see what the two runtimes knowingly do differently.

.PARAMETER Reference
    docker inspect output for JIM's containers.

.PARAMETER Candidate
    podman inspect output for JIM's containers, one file per installation compared. Each is named after its
    file, less .inspect.json; a missing file is skipped with a warning, since its leg has already failed.

.EXAMPLE
    ./test/ci/deployment/Compare-RuntimeParity.ps1 -Reference ./out/docker.inspect.json -Candidate ./out/podman-rootful.inspect.json, ./out/podman-rootless.inspect.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Reference,

    [Parameter(Mandatory)]
    [string[]]$Candidate
)

$ErrorActionPreference = 'Stop'

# Each runtime's name for each of JIM's containers.
$serviceNames = @{
    '/jim.web' = 'web'; '/jim.worker' = 'worker'; '/jim.scheduler' = 'scheduler'; '/jim.database' = 'database'
    'jim-web' = 'web'; 'jim-worker' = 'worker'; 'jim-scheduler' = 'scheduler'; 'jim-database-postgres' = 'database'
}

# What a Docker container keeps when nothing is dropped: moby's default capability set.
$dockerDefaultCapabilities = @('CAP_AUDIT_WRITE', 'CAP_CHOWN', 'CAP_DAC_OVERRIDE', 'CAP_FOWNER', 'CAP_FSETID',
    'CAP_KILL', 'CAP_MKNOD', 'CAP_NET_BIND_SERVICE', 'CAP_NET_RAW', 'CAP_SETFCAP', 'CAP_SETGID', 'CAP_SETPCAP',
    'CAP_SETUID', 'CAP_SYS_CHROOT')

$certificateMounts = 'Docker mounts the certificate and key as two Compose secrets, and Podman mounts the jim-tls secret as the folder holding them: JIM reads the same two paths on both.'

# The differences the two runtimes are meant to have. Service '*' means every service; On is the runtime that
# has the values (wildcards allowed) and the other lacks.
$IntendedDifferences = @(
    @{
        Service = '*'; Property = 'environment variable'; On = 'Podman'; Values = @('HOME', 'HOSTNAME', 'container')
        Reason = 'Podman sets these in every container.'
    }
    @{
        Service = 'web', 'worker', 'scheduler'; Property = 'environment variable'; On = 'Docker'
        Values = @('DOCKER_REGISTRY', 'JIM_VERSION', 'JIM_WEB_PORT')
        Reason = 'Settings of the Compose files themselves (the images and the published port), which env_file also passes into the containers, where nothing reads them. The pod files name the images, and jim.kube publishes the port.'
    }
    @{
        Service = 'database'; Property = 'environment variable'; On = 'Docker'; Values = @('JIM_*', 'DOCKER_REGISTRY')
        Reason = 'Docker Compose''s env_file passes the whole .env into the database container too, and PostgreSQL reads none of it; the Podman database pod is given only its own settings. To be removed with #1862.'
    }
    @{
        Service = 'web'; Property = 'mount'; On = 'Docker'; Values = @('/run/jim-tls/tls.crt', '/run/jim-tls/tls.key')
        Reason = $certificateMounts
    }
    @{
        Service = 'web'; Property = 'mount'; On = 'Podman'; Values = @('/run/jim-tls')
        Reason = $certificateMounts
    }
    @{
        Service = 'database'; Property = 'capability'; On = 'Docker'; Values = @('CAP_AUDIT_WRITE', 'CAP_MKNOD', 'CAP_NET_RAW')
        Reason = 'Neither definition sets the database''s capabilities, so each runtime applies its default set, and Docker''s is larger by these three.'
    }
)

function Get-ContainerModel {
    param($Container)
    $isDocker = $Container.Name.StartsWith('/')
    $hostConfig = $Container.HostConfig

    $mounts = @($Container.Mounts | ForEach-Object { $_.Destination })
    if ($hostConfig.Tmpfs) {
        $mounts += @($hostConfig.Tmpfs.PSObject.Properties.Name)
    }

    if ($isDocker) {
        $normalise = { param($c) if ($c -like 'CAP_*') { $c } else { "CAP_$c" } }
        $dropped = @($hostConfig.CapDrop | Where-Object { $_ } | ForEach-Object { & $normalise $_ })
        $kept = if ($dropped -contains 'CAP_ALL') { @() } else { $dockerDefaultCapabilities | Where-Object { $dropped -notcontains $_ } }
        $capabilities = @(@($kept) + @($hostConfig.CapAdd | Where-Object { $_ } | ForEach-Object { & $normalise $_ }))
    }
    else {
        $capabilities = @($Container.EffectiveCaps | Where-Object { $_ })
    }

    [pscustomobject]@{
        'environment variable' = @($Container.Config.Env | ForEach-Object { ($_ -split '=', 2)[0] } | Sort-Object -Unique)
        'mount' = @($mounts | Where-Object { $_ } | Sort-Object -Unique)
        'capability' = @($capabilities | Sort-Object -Unique)
        ReadOnlyRootFileSystem = [bool]$hostConfig.ReadonlyRootfs
        NoNewPrivileges = [bool](@($hostConfig.SecurityOpt) -match '^no-new-privileges(:true)?$')
        User = [string]$Container.Config.User
    }
}

function Read-Inspect {
    param([string]$Path)
    $models = @{}
    foreach ($container in @(Get-Content $Path -Raw | ConvertFrom-Json)) {
        $service = $serviceNames[$container.Name]
        if ($service) {
            $models[$service] = Get-ContainerModel $container
        }
    }
    $models
}

function Find-IntendedDifference {
    param([string]$Service, [string]$Property, [string]$On, [string]$Value)
    $IntendedDifferences | Where-Object {
        ($_.Service -contains '*' -or $_.Service -contains $Service) -and $_.Property -eq $Property -and $_.On -eq $On -and
            @($_.Values | Where-Object { $Value -like $_ }).Count -gt 0
    } | Select-Object -First 1
}

$docker = Read-Inspect $Reference
$unexpected = [System.Collections.Generic.List[string]]::new()
$intended = [System.Collections.Generic.List[object]]::new()

foreach ($path in $Candidate) {
    $leg = [System.IO.Path]::GetFileName($path) -replace '\.inspect\.json$', ''
    if (-not (Test-Path $path)) {
        Write-Warning "Not comparing ${leg}: there is no inspect output from it at $path"
        continue
    }
    $podman = Read-Inspect $path

    foreach ($service in 'web', 'worker', 'scheduler', 'database') {
        $d = $docker[$service]
        $p = $podman[$service]
        if (-not $d -or -not $p) {
            $where = if ($d) { $leg } else { 'Docker' }
            $unexpected.Add("${leg}: ${service} is not running on $where")
            continue
        }

        foreach ($property in 'environment variable', 'mount', 'capability') {
            foreach ($side in @(
                    @{ On = 'Docker'; Values = @($d.$property | Where-Object { $p.$property -notcontains $_ }) }
                    @{ On = 'Podman'; Values = @($p.$property | Where-Object { $d.$property -notcontains $_ }) })) {
                foreach ($value in $side.Values) {
                    $description = "${leg}: ${service}: $property $value is on $($side.On) only"
                    $reason = Find-IntendedDifference -Service $service -Property $property -On $side.On -Value $value
                    if ($reason) {
                        $intended.Add([pscustomobject]@{ Leg = $leg; Service = $service; Value = $value; Reason = $reason.Reason })
                    }
                    else {
                        $unexpected.Add($description)
                    }
                }
            }
        }

        if ($d.ReadOnlyRootFileSystem -ne $p.ReadOnlyRootFileSystem) {
            $unexpected.Add("${leg}: ${service}: read-only root file system is $($d.ReadOnlyRootFileSystem) on Docker and $($p.ReadOnlyRootFileSystem) on Podman")
        }
        if ($d.NoNewPrivileges -ne $p.NoNewPrivileges) {
            $unexpected.Add("${leg}: ${service}: no-new-privileges is $($d.NoNewPrivileges) on Docker and $($p.NoNewPrivileges) on Podman")
        }
        if ($d.User -ne $p.User) {
            $unexpected.Add("${leg}: ${service}: runs as user '$($d.User)' on Docker and '$($p.User)' on Podman")
        }
    }
    Write-Host "Compared Docker with $leg"
}

# One line per reason, naming what it covers, rather than one per value.
if ($intended.Count -gt 0) {
    Write-Host "`nIntended differences:"
    foreach ($group in $intended | Group-Object Reason) {
        $covers = ($group.Group | Group-Object Service | ForEach-Object {
                "$($_.Name): $((@($_.Group.Value) | Sort-Object -Unique) -join ', ')"
            }) -join '; '
        Write-Host "  - $($group.Name)`n      $covers"
    }
}

if ($unexpected.Count -gt 0) {
    throw "The runtimes run JIM differently:`n$(($unexpected | ForEach-Object { "  - $_" }) -join "`n")`nMake the Compose files and the pod files agree, or, if the difference is intended, add it to `$IntendedDifferences in Compare-RuntimeParity.ps1 with its reason."
}
Write-Host "`nNo unintended differences."
