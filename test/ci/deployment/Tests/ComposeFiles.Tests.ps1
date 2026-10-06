# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for what the Docker Compose files give each of JIM's containers.

.DESCRIPTION
    Renders the production and development stacks with `docker compose config`, which needs neither a Docker
    daemon nor the images, against a settings file holding recognisable secrets, and checks what each service is
    given. The deployment-boot job sees the same in running containers; this sees it on every pull request, in
    seconds.
#>

BeforeDiscovery {
    $null = & docker compose version 2>&1
    $script:NoCompose = $LASTEXITCODE -ne 0
    $script:Stacks = @(
        @{ Stack = 'production'; Files = @('docker-compose.yml', 'docker-compose.production.yml') }
        @{ Stack = 'development'; Files = @('docker-compose.yml', 'docker-compose.override.yml') }
    )
}

BeforeAll {
    $script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..')).Path

    # Renders a stack from copies of its files, beside a settings file of the given lines, and returns the
    # environment (a hashtable of name to value), command and /dev/shm size (in bytes) of each service.
    function Get-ComposeEnvironment {
        param([string[]]$Files, [string[]]$Settings)
        $project = Join-Path $TestDrive ([guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Path $project | Out-Null
        foreach ($name in $Files) {
            $source = if ($name -eq 'docker-compose.production.yml') { Join-Path 'deploy' $name } else { $name }
            Copy-Item (Join-Path $script:RepositoryRoot $source) (Join-Path $project $name)
        }
        Set-Content -Path (Join-Path $project '.env') -Value $Settings

        # The bundled database is in the with-db profile, which an installation using an external server leaves out.
        $arguments = @('compose', '--project-directory', $project, '--profile', 'with-db') +
            @($Files | ForEach-Object { '-f', (Join-Path $project $_) }) + @('config', '--format', 'json')
        $output = & docker @arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "docker compose config failed: $($output -join "`n")"
        }
        $services = @{}
        foreach ($service in (($output | Where-Object { $_ -is [string] }) -join "`n" | ConvertFrom-Json).services.PSObject.Properties) {
            $environment = @{}
            if ($service.Value.environment) {
                foreach ($variable in $service.Value.environment.PSObject.Properties) {
                    $environment[$variable.Name] = $variable.Value
                }
            }
            $services[$service.Name] = [pscustomobject]@{
                Environment = $environment
                Command = @($service.Value.command)
                ShmSize = $service.Value.shm_size
            }
        }
        $services
    }

    $script:Settings = @(
        'LANG=en_GB.UTF-8'
        'DOCKER_REGISTRY=ghcr.io/tetronio'
        'JIM_VERSION=1.2.3'
        'JIM_DB_HOSTNAME=jim.database'
        'JIM_DB_NAME=jim'
        'JIM_DB_USERNAME=jim'
        'JIM_DB_PASSWORD=database-password-for-test'
        'JIM_DB_LOG_MIN_DURATION=750'
        'JIM_SSO_AUTHORITY=https://idp.example.test/realms/jim'
        'JIM_SSO_CLIENT_ID=jim-web'
        'JIM_SSO_SECRET=sso-secret-for-test'
        'JIM_INFRASTRUCTURE_API_KEY=jim_ak_api-key-for-test-0123456789abcdef'
    )
}

Describe 'The database container''s environment on the <Stack> stack' -Skip:$script:NoCompose -ForEach $script:Stacks {
    BeforeAll {
        $script:Services = Get-ComposeEnvironment -Files $Files -Settings $script:Settings
    }

    It 'gives PostgreSQL only its own settings' {
        # PostgreSQL reads none of JIM's settings, and its superuser can read the server's environment, so
        # nothing else belongs there (#1862).
        @($script:Services['jim.database'].Environment.Keys | Sort-Object) |
            Should -Be @('LANG', 'POSTGRES_DB', 'POSTGRES_PASSWORD', 'POSTGRES_USER')
    }

    It 'still gives PostgreSQL the database name, account and password from the settings file' {
        $database = $script:Services['jim.database'].Environment
        $database['POSTGRES_DB'] | Should -Be 'jim'
        $database['POSTGRES_USER'] | Should -Be 'jim'
        $database['POSTGRES_PASSWORD'] | Should -Be 'database-password-for-test'
    }

    It 'still applies the slow query threshold from the settings file' {
        $script:Services['jim.database'].Command | Should -Contain 'log_min_duration_statement=750'
    }

    It 'drops a session whose client has gone within two minutes' {
        # Without TCP keepalives PostgreSQL holds such a session for about two hours, Linux's default. On Podman
        # every stop of JIM leaves its sessions so, since the pod's network goes before its services close their
        # connections (#1980).
        $command = $script:Services['jim.database'].Command
        $command | Should -Contain 'tcp_keepalives_idle=60'
        $command | Should -Contain 'tcp_keepalives_interval=10'
        $command | Should -Contain 'tcp_keepalives_count=6'
    }

    It 'still gives JIM''s own services the settings file, secrets included' {
        $script:Services['jim.web'].Environment['JIM_SSO_SECRET'] | Should -Be 'sso-secret-for-test'
        $script:Services['jim.worker'].Environment['JIM_DB_PASSWORD'] | Should -Be 'database-password-for-test'
    }
}

Describe 'The bundled PostgreSQL''s memory on the production stack' -Skip:$script:NoCompose {
    BeforeAll {
        $script:ProductionFiles = @('docker-compose.yml', 'docker-compose.production.yml')
    }

    It 'takes its memory settings from the settings file, where the installer sizes them to the host' {
        $sized = $script:Settings + @(
            'JIM_DB_SHARED_BUFFERS=1536MB'
            'JIM_DB_EFFECTIVE_CACHE_SIZE=3072MB'
            'JIM_DB_MAINTENANCE_WORK_MEM=384MB'
            'JIM_DB_WORK_MEM=7MB'
            'JIM_DB_SHM_SIZE=1920mb'
        )

        $database = (Get-ComposeEnvironment -Files $script:ProductionFiles -Settings $sized)['jim.database']

        $database.Command | Should -Contain 'shared_buffers=1536MB'
        $database.Command | Should -Contain 'effective_cache_size=3072MB'
        $database.Command | Should -Contain 'maintenance_work_mem=384MB'
        $database.Command | Should -Contain 'work_mem=7MB'
        [long]$database.ShmSize | Should -Be (1920 * 1MB)
    }

    It 'fits the documented 4 GB minimum when the settings file does not size it' {
        # Every release up to v0.16.0 asked for 8 GB of shared_buffers, which no host with less than about 10 GB of
        # memory could give, so the bundled database never started on the documented 4 GB or 8 GB hosts (#1943).
        $database = (Get-ComposeEnvironment -Files $script:ProductionFiles -Settings $script:Settings)['jim.database']

        $database.Command | Should -Contain 'shared_buffers=1GB'
        $database.Command | Should -Contain 'effective_cache_size=2GB'
        $database.Command | Should -Contain 'maintenance_work_mem=256MB'
        $database.Command | Should -Contain 'work_mem=4MB'
        [long]$database.ShmSize | Should -Be (1280 * 1MB)
    }
}
