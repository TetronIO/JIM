# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for test/ci/deployment/Select-KernelFault.ps1.

.DESCRIPTION
    Passes kernel log lines, as an Ubuntu 24.04 host logs them, through the script and checks it returns those the
    deployment-boot check must fail on, and none of a host's ordinary noise.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Select-KernelFault.ps1')).Path

    function Select-Fault {
        param([string[]]$Line)
        @($Line | & $script:ScriptPath)
    }
}

Describe 'Select-KernelFault' {
    It 'returns the denial of a .NET runtime''s signal to its own threads under rootful Podman (#1953)' {
        $line = 'Oct 06 11:02:13 lab kernel: audit: type=1400 audit(1759748533.120:412): apparmor="DENIED" operation="signal" class="signal" profile="containers-default-0.57.4-apparmor1" pid=2130 comm=".NET BGC" requested_mask="send" denied_mask="send" signal=rtmin+2 peer="containers-default-0.57.4-apparmor1//&crun"'

        Select-Fault $line | Should -Be @($line)
    }

    It 'returns a denial under a runtime''s own profile, such as crun''s blocking the network' {
        $line = 'audit: type=1400 audit(1759748533.120:413): apparmor="DENIED" operation="create" class="net" info="failed af match" profile="crun" pid=2101 comm="dotnet" family="inet" sock_type="stream" protocol=6 requested="create" denied="create"'

        Select-Fault $line | Should -Be @($line)
    }

    It 'returns a denial under Docker''s profile for containers' {
        $line = 'audit: type=1400 audit(1759748533.120:414): apparmor="DENIED" operation="signal" class="signal" profile="docker-default" pid=2200 comm="dotnet" requested_mask="send" denied_mask="send" signal=usr1 peer="docker-default"'

        Select-Fault $line | Should -Be @($line)
    }

    It 'returns a process the kernel killed for a fault, whether it reports a trap or a segfault' {
        $trap = 'traps: .NET BGC[2130] general protection fault ip:771fa44429a2 sp:76de60cd6970 error:0 in libc.so.6[771fa4442000+189000]'
        $segfault = 'dotnet[4321]: segfault at 0 ip 00007f3c1a2b3c4d sp 00007ffd1e2f3a40 error 4 in libcoreclr.so[7f3c1a000000+500000]'

        Select-Fault $trap, $segfault | Should -Be @($trap, $segfault)
    }

    It 'returns nothing for a denial under another program''s profile' {
        Select-Fault 'audit: type=1400 audit(1759748533.120:415): apparmor="DENIED" operation="open" class="file" profile="snap.lxd.daemon" name="/proc/1/environ" pid=900 comm="lxd" requested_mask="r" denied_mask="r"' |
            Should -BeNullOrEmpty
    }

    It 'returns nothing for Podman loading its profile, or for a rule in complain mode' {
        Select-Fault @(
            'audit: type=1400 audit(1759748533.120:416): apparmor="STATUS" operation="profile_load" profile="unconfined" name="containers-default-0.57.4-apparmor1" pid=2000 comm="apparmor_parser"'
            'audit: type=1400 audit(1759748533.120:417): apparmor="ALLOWED" operation="signal" class="signal" profile="crun" pid=2001 comm="crun" requested_mask="send" denied_mask="send" signal=term peer="containers-default-0.57.4-apparmor1//&crun"'
        ) | Should -BeNullOrEmpty
    }

    It 'returns nothing for a container runtime''s ordinary messages' {
        Select-Fault @(
            'podman0: port 1(veth0) entered blocking state'
            'IPv6: ADDRCONF(NETDEV_CHANGE): veth0: link becomes ready'
            ''
        ) | Should -BeNullOrEmpty
    }
}
