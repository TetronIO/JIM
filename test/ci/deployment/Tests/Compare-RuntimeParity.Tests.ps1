# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for test/ci/deployment/Compare-RuntimeParity.ps1.

.DESCRIPTION
    Builds inspect output in the shape each runtime writes it (docker inspect and podman inspect), for a JIM
    whose four containers run alike, then changes one thing at a time and checks the comparison names it,
    or accepts it when it is one of the differences the script lists as intended.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Compare-RuntimeParity.ps1')).Path

    $script:DockerNames = @{ web = '/jim.web'; worker = '/jim.worker'; scheduler = '/jim.scheduler'; database = '/jim.database' }
    $script:PodmanNames = @{ web = 'jim-web'; worker = 'jim-worker'; scheduler = 'jim-scheduler'; database = 'jim-database-postgres' }
    $script:PodmanDefaultCapabilities = @('CAP_CHOWN', 'CAP_DAC_OVERRIDE', 'CAP_FOWNER', 'CAP_FSETID', 'CAP_KILL',
        'CAP_NET_BIND_SERVICE', 'CAP_SETFCAP', 'CAP_SETGID', 'CAP_SETPCAP', 'CAP_SETUID', 'CAP_SYS_CHROOT')

    # What a JIM service runs with, runtime-neutrally: the tests build each runtime's inspect output from it.
    function New-Service {
        param([string]$Name)
        if ($Name -eq 'database') {
            return @{
                Env = @('PATH', 'LANG', 'POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD', 'JIM_DB_LOG_MIN_DURATION')
                Mounts = @('/var/lib/postgresql', '/var/log/jim'); Tmpfs = @(); ReadOnly = $false
                DropAll = $false; User = ''; NoNewPrivileges = $false
            }
        }
        @{
            Env = @('PATH', 'LANG', 'JIM_DB_HOSTNAME', 'JIM_DB_PASSWORD', 'JIM_LOG_PATH')
            Mounts = @('/data/keys', '/var/log/jim'); Tmpfs = @('/tmp'); ReadOnly = $true
            DropAll = $true; User = 'app'; NoNewPrivileges = $true
        }
    }

    function New-Services {
        $services = [ordered]@{}
        foreach ($name in 'web', 'worker', 'scheduler', 'database') {
            $services[$name] = New-Service $name
        }
        $services
    }

    function ConvertTo-DockerInspect {
        param($Services)
        @(foreach ($name in $Services.Keys) {
            $s = $Services[$name]
            $tmpfs = @{}
            foreach ($path in $s.Tmpfs) { $tmpfs[$path] = '' }
            @{
                Name = $script:DockerNames[$name]
                Config = @{ User = $s.User; Env = @($s.Env | ForEach-Object { "$_=value" }) }
                Mounts = @($s.Mounts | ForEach-Object { @{ Type = 'volume'; Destination = $_ } })
                HostConfig = @{
                    ReadonlyRootfs = $s.ReadOnly
                    CapDrop = if ($s.DropAll) { @('ALL') } else { $null }
                    CapAdd = $null
                    SecurityOpt = if ($s.NoNewPrivileges) { @('no-new-privileges:true') } else { $null }
                    Tmpfs = if ($s.Tmpfs) { $tmpfs } else { $null }
                }
            }
        })
    }

    function ConvertTo-PodmanInspect {
        param($Services)
        @(foreach ($name in $Services.Keys) {
            $s = $Services[$name]
            $tmpfs = @{}
            foreach ($path in $s.Tmpfs) { $tmpfs[$path] = 'rprivate,nosuid,nodev,tmpcopyup' }
            @{
                Name = $script:PodmanNames[$name]
                Config = @{ User = $s.User; Env = @(@($s.Env) + @('HOME', 'HOSTNAME', 'container') | ForEach-Object { "$_=value" }) }
                Mounts = @($s.Mounts | ForEach-Object { @{ Type = 'volume'; Destination = $_ } })
                EffectiveCaps = if ($s.ContainsKey('Capabilities')) { $s.Capabilities } elseif ($s.DropAll) { $null } else { $script:PodmanDefaultCapabilities }
                HostConfig = @{
                    ReadonlyRootfs = $s.ReadOnly
                    CapDrop = if ($s.DropAll) { $script:PodmanDefaultCapabilities } else { @() }
                    CapAdd = @()
                    SecurityOpt = if ($s.NoNewPrivileges) { @('no-new-privileges') } else { @() }
                    Tmpfs = $tmpfs
                }
            }
        })
    }

    function Save-Inspect {
        param($Inspect, [string]$Name)
        $path = Join-Path $TestDrive "$Name.inspect.json"
        ConvertTo-Json -InputObject @($Inspect) -Depth 10 | Set-Content $path
        $path
    }

    # Runs the comparison of Docker against one or more Podman legs, returning the error it throws, if any.
    function Invoke-Comparison {
        param($Docker, [hashtable]$Podman)
        $reference = Save-Inspect (ConvertTo-DockerInspect $Docker) 'docker'
        $candidates = foreach ($leg in $Podman.Keys) { Save-Inspect (ConvertTo-PodmanInspect $Podman[$leg]) $leg }
        try {
            & $script:ScriptPath -Reference $reference -Candidate $candidates 6>$null
            $null
        }
        catch {
            $_.Exception.Message
        }
    }
}

Describe 'Compare-RuntimeParity' {
    It 'passes when both runtimes run JIM alike' {
        Invoke-Comparison -Docker (New-Services) -Podman @{ 'podman-rootful' = (New-Services) } | Should -BeNullOrEmpty
    }

    It 'fails naming a setting one runtime gives a service and the other does not' {
        $podman = New-Services
        $podman.worker.Env = @($podman.worker.Env | Where-Object { $_ -ne 'JIM_LOG_PATH' })

        Invoke-Comparison -Docker (New-Services) -Podman @{ 'podman-rootful' = $podman } |
            Should -BeLike '*podman-rootful*worker*JIM_LOG_PATH*Docker only*'
    }

    It 'fails naming a mount only one runtime has' {
        $podman = New-Services
        $podman.web.Mounts += '/connector-files'

        Invoke-Comparison -Docker (New-Services) -Podman @{ 'podman-rootful' = $podman } |
            Should -BeLike '*web*/connector-files*Podman only*'
    }

    It 'counts a tmpfs mount as a mount' {
        $podman = New-Services
        $podman.scheduler.Tmpfs = @()

        Invoke-Comparison -Docker (New-Services) -Podman @{ 'podman-rootful' = $podman } |
            Should -BeLike '*scheduler*/tmp*Docker only*'
    }

    It 'fails when the root file system is writable on one runtime' {
        $podman = New-Services
        $podman.web.ReadOnly = $false

        Invoke-Comparison -Docker (New-Services) -Podman @{ 'podman-rootful' = $podman } |
            Should -BeLike '*web*read-only root file system*'
    }

    It 'fails when a container keeps capabilities on one runtime' {
        $podman = New-Services
        $podman.worker.DropAll = $false

        Invoke-Comparison -Docker (New-Services) -Podman @{ 'podman-rootful' = $podman } |
            Should -BeLike '*worker*CAP_CHOWN*Podman only*'
    }

    It 'fails when no-new-privileges is off on one runtime' {
        $podman = New-Services
        $podman.scheduler.NoNewPrivileges = $false

        Invoke-Comparison -Docker (New-Services) -Podman @{ 'podman-rootful' = $podman } |
            Should -BeLike '*scheduler*no-new-privileges*'
    }

    It 'fails when a container runs as a different user' {
        $podman = New-Services
        $podman.web.User = 'root'

        Invoke-Comparison -Docker (New-Services) -Podman @{ 'podman-rootful' = $podman } |
            Should -BeLike '*web*user*app*root*'
    }

    It 'fails when a service runs on one runtime only' {
        $podman = New-Services
        $podman.Remove('scheduler')

        Invoke-Comparison -Docker (New-Services) -Podman @{ 'podman-rootful' = $podman } |
            Should -BeLike '*scheduler*not running on podman-rootful*'
    }

    It 'accepts the differences it lists as intended' {
        $docker = New-Services
        $podman = New-Services
        # Compose passes its own settings, and the whole .env for the database, into the containers.
        foreach ($name in 'web', 'worker', 'scheduler') {
            $docker[$name].Env += 'DOCKER_REGISTRY', 'JIM_VERSION', 'JIM_WEB_PORT'
        }
        # Compose puts the slow query threshold into the database's command itself; Podman's command reads it.
        $docker.database.Env = @($docker.database.Env | Where-Object { $_ -ne 'JIM_DB_LOG_MIN_DURATION' })
        # The certificate: two files on Docker, one secret folder on Podman.
        $docker.web.Mounts += '/run/jim-tls/tls.crt', '/run/jim-tls/tls.key'
        $podman.web.Mounts += '/run/jim-tls'

        Invoke-Comparison -Docker $docker -Podman @{ 'podman-rootful' = $podman } | Should -BeNullOrEmpty
    }

    It 'fails when the database container is given JIM''s own settings' {
        # PostgreSQL reads none of them, and its superuser can read the server's environment (#1862).
        $docker = New-Services
        $docker.database.Env += 'JIM_SSO_SECRET'

        Invoke-Comparison -Docker $docker -Podman @{ 'podman-rootful' = (New-Services) } |
            Should -BeLike '*database*JIM_SSO_SECRET*Docker only*'
    }

    It 'takes Docker''s default capabilities as what a container keeps when it drops none' {
        # Neither runtime is told the database's capabilities, so each applies its default set. Docker's adds
        # three to Podman's, which the script lists as intended; one Podman lacks is a difference.
        $podman = New-Services
        $podman.database.Capabilities = @($script:PodmanDefaultCapabilities | Where-Object { $_ -ne 'CAP_KILL' })

        Invoke-Comparison -Docker (New-Services) -Podman @{ 'podman-rootful' = $podman } |
            Should -BeLike '*database*CAP_KILL*Docker only*'
    }

    It 'compares every candidate, naming the one that differs' {
        $rootless = New-Services
        $rootless.worker.User = 'root'

        Invoke-Comparison -Docker (New-Services) -Podman @{ 'podman-rootful' = (New-Services); 'podman-rootless' = $rootless } |
            Should -BeLike '*podman-rootless*worker*user*'
    }
}
