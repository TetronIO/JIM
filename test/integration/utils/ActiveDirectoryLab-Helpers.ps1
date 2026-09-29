# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The integration runner's decisions and delivery steps for the Active Directory lab.

.DESCRIPTION
    The Active Directory lab is real Windows Server domain controllers running as Hyper-V virtual
    machines (test/integration/ad-lab/). Run-IntegrationTests.ps1 -DirectoryType ActiveDirectory reverts
    them to a checkpoint at the start of a run instead of starting containers, and Scenario 008's
    population reaches them over LDAPS from the LDAP toolbox container instead of ldbadd inside a Samba
    container. Everything the runner has to decide about that, and the delivery step the population
    scripts share, lives here so that it is tested (ActiveDirectoryLab-Helpers.Tests.ps1) rather than
    only visible on the first run against the lab.

    Callers dot-source LDAP-Helpers.ps1 and Invoke-LabControl.ps1 as well; this file uses
    Invoke-LabControl, Invoke-LdapTool, Get-LdapUri and Test-ActiveDirectoryConfig from them and does not
    load them itself, so that each is defined once.

    Checkpoints are Hyper-V production checkpoints named by the control plane
    (ad-lab/guest/LabDomainController.psm1, Get-LabCheckpointName): "baseline" after the build, and
    "populated-<template>-<hash>" after population. The hash covers the scripts that build the
    population (Get-ActiveDirectoryLabPopulateHash), so a checkpoint made by older scripts is not the
    name a run asks for, is never reverted to, and is rebuilt.
#>

Set-StrictMode -Version Latest

# The instances the lab has, in the order they are reverted and reported.
$script:AdLabInstanceOrder = @('Primary', 'Source', 'Target')

function Get-JimComposeArgument {
    <#
    .SYNOPSIS
        The docker compose -f arguments that select the JIM stack's compose files for a run.

    .DESCRIPTION
        Every docker compose call that starts, builds or stops the JIM stack (jim.web, jim.worker,
        jim.scheduler, the database) must use the same list of files or compose sees a different
        project each time. An Active Directory run adds the lab overlay, which gives the JIM
        containers extra_hosts entries for the domain controllers' FQDNs (their names are in no DNS the
        containers can see, and their certificates name the FQDN).

    .PARAMETER DirectoryType
        The run's directory type.

    .PARAMETER BaseArguments
        The arguments that address the JIM stack before the lab overlay is appended. Defaults to the two
        ordinary compose files; the runner passes Get-JimComposeArgs (IntegrationLane.ps1), so a lane's
        project name and override file are kept and the overlay follows them.
    .OUTPUTS
        string[]: arguments to splat after "docker compose".
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory=$true)]
        [string]$DirectoryType,

        [Parameter(Mandatory=$false)]
        [string[]]$BaseArguments = @('-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml')
    )

    $composeArguments = @($BaseArguments)
    if ($DirectoryType -eq 'ActiveDirectory') {
        $composeArguments += @('-f', 'test/integration/docker/docker-compose.ad-lab.yml')
    }
    return [string[]]$composeArguments
}

function Get-ActiveDirectoryLabInstance {
    <#
    .SYNOPSIS
        The lab instances (Primary, Source, Target) a scenario uses, in revert order.

    .DESCRIPTION
        Primary is always used: it is the run's DirectoryConfig. Scenarios 2 and 8 also use Source and
        Target (the resurgam.local and gentian.local forests), which the Samba lab runs as
        samba-ad-source and samba-ad-target. Scenario 25 uses Primary and Source: Primary has the Active
        Directory Recycle Bin on and Source has it off, and the scenario compares the two. Reverting a
        domain controller a scenario does not use costs minutes, so the rest get Primary alone.

    .PARAMETER ScenarioNumber
        The scenario's number, or $null when there is none.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory=$false)]
        [AllowNull()]
        [Nullable[int]]$ScenarioNumber
    )

    if ($ScenarioNumber -in 2, 8) {
        return [string[]]$script:AdLabInstanceOrder
    }
    if ($ScenarioNumber -eq 25) {
        return [string[]]@('Primary', 'Source')
    }
    return [string[]]@('Primary')
}

function Get-ActiveDirectoryLabCheckpointName {
    <#
    .SYNOPSIS
        Build a lab checkpoint name: "baseline", or "populated-<template>-<hash>".

    .DESCRIPTION
        The same shape as the control plane's Get-LabCheckpointName (which the tests hold this equal
        to): the template lower-cased, the hash 16 hexadecimal characters lower-cased. The control
        plane refuses any other name, so this refuses it first, with the reason.

    .PARAMETER Baseline
        Build "baseline".

    .PARAMETER Template
        The population template (alphanumeric, for example Nano or Scale100k50Groups).

    .PARAMETER Hash
        The 16 hexadecimal character populate script hash (Get-ActiveDirectoryLabPopulateHash).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Baseline', Justification = 'The switch only selects the parameter set.')]
    [CmdletBinding(DefaultParameterSetName = 'Populated')]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true, ParameterSetName = 'Baseline')]
        [switch]$Baseline,

        [Parameter(Mandatory=$true, ParameterSetName = 'Populated')]
        [ValidatePattern('^[A-Za-z0-9]+$')]
        [string]$Template,

        [Parameter(Mandatory=$true, ParameterSetName = 'Populated')]
        [ValidatePattern('^[0-9a-fA-F]{16}$')]
        [string]$Hash
    )

    if ($PSCmdlet.ParameterSetName -eq 'Baseline') {
        return 'baseline'
    }
    return ('populated-{0}-{1}' -f $Template.ToLowerInvariant(), $Hash.ToLowerInvariant())
}

function Get-ActiveDirectoryLabPopulateHash {
    <#
    .SYNOPSIS
        The 16 character hash of the scripts that build a populated lab checkpoint.

    .DESCRIPTION
        The same shape as the runner's Get-PopulateScriptHash (and so the Samba snapshot label): the
        raw text of each file concatenated in order, SHA-256, the first 16 hexadecimal characters,
        lower case. The files are the shared helpers, this file (it builds the LDIF that is delivered
        to a domain controller) and the population script itself: utils/Test-Helpers.ps1,
        utils/Test-GroupHelpers.ps1, utils/LDAP-Helpers.ps1, utils/ActiveDirectoryLab-Helpers.ps1 and
        Populate-SambaAD-Scenario-008.ps1. A file that is missing is skipped, as it is there.

    .PARAMETER ScriptRoot
        test/integration.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [string]$ScriptRoot
    )

    $filesToHash = @(
        (Join-Path (Join-Path $ScriptRoot 'utils') 'Test-Helpers.ps1'),
        (Join-Path (Join-Path $ScriptRoot 'utils') 'Test-GroupHelpers.ps1'),
        (Join-Path (Join-Path $ScriptRoot 'utils') 'LDAP-Helpers.ps1'),
        (Join-Path (Join-Path $ScriptRoot 'utils') 'ActiveDirectoryLab-Helpers.ps1'),
        (Join-Path $ScriptRoot 'Populate-SambaAD-Scenario-008.ps1')
    )
    $combinedContent = ''
    foreach ($file in $filesToHash) {
        if (Test-Path -LiteralPath $file) { $combinedContent += Get-Content -LiteralPath $file -Raw }
    }
    $hashBytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($combinedContent))
    return [System.BitConverter]::ToString($hashBytes).Replace('-', '').Substring(0, 16).ToLower()
}

function Select-ActiveDirectoryLabCheckpoint {
    <#
    .SYNOPSIS
        Which checkpoint a domain controller is reverted to for a scenario, and whether it then needs populating.

    .DESCRIPTION
        "baseline" (a clean, delegated OU=Corp) for Primary and for every scenario that populates live.
        Scenario 8 populates its Source and Target forests once and reuses the result: they are reverted
        to "populated-<template>-<hash>" when that checkpoint exists, and otherwise to "baseline" with
        NeedsPopulation set, so the runner populates and checkpoints them. A populated checkpoint made
        by different populate scripts, or for another template, is a different name and is therefore
        never chosen; that is how a stale one is rejected and rebuilt.

    .PARAMETER Instance
        Primary, Source or Target.

    .PARAMETER ScenarioNumber
        The scenario's number, or $null.

    .PARAMETER Template
        The population template.

    .PARAMETER Hash
        The populate script hash (Get-ActiveDirectoryLabPopulateHash).

    .PARAMETER AvailableCheckpoint
        The checkpoint names the machine has (Get-ActiveDirectoryLabState).

    .OUTPUTS
        A hashtable with Checkpoint (the name to revert to) and NeedsPopulation.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet('Primary', 'Source', 'Target')]
        [string]$Instance,

        [Parameter(Mandatory=$false)]
        [AllowNull()]
        [Nullable[int]]$ScenarioNumber,

        [Parameter(Mandatory=$true)]
        [string]$Template,

        [Parameter(Mandatory=$true)]
        [string]$Hash,

        [Parameter(Mandatory=$true)]
        [AllowEmptyCollection()]
        [string[]]$AvailableCheckpoint
    )

    $baseline = Get-ActiveDirectoryLabCheckpointName -Baseline

    if ($ScenarioNumber -eq 8 -and $Instance -in 'Source', 'Target') {
        $populated = Get-ActiveDirectoryLabCheckpointName -Template $Template -Hash $Hash
        if ($AvailableCheckpoint -ccontains $populated) {
            return @{ Checkpoint = $populated; NeedsPopulation = $false }
        }
        return @{ Checkpoint = $baseline; NeedsPopulation = $true }
    }

    return @{ Checkpoint = $baseline; NeedsPopulation = $false }
}

function ConvertFrom-LabDomainControllerJson {
    <#
    .SYNOPSIS
        Read the JSON Get-LabDomainController.ps1 -AsJson prints.

    .DESCRIPTION
        Returns a hashtable with Name, State, CheckpointNames (always an array) and GuestBuild (the
        guest's build string such as 26100.1742, or $null when the guest was not reachable). The text
        between the first "{" and the last "}" is parsed, so a banner the remote shell prints around the
        object does not break it. Throws, quoting what it was given, when there is no object.

    .PARAMETER Json
        The script's standard output.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [AllowNull()]
        [object]$Json
    )

    $text = (@($Json) | ForEach-Object { "$_" }) -join "`n"
    $first = $text.IndexOf('{')
    $last = $text.LastIndexOf('}')
    if ($first -lt 0 -or $last -lt $first) {
        $excerpt = if ($text.Length -gt 200) { $text.Substring(0, 200) + '...' } else { $text }
        throw "Get-LabDomainController.ps1 did not print a JSON object. It printed: '$excerpt'"
    }

    try {
        $parsed = $text.Substring($first, $last - $first + 1) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Get-LabDomainController.ps1 printed JSON that could not be read ($($_.Exception.Message))."
    }

    $checkpointNames = @()
    if ($parsed.PSObject.Properties['checkpoints'] -and $null -ne $parsed.checkpoints) {
        $checkpointNames = @($parsed.checkpoints | ForEach-Object { [string]$_.name })
    }

    $guestBuild = $null
    if ($parsed.PSObject.Properties['guest'] -and $null -ne $parsed.guest -and $parsed.guest.PSObject.Properties['buildString']) {
        $guestBuild = [string]$parsed.guest.buildString
    }

    return @{
        Name            = [string]$parsed.name
        State           = [string]$parsed.state
        CheckpointNames = $checkpointNames
        GuestBuild      = $guestBuild
    }
}

function Get-ActiveDirectoryLabState {
    <#
    .SYNOPSIS
        Ask the control plane about each lab virtual machine a run uses, and require its baseline checkpoint.

    .DESCRIPTION
        Runs Get-LabDomainController.ps1 -Name <vm> -AsJson on the hypervisor host (Invoke-LabControl)
        for each config, in Primary, Source, Target order. Throws, naming the machine and the remedy, if
        a machine has no "baseline" checkpoint: every reset reverts to it, so a run cannot start
        without one. A control plane failure is not caught; a run must not carry on against a machine it
        could not ask about.

    .PARAMETER DirectoryConfig
        A hashtable of instance name to ActiveDirectory config, for example @{ Primary = $p; Source = $s }.

    .OUTPUTS
        One hashtable per machine: Instance, VmName, State, CheckpointNames, GuestBuild.
    #>
    [CmdletBinding()]
    [OutputType([hashtable[]])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    $baseline = Get-ActiveDirectoryLabCheckpointName -Baseline
    $states = @()
    foreach ($instance in $script:AdLabInstanceOrder) {
        if (-not $DirectoryConfig.ContainsKey($instance)) { continue }
        $vmName = [string]$DirectoryConfig[$instance].VmName

        $json = Invoke-LabControl -Script 'Get-LabDomainController.ps1' -Arguments @('-Name', $vmName, '-AsJson')
        $state = ConvertFrom-LabDomainControllerJson -Json $json

        if ($state.CheckpointNames -cnotcontains $baseline) {
            throw "The lab domain controller '$vmName' has no '$baseline' checkpoint (it has: $(if ($state.CheckpointNames.Count -gt 0) { $state.CheckpointNames -join ', ' } else { 'none' })). Every reset reverts to it, so the machine has to be built first: see test/integration/ad-lab/README.md (New-LabDomainController.ps1)."
        }

        $states += @{
            Instance        = $instance
            VmName          = $vmName
            State           = $state.State
            CheckpointNames = $state.CheckpointNames
            GuestBuild      = $state.GuestBuild
        }
    }
    return $states
}

function Get-ActiveDirectoryLabRestorePlan {
    <#
    .SYNOPSIS
        Decide, for every machine a run uses, the checkpoint to revert to and whether it then needs populating.

    .PARAMETER State
        The machines' state (Get-ActiveDirectoryLabState).

    .PARAMETER ScenarioNumber
        The scenario's number, or $null.

    .PARAMETER Template
        The population template.

    .PARAMETER Hash
        The populate script hash (Get-ActiveDirectoryLabPopulateHash).

    .OUTPUTS
        One hashtable per machine: Instance, VmName, Checkpoint, NeedsPopulation.
    #>
    [CmdletBinding()]
    [OutputType([hashtable[]])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable[]]$State,

        [Parameter(Mandatory=$false)]
        [AllowNull()]
        [Nullable[int]]$ScenarioNumber,

        [Parameter(Mandatory=$true)]
        [string]$Template,

        [Parameter(Mandatory=$true)]
        [string]$Hash
    )

    $plan = @()
    foreach ($machine in $State) {
        $pick = Select-ActiveDirectoryLabCheckpoint -Instance $machine.Instance -ScenarioNumber $ScenarioNumber `
            -Template $Template -Hash $Hash -AvailableCheckpoint @($machine.CheckpointNames)
        $plan += @{
            Instance        = $machine.Instance
            VmName          = $machine.VmName
            Checkpoint      = $pick.Checkpoint
            NeedsPopulation = $pick.NeedsPopulation
        }
    }
    return $plan
}

function Invoke-ActiveDirectoryLabRestore {
    <#
    .SYNOPSIS
        Revert each machine in a plan to its checkpoint, through the control plane.

    .DESCRIPTION
        Runs Restore-LabDomainController.ps1 -Name <vm> -Checkpoint <name> on the hypervisor host for
        each entry, in order; that script applies the checkpoint, starts the machine and waits for it to
        answer. Stops at the first failure: a run must not carry on against a machine in an unknown
        state. The LDAPS bind and clock check that follow are Wait-ActiveDirectoryReady.ps1's job, and
        need the LDAP toolbox, which the caller starts.

    .PARAMETER Plan
        Get-ActiveDirectoryLabRestorePlan's result.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress in the runner, coloured like the runner''s own output.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable[]]$Plan
    )

    foreach ($entry in $Plan) {
        Write-Host "  Reverting $($entry.VmName) to checkpoint '$($entry.Checkpoint)'..." -ForegroundColor DarkGray
        Invoke-LabControl -Script 'Restore-LabDomainController.ps1' -Arguments @('-Name', $entry.VmName, '-Checkpoint', $entry.Checkpoint) | Out-Null
    }
}

function Invoke-ActiveDirectoryLabCheckpoint {
    <#
    .SYNOPSIS
        Take a production checkpoint of a lab machine, replacing one of the same name.

    .PARAMETER VmName
        The machine.

    .PARAMETER Checkpoint
        The checkpoint name (Get-ActiveDirectoryLabCheckpointName).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress in the runner, coloured like the runner''s own output.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$VmName,

        [Parameter(Mandatory=$true)]
        [string]$Checkpoint
    )

    Write-Host "  Taking checkpoint '$Checkpoint' of $VmName..." -ForegroundColor DarkGray
    Invoke-LabControl -Script 'Checkpoint-LabDomainController.ps1' -Arguments @('-Name', $VmName, '-Checkpoint', $Checkpoint, '-Replace') | Out-Null
}

function Invoke-ActiveDirectoryLabDelegation {
    <#
    .SYNOPSIS
        Delegate JIM's access over a container in a lab domain controller, at run time.

    .DESCRIPTION
        The Active Directory counterpart of Grant-JimAdDelegation (which runs jim-delegate.sh inside a
        Samba container): Grant-LabDelegation.ps1 on the hypervisor host applies the same access control
        entries, unchanged, with the JIM Connectors group as trustee. Idempotent.

    .PARAMETER VmName
        The machine.

    .PARAMETER ContainerDn
        The distinguished name of the container (an OU) to delegate over.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$VmName,

        [Parameter(Mandatory=$true)]
        [string]$ContainerDn
    )

    Invoke-LabControl -Script 'Grant-LabDelegation.ps1' -Arguments @('-Name', $VmName, '-ContainerDn', $ContainerDn) | Out-Null
}

function Get-ActiveDirectoryLabGuestBuild {
    <#
    .SYNOPSIS
        Each machine's guest operating system build, for the run metadata.

    .PARAMETER State
        The machines' state (Get-ActiveDirectoryLabState).

    .OUTPUTS
        An ordered hashtable of machine name to build string ("26100.1742"), or "unknown" when the
        guest was not reachable.
    #>
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable[]]$State
    )

    $builds = [ordered]@{}
    foreach ($machine in $State) {
        $builds[[string]$machine.VmName] = if ([string]::IsNullOrEmpty([string]$machine.GuestBuild)) { 'unknown' } else { [string]$machine.GuestBuild }
    }
    return $builds
}

function Format-ActiveDirectoryLabGuestBuild {
    <#
    .SYNOPSIS
        The guest builds as one line, "dc-primary 26100.1742, dc-source 26100.1742".

    .PARAMETER GuestBuilds
        Get-ActiveDirectoryLabGuestBuild' result.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [System.Collections.IDictionary]$GuestBuilds
    )

    return (@($GuestBuilds.Keys | ForEach-Object { "$_ $($GuestBuilds[$_])" }) -join ', ')
}

function Write-ActiveDirectoryLabLine {
    <#
    .SYNOPSIS
        Print one progress line, for the population scripts' Active Directory branch.

    .DESCRIPTION
        The population scripts report in colour with Write-Host throughout; this keeps that in one
        place for the code that delivers to a domain controller.

    .PARAMETER Text
        The line.

    .PARAMETER ForegroundColor
        The colour (default Gray).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress in the population scripts, coloured like their own output.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true, Position=0)]
        [AllowEmptyString()]
        [string]$Text,

        [Parameter(Mandatory=$false)]
        [System.ConsoleColor]$ForegroundColor = 'Gray'
    )

    Write-Host $Text -ForegroundColor $ForegroundColor
}

function Get-LdapWriteOutcome {
    <#
    .SYNOPSIS
        Classify what an ldapadd or ldapmodify run did, from its exit code and output.

    .DESCRIPTION
        The tools are run with -c (continue past an entry that fails), so the exit code alone cannot say
        what happened: it reflects one failure, or the last. This reads the output instead. Every
        "ldap_<operation>: <text> (<code>)" line is a result code; a code listed in AcceptedResultCode
        (68, entry already exists, for an OU the baseline already has; 20, value already present, for a
        member already in the group) is counted and tolerated, and any other is a failure. A non-zero
        exit code with no result-code line at all (docker could not run the tool, the connection was
        refused) is a failure too, with the output quoted. Each failure names the entry it belongs to
        (the "adding new entry" or "modifying entry" line before it) and carries the directory's
        additional info line, which is where Active Directory's DSID and reason are.

    .PARAMETER ExitCode
        The tool's exit code.

    .PARAMETER Output
        The tool's output, standard error merged in.

    .PARAMETER AcceptedResultCode
        Result codes that do not make the run fail.

    .OUTPUTS
        A hashtable: Succeeded, Entries (entries the tool attempted), Applied (entries that went in),
        AcceptedCount (tolerated result codes seen) and Failures (one string per failure).
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory=$true)]
        [int]$ExitCode,

        [Parameter(Mandatory=$false)]
        [AllowNull()]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [object[]]$Output,

        [Parameter(Mandatory=$false)]
        [int[]]$AcceptedResultCode = @()
    )

    $lines = @($Output | Where-Object { $null -ne $_ } | ForEach-Object { "$_" })
    $entryPattern = '^(adding new entry|modifying entry|deleting entry)\b'
    $resultPattern = '^\s*ldap_[A-Za-z_]+(?:\([^)]*\))?:\s.*\((-?\d+)\)\s*$'

    $entries = 0
    $resultLines = 0
    $acceptedCount = 0
    $failures = [System.Collections.Generic.List[string]]::new()
    $lastEntry = $null

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match $entryPattern) {
            $entries++
            $lastEntry = $line
            continue
        }
        if ($line -match $resultPattern) {
            $resultLines++
            $code = [int]$Matches[1]
            if ($code -in $AcceptedResultCode) {
                $acceptedCount++
                continue
            }
            $detail = ''
            if (($i + 1) -lt $lines.Count -and $lines[$i + 1] -match '^\s+additional info:') {
                $detail = ' [' + $lines[$i + 1].Trim() + ']'
            }
            $failure = if ($lastEntry) { "$lastEntry => $($line.Trim())$detail" } else { "$($line.Trim())$detail" }
            $failures.Add($failure)
        }
    }

    if ($failures.Count -eq 0 -and $ExitCode -ne 0 -and $resultLines -eq 0) {
        $shown = @($lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 5)
        $failures.Add($(if ($shown.Count -gt 0) { "exit code ${ExitCode}: " + ($shown -join ' | ') } else { "exit code $ExitCode with no output" }))
    }

    return @{
        Succeeded     = ($failures.Count -eq 0)
        Entries       = $entries
        Applied       = [Math]::Max(0, $entries - $resultLines)
        AcceptedCount = $acceptedCount
        Failures      = $failures.ToArray()
    }
}

function Get-LdapOrganizationalUnitLdif {
    <#
    .SYNOPSIS
        The LDIF that adds an organisational unit.

    .PARAMETER Dn
        The unit's distinguished name; its first RDN must be OU=<name>.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Dn
    )

    if ($Dn -notmatch '^OU=([^,]+),') {
        throw "Get-LdapOrganizationalUnitLdif: '$Dn' does not start with an OU= RDN."
    }
    return "dn: $Dn`nobjectClass: top`nobjectClass: organizationalUnit`nou: $($Matches[1])`n"
}

function Get-LdapMemberAddLdif {
    <#
    .SYNOPSIS
        The LDIF that adds members to a group, in modify records of at most ChunkSize members.

    .DESCRIPTION
        The Active Directory counterpart of "samba-tool group addmembers": one "add: member" modify
        record per chunk (Active Directory refuses a single modify that adds a very large number of
        values), separated by a blank line. A group with no members gives an empty string.

    .PARAMETER GroupDn
        The group's distinguished name.

    .PARAMETER MemberDn
        The distinguished names to add.

    .PARAMETER ChunkSize
        The most members in one record (default 500).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [string]$GroupDn,

        [Parameter(Mandatory=$true)]
        [AllowEmptyCollection()]
        [string[]]$MemberDn,

        [Parameter(Mandatory=$false)]
        [ValidateRange(1, 5000)]
        [int]$ChunkSize = 500
    )

    $builder = [System.Text.StringBuilder]::new()
    for ($start = 0; $start -lt $MemberDn.Count; $start += $ChunkSize) {
        $end = [Math]::Min($start + $ChunkSize, $MemberDn.Count) - 1
        if ($start -gt 0) { [void]$builder.Append("`n") }
        [void]$builder.Append("dn: $GroupDn`nchangetype: modify`nadd: member`n")
        foreach ($member in $MemberDn[$start..$end]) {
            [void]$builder.Append("member: $member`n")
        }
    }
    return $builder.ToString()
}

function Invoke-ActiveDirectoryLdifDelivery {
    <#
    .SYNOPSIS
        Deliver LDIF to an Active Directory domain controller with ldapadd or ldapmodify, and fail hard on any error.

    .DESCRIPTION
        The Active Directory counterpart of "docker cp; docker exec <container> ldbadd" in the Samba
        population scripts: the LDIF the script has already built is fed to the tool on standard input
        through Invoke-LdapTool, which runs it in the LDAP toolbox (a domain controller has no
        container), over LDAPS, binding as the domain administrator from the config. The tool runs with
        -c so one entry that fails does not hide the rest; Get-LdapWriteOutcome then reads the output,
        tolerating only the result codes in AcceptedResultCode. Anything else throws, naming the entries
        and the directory's own reason, because a half-populated directory would make a scenario fail
        somewhere far from the cause. The bind password is passed to the tool as it is throughout this
        harness (-w) and is never part of a message here.

    .PARAMETER DirectoryConfig
        An ActiveDirectory config from Get-DirectoryConfig for the instance being written to.

    .PARAMETER Tool
        ldapadd or ldapmodify.

    .PARAMETER Ldif
        The LDIF text. Empty is a no-op.

    .PARAMETER AcceptedResultCode
        Result codes that are not failures (68 for an entry that already exists, 20 for a member that
        is already present).

    .PARAMETER Description
        What is being delivered, for the error message ("users chunk 3 of 4").

    .OUTPUTS
        Get-LdapWriteOutcome's hashtable.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [ValidateSet('ldapadd', 'ldapmodify')]
        [string]$Tool,

        [Parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [string]$Ldif,

        [Parameter(Mandatory=$false)]
        [int[]]$AcceptedResultCode = @(),

        [Parameter(Mandatory=$false)]
        [string]$Description = 'LDIF delivery'
    )

    if (-not (Test-ActiveDirectoryConfig -DirectoryConfig $DirectoryConfig)) {
        throw "Invoke-ActiveDirectoryLdifDelivery: the directory config is for '$($DirectoryConfig['DirectoryType'])', not ActiveDirectory."
    }

    if ([string]::IsNullOrWhiteSpace($Ldif)) {
        return @{ Succeeded = $true; Entries = 0; Applied = 0; AcceptedCount = 0; Failures = @() }
    }

    $toolArguments = @(
        '-x', '-c',
        '-H', (Get-LdapUri -DirectoryConfig $DirectoryConfig),
        '-D', [string]$DirectoryConfig['BindDN'],
        '-w', [string]$DirectoryConfig['BindPassword']
    )
    $invocation = Invoke-LdapTool -DirectoryConfig $DirectoryConfig -Tool $Tool -Arguments $toolArguments -InputObject $Ldif -PassThruExitCode
    $outcome = Get-LdapWriteOutcome -ExitCode $invocation.ExitCode -Output @($invocation.Output) -AcceptedResultCode $AcceptedResultCode

    if (-not $outcome.Succeeded) {
        $shown = @($outcome.Failures | Select-Object -First 5)
        $more = if ($outcome.Failures.Count -gt $shown.Count) { " (and $($outcome.Failures.Count - $shown.Count) more)" } else { '' }
        throw "Delivering $Description to $($DirectoryConfig['Host']) with $Tool failed for $($outcome.Failures.Count) of $($outcome.Entries) entries$more. First: $($shown -join ' || ')"
    }

    return $outcome
}

function Invoke-ActiveDirectoryOrganisationalUnitAdd {
    <#
    .SYNOPSIS
        Add an organisational unit to a lab domain controller, tolerating one that already exists.

    .DESCRIPTION
        The baseline checkpoint already holds OU=Corp, OU=Users,OU=Corp and OU=Groups,OU=Corp, so
        "already exists" (result code 68) is the ordinary answer for those and is not an error, as it
        is not with "samba-tool ou create". Any other failure throws.

    .PARAMETER DirectoryConfig
        An ActiveDirectory config for the domain controller.

    .PARAMETER Dn
        The unit's distinguished name.

    .OUTPUTS
        "Created" or "Exists".
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$Dn
    )

    $outcome = Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $DirectoryConfig -Tool ldapadd `
        -Ldif (Get-LdapOrganizationalUnitLdif -Dn $Dn) -AcceptedResultCode 68 -Description "organisational unit $Dn"
    return $(if ($outcome.AcceptedCount -gt 0) { 'Exists' } else { 'Created' })
}

function Test-PasswordContainsToken {
    <#
    .SYNOPSIS
        Whether a password contains a token of an account's name, compared without regard to case.

    .DESCRIPTION
        Active Directory's complexity rule refuses a password that contains the account's sAMAccountName, or a
        part of its display name of three characters or more, whatever the password's length and mix of
        characters. A token shorter than three characters is not one it looks for, so it is ignored here too.
        Scenario 24 uses this both ways: to keep a fixture password clear of the names it is set on, and to
        build the one password that a length and category check cannot tell is unacceptable and a domain
        controller refuses.

    .PARAMETER Password
        The password.

    .PARAMETER Token
        The part of a name to look for.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
        Justification = 'Lab fixture passwords are plain-text test values, compared as text; a SecureString would only be unwrapped again at once.')]
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Password,

        [Parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [string]$Token
    )

    if ($Token.Length -lt 3) { return $false }
    return ($Password.IndexOf($Token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
}

function Get-ActiveDirectoryFixturePassword {
    <#
    .SYNOPSIS
        A random password for a throwaway lab account, one that satisfies a stock Active Directory policy.

    .DESCRIPTION
        For the accounts a scenario creates in a domain controller that the next checkpoint revert destroys.
        It draws on all four character classes (upper case, lower case, digits and symbols), starts with a
        letter or digit so that it is safe as the value of an LDAP tool option, uses characters that cannot be
        mistaken for one another, and is chosen with System.Security.Cryptography.RandomNumberGenerator, so that
        no password is written into a script. Tokens of the account's name the password must not contain (see
        Test-PasswordContainsToken) are avoided by drawing again.

    .PARAMETER Length
        How many characters, 12 to 128 (default 20).

    .PARAMETER Avoid
        Name tokens the password must not contain.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$false)]
        [ValidateRange(12, 128)]
        [int]$Length = 20,

        [Parameter(Mandatory=$false)]
        [string[]]$Avoid = @()
    )

    $upper = 'ABCDEFGHJKLMNPQRSTUVWXYZ'
    $lower = 'abcdefghijkmnopqrstuvwxyz'
    $digits = '23456789'
    $symbols = '!-_.'
    $everything = $upper + $lower + $digits + $symbols
    $leading = $upper + $lower + $digits

    $draw = { param([string]$Pool) $Pool[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($Pool.Length)] }

    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        # The first character is any but a symbol; every class is present in the rest, which is shuffled so
        # that the guaranteed characters do not sit in a predictable place.
        $rest = [System.Collections.Generic.List[char]]::new()
        foreach ($pool in $upper, $lower, $digits, $symbols) { $rest.Add((& $draw $pool)) }
        while ($rest.Count -lt ($Length - 1)) { $rest.Add((& $draw $everything)) }
        for ($index = $rest.Count - 1; $index -gt 0; $index--) {
            $swap = [System.Security.Cryptography.RandomNumberGenerator]::GetInt32($index + 1)
            $held = $rest[$index]
            $rest[$index] = $rest[$swap]
            $rest[$swap] = $held
        }

        $candidate = [string](& $draw $leading) + (-join $rest)
        $clashes = @($Avoid | Where-Object { Test-PasswordContainsToken -Password $candidate -Token $_ })
        if ($clashes.Count -eq 0) { return $candidate }
    }

    throw "Get-ActiveDirectoryFixturePassword: could not draw a $Length character password that avoids '$($Avoid -join "', '")' in 100 attempts."
}
