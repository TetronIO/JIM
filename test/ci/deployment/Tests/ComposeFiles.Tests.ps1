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
    # environment (a hashtable of name to value) and command of each service.
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
            $services[$service.Name] = [pscustomobject]@{ Environment = $environment; Command = @($service.Value.command) }
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

    It 'still gives JIM''s own services the settings file, secrets included' {
        $script:Services['jim.web'].Environment['JIM_SSO_SECRET'] | Should -Be 'sso-secret-for-test'
        $script:Services['jim.worker'].Environment['JIM_DB_PASSWORD'] | Should -Be 'database-password-for-test'
    }
}
