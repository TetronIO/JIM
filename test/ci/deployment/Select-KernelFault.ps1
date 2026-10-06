# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Returns the kernel log lines that say a container was harmed: an AppArmor denial under a container's profile or a
    container runtime's, or a process the kernel killed for a fault.

.DESCRIPTION
    A service that crashes is restarted by its runtime, and soon passes its health check again, so a check that waits
    only for JIM to be ready cannot see a crash. On Ubuntu 24.04, rootful Podman's AppArmor profiles denied the .NET
    runtime's signals to its own threads, so JIM's services aborted on every start, and the deployment-boot check
    stayed green throughout (#1953). The check now passes each leg's kernel log through this, and fails on anything
    it returns.

    A denial counts when its profile or its peer is a container's (containers-default, docker-default) or a container
    runtime's (crun, podman, runc), stacked or not; denials under other programs' profiles are the host's own. A
    fault counts whatever the process, since nothing else should crash on a host while JIM is installed on it.

.PARAMETER Line
    Kernel log lines, as journalctl or dmesg prints them.

.EXAMPLE
    sudo journalctl -k --no-pager | ./test/ci/deployment/Select-KernelFault.ps1
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromPipeline)]
    [AllowEmptyString()]
    [string[]]$Line
)

process {
    foreach ($text in $Line) {
        $denied = $text -match 'apparmor="DENIED"' -and
            $text -match '\b(?:profile|peer)="(?:containers-default|docker-default|crun|podman|runc)\b'
        if ($denied -or $text -match '\btraps: |: segfault at ') {
            $text
        }
    }
}
