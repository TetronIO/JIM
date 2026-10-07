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

Describe 'setup.sh jim_systemctl_command' -Skip:$script:NoBash {
    It 'as root, for a rootless installation, reaches the account''s systemd manager through its own bus, which needs no systemd-container (#1955)' {
        # Root, with the account jim as uid 1002. sudo stands in for the command the administrator would run, and
        # writes what it was given, one argument a line.
        $arrange = @'
PODMAN_ACCOUNT=jim
id() { if [ "$1" = "-u" ] && [ -z "${2:-}" ]; then echo 0; elif [ "$1" = "-u" ]; then echo 1002; else command id "$@"; fi; }
sudo() { printf '%s\n' "$@"; }
'@

        $result = Invoke-SetupFunction 'eval "$(jim_systemctl_command) status jim.service"' $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $arguments = @($result.Output -split "`r?`n" | Where-Object { $_ })
        # systemctl -M jim@ fails on a minimal RHEL-family host, which lacks the systemd-container package.
        $arguments | Should -Not -Contain '-M'
        $arguments[0..1] | Should -Be @('-u', 'jim')
        $arguments | Should -Contain 'XDG_RUNTIME_DIR=/run/user/1002'
        $arguments | Should -Contain 'DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1002/bus'
        $arguments[-4..-1] | Should -Be @('systemctl', '--user', 'status', 'jim.service')
    }
}

Describe 'setup.sh configure_apparmor' -Skip:$script:NoBash {
    BeforeAll {
        # A host with AppArmor's policy folder and the kernel's interface to it under the test's own folder: Ubuntu
        # 24.04's profiles for crun and podman, unless -NoRuntimeProfiles, with empty local overrides; the profiles the
        # kernel has loaded, -Loaded, of which -InUse are what some process runs under; and an apparmor_parser that
        # records what it was asked, and fails to compile with -PolicyBroken.
        function New-AppArmorHost {
            param(
                [switch]$NoRuntimeProfiles,
                [switch]$NetworkAllowed,
                [string]$SignalRuleFile = '',
                [string[]]$Loaded = @(),
                [string[]]$InUse = @(),
                [string]$Fix = 'true',
                [string]$Account = '',
                [string]$Enabled = 'Y',
                [switch]$PolicyBroken
            )
            $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            $policy = Join-Path $root 'apparmor.d'
            $kernel = Join-Path $root 'securityfs'
            New-Item -ItemType Directory -Path (Join-Path $policy 'local'), (Join-Path $policy 'abstractions'), $kernel | Out-Null
            if (-not $NoRuntimeProfiles) {
                foreach ($profile in 'crun', 'podman') {
                    $text = "abi <abi/4.0>,`ninclude <tunables/global>`n`nprofile $profile /usr/bin/$profile flags=(unconfined) {`n" +
                        "  userns,`n`n  include if exists <local/$profile>`n}`n"
                    Set-Content -Path (Join-Path $policy $profile) -Value $text -NoNewline
                    Set-Content -Path (Join-Path $policy 'local' $profile) -Value $(if ($NetworkAllowed) { "network,`n" } else { '' }) -NoNewline
                }
            }
            if ($SignalRuleFile) {
                New-Item -ItemType Directory -Path (Join-Path $policy 'abstractions' 'base.d') | Out-Null
                Set-Content -Path (Join-Path $policy 'abstractions' 'base.d' $SignalRuleFile) -NoNewline `
                    -Value "  signal peer=@{profile_name}//&{crun,podman},`n"
            }
            $listed = @($Loaded | ForEach-Object { "$_ (enforce)" }) + 'crun (unconfined)', 'podman (unconfined)'
            Set-Content -Path (Join-Path $kernel 'profiles') -Value ($listed -join "`n")

            $failCompile = if ($PolicyBroken) { 'return 1' } else { 'return 0' }
            $arrange = @"
APPARMOR_DIR='$policy'
APPARMOR_FS='$kernel'
PODMAN_ACCOUNT='$Account'
JIM_SETUP_FIX_APPARMOR='$Fix'
apparmor_enabled() { [ '$Enabled' = Y ]; }
apparmor_profile_in_use() { case ' $($InUse -join ' ') ' in *" `$1 "*) return 0 ;; esac; return 1; }
apparmor_parser() {
    echo "`$*" >> '$root/parser.log'
    case "`$1" in -Q*) cat > /dev/null; $failCompile ;; esac
}
"@
            [pscustomobject]@{
                Arrange = $arrange
                Policy = $policy
                SignalRules = Join-Path $policy 'abstractions' 'base.d' 'jim-podman'
                Removed = Join-Path $kernel '.remove'
                ParserLog = Join-Path $root 'parser.log'
            }
        }

        # The rules of a policy file, without comments or blank lines.
        function Get-Rule {
            param([string]$Path)
            @(Get-Content -Path $Path | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
        }
    }

    It 'allows the network in the local overrides of crun and podman, and reloads both' {
        $apparmor = New-AppArmorHost

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Rule (Join-Path $apparmor.Policy 'local' 'crun') | Should -Be @('network,')
        Get-Rule (Join-Path $apparmor.Policy 'local' 'podman') | Should -Be @('network,')
        $reloads = Get-Content $apparmor.ParserLog | Where-Object { $_ -like '-r *' }
        $reloads | Should -Contain "-r $(Join-Path $apparmor.Policy 'crun')"
        $reloads | Should -Contain "-r $(Join-Path $apparmor.Policy 'podman')"
    }

    It 'lets a profile signal itself stacked with crun''s or podman''s, in the base abstraction''s folder for site additions, and nothing more' {
        $apparmor = New-AppArmorHost

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Rule $apparmor.SignalRules | Should -Be @('signal peer=@{profile_name}//&{crun,podman},')
    }

    It 'checks the host''s policy still compiles with the rule, since every profile includes the base abstraction' {
        $apparmor = New-AppArmorHost

        Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange | Out-Null

        Get-Content $apparmor.ParserLog | Where-Object { $_ -like '-Q*' } | Should -Not -BeNullOrEmpty
    }

    It 'takes the rule back out and stops when the host''s policy no longer compiles with it' {
        $apparmor = New-AppArmorHost -PolicyBroken

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Not -Be 0
        $apparmor.SignalRules | Should -Not -Exist
        $result.Output | Should -BeLike '*abstractions/base.d/jim-podman*'
    }

    It 'adds only the signal rule to a host that has the network rule, as an installation of v0.16.0 or earlier does' {
        $apparmor = New-AppArmorHost -NetworkAllowed

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Rule (Join-Path $apparmor.Policy 'local' 'crun') | Should -Be @('network,')
        Get-Rule $apparmor.SignalRules | Should -Be @('signal peer=@{profile_name}//&{crun,podman},')
    }

    It 'changes nothing on a host that has both rules, the signal rule in a file of its own included' {
        $apparmor = New-AppArmorHost -NetworkAllowed -SignalRuleFile 'site' -Loaded 'containers-default-0.57.4-apparmor1'

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output.Trim() | Should -BeNullOrEmpty
        $apparmor.ParserLog | Should -Not -Exist
        $apparmor.SignalRules | Should -Not -Exist
        $apparmor.Removed | Should -Not -Exist
    }

    It 'changes nothing for a rootless installation, which Podman confines with no profile' {
        $apparmor = New-AppArmorHost -Account 'jim'

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $apparmor.ParserLog | Should -Not -Exist
        $apparmor.SignalRules | Should -Not -Exist
    }

    It 'changes nothing where AppArmor is off' {
        $apparmor = New-AppArmorHost -Enabled 'N'

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $apparmor.ParserLog | Should -Not -Exist
        $apparmor.SignalRules | Should -Not -Exist
    }

    It 'changes nothing where Podman''s runtimes have no profiles of their own, so nothing is stacked' {
        $apparmor = New-AppArmorHost -NoRuntimeProfiles

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $apparmor.ParserLog | Should -Not -Exist
        $apparmor.SignalRules | Should -Not -Exist
    }

    It 'stops, changing nothing, and says what to do by hand when the rules are declined' {
        $apparmor = New-AppArmorHost -Fix 'false'

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Not -Be 0
        Get-Rule (Join-Path $apparmor.Policy 'local' 'crun') | Should -BeNullOrEmpty
        $apparmor.SignalRules | Should -Not -Exist
        $result.Output | Should -BeLike '*abstractions/base.d/jim-podman*'
        $result.Output | Should -BeLike '*--rootless*'
        $result.Output | Should -BeLike '*/administration/podman/#firewall-selinux-and-apparmor*'
    }

    It 'unloads Podman''s profile for containers when no process runs under it, so that the next container loads it with the rule' {
        $apparmor = New-AppArmorHost -Loaded 'containers-default-0.57.4-apparmor1'

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Content $apparmor.Removed -Raw | Should -Be 'containers-default-0.57.4-apparmor1'
    }

    It 'leaves a profile a process runs under loaded, which would leave the process unconfined, and says to restart the server' {
        $apparmor = New-AppArmorHost -Loaded 'containers-default-0.57.4-apparmor1' -InUse 'containers-default-0.57.4-apparmor1'

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $apparmor.Removed | Should -Not -Exist
        $result.Output | Should -BeLike '*restart*server*'
    }

    It 'unloads nothing when it adds only the network rule' {
        $apparmor = New-AppArmorHost -SignalRuleFile 'site' -Loaded 'containers-default-0.57.4-apparmor1'

        $result = Invoke-SetupFunction 'configure_apparmor' $apparmor.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Rule (Join-Path $apparmor.Policy 'local' 'crun') | Should -Be @('network,')
        $apparmor.Removed | Should -Not -Exist
    }
}

Describe 'setup.sh size_database' -Skip:$script:NoBash {
    BeforeAll {
        $script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..')).Path

        # A settings file made from the release's template for the runtime, with any -Existing lines added, on a
        # host with the given memory (in MB; empty when it cannot be read).
        function New-SizingArrangement {
            param([string]$MemoryMB, [string]$Runtime = 'docker', [string[]]$Existing = @(), [string]$Environment = '')
            $dir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $dir | Out-Null
            if ($Runtime -eq 'podman') {
                $config = Join-Path $dir 'jim-config.yaml'
                Copy-Item (Join-Path $script:RepositoryRoot 'deploy' 'podman' 'jim-config.yaml') $config
            }
            else {
                $config = Join-Path $dir '.env'
                Copy-Item (Join-Path $script:RepositoryRoot '.env.example') $config
            }
            if ($Existing) {
                Add-Content -Path $config -Value $Existing
            }
            [pscustomobject]@{
                Config = $config
                Arrange = "RUNTIME=$Runtime`nCONFIG_FILE='$config'`nhost_memory_mb() { echo '$MemoryMB'; }`n$Environment"
            }
        }

        # The settings a file sets, uncommented, as name to value.
        function Get-Settings {
            param([string]$Path)
            $settings = @{}
            foreach ($line in Get-Content $Path) {
                if ($line -match '^\s*(JIM_DB_[A-Z_]+)\s*[=:]\s*"?([^"]*)"?\s*$') {
                    $settings[$Matches[1]] = $Matches[2]
                }
            }
            $settings
        }
    }

    It 'sizes PostgreSQL to the host''s memory, and says what it chose' {
        $arrangement = New-SizingArrangement -MemoryMB 6144

        $result = Invoke-SetupFunction 'size_database' $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $settings = Get-Settings $arrangement.Config
        $settings['JIM_DB_SHARED_BUFFERS'] | Should -Be '1536MB'
        $settings['JIM_DB_EFFECTIVE_CACHE_SIZE'] | Should -Be '3072MB'
        $settings['JIM_DB_MAINTENANCE_WORK_MEM'] | Should -Be '384MB'
        $settings['JIM_DB_WORK_MEM'] | Should -Be '7MB'
        $settings['JIM_DB_SHM_SIZE'] | Should -Be '1920mb'
        $result.Output | Should -BeLike '*6.0 GB*shared_buffers 1536MB*'
    }

    It 'gives a host of the documented 4 GB minimum settings that start on it' {
        # A "4 GB" machine reports a little less, the kernel's share taken out.
        $arrangement = New-SizingArrangement -MemoryMB 3900

        Invoke-SetupFunction 'size_database' $arrangement.Arrange | Out-Null

        $settings = Get-Settings $arrangement.Config
        $settings['JIM_DB_SHARED_BUFFERS'] | Should -Be '975MB'
        $settings['JIM_DB_EFFECTIVE_CACHE_SIZE'] | Should -Be '1950MB'
        $settings['JIM_DB_MAINTENANCE_WORK_MEM'] | Should -Be '243MB'
        $settings['JIM_DB_WORK_MEM'] | Should -Be '4MB'
    }

    It 'stops shared_buffers at 8 GB and maintenance_work_mem at 2 GB on a large host, which JIM''s services share' {
        $arrangement = New-SizingArrangement -MemoryMB 65536

        Invoke-SetupFunction 'size_database' $arrangement.Arrange | Out-Null

        $settings = Get-Settings $arrangement.Config
        $settings['JIM_DB_SHARED_BUFFERS'] | Should -Be '8192MB'
        $settings['JIM_DB_EFFECTIVE_CACHE_SIZE'] | Should -Be '32768MB'
        $settings['JIM_DB_MAINTENANCE_WORK_MEM'] | Should -Be '2048MB'
        $settings['JIM_DB_WORK_MEM'] | Should -Be '95MB'
        $settings['JIM_DB_SHM_SIZE'] | Should -Be '10240mb'
    }

    It 'takes a value set in the environment over its own, for automation' {
        $arrangement = New-SizingArrangement -MemoryMB 6144 -Environment 'JIM_DB_SHARED_BUFFERS=2GB'

        Invoke-SetupFunction 'size_database' $arrangement.Arrange | Out-Null

        $settings = Get-Settings $arrangement.Config
        $settings['JIM_DB_SHARED_BUFFERS'] | Should -Be '2GB'
        $settings['JIM_DB_WORK_MEM'] | Should -Be '7MB'
    }

    It 'says which settings it was given, rather than chose for the host' {
        $arrangement = New-SizingArrangement -MemoryMB 6144 -Environment 'JIM_DB_SHARED_BUFFERS=1GB'

        $result = Invoke-SetupFunction 'size_database' $arrangement.Arrange

        $result.Output | Should -BeLike '*shared_buffers 1GB (given)*'
        $result.Output | Should -BeLike '*effective_cache_size 3072MB,*'
        $result.Output | Should -Not -BeLike '*3072MB (given)*'
    }

    It 'stops before writing anything when the host cannot give the shared_buffers it was given' {
        # The host #1948 was found on: 8GB given, on 5.7 GB.
        $arrangement = New-SizingArrangement -MemoryMB 5836 -Environment 'JIM_DB_SHARED_BUFFERS=8GB'
        $before = Get-Content -Raw $arrangement.Config

        $result = Invoke-SetupFunction 'size_database' $arrangement.Arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*JIM_DB_SHARED_BUFFERS*8GB*5.7 GB*'
        $result.Output | Should -BeLike '*#bundled-postgresql-memory*'
        $result.Output | Should -Not -Match '\[OK\]'
        Get-Content -Raw $arrangement.Config | Should -BeExactly $before
    }

    It 'counts a shared_buffers given without a unit in 8 kB pages, as PostgreSQL does' {
        # 1048576 pages of 8 kB is 8 GB.
        $arrangement = New-SizingArrangement -MemoryMB 6144 -Environment 'JIM_DB_SHARED_BUFFERS=1048576'

        $result = Invoke-SetupFunction 'size_database' $arrangement.Arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*JIM_DB_SHARED_BUFFERS*1048576*'
    }

    It 'warns, naming the size it would choose, when a given shared_buffers is more than half the host''s memory' {
        $arrangement = New-SizingArrangement -MemoryMB 6144 -Environment 'JIM_DB_SHARED_BUFFERS=4GB'

        $result = Invoke-SetupFunction 'size_database' $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        (Get-Settings $arrangement.Config)['JIM_DB_SHARED_BUFFERS'] | Should -Be '4GB'
        $result.Output | Should -Match '\[WARN\][^\n]*JIM_DB_SHARED_BUFFERS[^\n]*4GB[^\n]*1536MB'
    }

    It 'does not warn about a given shared_buffers of half the host''s memory, fractional or not' {
        foreach ($given in '3GB', '1.5GB', '3072MB') {
            $arrangement = New-SizingArrangement -MemoryMB 6144 -Environment "JIM_DB_SHARED_BUFFERS='$given'"

            $result = Invoke-SetupFunction 'size_database' $arrangement.Arrange

            $result.ExitCode | Should -Be 0 -Because $result.Output
            $result.Output | Should -Not -Match '\[WARN\]' -Because "$given is half the host's memory or less"
        }
    }

    It 'warns that it could not check a given shared_buffers it cannot read, such as a unit in the wrong case' {
        # PostgreSQL's units are case-sensitive, and refuse 8gb.
        $arrangement = New-SizingArrangement -MemoryMB 6144 -Environment 'JIM_DB_SHARED_BUFFERS=8gb'

        $result = Invoke-SetupFunction 'size_database' $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Match '\[WARN\][^\n]*JIM_DB_SHARED_BUFFERS[^\n]*8gb[^\n]*case'
    }

    It 'checks nothing an upgrade keeps, a given shared_buffers included, since it writes none of it' {
        $arrangement = New-SizingArrangement -MemoryMB 5836 -Existing 'JIM_DB_SHARED_BUFFERS=1GB' -Environment 'JIM_DB_SHARED_BUFFERS=8GB'

        $result = Invoke-SetupFunction 'size_database keep' $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        (Get-Settings $arrangement.Config)['JIM_DB_SHARED_BUFFERS'] | Should -Be '1GB'
        $result.Output | Should -Not -Match '\[WARN\]'
    }

    It 'keeps the settings an installation already has, filling in only the missing ones, when upgrading' {
        $arrangement = New-SizingArrangement -MemoryMB 6144 -Existing 'JIM_DB_SHARED_BUFFERS=3GB'

        Invoke-SetupFunction 'size_database keep' $arrangement.Arrange | Out-Null

        $settings = Get-Settings $arrangement.Config
        $settings['JIM_DB_SHARED_BUFFERS'] | Should -Be '3GB'
        $settings['JIM_DB_EFFECTIVE_CACHE_SIZE'] | Should -Be '3072MB'
    }

    It 'on Podman too, keeps the settings jim-config.yaml already has, quoted as the installer writes them' {
        $arrangement = New-SizingArrangement -MemoryMB 6144 -Runtime podman -Existing '  JIM_DB_SHARED_BUFFERS: "3GB"'

        Invoke-SetupFunction 'size_database keep' $arrangement.Arrange | Out-Null

        $settings = Get-Settings $arrangement.Config
        $settings['JIM_DB_SHARED_BUFFERS'] | Should -Be '3GB'
        $settings['JIM_DB_WORK_MEM'] | Should -Be '7MB'
    }

    It 'on Podman, writes jim-config.yaml, leaving out the /dev/shm size, which Podman cannot set' {
        $arrangement = New-SizingArrangement -MemoryMB 6144 -Runtime podman

        $result = Invoke-SetupFunction 'size_database' $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $settings = Get-Settings $arrangement.Config
        $settings['JIM_DB_SHARED_BUFFERS'] | Should -Be '1536MB'
        $settings['JIM_DB_WORK_MEM'] | Should -Be '7MB'
        $settings.ContainsKey('JIM_DB_SHM_SIZE') | Should -BeFalse
    }

    It 'leaves the defaults, saying so, when it cannot read the host''s memory' {
        $arrangement = New-SizingArrangement -MemoryMB ''

        $result = Invoke-SetupFunction 'size_database' $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        (Get-Settings $arrangement.Config).ContainsKey('JIM_DB_SHARED_BUFFERS') | Should -BeFalse
        $result.Output | Should -BeLike '*Could not read this host''s memory*'
    }
}

Describe 'setup.sh infrastructure API key' -Skip:$script:NoBash {
    BeforeAll {
        $script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..')).Path
        $script:Key = 'jim_ak_0123456789abcdef0123456789abcdef'

        # A settings file made from the release's template for the runtime, with the given environment.
        function New-KeyArrangement {
            param([string]$Runtime = 'docker', [string]$Environment = '')
            $dir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $dir | Out-Null
            $config = if ($Runtime -eq 'podman') { Join-Path $dir 'jim-config.yaml' } else { Join-Path $dir '.env' }
            $template = if ($Runtime -eq 'podman') { Join-Path $script:RepositoryRoot 'deploy' 'podman' 'jim-config.yaml' } else { Join-Path $script:RepositoryRoot '.env.example' }
            Copy-Item $template $config
            [pscustomobject]@{
                Dir = $dir
                Config = $config
                Arrange = "RUNTIME=$Runtime`nCONFIG_FILE='$config'`n$Environment"
            }
        }
    }

    # Until #1950 only Podman stored the key, so an automated Docker install that set it had no key to use.
    It 'on Docker, writes a given key to .env, which compose passes to JIM' {
        $arrangement = New-KeyArrangement -Environment "JIM_INFRASTRUCTURE_API_KEY=$script:Key"

        $result = Invoke-SetupFunction 'configure_infrastructure_api_key' $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Content $arrangement.Config | Should -Contain "JIM_INFRASTRUCTURE_API_KEY=$script:Key"
    }

    It 'on Podman, stores a given key in the jim-secrets secret, and not in jim-config.yaml' {
        # What the installer gives Podman's secret store, which it reads from standard input.
        $secret = Join-Path $TestDrive "$([Guid]::NewGuid().ToString('N')).yaml"
        $arrangement = New-KeyArrangement -Runtime podman -Environment @"
JIM_INFRASTRUCTURE_API_KEY=$script:Key
JIM_DB_PASSWORD=db-password
JIM_SSO_SECRET=sso-secret
as_jim_account() { cat > '$secret'; }
"@

        $result = Invoke-SetupFunction "configure_infrastructure_api_key`nstore_podman_secrets" $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Content $secret | Should -Contain "  JIM_INFRASTRUCTURE_API_KEY: `"$script:Key`""
        Get-Content $arrangement.Config | Where-Object { $_ -match '^\s*JIM_INFRASTRUCTURE_API_KEY:' } | Should -BeNullOrEmpty
    }

    It 'writes nothing when no key is given' {
        $arrangement = New-KeyArrangement
        $before = Get-Content -Raw $arrangement.Config

        $result = Invoke-SetupFunction 'check_infrastructure_api_key; configure_infrastructure_api_key' $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Content -Raw $arrangement.Config | Should -BeExactly $before
    }

    # JIM ignores a key it would not accept, logging only a warning, so automation would find out at its first call.
    It 'stops, naming the rule, when a given key is <Reason>' -TestCases @(
        @{ Reason = 'missing the jim_ak_ prefix'; Value = 'abc_0123456789abcdef0123456789abcdef' }
        @{ Reason = 'shorter than 32 characters'; Value = 'jim_ak_0123456789' }
    ) {
        $arrangement = New-KeyArrangement -Environment "JIM_INFRASTRUCTURE_API_KEY=$Value"

        $result = Invoke-SetupFunction 'check_infrastructure_api_key' $arrangement.Arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*JIM_INFRASTRUCTURE_API_KEY*jim_ak_*32*'
    }

    It 'accepts a key of exactly 32 characters, as JIM does' {
        $arrangement = New-KeyArrangement -Environment "JIM_INFRASTRUCTURE_API_KEY=jim_ak_$('a' * 25)"

        $result = Invoke-SetupFunction 'check_infrastructure_api_key' $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
    }
}

Describe 'setup.sh configure_database' -Skip:$script:NoBash {
    It 'sizes the bundled PostgreSQL to the host' {
        $env = Join-Path $TestDrive "$([Guid]::NewGuid().ToString('N')).env"
        Copy-Item (Join-Path $PSScriptRoot '..' '..' '..' '..' '.env.example') $env
        $arrange = "RUNTIME=docker`nCONFIG_FILE='$env'`nJIM_SETUP_DB_MODE=bundled`nhost_memory_mb() { echo 6144; }`nbundled_database_exists() { return 1; }"

        $result = Invoke-SetupFunction 'configure_database' $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Content $env | Should -Contain 'JIM_DB_SHARED_BUFFERS=1536MB'
    }

    It 'sizes nothing for your own PostgreSQL server' {
        $env = Join-Path $TestDrive "$([Guid]::NewGuid().ToString('N')).env"
        Copy-Item (Join-Path $PSScriptRoot '..' '..' '..' '..' '.env.example') $env
        $arrange = "RUNTIME=docker`nCONFIG_FILE='$env'`nJIM_SETUP_DB_MODE=external`nJIM_DB_HOSTNAME=db.example.test`nJIM_DB_NAME=jim`nJIM_DB_USERNAME=jim`nJIM_DB_PASSWORD=secret`nhost_memory_mb() { echo 6144; }"

        $result = Invoke-SetupFunction 'configure_database' $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Content $env | Where-Object { $_ -like 'JIM_DB_SHARED_BUFFERS=*' } | Should -BeNullOrEmpty
    }
}

Describe 'setup.sh wait_for_jim, when JIM does not become ready' -Skip:$script:NoBash {
    BeforeAll {
        # An installation whose web service never passes its health check, on a runtime faked from a table of each
        # container's state: "status|restarts|health" ("missing" for a container that does not exist). Logs are
        # one line naming the container, and the database's ends in PostgreSQL's out-of-memory failure (#1943).
        function Get-UnreadyArrangement {
            param([string]$Runtime = 'docker', [hashtable]$States, [string]$BundledDatabase = 'true')
            $cases = ($States.Keys | ForEach-Object { "            $_) state='$($States[$_])' ;;" }) -join "`n"
            $lookup = @"
container_state() {
        case "`$1" in
$cases
            *) state='running|0|healthy' ;;
        esac
    }
"@
            $fake = if ($Runtime -eq 'docker') {
                @"
docker() {
    $lookup
    case "`$1" in
        inspect)
            container_state "`$4"
            [ "`$state" != missing ] || return 1
            echo "`$state" ;;
        logs)
            echo "log of `$4"
            [ "`$4" != jim.database ] || echo 'FATAL:  could not map anonymous shared memory: Cannot allocate memory' ;;
    esac
}
"@
            }
            else {
                @"
as_jim_account() { "`$@"; }
podman() {
    $lookup
    case "`$1" in
        inspect)
            container_state "`$4"
            [ "`$state" != missing ] || return 1
            echo "`${state%|*}" ;;
        healthcheck)
            container_state "`$3"
            [ "`${state##*|}" != unhealthy ] ;;
        logs)
            echo "log of `$4"
            [ "`$4" != jim-database-postgres ] || echo 'FATAL:  could not map anonymous shared memory: Cannot allocate memory' ;;
    esac
}
"@
            }
            @"
RUNTIME=$Runtime
USE_BUNDLED_DB=$BundledDatabase
PODMAN_ACCOUNT=
READY_TIMEOUT_SECONDS=0
jim_is_healthy() { return 1; }
$fake
"@
        }
    }

    It 'names the database when it keeps restarting, with the end of its log, and how to see more' {
        $arrange = Get-UnreadyArrangement -States @{ 'jim.database' = 'restarting|9|'; 'jim.web' = 'running|0|unhealthy' }

        $result = Invoke-SetupFunction 'wait_for_jim /opt/jim; echo "READY=$JIM_READY"' $arrange

        $result.Output | Should -BeLike '*READY=false*'
        $result.Output | Should -BeLike '*jim.database: restarting, restarted 9 times*'
        $result.Output | Should -BeLike '*could not map anonymous shared memory*'
        $result.Output | Should -BeLike '*jim.web: unhealthy*'
        $result.Output | Should -BeLike '*--profile with-db logs jim.database jim.web*'
    }

    It 'names the database first, as the service the others wait for' {
        $arrange = Get-UnreadyArrangement -States @{ 'jim.database' = 'restarting|9|'; 'jim.web' = 'running|0|unhealthy' }

        $output = (Invoke-SetupFunction 'wait_for_jim /opt/jim' $arrange).Output

        $output.IndexOf('jim.database:') | Should -BeLessThan $output.IndexOf('jim.web:')
        $output | Should -BeLike '*fix the database first*'
    }

    It 'leaves out the containers that are running properly' {
        $arrange = Get-UnreadyArrangement -States @{ 'jim.database' = 'restarting|9|' }

        $result = Invoke-SetupFunction 'wait_for_jim /opt/jim' $arrange

        $result.Output | Should -Not -BeLike '*jim.worker:*'
        $result.Output | Should -Not -BeLike '*log of jim.worker*'
    }

    It 'names a container that was never created' {
        $arrange = Get-UnreadyArrangement -States @{ 'jim.worker' = 'missing' }

        $result = Invoke-SetupFunction 'wait_for_jim /opt/jim' $arrange

        $result.Output | Should -BeLike '*jim.worker: not created*'
    }

    It 'names a container that stopped, with its exit code' {
        $arrange = Get-UnreadyArrangement -States @{ 'jim.scheduler' = 'exited|0||139' }

        $result = Invoke-SetupFunction 'wait_for_jim /opt/jim' $arrange

        $result.Output | Should -BeLike '*jim.scheduler: exited (code 139)*'
    }

    It 'says where to look when every container is running but JIM is not ready, the database included' {
        $arrange = Get-UnreadyArrangement -States @{}

        $result = Invoke-SetupFunction 'wait_for_jim /opt/jim' $arrange

        $result.Output | Should -BeLike '*JIM is not ready after 10 minutes*'
        $result.Output | Should -BeLike '*--profile with-db logs jim.database jim.worker jim.scheduler jim.web*'
    }

    It 'checks no database container when JIM uses your own PostgreSQL server' {
        $arrange = Get-UnreadyArrangement -States @{ 'jim.database' = 'missing' } -BundledDatabase 'false'

        $result = Invoke-SetupFunction 'wait_for_jim /opt/jim' $arrange

        $result.Output | Should -Not -BeLike '*jim.database*'
        $result.Output | Should -Not -BeLike '*--profile with-db*'
    }

    It 'on Podman, names the database pod''s container, with the end of its log' {
        $arrange = Get-UnreadyArrangement -Runtime podman -States @{ 'jim-database-postgres' = 'running|12|'; 'jim-web' = 'running|0|unhealthy' }

        $result = Invoke-SetupFunction 'wait_for_jim /opt/jim' $arrange

        $result.Output | Should -BeLike '*jim-database-postgres: restarted 12 times*'
        $result.Output | Should -BeLike '*could not map anonymous shared memory*'
        $result.Output | Should -BeLike '*jim-web: unhealthy*'
        $result.Output | Should -BeLike '*podman logs jim-database-postgres*'
    }
}

Describe 'setup.sh launch_jim, on Docker' -Skip:$script:NoBash {
    BeforeAll {
        # A Docker host whose jim.web, if any, last started at the given time. Compose runs in a subshell, so the
        # start time is kept in a file, which `up` rewrites when it starts jim.web (-UpStartsWeb) and leaves
        # alone when it finds nothing changed. The fake docker prints each compose command it runs.
        function Get-LaunchArrangement {
            param([string]$WebStartedAt, [switch]$UpStartsWeb)
            $state = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            Set-Content -NoNewline -Path $state -Value $WebStartedAt
            $onUp = if ($UpStartsWeb) { "echo 2026-10-06T13:00:00Z > '$state'" } else { ':' }
            @"
RUNTIME=docker
USE_BUNDLED_DB=true
JIM_SETUP_AUTO_START=true
wait_for_jim() { echo "waited for JIM"; }
docker() {
    case "`$1" in
        inspect) [ -s '$state' ] || return 1; cat '$state' ;;
        ps) [ -s '$state' ] && echo 0123456789ab ;;
        compose)
            echo "ran: docker `$*"
            case " `$* " in *" up "*) $onUp ;; esac ;;
    esac
}
"@
        }
    }

    It 'restarts jim.web when a re-run leaves it running, so that it serves the certificate just installed' {
        $arrange = Get-LaunchArrangement -WebStartedAt '2026-10-06T12:36:37Z'

        $result = Invoke-SetupFunction "launch_jim '$TestDrive'" $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*ran: docker compose -f docker-compose.yml -f docker-compose.production.yml restart jim.web*'
        $result.Output.IndexOf('restart jim.web') | Should -BeLessThan $result.Output.IndexOf('waited for JIM')
    }

    It 'does not restart jim.web when starting JIM started it, as a first install does' {
        $arrange = Get-LaunchArrangement -WebStartedAt '' -UpStartsWeb

        $result = Invoke-SetupFunction "launch_jim '$TestDrive'" $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*ran: docker compose * up -d*'
        $result.Output | Should -Not -BeLike '*restart jim.web*'
    }

    It 'does not restart jim.web again when starting JIM recreated it, as a changed configuration does' {
        $arrange = Get-LaunchArrangement -WebStartedAt '2026-10-06T12:36:37Z' -UpStartsWeb

        $result = Invoke-SetupFunction "launch_jim '$TestDrive'" $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Not -BeLike '*restart jim.web*'
    }
}

Describe 'setup.sh launch_jim, when JIM is not to start now' -Skip:$script:NoBash {
    BeforeAll {
        # "Start JIM now?" answered no, on the given runtime, with JIM running or not. The fakes run nothing: the
        # installer only prints the commands that start JIM later.
        function Get-DeclinedArrangement {
            param([ValidateSet('docker', 'podman')][string]$Runtime, [switch]$Systemd, [switch]$Running)
            $status = if ($Running) { 0 } else { 1 }
            @"
RUNTIME=$Runtime
PODMAN_SYSTEMD=$(if ($Systemd) { 'true' } else { 'false' })
PODMAN_ACCOUNT=
USE_BUNDLED_DB=true
JIM_WEB_PORT=443
prompt_yn() { return 1; }
docker() { [ "`$1" = ps ] && [ $status -eq 0 ] && echo 0123456789ab; return 0; }
jim_systemctl() { [ "`$1" = is-active ] && return $status; return 1; }
as_jim_account() { [ "`$*" = 'podman pod exists jim' ] && return $status; return 1; }
"@
        }
    }

    It 'on Docker, gives the command that starts JIM when it is not running' {
        $result = Invoke-SetupFunction "launch_jim '$TestDrive'" (Get-DeclinedArrangement -Runtime docker)

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db up -d*'
        $result.Output | Should -Not -BeLike '*--force-recreate*'
    }

    It 'on Docker, gives a command that recreates JIM when it is running, so that jim.web loads the certificate just installed' {
        $result = Invoke-SetupFunction "launch_jim '$TestDrive'" (Get-DeclinedArrangement -Runtime docker -Running)

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db up -d --force-recreate*'
    }

    It 'on Podman with systemd, gives the commands that start JIM when it is not running' {
        $result = Invoke-SetupFunction "launch_jim '$TestDrive'" (Get-DeclinedArrangement -Runtime podman -Systemd)

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*systemctl start jim-database.service*'
        $result.Output | Should -BeLike '*systemctl start jim.service*'
    }

    It 'on Podman with systemd, gives the commands that restart JIM when it is running, as starting a running service does nothing' {
        $result = Invoke-SetupFunction "launch_jim '$TestDrive'" (Get-DeclinedArrangement -Runtime podman -Systemd -Running)

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*systemctl restart jim-database.service*'
        $result.Output | Should -BeLike '*systemctl restart jim.service*'
        $result.Output | Should -Not -BeLike '*systemctl start *'
        $result.Output | Should -Not -BeLike '*next boot*'
    }

    It 'on Podman without systemd, gives the commands that replace JIM''s pods, running or not' {
        $result = Invoke-SetupFunction "launch_jim '$TestDrive'" (Get-DeclinedArrangement -Runtime podman -Running)

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*podman kube play --replace*jim-database.yaml*'
        $result.Output | Should -BeLike '*podman kube play --replace*jim.yaml*'
    }
}

Describe 'setup.sh install_podman_units' -Skip:$script:NoBash {
    It 'reloads systemd once the units are installed, so that Quadlet generates jim.service before anything starts it' {
        $source = Join-Path $TestDrive 'podman'
        $units = Join-Path $TestDrive 'units'
        New-Item -ItemType Directory -Path $source | Out-Null
        foreach ($unit in 'jim.network', 'jim.kube', 'jim-database.kube') {
            '[Unit]' | Set-Content (Join-Path $source $unit)
        }
        $arrange = @"
PODMAN_SOURCE='$source'
PODMAN_ACCOUNT=
USE_BUNDLED_DB=true
JIM_WEB_PORT=443
podman_unit_dir() { printf '%s' '$units'; }
jim_systemctl() { [ -f '$units/jim.kube' ] && echo "systemctl `$* with jim.kube installed"; }
"@

        $result = Invoke-SetupFunction "install_podman_units '$TestDrive'" $arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -BeLike '*systemctl daemon-reload with jim.kube installed*'
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

Describe 'setup.sh upgrade_installation, sizing the bundled PostgreSQL' -Skip:$script:NoBash {
    BeforeAll {
        # A Docker installation of 1.0.0 whose .env sets no database sizes, as every one before #1943, and a 1.1.0
        # bundle beside it. Everything that would touch Docker or the network is replaced; the upgrade runs through.
        function New-SizingUpgradeArrangement {
            param([string]$DatabaseHost)
            $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path (Join-Path $root 'jim'), (Join-Path $root 'bundle/compose') -Force | Out-Null
            foreach ($name in 'docker-compose.yml', 'docker-compose.production.yml') {
                'services: {}' | Set-Content -NoNewline (Join-Path $root "jim/$name")
                'services: {}' | Set-Content -NoNewline (Join-Path $root "bundle/compose/$name")
            }
            "JIM_VERSION=1.0.0`nJIM_DB_HOSTNAME=$DatabaseHost`n" | Set-Content -NoNewline (Join-Path $root 'jim/.env')
            Copy-Item (Join-Path $PSScriptRoot '..' '..' '..' '..' '.env.example') (Join-Path $root 'bundle/compose/.env.example')
            "1.1.0`n" | Set-Content -NoNewline (Join-Path $root 'bundle/VERSION')
            [pscustomobject]@{
                Env = Join-Path $root 'jim/.env'
                Arrange = @"
JIM_INSTALL_DIR='$root/jim'
BUNDLE_DIR='$root/bundle'
JIM_SETUP_BACKUP_CONFIRMED=true
host_memory_mb() { echo 6144; }
check_prerequisites() { :; }
check_compose_edits() { :; }
load_bundle_images() { :; }
check_database_image() { DATABASE_IMAGE_ID=''; }
save_installer_copy() { :; }
docker() { return 0; }
wait_for_jim() { JIM_READY=true; }
"@
            }
        }
    }

    It 'sizes the bundled PostgreSQL to the host when .env does not' {
        # Before #1943 the compose file sized it for a 64 GB host; its defaults now suit a 4 GB one, so an upgrade
        # sizes it rather than leave a large host on the small defaults.
        $arrangement = New-SizingUpgradeArrangement -DatabaseHost 'jim.database'

        $result = Invoke-SetupFunction 'upgrade_installation' $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Content $arrangement.Env | Should -Contain 'JIM_DB_SHARED_BUFFERS=1536MB'
        Get-Content $arrangement.Env | Should -Contain 'JIM_DB_SHM_SIZE=1920mb'
    }

    It 'sizes nothing for your own PostgreSQL server' {
        $arrangement = New-SizingUpgradeArrangement -DatabaseHost 'db.example.test'

        $result = Invoke-SetupFunction 'upgrade_installation' $arrangement.Arrange

        $result.ExitCode | Should -Be 0 -Because $result.Output
        Get-Content $arrangement.Env | Where-Object { $_ -like 'JIM_DB_SHARED_BUFFERS=*' } | Should -BeNullOrEmpty
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

Describe 'setup.sh main, stopping before it starts JIM' -Skip:$script:NoBash {
    BeforeAll {
        $script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..')).Path

        # An installation of 1.0.0 as an earlier run left it, with a certificate the installer created, and a 1.1.0
        # bundle beside it. The installer is told to replace its configuration, and given the settings to answer every
        # question with, a certificate for new names included; everything that would touch the container runtime, the
        # network or the host's ports is replaced, and starting JIM only says so. -Fresh leaves the installation out,
        # for a first install.
        function New-ReinstallArrangement {
            param([string]$Environment = '', [ValidateSet('docker', 'podman')][string]$Runtime = 'docker', [switch]$Fresh)
            $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            $installation = Join-Path $root 'jim'
            $bundle = Join-Path $root 'bundle'
            New-Item -ItemType Directory -Path (Join-Path $bundle 'compose'), (Join-Path $bundle 'docker-images'), (Join-Path $bundle 'podman') -Force | Out-Null
            foreach ($name in 'docker-compose.yml', 'docker-compose.production.yml') {
                "# v1.1.0's $name" | Set-Content -NoNewline (Join-Path $bundle "compose/$name")
            }
            Copy-Item (Join-Path $script:RepositoryRoot '.env.example') (Join-Path $bundle 'compose/.env.example')
            foreach ($name in 'jim.yaml', 'jim-database.yaml', 'jim-config.yaml') {
                Copy-Item (Join-Path $script:RepositoryRoot 'deploy' 'podman' $name) (Join-Path $bundle "podman/$name")
            }
            "1.1.0`n" | Set-Content -NoNewline (Join-Path $bundle 'VERSION')

            if (-not $Fresh) {
                New-Item -ItemType Directory -Path (Join-Path $installation 'tls') -Force | Out-Null
                if ($Runtime -eq 'podman') {
                    foreach ($name in 'jim.yaml', 'jim-database.yaml') {
                        "# v1.0.0's $name" | Set-Content -NoNewline (Join-Path $installation $name)
                    }
                    "data:`n  JIM_SSO_AUTHORITY: `"https://idp.example.test/realms/jim`"`n" |
                        Set-Content -NoNewline (Join-Path $installation 'jim-config.yaml')
                    "JIM_RUNTIME=podman`nJIM_PODMAN_ACCOUNT=`nJIM_PODMAN_SYSTEMD=true`nJIM_WEB_PORT=443`n" |
                        Set-Content -NoNewline (Join-Path $installation 'install.conf')
                }
                else {
                    foreach ($name in 'docker-compose.yml', 'docker-compose.production.yml') {
                        "# v1.0.0's $name" | Set-Content -NoNewline (Join-Path $installation $name)
                    }
                    (@(
                        'DOCKER_REGISTRY=ghcr.io/tetronio/'
                        'JIM_VERSION=1.0.0'
                        'JIM_DB_HOSTNAME=jim.database'
                        'JIM_DB_PASSWORD=the-database-password'
                        'JIM_SSO_AUTHORITY=https://idp.example.test/realms/jim'
                        'JIM_WEB_PORT=443'
                        'JIM_DB_SHARED_BUFFERS=1459MB'
                    ) -join "`n") + "`n" | Set-Content -NoNewline (Join-Path $installation '.env')
                    "# the compose files' checksums`n" | Set-Content -NoNewline (Join-Path $installation 'compose-files.sha256')
                }
                "# v1.0.0's installer`n" | Set-Content -NoNewline (Join-Path $installation 'setup.sh')
                "jim.example.test`n" | Set-Content -NoNewline (Join-Path $installation 'tls/names')
                foreach ($name in 'ca.crt', 'ca.key', 'tls.crt', 'tls.key') {
                    "v1.0.0's $name" | Set-Content -NoNewline (Join-Path $installation "tls/$name")
                }
            }

            $podman = ''
            if ($Runtime -eq 'podman') {
                # Run as root, as by default; Podman holds no secret yet, and has every image.
                $podman = @'
PODMAN_SYSTEMD=true
choose_podman_account() { PODMAN_ACCOUNT=''; }
prepare_podman_account() { :; }
check_account_environment() { :; }
as_jim_account() { return 1; }
verify_podman_images() { :; }
store_podman_secrets() { echo "stored the secrets in Podman"; }
store_tls_secret() { echo "stored the certificate in Podman"; }
install_podman_units() { echo "installed the systemd units"; }
'@
            }

            [pscustomobject]@{
                Installation = $installation
                Arrange = @"
JIM_INSTALL_DIR='$installation'
BUNDLE_DIR='$bundle'
choose_runtime() { RUNTIME=$Runtime; }
prompt_yn() { return 0; }
check_prerequisites() { :; }
host_memory_mb() { echo 5836; }
load_bundle_images() { :; }
check_database_image() { DATABASE_IMAGE_ID=''; }
verify_bundle_images() { :; }
port_problem() { :; }
set_tls_key_owner() { :; }
launch_jim() { echo "started JIM"; }
show_summary() { :; }
$podman
JIM_SETUP_DB_MODE=bundled
JIM_SSO_AUTHORITY=https://idp.example.org/realms/jim
JIM_SSO_CLIENT_ID=jim
JIM_SSO_SECRET=the-client-secret
JIM_SSO_API_SCOPE=api://jim/access_as_user
JIM_SSO_CLAIM_TYPE=sub
JIM_SSO_MV_ATTRIBUTE='Subject Identifier'
JIM_SSO_INITIAL_ADMIN=admin
JIM_WEB_PORT=443
JIM_SETUP_TLS_MODE=generate
JIM_SETUP_TLS_NAMES=jim.example.org
JIM_TRUSTED_PROXIES=
$Environment
"@
            }
        }

        # Every file and folder under a folder, hidden ones included, with each file's content.
        function Get-FolderState {
            param([string]$Path)
            if (-not (Test-Path $Path)) {
                return '(no folder)'
            }
            $entries = Get-ChildItem -Path $Path -Recurse -Force | Sort-Object FullName | ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($Path, $_.FullName)
                if ($_.PSIsContainer) { "$relative/" } else { "${relative}: $(Get-Content -Raw $_.FullName)" }
            }
            $entries -join "`n"
        }
    }

    It 'puts the installation back as it was when it refuses a setting, so that JIM starts on it as before (#1994)' {
        # As #1994 was found: 8GB of shared_buffers given, on a host with 5.7 GB.
        $arrangement = New-ReinstallArrangement -Environment 'JIM_DB_SHARED_BUFFERS=8GB'
        $before = Get-FolderState $arrangement.Installation

        $result = Invoke-SetupFunction 'main' $arrangement.Arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*JIM_DB_SHARED_BUFFERS is 8GB*'
        Get-FolderState $arrangement.Installation | Should -BeExactly $before
    }

    It 'puts back the certificate and its authority too when Ctrl+C stops it after it created new ones' {
        # New names need a new certificate authority, which the installer creates before it asks about a proxy.
        $arrangement = New-ReinstallArrangement -Environment 'configure_proxy() { kill -INT $$; }'
        $before = Get-FolderState $arrangement.Installation

        $result = Invoke-SetupFunction 'main' $arrangement.Arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*Created a certificate authority*'
        $result.Output | Should -Not -BeLike '*started JIM*'
        Get-FolderState $arrangement.Installation | Should -BeExactly $before
    }

    It 'keeps the new configuration once it goes on to start JIM, whether or not JIM then starts' {
        $arrangement = New-ReinstallArrangement -Environment 'launch_jim() { echo "started JIM"; exit 1; }'

        $result = Invoke-SetupFunction 'main' $arrangement.Arrange

        $result.Output | Should -BeLike '*started JIM*'
        $settings = Get-Content (Join-Path $arrangement.Installation '.env')
        $settings | Should -Contain 'JIM_VERSION=1.1.0'
        $settings | Should -Contain 'JIM_SSO_AUTHORITY=https://idp.example.org/realms/jim'
        $settings | Should -Contain 'JIM_DB_PASSWORD=the-database-password'
        Get-Content -Raw (Join-Path $arrangement.Installation 'docker-compose.yml') | Should -BeExactly "# v1.1.0's docker-compose.yml"
        Get-Content (Join-Path $arrangement.Installation 'tls/names') | Should -Be 'jim.example.org'
        Get-ChildItem -Force -Name $arrangement.Installation | Sort-Object |
            Should -Be (@('.env', 'compose-files.sha256', 'docker-compose.production.yml', 'docker-compose.yml', 'setup.sh', 'tls') | Sort-Object)
    }

    It 'leaves nothing behind when a first installation stops, so that running it again does not keep the template''s database password' {
        # The topology is refused before the run writes a database password, leaving the template's placeholder.
        $arrangement = New-ReinstallArrangement -Fresh -Environment 'JIM_SETUP_DB_MODE=sideways'

        $result = Invoke-SetupFunction 'main' $arrangement.Arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*Invalid JIM_SETUP_DB_MODE*'
        $arrangement.Installation | Should -Not -Exist
    }

    It 'stops, changing nothing, when an earlier run that stopped left the installation''s files set aside' {
        $arrangement = New-ReinstallArrangement
        New-Item -ItemType Directory -Path (Join-Path $arrangement.Installation '.before-setup') | Out-Null
        "JIM_VERSION=0.9.0`n" | Set-Content -NoNewline (Join-Path $arrangement.Installation '.before-setup/.env')
        $before = Get-FolderState $arrangement.Installation

        $result = Invoke-SetupFunction 'main' $arrangement.Arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike "*$(Join-Path $arrangement.Installation '.before-setup')*"
        Get-FolderState $arrangement.Installation | Should -BeExactly $before
    }

    It 'on Podman, puts the installation back when it stops for want of AppArmor''s rules, having stored nothing in Podman' {
        $arrangement = New-ReinstallArrangement -Runtime podman -Environment 'configure_apparmor() { fatal "JIM needs a network rule"; }'
        $before = Get-FolderState $arrangement.Installation

        $result = Invoke-SetupFunction 'main' $arrangement.Arrange

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -BeLike '*JIM needs a network rule*'
        $result.Output | Should -Not -BeLike '*stored the secrets in Podman*'
        $result.Output | Should -Not -BeLike '*installed the systemd units*'
        Get-FolderState $arrangement.Installation | Should -BeExactly $before
    }
}
