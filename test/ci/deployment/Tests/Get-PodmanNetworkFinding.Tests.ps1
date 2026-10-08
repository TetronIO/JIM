# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for test/ci/deployment/Get-PodmanNetworkFinding.ps1.

.DESCRIPTION
    Gives the script snapshots of a rootful Podman JIM's network, as Invoke-PodmanRestartLoop.ps1 captures them,
    each with one fault, and checks it names that fault, and nothing at all for a healthy network (#2009).
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Get-PodmanNetworkFinding.ps1')).Path

    # A healthy network: the name resolves to the database pod's address, both pods are on the bridge, firewalld
    # trusts the network, and the database answers. Each test breaks one thing.
    function New-Snapshot {
        param([hashtable]$Change = @{})
        $snapshot = [ordered]@{
            DatabaseAddress = '10.89.1.14'
            PreviousDatabaseAddresses = @('10.89.1.6')
            ResolvedAddresses = @('10.89.1.14')
            AardvarkAddresses = @('10.89.1.14')
            TcpByName = [pscustomobject]@{ ExitCode = 0; Message = ''; Milliseconds = 3 }
            TcpByAddress = [pscustomobject]@{ ExitCode = 0; Message = ''; Milliseconds = 2 }
            DatabaseNeighbour = $null
            DatabasePodOnBridge = $true
            JimPodOnBridge = $true
            DatabaseVeth = 'veth0'
            JimVeth = 'veth1'
            NetworkManagerDevices = @()
            NetworkManagerReleases = @()
            FirewalldRunning = $true
            Subnet = '10.89.1.0/24'
            TrustedSources = @('10.89.1.0/24')
            BridgeTrafficFiltered = $false
            ReloadHelperActive = $true
            NetworkErrors = @()
        }
        foreach ($key in $Change.Keys) { $snapshot[$key] = $Change[$key] }
        [pscustomobject]$snapshot
    }

    function Get-Finding {
        param([pscustomobject]$Snapshot)
        Write-Output -NoEnumerate @(& $script:ScriptPath -Snapshot $Snapshot)
    }
}

Describe 'Get-PodmanNetworkFinding' {
    It 'finds nothing on a healthy network' {
        Get-Finding (New-Snapshot) | Should -BeNullOrEmpty
    }

    It 'names a stale name, saying when it is an earlier round''s address of the database' {
        $snapshot = New-Snapshot @{ ResolvedAddresses = @('10.89.1.6'); AardvarkAddresses = @('10.89.1.6') }

        $findings = Get-Finding $snapshot

        $findings | Should -HaveCount 1
        $findings[0] | Should -BeLike '*resolves jim-database to 10.89.1.6*not the database pod''s address (10.89.1.14)*earlier*'
    }

    It 'names a stale record aardvark-dns serves beside the current one' {
        $snapshot = New-Snapshot @{ ResolvedAddresses = @('10.89.1.6', '10.89.1.14'); AardvarkAddresses = @('10.89.1.6', '10.89.1.14') }

        $findings = Get-Finding $snapshot

        $findings | Should -HaveCount 1
        $findings[0] | Should -BeLike '*10.89.1.6*'
    }

    It 'names a name that does not resolve at all' {
        $snapshot = New-Snapshot @{
            ResolvedAddresses = @()
            TcpByName = [pscustomobject]@{ ExitCode = 1; Message = 'bash: jim-database: Temporary failure in name resolution'; Milliseconds = 4012 }
        }

        $findings = Get-Finding $snapshot

        $findings | Should -HaveCount 1
        $findings[0] | Should -BeLike '*cannot resolve jim-database*'
    }

    It 'names a pod whose interface is not on the network''s bridge' {
        $findings = Get-Finding (New-Snapshot @{ DatabasePodOnBridge = $false })

        $findings | Should -HaveCount 1
        $findings[0] | Should -BeLike '*database pod*not attached to the bridge*'
    }

    It 'names NetworkManager, and the fix, when it holds a connection on the veth of a pod that is off the bridge (#2009)' {
        $snapshot = New-Snapshot @{
            DatabasePodOnBridge = $false
            NetworkManagerDevices = @(
                [pscustomobject]@{ Device = 'veth1'; State = 'unmanaged'; Connection = '' }
                [pscustomobject]@{ Device = 'veth0'; State = 'connecting (getting IP configuration)'; Connection = 'Wired connection 1' })
        }

        $findings = Get-Finding $snapshot

        $findings | Should -HaveCount 1
        $findings[0] | Should -BeLike '*NetworkManager*veth0*off the bridge*Wired connection 1*unmanaged-devices*'
    }

    It 'names NetworkManager from its journal when it released the veth from the bridge' {
        $snapshot = New-Snapshot @{
            DatabasePodOnBridge = $false
            NetworkManagerReleases = @('<info>  [1791442581.2] device (veth0): released from controller device podman2')
        }

        $findings = Get-Finding $snapshot

        $findings | Should -HaveCount 1
        $findings[0] | Should -BeLike '*NetworkManager*veth0*off the bridge*'
    }

    It 'names firewalld no longer trusting the network, and the missing reload helper' {
        $snapshot = New-Snapshot @{ TrustedSources = @(); BridgeTrafficFiltered = $true; ReloadHelperActive = $false }

        $findings = Get-Finding $snapshot

        $findings | Should -HaveCount 2
        $findings[0] | Should -BeLike '*10.89.1.0/24*not in firewalld''s trusted zone*'
        $findings[1] | Should -BeLike '*netavark-firewalld-reload.service*'
    }

    It 'tells no route to an address that answers on the network, which something rejects, from no route to one nothing answers for' {
        $unreachable = [pscustomobject]@{ ExitCode = 1; Message = 'bash: connect: No route to host'; Milliseconds = 3 }
        # A rejected connection can still take a second or more: the kernel limits how often it sends the reject, so
        # the first attempt can go unanswered and be retried. The neighbour entry tells the two apart, not the time.
        $rejectedLate = [pscustomobject]@{ ExitCode = 1; Message = 'bash: connect: No route to host'; Milliseconds = 1021 }
        $unanswered = [pscustomobject]@{ ExitCode = 1; Message = 'bash: connect: No route to host'; Milliseconds = 3064 }

        $rejected = Get-Finding (New-Snapshot @{ TcpByAddress = $unreachable; DatabaseNeighbour = 'REACHABLE' })
        $retried = Get-Finding (New-Snapshot @{ TcpByAddress = $rejectedLate; DatabaseNeighbour = 'STALE' })
        $missing = Get-Finding (New-Snapshot @{ TcpByAddress = $unanswered; DatabaseNeighbour = 'FAILED' })

        $rejected | Should -HaveCount 1
        $rejected[0] | Should -BeLike '*rejected*answers on the network*'
        $retried[0] | Should -BeLike '*rejected*1021 ms*'
        $missing | Should -HaveCount 1
        $missing[0] | Should -BeLike '*3064 ms*nothing on the network answered*'
    }

    It 'draws no conclusion from the time a connection took where the neighbour entry is unknown' {
        # Once the neighbour entry has failed, the kernel can refuse the next connections at once, so a quick "no route
        # to host" is no sign of a firewall (#2009, where it was a pod taken off its bridge).
        $instant = New-Snapshot @{ TcpByAddress = [pscustomobject]@{ ExitCode = 1; Message = 'bash: connect: No route to host'; Milliseconds = 3 } }
        $slow = New-Snapshot @{ TcpByAddress = [pscustomobject]@{ ExitCode = 1; Message = 'bash: connect: No route to host'; Milliseconds = 3064 } }

        foreach ($snapshot in $instant, $slow) {
            $finding = (Get-Finding $snapshot)[0]
            $finding | Should -BeLike '*no route to host*'
            $finding | Should -Not -BeLike '*reject*'
            $finding | Should -Not -BeLike '*answered*'
        }
    }

    It 'names PostgreSQL not listening, and a connection dropped without an answer' {
        $refused = New-Snapshot @{ TcpByAddress = [pscustomobject]@{ ExitCode = 1; Message = 'bash: connect: Connection refused'; Milliseconds = 1 } }
        $dropped = New-Snapshot @{ TcpByAddress = [pscustomobject]@{ ExitCode = 124; Message = ''; Milliseconds = 10004 } }

        (Get-Finding $refused)[0] | Should -BeLike '*nothing listens on port 5432*'
        (Get-Finding $dropped)[0] | Should -BeLike '*no answer within 10004 ms*'
    }

    It 'names errors Podman''s network tools logged' {
        $snapshot = New-Snapshot @{ NetworkErrors = @('netavark: Error adding subnet 10.89.1.0/24 to firewalld trusted zone: timed out', 'second') }

        $findings = Get-Finding $snapshot

        $findings | Should -HaveCount 1
        $findings[0] | Should -BeLike '*2 errors*Error adding subnet*'
    }
}
