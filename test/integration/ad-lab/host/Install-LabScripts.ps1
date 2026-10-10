# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Deploys the Active Directory lab's host scripts from a JIM checkout to the Hyper-V host.

.DESCRIPTION
    The runner calls <JIM_AD_LAB_SCRIPT_ROOT>\<script>.ps1 over SSH (default C:\jim-ad-lab), so the control-plane
    scripts have to sit in a folder on the host, in the deployed layout that Resolve-LabLayout in
    guest/LabDomainController.psm1 expects:

        <Destination>\*.ps1                            the host scripts, flat
        <Destination>\guest\*                          the guest scripts, module and autounattend.xml
        <Destination>\delegation\jim-ad-delegation.acl the delegation file, the same one the Samba image uses
        <Destination>\settings.example.json            a starting point for settings.json

    Run it from a clone of the repository, in an elevated PowerShell 7 session on the host. It is idempotent: a file
    already identical to its source is left alone, a changed one is replaced, and the output says which. Nothing in
    the destination is ever deleted.

    settings.json (read by Invoke-LabRebuild.ps1) is the operator's own file. It is never written unless
    -CreateSettings is given, and then only when it does not exist yet; an existing settings.json makes the run
    refuse before anything is copied. settings.example.json is rewritten on every run, so it always matches the
    keys the scripts of this checkout read. It holds placeholders, never a secret; the passwords live in the
    jim-lab account's environment (see README.md).

    Host prerequisites (Hyper-V, OpenSSH, the jim-lab account, the switches) are the host's own concern and live in
    the ci-runners repository, docs/hyperv-host.md. This script only places JIM's own scripts.

.PARAMETER Destination
    Where the scripts go. Default C:\jim-ad-lab, which is the runner's default JIM_AD_LAB_SCRIPT_ROOT.

.PARAMETER RepositoryRoot
    The JIM checkout to copy from. Defaults to the checkout this script is in.

.PARAMETER CreateSettings
    Also create settings.json from the example when it does not exist. Refuses, and copies nothing, when it does.

.EXAMPLE
    .\Install-LabScripts.ps1

.EXAMPLE
    .\Install-LabScripts.ps1 -CreateSettings
    notepad C:\jim-ad-lab\settings.json
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[CmdletBinding()]
param(
    [string]$Destination = 'C:\jim-ad-lab',

    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..' '..' '..' '..'),

    [switch]$CreateSettings
)

function Get-LabDeploymentMap {
    <#
    .SYNOPSIS
        Maps repository files onto their place in the deployed layout. Pure: reads no file system.

    .DESCRIPTION
        HostScript holds file names from test/integration/ad-lab/host; GuestFile holds paths relative to
        test/integration/ad-lab/guest. Only scripts and modules (.ps1, .psm1, .psd1) are host scripts, and this
        deployment script is not one of them: it runs from the checkout, not from the host's script root. The
        delegation file is always mapped. Returns one object per file with Area, Source and Destination.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$Destination,

        [AllowEmptyCollection()]
        [string[]]$HostScript = @(),

        [AllowEmptyCollection()]
        [string[]]$GuestFile = @()
    )

    $labRoot = Join-Path $RepositoryRoot 'test' 'integration' 'ad-lab'
    $map = [System.Collections.Generic.List[object]]::new()

    foreach ($name in ($HostScript | Sort-Object)) {
        if ($name -notmatch '\.(ps1|psm1|psd1)$') { continue }
        if ($name -eq 'Install-LabScripts.ps1') { continue }
        $map.Add([pscustomobject]@{
            Area        = 'host'
            Source      = Join-Path $labRoot 'host' $name
            Destination = Join-Path $Destination $name
        })
    }

    foreach ($relative in ($GuestFile | Sort-Object)) {
        $map.Add([pscustomobject]@{
            Area        = 'guest'
            Source      = Join-Path $labRoot 'guest' $relative
            Destination = Join-Path (Join-Path $Destination 'guest') $relative
        })
    }

    $map.Add([pscustomobject]@{
        Area        = 'delegation'
        Source      = Join-Path $RepositoryRoot 'test' 'integration' 'docker' 'samba-ad-prebuilt' 'delegation' 'jim-ad-delegation.acl'
        Destination = Join-Path (Join-Path $Destination 'delegation') 'jim-ad-delegation.acl'
    })

    return $map.ToArray()
}

function Get-LabSettingsExample {
    <#
    .SYNOPSIS
        The text of settings.example.json: the keys Invoke-LabRebuild.ps1 reads, with placeholder values.

    .DESCRIPTION
        Pure. The addresses are placeholders in the lab's range, and the paths are examples; the operator replaces
        them. There is no secret here, by design.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()

    # 10.99.0.1 is the host's own address on the Internal Lab switch. It serves NTP to the domain controllers
    # (ntpServer) and is their gateway, which leads nowhere by design: the switch has no uplink. There is no
    # dnsForwarder, because there is nothing to forward to.
    $settings = [ordered]@{
        isoPath                   = 'D:\media\WindowsServer2025.iso'
        cumulativeUpdateDirectory = 'D:\media\updates'
        vhdDirectory              = 'D:\Hyper-V\Virtual Hard Disks'
        switchName                = 'Lab'
        ntpServer                 = '10.99.0.1'
        # Applied to every forest, in this order (Extend phase). Exchange: the full organisation, named after the forest.
        directoryExtensions       = @([ordered]@{ product = 'Exchange'; isoPath = 'D:\media\ExchangeServerSE-x64.iso' })
        domainControllers         = [ordered]@{
            'dc-primary' = [ordered]@{ domain = 'PANOPLY.LOCAL'; ipAddress = '10.99.0.11'; prefixLength = 24; gateway = '10.99.0.1'; enableRecycleBin = $true }
            'dc-source'  = [ordered]@{ domain = 'RESURGAM.LOCAL'; ipAddress = '10.99.0.12'; prefixLength = 24; gateway = '10.99.0.1'; enableRecycleBin = $false }
            'dc-target'  = [ordered]@{ domain = 'GENTIAN.LOCAL'; ipAddress = '10.99.0.13'; prefixLength = 24; gateway = '10.99.0.1'; enableRecycleBin = $false }
        }
    }

    return ($settings | ConvertTo-Json -Depth 5)
}

# Dot-sourced (by the tests), the functions above are all that is wanted.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Heading {
    param([string]$Text)
    Write-Host ''
    Write-Host "=== $Text" -ForegroundColor Cyan
}

$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$Destination = [System.IO.Path]::GetFullPath($Destination)
$labRoot = Join-Path $RepositoryRoot 'test' 'integration' 'ad-lab'
$hostDirectory = Join-Path $labRoot 'host'
$guestDirectory = Join-Path $labRoot 'guest'

if (-not ((Test-Path -LiteralPath $hostDirectory -PathType Container) -and (Test-Path -LiteralPath $guestDirectory -PathType Container))) {
    throw "'$RepositoryRoot' is not a JIM checkout: expected $hostDirectory and $guestDirectory. Pass -RepositoryRoot."
}

$hostScripts = @(Get-ChildItem -LiteralPath $hostDirectory -File | ForEach-Object { $_.Name })
$guestFiles = @(Get-ChildItem -LiteralPath $guestDirectory -File -Recurse | ForEach-Object {
        [System.IO.Path]::GetRelativePath($guestDirectory, $_.FullName)
    })
$map = Get-LabDeploymentMap -RepositoryRoot $RepositoryRoot -Destination $Destination -HostScript $hostScripts -GuestFile $guestFiles

$missing = @($map | Where-Object { -not (Test-Path -LiteralPath $_.Source -PathType Leaf) })
if ($missing.Count -gt 0) {
    throw "The checkout at '$RepositoryRoot' is missing: $(($missing | ForEach-Object { $_.Source }) -join '; ')"
}

$settingsPath = Join-Path $Destination 'settings.json'
$examplePath = Join-Path $Destination 'settings.example.json'
if ($CreateSettings -and (Test-Path -LiteralPath $settingsPath)) {
    throw "Refusing to overwrite the existing $settingsPath. Nothing was copied. Edit it by hand, or run without -CreateSettings to redeploy the scripts only."
}

Write-Heading "Deploying the lab scripts from $RepositoryRoot to $Destination"
$counts = @{ new = 0; updated = 0; unchanged = 0 }
foreach ($entry in $map) {
    $targetDirectory = Split-Path -Parent $entry.Destination
    if (-not (Test-Path -LiteralPath $targetDirectory)) {
        $null = New-Item -ItemType Directory -Path $targetDirectory -Force
    }

    $outcome = 'new'
    if (Test-Path -LiteralPath $entry.Destination) {
        $same = (Get-FileHash -LiteralPath $entry.Source -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $entry.Destination -Algorithm SHA256).Hash
        $outcome = if ($same) { 'unchanged' } else { 'updated' }
    }
    if ($outcome -ne 'unchanged') {
        Copy-Item -LiteralPath $entry.Source -Destination $entry.Destination -Force
    }
    $counts[$outcome]++
    Write-Host ('  {0,-9} {1}' -f $outcome, [System.IO.Path]::GetRelativePath($Destination, $entry.Destination))
}

Write-Heading 'Settings'
$exampleText = Get-LabSettingsExample
Set-Content -LiteralPath $examplePath -Value $exampleText -Encoding utf8
Write-Host "  rewritten  settings.example.json"

if ($CreateSettings) {
    Set-Content -LiteralPath $settingsPath -Value $exampleText -Encoding utf8
    Write-Host "  created    settings.json (placeholders: edit every value before use)" -ForegroundColor Yellow
}
elseif (-not (Test-Path -LiteralPath $settingsPath)) {
    Write-Host "  settings.json does not exist yet. Copy settings.example.json to settings.json and edit it (or re-run with -CreateSettings)." -ForegroundColor Yellow
}
else {
    Write-Host "  settings.json left untouched"
}

Write-Heading 'Done'
Write-Host ("  {0} new, {1} updated, {2} unchanged, in {3}" -f $counts['new'], $counts['updated'], $counts['unchanged'], $Destination)
Write-Host "  The runner reaches these through JIM_AD_LAB_SCRIPT_ROOT=$Destination"
