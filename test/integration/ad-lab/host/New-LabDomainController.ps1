# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Builds one lab domain controller on this Hyper-V host, unattended, from Windows Server 2025 installation media.

.DESCRIPTION
    Creates a Generation 2 VM (UEFI, Secure Boot, production checkpoints only), attaches the Windows ISO and a
    small second ISO carrying autounattend.xml, installs unattended with the Automatic Virtual Machine Activation
    key, then drives the guest through PowerShell Direct (Invoke-Command -VMName; no network or guest listener is
    needed): it copies the guest files into C:\jim-ad-lab, runs Initialize-LabDomainController.ps1 phase by phase
    across restarts (Prepare, Promote, Configure, Verify), installs the cumulative update if one was given, and
    finally takes the 'baseline' checkpoint.

    Safe to re-run after a failed step. If the VM exists the create step is skipped, and every guest phase checks
    whether it has already happened, so the sequence converges. To start again from nothing, run
    Remove-LabDomainController.ps1 -Force first.

    The unattend ISO contains the Administrator password (encoded, not encrypted; unavoidable, Windows Setup
    reads it from there). It is built in a temporary folder, detached and deleted as soon as the install has
    finished. Passwords are otherwise never written to disk or logged.

    Host prerequisites are in test/integration/ad-lab/README.md.

.PARAMETER Name
    The VM name, for example dc-primary, dc-source or dc-target.

.PARAMETER Domain
    The forest root domain, for example PANOPLY.LOCAL. The domain controller is <ComputerName>.<domain>.

.PARAMETER IsoPath
    Path to a NON-evaluation Windows Server 2025 ISO (an evaluation domain controller cannot be converted, and its
    timer runs in real time regardless of checkpoint reverts).

.PARAMETER VhdDirectory
    Folder that receives the VM's files: <VhdDirectory>\<Name>\.

.PARAMETER SwitchName
    The external virtual switch on the CI VLAN.

.PARAMETER IPAddress
    Static IPv4 address for the domain controller.

.PARAMETER PrefixLength
    Prefix length of the subnet.

.PARAMETER Gateway
    Default gateway.

.PARAMETER DnsForwarder
    DNS server the domain controller forwards to.

.PARAMETER NtpServer
    NTP source for w32time; use the same one the host and the runner use.

.PARAMETER AdministratorPassword
    Administrator (and Directory Services Restore Mode) password, as a SecureString. Falls back to the
    environment variable JIM_AD_LAB_ADMIN_PASSWORD.

.PARAMETER ServiceAccountPassword
    The svc-jim password, as a SecureString. Falls back to the environment variable JIM_AD_LAB_JIM_PASSWORD.

.PARAMETER EnableRecycleBin
    Enable the Active Directory Recycle Bin (irreversible). dc-primary has it on; dc-source and dc-target do not.

.PARAMETER ExtraCertificateNames
    Extra DNS names for the LDAPS certificate, in addition to the FQDN, the short name and the domain.

.PARAMETER CumulativeUpdatePath
    A .msu cumulative update to install (from inside the guest, by DISM) after promotion and before configuration.

.PARAMETER MemoryStartupBytes
    Static memory. Default 4 GB.

.PARAMETER ProcessorCount
    Virtual processors. Default 2.

.PARAMETER VhdSizeBytes
    Size of the dynamically expanding system disk. Default 80 GB.

.PARAMETER ComputerName
    The domain controller's host name. Default dc1, giving dc1.panoply.local and so on.

.PARAMETER NetBiosName
    NetBIOS domain name; defaults to the first label of the domain.

.PARAMETER UILanguage
    Windows UI language. The Windows Server media carries only en-US, and Setup fails on a language it does not
    hold, so the default is en-US; formats, system locale and keyboard are set from -Locale.

.PARAMETER Locale
    System locale, user locale and keyboard. Default en-GB.

.PARAMETER TimeZone
    Windows time zone identifier. Default UTC.

.PARAMETER InstallTimeoutMinutes
    How long to wait for the unattended install to finish. Default 90.

.PARAMETER SkipBaselineCheckpoint
    Build and verify, but do not take the baseline checkpoint.

.EXAMPLE
    $admin = Read-Host 'Administrator password' -AsSecureString
    $jim = Read-Host 'svc-jim password' -AsSecureString
    .\New-LabDomainController.ps1 -Name dc-primary -Domain PANOPLY.LOCAL -IsoPath D:\iso\ws2025.iso -VhdDirectory D:\vm `
        -SwitchName CI-VLAN -IPAddress 10.20.30.41 -PrefixLength 24 -Gateway 10.20.30.1 -DnsForwarder 10.20.30.2 `
        -NtpServer 10.20.30.2 -AdministratorPassword $admin -ServiceAccountPassword $jim -EnableRecycleBin `
        -ExtraCertificateNames samba-ad-primary

.EXAMPLE
    .\New-LabDomainController.ps1 -Name dc-source -Domain RESURGAM.LOCAL -IsoPath D:\iso\ws2025.iso -VhdDirectory D:\vm `
        -SwitchName CI-VLAN -IPAddress 10.20.30.42 -PrefixLength 24 -Gateway 10.20.30.1 -DnsForwarder 10.20.30.2 `
        -NtpServer 10.20.30.2

    With the passwords in JIM_AD_LAB_ADMIN_PASSWORD and JIM_AD_LAB_JIM_PASSWORD.

.EXAMPLE
    .\New-LabDomainController.ps1 -Name dc-target -Domain GENTIAN.LOCAL -IsoPath D:\iso\ws2025.iso -VhdDirectory D:\vm `
        -SwitchName CI-VLAN -IPAddress 10.20.30.43 -PrefixLength 24 -Gateway 10.20.30.1 -DnsForwarder 10.20.30.2 `
        -NtpServer 10.20.30.2 -CumulativeUpdatePath D:\updates\windows-server-2025-latest.msu
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'The parameters are read by the helper functions below, which see them through the script scope.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Name,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$')]
    [string]$Domain,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$IsoPath,

    [Parameter(Mandatory)]
    [string]$VhdDirectory,

    [Parameter(Mandatory)]
    [string]$SwitchName,

    [Parameter(Mandatory)]
    [System.Net.IPAddress]$IPAddress,

    [Parameter(Mandatory)]
    [ValidateRange(8, 30)]
    [int]$PrefixLength,

    [Parameter(Mandatory)]
    [System.Net.IPAddress]$Gateway,

    [Parameter(Mandatory)]
    [System.Net.IPAddress]$DnsForwarder,

    [Parameter(Mandatory)]
    [string]$NtpServer,

    [securestring]$AdministratorPassword,

    [securestring]$ServiceAccountPassword,

    [switch]$EnableRecycleBin,

    [string[]]$ExtraCertificateNames = @(),

    [string]$CumulativeUpdatePath,

    [uint64]$MemoryStartupBytes = 4GB,

    [ValidateRange(1, 32)]
    [int]$ProcessorCount = 2,

    [uint64]$VhdSizeBytes = 80GB,

    [string]$ComputerName = 'dc1',

    [string]$NetBiosName,

    [string]$UILanguage = 'en-US',

    [string]$Locale = 'en-GB',

    [string]$TimeZone = 'UTC',

    [ValidateRange(10, 480)]
    [int]$InstallTimeoutMinutes = 90,

    [switch]$SkipBaselineCheckpoint
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

$guestRoot = 'C:\jim-ad-lab'

function Write-Heading {
    param([string]$Text)
    Write-Host ''
    Write-Host "=== $Text ===" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host "  $Text" -ForegroundColor Green
}

function Get-InstallImageName {
    # Reads the image names off the media's install.wim and picks the Datacenter (Desktop Experience) one.
    $mounted = Mount-DiskImage -ImagePath $IsoPath -PassThru
    try {
        $drive = ($mounted | Get-Volume).DriveLetter
        $wim = "${drive}:\sources\install.wim"
        if (-not (Test-Path -LiteralPath $wim)) {
            $wim = "${drive}:\sources\install.esd"
        }
        $images = @(Get-WindowsImage -ImagePath $wim)
        return (Resolve-LabInstallImage -Image $images).ImageName
    }
    finally {
        $null = Dismount-DiskImage -ImagePath $IsoPath
    }
}

function Invoke-UnattendIsoBuild {
    param([string]$ImageName, [securestring]$Password, [string]$IsoOutputPath)

    $template = Get-Content -LiteralPath (Join-Path $layout.GuestDirectory 'autounattend.xml') -Raw
    $values = @{
        COMPUTER_NAME              = $info.ShortName
        ADMIN_PASSWORD_ENCODED     = (ConvertTo-LabUnattendPassword -Password $Password -Kind AdministratorPassword)
        AUTOLOGON_PASSWORD_ENCODED = (ConvertTo-LabUnattendPassword -Password $Password -Kind Password)
        IMAGE_NAME                 = $ImageName
        PRODUCT_KEY                = (Get-LabAvmaKey -Edition Datacenter)
        UI_LANGUAGE                = $UILanguage
        LOCALE                     = $Locale
        TIME_ZONE                  = $TimeZone
    }
    $xml = ConvertTo-LabUnattendXml -Template $template -Values $values

    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ('jim-ad-lab-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $staging
    try {
        [System.IO.File]::WriteAllText((Join-Path $staging 'autounattend.xml'), $xml, (New-Object System.Text.UTF8Encoding($false)))
        New-LabIsoImage -SourceDirectory $staging -IsoPath $IsoOutputPath -VolumeLabel 'UNATTEND'
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-VmCreation {
    param([string]$ImageName, [securestring]$Password)

    $vmFolder = Join-Path $VhdDirectory $Name
    $vhdPath = Join-Path $vmFolder "$Name.vhdx"
    if (Test-Path -LiteralPath $vhdPath) {
        throw "The disk '$vhdPath' exists but there is no VM called '$Name'. Remove it, or use a different -VhdDirectory."
    }
    $null = New-Item -ItemType Directory -Path $vmFolder -Force
    $null = New-VHD -Path $vhdPath -SizeBytes $VhdSizeBytes -Dynamic

    $null = New-VM -Name $Name -Generation 2 -MemoryStartupBytes $MemoryStartupBytes -VHDPath $vhdPath -SwitchName $SwitchName -Path $VhdDirectory
    Set-VM -Name $Name -StaticMemory -ProcessorCount $ProcessorCount -AutomaticCheckpointsEnabled $false `
        -CheckpointType ProductionOnly -AutomaticStartAction Nothing -AutomaticStopAction ShutDown `
        -Notes (ConvertTo-LabVmNote -Domain $Domain -ShortName $info.ShortName -NetBiosName $info.NetBiosName -IPAddress $IPAddress.ToString())

    # Secure Boot with the default (Microsoft Windows) template, which is what a Windows guest wants.
    Set-VMFirmware -VMName $Name -EnableSecureBoot On

    $windowsDrive = Add-VMDvdDrive -VMName $Name -Path $IsoPath -Passthru
    Invoke-UnattendIsoBuild -ImageName $ImageName -Password $Password -IsoOutputPath $script:UnattendIso
    $null = Add-VMDvdDrive -VMName $Name -Path $script:UnattendIso
    Set-VMFirmware -VMName $Name -FirstBootDevice $windowsDrive
    Write-Ok "Created $Name (Generation 2, $($MemoryStartupBytes / 1GB) GB, $ProcessorCount vCPU, production checkpoints only)"
}

# --- Validate ---------------------------------------------------------------------------------

try {
    Assert-LabWindows -Feature 'Building a domain controller'
    if (-not (Test-LabVmName -Name $Name)) {
        throw "'$Name' is not a valid VM name here: letters, digits and hyphens only."
    }
    if ($CumulativeUpdatePath -and -not (Test-Path -LiteralPath $CumulativeUpdatePath -PathType Leaf)) {
        throw "The cumulative update '$CumulativeUpdatePath' does not exist."
    }
    if (-not (Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue)) {
        throw "The virtual switch '$SwitchName' does not exist on this host."
    }

    $layout = Resolve-LabLayout -ScriptRoot $PSScriptRoot
    $info = Get-LabDomainInfo -Domain $Domain -ShortName $ComputerName -NetBiosName $NetBiosName
    $adminSecret = Resolve-LabSecret -Value $AdministratorPassword -EnvironmentVariable 'JIM_AD_LAB_ADMIN_PASSWORD'
    $jimSecret = Resolve-LabSecret -Value $ServiceAccountPassword -EnvironmentVariable 'JIM_AD_LAB_JIM_PASSWORD'
    $script:UnattendIso = Join-Path (Join-Path $VhdDirectory $Name) "$Name-unattend.iso"

    $localCredential = New-Object System.Management.Automation.PSCredential((Get-LabAdministratorUserName -NetBiosName $info.NetBiosName), $adminSecret)
    $domainCredential = New-Object System.Management.Automation.PSCredential((Get-LabAdministratorUserName -NetBiosName $info.NetBiosName -Promoted), $adminSecret)
    $credentials = @($localCredential, $domainCredential)

    Write-Heading "Building $Name ($($info.Fqdn))"

    # --- The VM ---------------------------------------------------------------------------------
    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    $installPending = $false
    if ($null -eq $vm) {
        $imageName = Get-InstallImageName
        Write-Ok "Installation image: $imageName"
        Invoke-VmCreation -ImageName $imageName -Password $adminSecret
        $installPending = $true
    }
    else {
        if (-not (Test-LabVmNote -Notes $vm.Notes)) {
            throw "A VM called '$Name' exists but was not created by this lab (its Notes carry no lab marker); refusing to touch it."
        }
        Write-Ok "$Name already exists; continuing from where it stands."
        $installPending = [bool](Get-VMDvdDrive -VMName $Name | Where-Object { $_.Path -like '*-unattend.iso' })
        if ((Get-VM -Name $Name).CheckpointType -ne 'ProductionOnly') {
            Set-VM -Name $Name -CheckpointType ProductionOnly
        }
    }

    # Time Synchronization stays ENABLED so a production-checkpoint revert boots with the host's clock. Data Exchange is
    # what AVMA needs; VSS is what production checkpoints need.
    Enable-VMIntegrationService -VMName $Name -Name 'Time Synchronization', 'Heartbeat', 'Key-Value Pair Exchange', 'Shutdown', 'VSS'

    if ((Get-VM -Name $Name).State -ne 'Running') {
        Start-VM -Name $Name
        if ($installPending) {
            # Windows media asks "Press any key to boot from CD or DVD" and gives up if nobody does.
            Send-LabKeyPress -VMName $Name -Count 15
        }
    }

    # --- Wait for the install -------------------------------------------------------------------
    if ($installPending) {
        Write-Heading "Waiting for the unattended install (up to $InstallTimeoutMinutes minutes)"
    }
    $guestTimeout = if ($installPending) { $InstallTimeoutMinutes * 60 } else { 600 }
    $credential = Wait-LabGuestReady -VMName $Name -Credential $credentials -TimeoutSeconds $guestTimeout
    Write-Ok "The guest answers PowerShell Direct as $($credential.UserName)"

    if ($installPending) {
        # The install has finished: eject the media and delete the ISO that carries the password.
        foreach ($drive in @(Get-VMDvdDrive -VMName $Name)) {
            Set-VMDvdDrive -VMName $Name -ControllerNumber $drive.ControllerNumber -ControllerLocation $drive.ControllerLocation -Path $null
        }
        Remove-Item -LiteralPath $script:UnattendIso -Force -ErrorAction SilentlyContinue
        $disk = Get-VMHardDiskDrive -VMName $Name | Select-Object -First 1
        Set-VMFirmware -VMName $Name -FirstBootDevice $disk
        Write-Ok 'Ejected the install media and deleted the unattend ISO'
    }

    # --- Drive the guest ------------------------------------------------------------------------
    Write-Heading 'Copying the lab files into the guest'
    Copy-LabAssetToGuest -VMName $Name -Credential $credential -Destination $guestRoot -Path @(
        (Join-Path $layout.GuestDirectory 'Initialize-LabDomainController.ps1'),
        $layout.ModulePath,
        $layout.DelegationAclPath
    )

    $settings = @{
        ComputerName           = $info.ShortName
        Domain                 = $Domain
        NetBiosName            = $info.NetBiosName
        IPAddress              = $IPAddress.ToString()
        PrefixLength           = $PrefixLength
        Gateway                = $Gateway.ToString()
        DnsForwarder           = $DnsForwarder.ToString()
        NtpServer              = $NtpServer
        SafeModePassword       = $adminSecret
        ServiceAccountPassword = $jimSecret
        EnableRecycleBin       = [bool]$EnableRecycleBin
        ExtraCertificateNames  = @($ExtraCertificateNames)
        VmName                 = $Name
        LabRoot                = $guestRoot
    }

    foreach ($phase in @('Prepare', 'Promote')) {
        Write-Heading "Phase: $phase"
        $result = Invoke-LabGuestPhase -VMName $Name -Credential $credential -Phase $phase -Settings $settings
        if ($result.RebootRequired) {
            Write-Ok "Restarting after $phase"
            $credential = Restart-LabGuest -VMName $Name -Credential $credentials
        }
    }

    if ($CumulativeUpdatePath) {
        Write-Heading 'Installing the cumulative update'
        Copy-LabAssetToGuest -VMName $Name -Credential $credential -Destination $guestRoot -Path @($CumulativeUpdatePath)
        $guestUpdate = Join-Path $guestRoot (Split-Path $CumulativeUpdatePath -Leaf)
        # DISM, not wusa: the Windows Update Standalone Installer refuses to run from a remote (network-logon) session
        # with access denied, which PowerShell Direct is; DISM installs the same .msu.
        Invoke-Command -VMName $Name -Credential $credential -ArgumentList $guestUpdate -ScriptBlock {
            param($package)
            $null = Add-WindowsPackage -Online -PackagePath $package -NoRestart
            Remove-Item -LiteralPath $package -Force
        }
        Write-Ok 'Installed; restarting'
        $credential = Restart-LabGuest -VMName $Name -Credential $credentials
    }

    Write-Heading 'Phase: Configure'
    $null = Invoke-LabGuestPhase -VMName $Name -Credential $credential -Phase 'Configure' -Settings $settings
    # The certificate and the time source are cleanest on a fresh boot, so Configure is always followed by a restart.
    $credential = Restart-LabGuest -VMName $Name -Credential $credentials

    Write-Heading 'Phase: Verify'
    $null = Invoke-LabGuestPhase -VMName $Name -Credential $credential -Phase 'Verify' -Settings $settings

    if (-not (Get-VMIntegrationService -VMName $Name -Name 'Time Synchronization').Enabled) {
        throw 'The Time Synchronization integration service is disabled; a production-checkpoint revert would not boot with the host clock.'
    }

    $build = Get-LabGuestOsBuild -VMName $Name -Credential $credential
    Write-Ok "Guest OS build: $($build.buildString) ($($build.productName) $($build.displayVersion))"

    # --- Baseline -------------------------------------------------------------------------------
    if ($SkipBaselineCheckpoint) {
        Write-Host 'Skipping the baseline checkpoint as requested.' -ForegroundColor Yellow
    }
    else {
        Write-Heading 'Taking the baseline checkpoint'
        # Let the directory settle after the restart before capturing it.
        Start-Sleep -Seconds 30
        & (Join-Path $PSScriptRoot 'Checkpoint-LabDomainController.ps1') -Name $Name -Checkpoint (Get-LabCheckpointName -Baseline) -Replace
        if ($LASTEXITCODE -ne 0) { throw 'Taking the baseline checkpoint failed.' }
    }

    Write-Heading 'Done'
    Write-Ok "$Name is built: $($info.Fqdn) at $IPAddress, OS build $($build.buildString)."
}
catch {
    [Console]::Error.WriteLine("New-LabDomainController failed: $($_.Exception.Message)")
    exit 1
}
