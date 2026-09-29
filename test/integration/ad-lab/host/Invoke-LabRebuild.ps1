# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    One phase of the monthly rebuild of the Active Directory lab: builds a candidate set of domain controllers beside
    the live set, and swaps names only once the suite has passed against the candidates.

.DESCRIPTION
    The lab is patched by rebuild and never in place. After Patch Tuesday the ad-lab-rebuild.yml workflow drives this
    script over the control plane, one phase at a time, and runs the full suite between Stage and Promote:

        Prune     Remove each <name>-prev that was demoted at least -PrevRetentionDays days ago. Runs first, so the
                  old set's disk is free before three new disks are built.
        Build     For each domain controller: stop the live VM (the candidate takes its address), build
                  <name>-candidate from settings.json and New-LabDomainController.ps1 with the newest cumulative
                  update in cumulativeUpdateDirectory, and take its baseline checkpoint. A finished candidate under a
                  day old is reused and an unfinished one resumed; an older one is a leftover from a red run and is
                  replaced. -Fresh replaces it regardless.
        Stage     Stop the live VMs and start the candidates from their baseline checkpoints, waiting until each
                  answers on LDAPS. The workflow then runs the suite with JIM_AD_LAB_<ROLE>_VM pointed at them.
        Promote   Green: name each live VM <name>-prev (stamping when, in its Notes) and each candidate <name>. Every
                  candidate is checked before any is renamed, and a promotion interrupted part way finishes when run
                  again.
        Rollback  Red: stop the candidates (left in place for diagnosis) and start the live VMs again from their
                  baseline checkpoints. Also finishes putting the live VM back if a promotion was interrupted.
        Status    Read-only: which VMs exist, their state, and the live guest OS build.

    Every phase can be run again and converges. The live set's names never change, so the nightly run's configuration
    never does: the suite reaches the candidates only through JIM_AD_LAB_<ROLE>_VM for that one run. This script never
    touches the runner VM or the hypervisor host: patching the host is a manual, announced event, because it restarts
    every VM.

    Each domain controller is built with the live one's own address (its checkpoints carry the address, so it cannot be
    changed afterwards). The live VM is therefore OFF from the start of Build until Promote or Rollback; the workflow
    shares the nightly's concurrency group so nothing else runs in that time. If a run is cancelled or the runner dies
    mid-rebuild, run this script with -Phase Rollback on the host (see README.md, Monthly rebuild).

    Each candidate's disk goes in a folder of its own for the rebuild (<vhdDirectory>\rebuild-yyyyMMdd), because a
    promoted candidate keeps the folder it was built in and the next rebuild's candidate has the same name.

    With -AsJson, standard output is exactly one JSON document and everything else goes to standard error, which is
    what the runner reads:

        { phase, success, roles: [ { role, live, candidate, prev, action, detail, guestBuild } ] }

    A failure exits non-zero with the reason on standard error. Progress is also appended to a log under
    %ProgramData%\jim-ad-lab\logs (or the temporary folder when that cannot be written). The lab scripts it drives run
    in their own processes and read the two passwords from the host account's environment
    (JIM_AD_LAB_ADMIN_PASSWORD and JIM_AD_LAB_JIM_PASSWORD); nothing secret is a parameter or is logged.

.PARAMETER Phase
    Prune, Build, Stage, Promote, Rollback or Status.

.PARAMETER Role
    Limit the phase to these live VM names (dc-primary, dc-source, dc-target; comma-separated is accepted). Default: all
    three. The workflow builds one at a time so each is its own control-plane call.

.PARAMETER SettingsPath
    The settings file. Default: settings.json beside this script (C:\jim-ad-lab\settings.json). Only Build reads more
    from it than the names; see test/integration/ad-lab/README.md.

.PARAMETER Fresh
    Build only: replace an existing candidate even when it is recent.

.PARAMETER MaxCandidateAgeHours
    Build only: a candidate older than this is a leftover and is replaced. Default 24.

.PARAMETER PrevRetentionDays
    Prune only: how long a previous set is kept as the rollback copy. Default 7.

.PARAMETER AsJson
    Print the result as one JSON document and send progress to standard error.

.EXAMPLE
    .\Invoke-LabRebuild.ps1 -Phase Status

.EXAMPLE
    .\Invoke-LabRebuild.ps1 -Phase Build -Role dc-primary -AsJson

.EXAMPLE
    .\Invoke-LabRebuild.ps1 -Phase Rollback
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style; JSON mode sends it to standard error.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Prune', 'Build', 'Stage', 'Promote', 'Rollback', 'Status')]
    [string]$Phase,

    [string[]]$Role = @(),

    [string]$SettingsPath,

    [switch]$Fresh,

    [ValidateRange(1, 720)]
    [int]$MaxCandidateAgeHours = 24,

    [ValidateRange(1, 365)]
    [int]$PrevRetentionDays = 7,

    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$labModule = @(
    (Join-Path $PSScriptRoot 'guest/LabDomainController.psm1'),
    (Join-Path (Join-Path $PSScriptRoot '..') 'guest/LabDomainController.psm1')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $labModule) {
    [Console]::Error.WriteLine('Cannot find guest/LabDomainController.psm1 beside or above this script; see the layout notes in README.md.')
    exit 1
}
Import-Module $labModule -Force

$script:LogPath = $null
$script:VhdRoot = $null
$script:FolderName = @()

function Initialize-RebuildLog {
    # A log that cannot be written must never stop a rebuild, so every failure here is swallowed.
    $candidates = @()
    if ($env:ProgramData) {
        $candidates += (Join-Path (Join-Path $env:ProgramData 'jim-ad-lab') 'logs')
    }
    $candidates += [System.IO.Path]::GetTempPath()
    foreach ($directory in $candidates) {
        try {
            $null = New-Item -ItemType Directory -Path $directory -Force -ErrorAction Stop
            $path = Join-Path $directory ('Invoke-LabRebuild-{0}.log' -f [datetime]::UtcNow.ToString('yyyyMMdd'))
            Add-Content -LiteralPath $path -Value ('--- {0:u} phase {1} ---' -f [datetime]::UtcNow, $Phase) -ErrorAction Stop
            $script:LogPath = $path
            return
        }
        catch {
            continue
        }
    }
}

function Write-RebuildLog {
    param([string]$Text, [switch]$FileOnly)

    if ($script:LogPath) {
        try {
            Add-Content -LiteralPath $script:LogPath -Value ('{0:HH:mm:ss} {1}' -f [datetime]::UtcNow, $Text) -ErrorAction Stop
        }
        catch {
            $script:LogPath = $null
        }
    }
    if ($FileOnly) {
        return
    }
    if ($AsJson) {
        [Console]::Error.WriteLine($Text)
    }
    else {
        Write-Host $Text
    }
}

function Invoke-LabScript {
    # Runs a sibling lab script in its own process (its own module import, StrictMode and exit code), logging its
    # output as it arrives. Returns ExitCode and Output. With -StdoutOnly, standard error is discarded and nothing is
    # logged, so a JSON reply can be parsed.
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [string[]]$Arguments = @(),

        [switch]$StdoutOnly
    )

    $path = Join-Path $PSScriptRoot $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Cannot find $Name beside this script ($PSScriptRoot)."
    }
    $executable = (Get-Process -Id $PID).Path
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $global:LASTEXITCODE = 0
        if ($StdoutOnly) {
            $lines = @(& $executable -NoProfile -NonInteractive -File $path @Arguments 2>$null | ForEach-Object { [string]$_ })
        }
        else {
            $lines = @(& $executable -NoProfile -NonInteractive -File $path @Arguments 2>&1 | ForEach-Object {
                    $line = [string]$_
                    Write-RebuildLog $line
                    $line
                })
        }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{ ExitCode = $code; Output = $lines }
}

function Get-LastLine {
    param([string[]]$Line, [int]$Count = 15)

    return ((@($Line | Where-Object { $_ -and $_.Trim() }) | Select-Object -Last $Count) -join ' | ')
}

# --- The adapter: the only place this script touches Hyper-V ------------------------------------

function Get-RebuildAdapter {
    # The adapter's script blocks run later, from inside the module, so what they need is held in script scope
    # ($script:VhdRoot, $script:FolderName), not in this function's own.
    return @{
        GetVm         = {
            param($Name)
            $found = @(Get-VM -Name $Name -ErrorAction SilentlyContinue)
            if ($found.Count -gt 1) {
                throw "There is more than one VM called '$Name'."
            }
            if ($found.Count -eq 0) {
                return $null
            }
            $vm = $found[0]
            $baseline = @(Get-VMSnapshot -VMName $Name -Name (Get-LabCheckpointName -Baseline) -ErrorAction SilentlyContinue).Count -gt 0
            return [pscustomobject]@{
                Name        = [string]$vm.Name
                State       = [string]$vm.State
                Notes       = [string]$vm.Notes
                CreatedUtc  = ([datetime]$vm.CreationTime).ToUniversalTime()
                HasBaseline = $baseline
            }
        }

        StopVm        = {
            param($Name)
            if ((Get-VM -Name $Name).State -eq 'Off') {
                return
            }
            Write-RebuildLog "Shutting down $Name."
            try {
                Stop-VM -Name $Name -Force -ErrorAction Stop
            }
            catch {
                Write-RebuildLog "A graceful shutdown of $Name failed: $($_.Exception.Message)"
            }
            $deadline = (Get-Date).AddSeconds(300)
            while (((Get-VM -Name $Name).State -ne 'Off') -and ((Get-Date) -lt $deadline)) {
                Start-Sleep -Seconds 3
            }
            if ((Get-VM -Name $Name).State -ne 'Off') {
                Write-RebuildLog "$Name did not shut down in 5 minutes; turning it off."
                Stop-VM -Name $Name -TurnOff -Force
            }
        }

        RestoreVm     = {
            param($Name, $Checkpoint)
            $result = Invoke-LabScript -Name 'Restore-LabDomainController.ps1' -Arguments @('-Name', $Name, '-Checkpoint', $Checkpoint, '-TimeoutSeconds', '900')
            if ($result.ExitCode -ne 0) {
                throw "Restoring $Name to '$Checkpoint' failed (exit code $($result.ExitCode)): $(Get-LastLine -Line $result.Output)"
            }
        }

        RenameVm      = {
            param($Name, $NewName)
            Get-VM -Name $Name | Rename-VM -NewName $NewName
        }

        SetNotes      = {
            param($Name, $Notes)
            Set-VM -Name $Name -Notes $Notes
        }

        RemoveVm      = {
            param($Name)
            $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
            $vmPath = $null
            if ($null -ne $vm) {
                $vmPath = [string]$vm.Path
            }
            $result = Invoke-LabScript -Name 'Remove-LabDomainController.ps1' -Arguments @('-Name', $Name, '-Force')
            if ($result.ExitCode -ne 0) {
                throw "Removing $Name failed (exit code $($result.ExitCode)): $(Get-LastLine -Line $result.Output)"
            }
            # Remove-LabDomainController.ps1 deletes the VM's folder only when it carries the VM's name, and a renamed
            # VM keeps the name it was built under, so clear such a folder here (never one that still holds a disk).
            if ($vmPath -and $script:VhdRoot -and (Test-LabOwnedVmFolder -Path $vmPath -Root $script:VhdRoot -Name $script:FolderName) -and (Test-Path -LiteralPath $vmPath)) {
                $disks = @(Get-ChildItem -LiteralPath $vmPath -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.vhdx', '.avhdx', '.vhd', '.avhd' })
                if ($disks.Count -eq 0) {
                    Remove-Item -LiteralPath $vmPath -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }

        BuildVm       = {
            param($Parameters)
            $result = Invoke-LabScript -Name 'New-LabDomainController.ps1' -Arguments (ConvertTo-LabChildArgument -Parameter $Parameters)
            if ($result.ExitCode -ne 0) {
                throw "New-LabDomainController.ps1 failed for $($Parameters.Name) (exit code $($result.ExitCode)): $(Get-LastLine -Line $result.Output)"
            }
        }

        GetGuestBuild = {
            param($Name)
            $result = Invoke-LabScript -Name 'Get-LabDomainController.ps1' -Arguments @('-Name', $Name, '-AsJson') -StdoutOnly
            if ($result.ExitCode -ne 0) {
                return $null
            }
            $status = ($result.Output -join "`n") | ConvertFrom-Json
            if ($null -eq $status.guest) {
                return $null
            }
            return [string]$status.guest.buildString
        }

        NowUtc        = { return [datetime]::UtcNow }

        Log           = { param($Text) Write-RebuildLog $Text }
    }
}

# --- Run the phase -----------------------------------------------------------------------------

try {
    Assert-LabWindows -Feature 'The monthly rebuild'
    Initialize-RebuildLog

    $settingsFile = $SettingsPath
    if ([string]::IsNullOrEmpty($settingsFile)) {
        $settingsFile = Join-Path $PSScriptRoot 'settings.json'
    }

    if ($Phase -eq 'Build') {
        if (-not (Test-Path -LiteralPath $settingsFile -PathType Leaf)) {
            throw "The settings file '$settingsFile' does not exist. Run Install-LabScripts.ps1 -CreateSettings, then fill it in (README.md)."
        }
        $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json

        $updatePath = $null
        $updateDirectory = $null
        if ($null -ne $settings.PSObject.Properties['cumulativeUpdateDirectory']) {
            $updateDirectory = [string]$settings.cumulativeUpdateDirectory
        }
        if (-not [string]::IsNullOrWhiteSpace($updateDirectory)) {
            if (Test-Path -LiteralPath $updateDirectory -PathType Container) {
                $update = Get-LabNewestCumulativeUpdate -File @(Get-ChildItem -LiteralPath $updateDirectory -Filter '*.msu' -File -ErrorAction SilentlyContinue)
                if ($null -ne $update) {
                    $updatePath = $update.FullName
                    Write-RebuildLog "Cumulative update: $($update.Name), the newest .msu in $updateDirectory."
                }
                else {
                    Write-RebuildLog "No .msu in ${updateDirectory}: building from the media as it is."
                }
            }
            else {
                Write-RebuildLog "cumulativeUpdateDirectory '$updateDirectory' does not exist: building from the media as it is."
            }
        }

        $plan = ConvertFrom-LabRebuildSetting -Settings $settings -GenerationName (Get-LabRebuildGenerationName -NowUtc ([datetime]::UtcNow)) -CumulativeUpdatePath $updatePath
    }
    else {
        # Only the names are needed to stage, promote, roll back, prune or report, and a rollback must never be
        # blocked by a settings file that is wrong for building.
        $settings = $null
        if (Test-Path -LiteralPath $settingsFile -PathType Leaf) {
            try {
                $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
            }
            catch {
                Write-RebuildLog "The settings file could not be read ($($_.Exception.Message)); carrying on with the lab's fixed names."
            }
        }
        $plan = ConvertFrom-LabRebuildSetting -Settings $settings -NamesOnly
    }

    $script:VhdRoot = $plan.VhdDirectory
    $script:FolderName = @($plan.Roles | ForEach-Object { $_.LiveName; $_.CandidateName; $_.PrevName })
    $adapter = Get-RebuildAdapter

    Write-RebuildLog "Phase $Phase."
    $results = @(Invoke-LabRebuildPhase -Phase $Phase -Plan $plan -Adapter $adapter -Role $Role -Fresh:$Fresh `
            -MaxCandidateAgeHours $MaxCandidateAgeHours -PrevRetentionDays $PrevRetentionDays)

    if ($AsJson) {
        [pscustomobject][ordered]@{ phase = $Phase; success = $true; roles = $results } | ConvertTo-Json -Depth 5 -Compress
    }
    else {
        Write-Host ''
        foreach ($result in $results) {
            $build = 'build not read'
            if ($result.guestBuild) {
                $build = "guest build $($result.guestBuild)"
            }
            Write-Host ('{0,-8} {1,-16} {2}  ({3})' -f $result.role, $result.action, $result.detail, $build) -ForegroundColor Green
        }
        Write-Host ''
        Write-Host "$Phase finished for $($results.Count) domain controller(s)." -ForegroundColor Green
    }
}
catch {
    Write-RebuildLog "Invoke-LabRebuild failed: $($_.Exception.Message)" -FileOnly
    [Console]::Error.WriteLine("Invoke-LabRebuild failed: $($_.Exception.Message)")
    exit 1
}
