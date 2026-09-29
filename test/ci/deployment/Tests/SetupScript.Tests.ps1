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
