# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Says what is wrong with a rootful Podman JIM's network, from a snapshot Invoke-PodmanRestartLoop.ps1 captured.

.DESCRIPTION
    Each finding is one sentence naming a fault that would stop JIM's services reaching their database by name,
    and returns nothing for a healthy network. The faults are those #2009's investigation could not rule out, after
    restarts on AlmaLinux 9 with rootful Podman left the services logging "jim-database:5432: No route to host":

      - The name resolves to an address that is not the database pod's, or not only to it (a stale record).
      - The name does not resolve.
      - A pod's interface is not attached to the network's bridge.
      - firewalld no longer trusts the network, as after a reload with nothing to restore Podman's rules.
      - The database's own address has no route: something rejects the connection, though the address answers on
        the network; or nothing on the network answers for the address at all. Or nothing listens on the port.
      - Podman's network tools logged errors.

    "No route to host" means either of two things, and the time it took cannot tell them apart: a reject usually
    comes at once, but the kernel limits how often it sends one, so a reject can come only on the retry a second or
    more later, near the three seconds an unanswered address takes. The jim pod's neighbour entry for the address
    does tell them apart: resolved where the address answers, FAILED or INCOMPLETE where nothing does.

.PARAMETER Snapshot
    The network as Invoke-PodmanRestartLoop.ps1 captures it. TcpByName and TcpByAddress are bash's attempt to open
    port 5432 from the jim pod: its exit code (124 when it timed out), its message, and how long it took.
    DatabaseNeighbour is the state of the jim pod's neighbour entry for the database's address, if it has one.

.EXAMPLE
    Get-Content ./round-04/snapshot.json | ConvertFrom-Json | ForEach-Object { ./test/ci/deployment/Get-PodmanNetworkFinding.ps1 -Snapshot $_ }
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [pscustomobject]$Snapshot
)

$database = $Snapshot.DatabaseAddress
$resolved = @($Snapshot.ResolvedAddresses | Where-Object { $_ })
$stale = @(@($resolved) + @($Snapshot.AardvarkAddresses) | Where-Object { $_ -and $_ -ne $database } | Select-Object -Unique)

if (-not $database) {
    'The database pod has no address on the jim network: it is not running, or not attached to the network.'
}
if ($resolved.Count -eq 0) {
    'The jim pod cannot resolve jim-database: aardvark-dns, at the network''s gateway, did not answer or holds no record for it.'
}
elseif ($database -and $stale.Count -gt 0) {
    $earlier = @($stale | Where-Object { $_ -in @($Snapshot.PreviousDatabaseAddresses) })
    $sentence = "The jim pod resolves jim-database to $($resolved -join ', '), and aardvark-dns holds $(@($Snapshot.AardvarkAddresses) -join ', ')"
    if ($resolved -notcontains $database) {
        $sentence = "The jim pod resolves jim-database to $($resolved -join ', '), which is not the database pod's address ($database)"
    }
    $sentence += ": $($stale -join ', ') is a stale record"
    if ($earlier.Count -gt 0) {
        $sentence += ", the database's address in an earlier round"
    }
    "$sentence."
}

if ($Snapshot.DatabasePodOnBridge -eq $false) {
    'The database pod''s interface is not attached to the bridge of the jim network, so nothing on the network can reach it.'
}
if ($Snapshot.JimPodOnBridge -eq $false) {
    'The jim pod''s interface is not attached to the bridge of the jim network, so its services can reach nothing on it.'
}

if ($Snapshot.FirewalldRunning -and $Snapshot.Subnet -and @($Snapshot.TrustedSources) -notcontains $Snapshot.Subnet) {
    $effect = if ($Snapshot.BridgeTrafficFiltered) {
        'so firewalld rejects the pods'' name lookups and, as this host filters bridged traffic, their connections to the database'
    }
    else {
        'so firewalld rejects the pods'' name lookups'
    }
    "The jim network ($($Snapshot.Subnet)) is not in firewalld's trusted zone, $effect."
    if (-not $Snapshot.ReloadHelperActive) {
        'netavark-firewalld-reload.service is not running, so nothing puts the jim network back in the trusted zone after a firewalld reload.'
    }
}

$probe = $Snapshot.TcpByAddress
if ($database -and $probe -and $probe.ExitCode -ne 0) {
    $milliseconds = [int]$probe.Milliseconds
    $where = "Connecting from the jim pod to the database pod's own address (${database}:5432)"
    if ($probe.ExitCode -eq 124) {
        "$where got no answer within $milliseconds ms: something drops the connection silently."
    }
    elseif ($probe.Message -match 'No route to host|Host is unreachable') {
        $neighbour = "$($Snapshot.DatabaseNeighbour)".Trim()
        if ($neighbour -in 'FAILED', 'INCOMPLETE') {
            "$where failed after $milliseconds ms with no route to host, and nothing on the network answered for the address (its neighbour entry is $neighbour)."
        }
        elseif ($neighbour) {
            "$where was rejected after $milliseconds ms with no route to host, though the address answers on the network (its neighbour entry is $neighbour): a firewall, typically, rejects the connection."
        }
        elseif ($milliseconds -lt 2500) {
            "$where failed after $milliseconds ms with no route to host: probably rejected, typically by a firewall, as it came sooner than an unanswered address takes."
        }
        else {
            "$where failed after $milliseconds ms with no route to host: probably nothing on the network answered for the address."
        }
    }
    elseif ($probe.Message -match 'Connection refused') {
        "$where was refused: the pod is reachable, but nothing listens on port 5432."
    }
    else {
        "$where failed after $milliseconds ms: $($probe.Message)"
    }
}

$errors = @($Snapshot.NetworkErrors | Where-Object { $_ })
if ($errors.Count -gt 0) {
    $noun = if ($errors.Count -eq 1) { 'error' } else { 'errors' }
    "Podman's network tools logged $($errors.Count) $noun since the round began; the first: $($errors[0])"
}
