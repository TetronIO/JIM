# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for functions of deploy/setup.sh that decide something without changing the host.

.DESCRIPTION
    Sources the installer in bash, which then defines its functions without running, replaces the commands a
    function asks the host with fakes, and runs the function. The deployment-boot job runs the installer
    itself; these cover the cases that job's hosts cannot show.
#>

BeforeDiscovery {
    $script:NoBash = -not (Get-Command bash -ErrorAction SilentlyContinue)
}

BeforeAll {
    $script:SetupPath = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..' 'deploy' 'setup.sh')).Path

    # Runs one of the installer's functions with the given variables and shell functions defined first, and
    # returns what it wrote and how it exited.
    function Invoke-SetupFunction {
        param([string]$Function, [string]$Arrange = '')
        $script = @"
source "`$1"
setup_colours
$Arrange
$Function
"@
        # This file is checked out with CRLF line endings (.gitattributes), which bash would read as part of
        # each command.
        $script = $script -replace "`r", ''
        $output = & bash -c $script 'setup-test' $script:SetupPath 2>&1
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String) }
    }

    # A rootless installation under the account jim, whose systemd manager reports the given environment.
    function Get-RootlessArrangement {
        param([string[]]$ManagerEnvironment)
        $lines = ($ManagerEnvironment | ForEach-Object { "printf '%s\n' '$_'" }) -join '; '
        if (-not $lines) { $lines = ':' }
        @"
PODMAN_ACCOUNT=jim
PODMAN_SYSTEMD=true
account_home() { printf '/home/jim'; }
id() { if [ "`$1" = "-u" ]; then echo 1002; else command id "`$@"; fi; }
jim_systemctl() { [ "`$1" = "show-environment" ] || return 1; $lines; }
"@
    }
}

Describe 'setup.sh check_account_environment' -Skip:$script:NoBash {
    It 'passes when the account''s manager uses the account''s own folders' {
        $arrange = Get-RootlessArrangement @('HOME=/home/jim', 'PATH=/usr/bin', 'XDG_RUNTIME_DIR=/run/user/1002')

        $result = Invoke-SetupFunction 'check_account_environment' $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
    }

    It 'passes when the folders are set to the ones Podman uses anyway' {
        $arrange = Get-RootlessArrangement @('XDG_RUNTIME_DIR=/run/user/1002', 'XDG_CONFIG_HOME=/home/jim/.config',
            'XDG_DATA_HOME=/home/jim/.local/share')

        $result = Invoke-SetupFunction 'check_account_environment' $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
    }

    It 'stops, naming the setting and where it comes from, when the configuration folder is another account''s' {
        $arrange = Get-RootlessArrangement @('XDG_RUNTIME_DIR=/run/user/1002', 'XDG_CONFIG_HOME=/home/runner/.config')

        $result = Invoke-SetupFunction 'check_account_environment' $arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*XDG_CONFIG_HOME=/home/runner/.config*'
        $result.Output | Should -BeLike '*/etc/environment*'
        $result.Output | Should -BeLike '*systemctl restart user@1002.service*'
    }

    It 'stops when the runtime folder is another account''s' {
        $arrange = Get-RootlessArrangement @('XDG_RUNTIME_DIR=/run/user/1001')

        $result = Invoke-SetupFunction 'check_account_environment' $arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*XDG_RUNTIME_DIR=/run/user/1001*'
    }

    It 'stops when the data folder, which holds Podman''s storage, is elsewhere' {
        $arrange = Get-RootlessArrangement @('XDG_RUNTIME_DIR=/run/user/1002', 'XDG_DATA_HOME=/srv/shared')

        $result = Invoke-SetupFunction 'check_account_environment' $arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*XDG_DATA_HOME=/srv/shared*'
    }

    It 'stops when it cannot read the manager''s environment' {
        $arrange = (Get-RootlessArrangement @()) + "`njim_systemctl() { return 1; }"

        $result = Invoke-SetupFunction 'check_account_environment' $arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*user@1002.service*'
    }

    It 'checks nothing when JIM runs as root' {
        $arrange = @'
PODMAN_ACCOUNT=
PODMAN_SYSTEMD=true
jim_systemctl() { echo "asked systemd"; return 1; }
'@

        $result = Invoke-SetupFunction 'check_account_environment' $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Not -BeLike '*asked systemd*'
    }
}

Describe 'setup.sh pinned_database_image' -Skip:$script:NoBash {
    It 'reads the PostgreSQL image docker-compose.yml pins, without the JIM_DB_IMAGE syntax around it' {
        $compose = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..' 'docker-compose.yml')).Path
        $expected = & (Join-Path $PSScriptRoot '..' '..' '..' '..' 'scripts' 'Get-PostgresImageReference.ps1')

        $result = Invoke-SetupFunction "pinned_database_image '$compose'"

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output.Trim() | Should -Be $expected
    }
}

Describe 'setup.sh check_database_image' -Skip:$script:NoBash {
    BeforeAll {
        $script:Pinned = 'docker.io/library/postgres:18.6@sha256:' + ('a' * 64)
        $script:LoadedId = 'sha256:' + ('b' * 64)

        # A bundle whose PostgreSQL image ID record holds the given IDs, and a compose file pinning the image.
        function New-DatabaseImageArrangement {
            param([string[]]$RecordedIds, [switch]$PinResolves, [switch]$NoRecord, [switch]$NoBundle)
            $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path (Join-Path $root 'bundle/docker-images') -Force | Out-Null
            "services:`n  jim.database:`n    image: `${JIM_DB_IMAGE:-$($script:Pinned)}`n" | Set-Content -NoNewline (Join-Path $root 'compose.yml')
            if (-not $NoRecord) {
                (($RecordedIds -join "`n") + "`n") | Set-Content -NoNewline (Join-Path $root 'bundle/docker-images/postgres-18.image-ids')
            }
            $bundle = if ($NoBundle) { '' } else { "$root/bundle" }
            $pinStatus = if ($PinResolves) { 0 } else { 1 }
            [pscustomobject]@{
                Compose = "$root/compose.yml"
                # docker image inspect <pin> answers as the image store would; inspecting the loaded name by -f
                # gives the loaded image's ID.
                Arrange = @"
BUNDLE_DIR='$bundle'
docker() { if [ "`$3" = "-f" ]; then echo '$($script:LoadedId)'; return 0; fi; return $pinStatus; }
"@
            }
        }
    }

    It 'leaves JIM_DB_IMAGE unset when Docker finds the pinned image' {
        $arrangement = New-DatabaseImageArrangement -RecordedIds @($script:LoadedId) -PinResolves

        $result = Invoke-SetupFunction "check_database_image '$($arrangement.Compose)'; echo ""ID=[`$DATABASE_IMAGE_ID]""" $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*ID=`[`]*'
    }

    It 'runs the loaded image by its ID when the pin does not resolve and the ID is the one the bundle records' {
        $arrangement = New-DatabaseImageArrangement -RecordedIds @(('sha256:' + ('c' * 64)), $script:LoadedId)

        $result = Invoke-SetupFunction "check_database_image '$($arrangement.Compose)'; echo ""ID=[`$DATABASE_IMAGE_ID]""" $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike "*ID=``[$($script:LoadedId)``]*"
    }

    It 'stops when the loaded image is not the one the bundle records' {
        $arrangement = New-DatabaseImageArrangement -RecordedIds @(('sha256:' + ('c' * 64)))

        $result = Invoke-SetupFunction "check_database_image '$($arrangement.Compose)'" $arrangement.Arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*is not the PostgreSQL image this bundle ships*'
    }

    It 'stops when the bundle records no ID to check the image by' {
        $arrangement = New-DatabaseImageArrangement -NoRecord

        $result = Invoke-SetupFunction "check_database_image '$($arrangement.Compose)'" $arrangement.Arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*does not record the PostgreSQL image*'
    }

    It 'stops, rather than trusting a name, when a downloaded image does not resolve by its pin' {
        $arrangement = New-DatabaseImageArrangement -RecordedIds @($script:LoadedId) -NoBundle

        $result = Invoke-SetupFunction "check_database_image '$($arrangement.Compose)'" $arrangement.Arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*after downloading it*'
    }
}

Describe 'setup.sh check_compose_edits' -Skip:$script:NoBash {
    BeforeAll {
        # An installation with the two compose files the release ships, and optionally the record of them.
        function New-ComposeInstallation {
            param([switch]$NoRecord)
            $dir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $dir | Out-Null
            'services: {}' | Set-Content -NoNewline (Join-Path $dir 'docker-compose.yml')
            'services: {}' | Set-Content -NoNewline (Join-Path $dir 'docker-compose.production.yml')
            if (-not $NoRecord) {
                & bash -c "cd '$dir' && sha256sum docker-compose.yml docker-compose.production.yml > compose-files.sha256"
            }
            $dir
        }
    }

    It 'passes when the compose files are as installed' {
        $dir = New-ComposeInstallation

        $result = Invoke-SetupFunction "check_compose_edits '$dir'"

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Not -BeLike '*ERR*'
    }

    It 'stops, naming the file, when a compose file was edited after it was installed' {
        $dir = New-ComposeInstallation
        Add-Content -Path (Join-Path $dir 'docker-compose.production.yml') -Value '# a local change'

        $result = Invoke-SetupFunction "check_compose_edits '$dir'"

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*edited after they were installed: docker-compose.production.yml*'
        $result.Output | Should -Not -BeLike '*docker-compose.yml *'
    }

    It 'warns, and carries on, when the installation has no record of its compose files' {
        $dir = New-ComposeInstallation -NoRecord

        $result = Invoke-SetupFunction "check_compose_edits '$dir'"

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*no record of its compose files*'
    }
}

Describe 'setup.sh read_deployment_compose_files' -Skip:$script:NoBash {
    It 'uses every compose file JIM was last started with, an administrator''s own included' {
        $dir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir | Out-Null
        foreach ($name in 'docker-compose.yml', 'docker-compose.production.yml', 'docker-compose.local.yml') {
            'services: {}' | Set-Content -NoNewline (Join-Path $dir $name)
        }
        $arrange = "docker() { echo '$dir/docker-compose.yml,$dir/docker-compose.production.yml,$dir/docker-compose.local.yml'; }"

        $result = Invoke-SetupFunction "read_deployment_compose_files '$dir'; echo ""FILES=`${UPGRADE_COMPOSE_FILES[*]}""" $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike "*FILES=-f $dir/docker-compose.yml -f $dir/docker-compose.production.yml -f $dir/docker-compose.local.yml*"
    }

    It 'stops when a compose file JIM was started with no longer exists' {
        $arrange = "docker() { echo '/nonexistent/docker-compose.yml'; }"

        $result = Invoke-SetupFunction "read_deployment_compose_files /nonexistent" $arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*/nonexistent/docker-compose.yml, which no longer exists*'
    }

    It 'uses the files the installer starts JIM with when there is no container to ask' {
        $arrange = 'docker() { return 1; }'

        $result = Invoke-SetupFunction "read_deployment_compose_files /opt/jim; echo ""FILES=`${UPGRADE_COMPOSE_FILES[*]}""" $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*FILES=-f docker-compose.yml -f docker-compose.production.yml*'
    }
}

Describe 'setup.sh report_new_settings' -Skip:$script:NoBash {
    It 'names the settings the new template has that .env does not mention, set or commented out' {
        $dir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir | Out-Null
        "JIM_A=1`n# JIM_B=2`nJIM_C=3`n#   EXAMPLE_ONLY=4`n" | Set-Content -NoNewline (Join-Path $dir 'template')
        "JIM_A=5`n#JIM_C=6`n" | Set-Content -NoNewline (Join-Path $dir 'env')

        $result = Invoke-SetupFunction "report_new_settings '$dir/template' '$dir/env'"

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*keep their defaults: JIM_B.*'
    }
}

Describe 'setup.sh confirm_backup' -Skip:$script:NoBash {
    It 'cancels the upgrade, changing nothing, when the backup is not confirmed' {
        $result = Invoke-SetupFunction 'confirm_backup; echo "carried on"' 'prompt_yn() { return 1; }'

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*Upgrade cancelled; nothing has changed*'
        $result.Output | Should -Not -BeLike '*carried on*'
    }

    It 'does not ask when JIM_SETUP_BACKUP_CONFIRMED is true' {
        $arrange = "JIM_SETUP_BACKUP_CONFIRMED=true`nprompt_yn() { echo asked; return 1; }"

        $result = Invoke-SetupFunction 'confirm_backup; echo "carried on"' $arrange

        $result.Output | Should -BeLike '*carried on*'
        $result.Output | Should -Not -BeLike '*asked*'
    }
}

Describe 'setup.sh upgrade_installation' -Skip:$script:NoBash {
    BeforeAll {
        # A Docker installation running the given version, and a bundle of the given version beside it.
        function New-UpgradeArrangement {
            param([string]$Installed, [string]$Bundled)
            $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path (Join-Path $root 'jim'), (Join-Path $root 'bundle') -Force | Out-Null
            'services: {}' | Set-Content -NoNewline (Join-Path $root 'jim/docker-compose.production.yml')
            "JIM_VERSION=$Installed`n" | Set-Content -NoNewline (Join-Path $root 'jim/.env')
            "$Bundled`n" | Set-Content -NoNewline (Join-Path $root 'bundle/VERSION')
            @"
JIM_INSTALL_DIR='$root/jim'
BUNDLE_DIR='$root/bundle'
check_prerequisites() { :; }
check_compose_edits() { echo "went on to upgrade"; exit 0; }
"@
        }
    }

    It 'changes nothing when JIM already runs the release' {
        $result = Invoke-SetupFunction 'upgrade_installation' (New-UpgradeArrangement -Installed '1.2.0' -Bundled '1.2.0')

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*already runs v1.2.0; there is nothing to upgrade*'
        $result.Output | Should -Not -BeLike '*went on to upgrade*'
    }

    It 'refuses to go back to an older release' {
        $result = Invoke-SetupFunction 'upgrade_installation' (New-UpgradeArrangement -Installed '1.10.0' -Bundled '1.9.0')

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*runs v1.10.0, which is newer than v1.9.0*'
    }

    It 'goes on to upgrade to a newer release, comparing versions numerically' {
        $result = Invoke-SetupFunction 'upgrade_installation' (New-UpgradeArrangement -Installed '1.9.0' -Bundled '1.10.0')

        $result.Output | Should -BeLike '*from v1.9.0 to v1.10.0*'
        $result.Output | Should -BeLike '*went on to upgrade*'
    }
}

Describe 'setup.sh finish_upgrade' -Skip:$script:NoBash {
    BeforeAll {
        function New-SwappedInstallation {
            $dir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $dir | Out-Null
            foreach ($name in 'docker-compose.yml', 'docker-compose.production.yml', '.env') {
                'new' | Set-Content -NoNewline (Join-Path $dir $name)
                'previous' | Set-Content -NoNewline (Join-Path $dir "$name.previous")
            }
            $dir
        }
    }

    It 'puts the previous compose files and .env back when the upgrade stops before restarting JIM' {
        $dir = New-SwappedInstallation

        $result = Invoke-SetupFunction 'finish_upgrade' "UPGRADE_INSTALL_DIR='$dir'`nUPGRADE_SWAPPED=true"

        $result.ExitCode | Should -Be 0 -Because $result.Output
        foreach ($name in 'docker-compose.yml', 'docker-compose.production.yml', '.env') {
            Get-Content -Raw (Join-Path $dir $name) | Should -Be 'previous' -Because $name
        }
    }

    It 'keeps the new files once JIM has been restarted on the new release' {
        $dir = New-SwappedInstallation

        Invoke-SetupFunction 'finish_upgrade' "UPGRADE_INSTALL_DIR='$dir'`nUPGRADE_SWAPPED=true`nUPGRADE_STARTED=true" | Out-Null

        Get-Content -Raw (Join-Path $dir '.env') | Should -Be 'new'
    }
}
