# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Builds the lab's AD FS server on this Hyper-V host, unattended, as a member of a lab domain controller's forest.

.DESCRIPTION
    Creates the VM and installs Windows exactly as New-LabDomainController.ps1 does (Generation 2, production
    checkpoints only, the AVMA key), then drives the guest through Initialize-LabFederationServer.ps1 over PowerShell
    Direct: License, Prepare, Join, then on the domain controller the KDS root key, the group managed service account
    and the federation service's DNS record, then Farm and Verify. Finally it takes the 'baseline' checkpoint of both
    VMs.

    The AD FS server depends on its domain controller: its computer account, the gMSA and the KDS root key live in
    the directory. The domain controller is reverted to its checkpoints all the time, so this build:
      - starts a new server by reverting the domain controller to 'baseline', so no test data from an earlier run
        is captured;
      - when the server is built, takes the domain controller's 'baseline' again, so the accounts are in every
        checkpoint from now on;
      - removes the domain controller's 'populated-*' checkpoints, which were taken from the old baseline and lack
        the accounts (the runner takes them again on its next run).
    From then on the two are always reverted together, each to its own baseline. Do not run this while a lab run is
    in progress.

    Safe to re-run after a failed step: the VM is reused and every guest phase checks what is already done. To start
    again from nothing, run Remove-LabDomainController.ps1 -Name <server> -Force first.

    The unattend ISO contains the Administrator password (encoded, not encrypted; Windows Setup reads it from
    there). It is deleted as soon as the install has finished. Passwords are otherwise never written to disk or
    logged.

.PARAMETER Name
    The VM name, for example adfs-primary.

.PARAMETER DomainControllerName
    The lab domain controller whose forest the server joins, for example dc-primary. Its domain, NetBIOS name and
    address (the server's DNS server) are read from its Notes.

.PARAMETER IsoPath
    Path to a Windows Server 2025 ISO holding a Datacenter image (full or evaluation media).

.PARAMETER VhdDirectory
    Folder that receives the VM's files: <VhdDirectory>\<Name>\.

.PARAMETER SwitchName
    The virtual switch the server attaches to: the Internal Lab switch, beside the domain controllers.

.PARAMETER IPAddress
    Static IPv4 address for the server, which the federation service's DNS record points at.

.PARAMETER PrefixLength
    Prefix length of the subnet.

.PARAMETER Gateway
    Default gateway: the host's address on the Lab switch, which leads nowhere by design.

.PARAMETER ComputerName
    The server's host name. Default adfs1.

.PARAMETER ServiceName
    The federation service's host label. Default adfs, giving https://adfs.<domain>/adfs. It must differ from the
    computer name.

.PARAMETER AdministratorPassword
    The Administrator password, the same for the local account and the domain's. Falls back to the environment
    variable JIM_AD_LAB_ADMIN_PASSWORD.

.PARAMETER CumulativeUpdatePath
    A .msu cumulative update to install after the domain join and before the farm is created.

.PARAMETER MemoryStartupBytes
    Static memory. Default 4 GB.

.PARAMETER ProcessorCount
    Virtual processors. Default 2.

.PARAMETER VhdSizeBytes
    Size of the dynamically expanding system disk. Default 80 GB.

.PARAMETER UILanguage
    Windows UI language. Default en-US, the only one the Windows Server media carries.

.PARAMETER Locale
    System locale, user locale and keyboard. Default en-GB.

.PARAMETER TimeZone
    Windows time zone identifier. Default UTC.

.PARAMETER InstallTimeoutMinutes
    How long to wait for the unattended install to finish. Default 90.

.PARAMETER SkipBaselineCheckpoint
    Build and verify, but take no checkpoint of either VM.

.EXAMPLE
    $admin = Read-Host 'Administrator password' -AsSecureString
    .\New-LabFederationServer.ps1 -Name adfs-primary -DomainControllerName dc-primary -IsoPath D:\iso\ws2025.iso `
        -VhdDirectory D:\vm -SwitchName Lab -IPAddress 10.99.0.21 -PrefixLength 24 -Gateway 10.99.0.1 `
        -AdministratorPassword $admin
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'The parameters are read by the helper functions below, which see them through the script scope.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Name,

    [Parameter(Mandatory)]
    [string]$DomainControllerName,

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

    [string]$ComputerName = 'adfs1',

    [string]$ServiceName = 'adfs',

    [securestring]$AdministratorPassword,

    [string]$CumulativeUpdatePath,

    [uint64]$MemoryStartupBytes = 4GB,

    [ValidateRange(1, 32)]
    [int]$ProcessorCount = 2,

    [uint64]$VhdSizeBytes = 80GB,

    [string]$UILanguage = 'en-US',

    [string]$Locale = 'en-GB',

    [string]$TimeZone = 'UTC',

    [ValidateRange(10, 480)]
    [int]$InstallTimeoutMinutes = 90,

    [switch]$SkipBaselineCheckpoint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$guestDirectory = @(
    (Join-Path $PSScriptRoot 'guest'),
    (Join-Path (Join-Path $PSScriptRoot '..') 'guest')
) | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'LabFederationServer.psm1') } | Select-Object -First 1
if (-not $guestDirectory) {
    [Console]::Error.WriteLine('Cannot find guest/LabFederationServer.psm1 beside or above this script; see the layout notes in README.md.')
    exit 1
}
Import-Module (Join-Path $guestDirectory 'LabDomainController.psm1') -Force
Import-Module (Join-Path $guestDirectory 'LabFederationServer.psm1') -Force

$guestRoot = 'C:\jim-ad-lab'
$guestScript = Join-Path $guestRoot 'Initialize-LabFederationServer.ps1'

function Write-Heading {
    param([string]$Text)
    Write-Host ''
    Write-Host "=== $Text ===" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host "  $Text" -ForegroundColor Green
}

function Invoke-Phase {
    param([string]$Phase)
    Write-Heading "Phase: $Phase"
    $arguments = Get-LabFederationPhaseArgument -Phase $Phase -Settings $settings
    $result = Invoke-LabGuestPhase -VMName $Name -Credential $script:Credential -Arguments $arguments -GuestScript $guestScript
    if ($result.RebootRequired) {
        Write-Ok "Restarting after $Phase"
        $script:Credential = Restart-LabGuest -VMName $Name -Credential $credentials
    }
    return $result
}

function Wait-DomainController {
    # Waits until the domain controller answers PowerShell Direct and its directory answers the ActiveDirectory
    # module, which a freshly reverted domain controller takes a minute or two to do.
    $credential = Wait-LabGuestReady -VMName $DomainControllerName -Credential @($domainCredential) -TimeoutSeconds 900
    $deadline = (Get-Date).AddSeconds(900)
    while ($true) {
        $ready = Invoke-Command -VMName $DomainControllerName -Credential $credential -ScriptBlock {
            try { Import-Module ActiveDirectory -ErrorAction Stop -WarningAction SilentlyContinue; $null = Get-ADDomain -ErrorAction Stop; $true } catch { $false }
        }
        if ($ready) { return $credential }
        if ((Get-Date) -gt $deadline) { throw "The directory on '$DomainControllerName' did not answer within 15 minutes." }
        Start-Sleep -Seconds 10
    }
}

function Invoke-DomainPreparation {
    # On the domain controller: the KDS root key, the gMSA AD FS runs as, and the federation service's DNS record.
    # Idempotent; prints one line per change.
    param([System.Management.Automation.PSCredential]$Credential)

    $lines = Invoke-Command -VMName $DomainControllerName -Credential $Credential -ArgumentList $info.GmsaName, $info.ServiceFqdn, $info.ServiceName, $info.DnsDomain, $info.ComputerAccount, $IPAddress.ToString() -ScriptBlock {
        param($gmsaName, $serviceFqdn, $serviceName, $zone, $computerAccount, $address)
        $ErrorActionPreference = 'Stop'
        Import-Module ActiveDirectory -WarningAction SilentlyContinue
        Import-Module DnsServer

        if (-not (Get-KdsRootKey)) {
            # A KDS root key is usable ten hours after its effective time, so that every domain controller has it
            # first. The lab forest has one domain controller, so it is back-dated by ten hours, which Microsoft
            # documents for test environments:
            #   https://learn.microsoft.com/windows-server/security/group-managed-service-accounts/create-the-key-distribution-services-kds-root-key
            $null = Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))
            'Created the KDS root key (back-dated ten hours: one domain controller)'
        }

        $computer = Get-ADComputer -Identity $computerAccount.TrimEnd('$')
        $account = Get-ADServiceAccount -Filter "Name -eq '$gmsaName'" -Properties PrincipalsAllowedToRetrieveManagedPassword
        if ($null -eq $account) {
            New-ADServiceAccount -Name $gmsaName -DNSHostName $serviceFqdn -PrincipalsAllowedToRetrieveManagedPassword $computer
            "Created the group managed service account $gmsaName, retrievable by $computerAccount"
        }
        elseif (@($account.PrincipalsAllowedToRetrieveManagedPassword) -notcontains $computer.DistinguishedName) {
            Set-ADServiceAccount -Identity $gmsaName -PrincipalsAllowedToRetrieveManagedPassword $computer
            "Allowed $computerAccount to retrieve the password of $gmsaName"
        }

        $records = @(Get-DnsServerResourceRecord -ZoneName $zone -Name $serviceName -RRType A -ErrorAction SilentlyContinue)
        $right = @($records | Where-Object { $_.RecordData.IPv4Address.IPAddressToString -eq $address })
        if (($records.Count -ne 1) -or ($right.Count -ne 1)) {
            foreach ($record in $records) { Remove-DnsServerResourceRecord -ZoneName $zone -InputObject $record -Force }
            Add-DnsServerResourceRecordA -ZoneName $zone -Name $serviceName -IPv4Address $address
            "Registered $serviceFqdn at $address in DNS"
        }
    }
    foreach ($line in @($lines)) { Write-Ok $line }
}

function Invoke-Checkpoint {
    param([string]$VmName)
    # The script runs in this session and sets an exit code only when it fails, so clear whatever an earlier native
    # command left in LASTEXITCODE first; otherwise a checkpoint that was taken reads as a failure.
    $global:LASTEXITCODE = 0
    & (Join-Path $PSScriptRoot 'Checkpoint-LabDomainController.ps1') -Name $VmName -Checkpoint (Get-LabCheckpointName -Baseline) -Replace
    if ($LASTEXITCODE -ne 0) { throw "Taking the baseline checkpoint of '$VmName' failed." }
}

# --- Validate ---------------------------------------------------------------------------------

try {
    Assert-LabWindows -Feature 'Building the AD FS server'
    foreach ($candidate in $Name, $DomainControllerName) {
        if (-not (Test-LabVmName -Name $candidate)) {
            throw "'$candidate' is not a valid VM name here: letters, digits and hyphens only."
        }
    }
    if ($CumulativeUpdatePath -and -not (Test-Path -LiteralPath $CumulativeUpdatePath -PathType Leaf)) {
        throw "The cumulative update '$CumulativeUpdatePath' does not exist."
    }
    if (-not (Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue)) {
        throw "The virtual switch '$SwitchName' does not exist on this host."
    }

    $dcVm = Get-VM -Name $DomainControllerName -ErrorAction SilentlyContinue
    if ($null -eq $dcVm -or -not (Test-LabVmNote -Notes $dcVm.Notes)) {
        throw "There is no lab domain controller called '$DomainControllerName'. Build it first (New-LabDomainController.ps1)."
    }
    $dcMeta = ConvertFrom-LabVmNote -Notes $dcVm.Notes
    if ($dcMeta.Role -ne 'DomainController' -or [string]::IsNullOrEmpty($dcMeta.IPAddress)) {
        throw "'$DomainControllerName' is not a lab domain controller with an address in its Notes."
    }
    if (@(Get-VMSnapshot -VMName $DomainControllerName -Name (Get-LabCheckpointName -Baseline) -ErrorAction SilentlyContinue).Count -eq 0) {
        throw "'$DomainControllerName' has no baseline checkpoint. Finish building it first."
    }

    $info = Get-LabFederationInfo -Domain $dcMeta.Domain -NetBiosName $dcMeta.NetBiosName -ServerName $ComputerName -ServiceName $ServiceName
    $adminSecret = Resolve-LabSecret -Value $AdministratorPassword -EnvironmentVariable 'JIM_AD_LAB_ADMIN_PASSWORD'
    $unattendIso = Join-Path (Join-Path $VhdDirectory $Name) "$Name-unattend.iso"

    $localCredential = New-Object System.Management.Automation.PSCredential((Get-LabAdministratorUserName -NetBiosName $info.NetBiosName), $adminSecret)
    $domainCredential = New-Object System.Management.Automation.PSCredential((Get-LabAdministratorUserName -NetBiosName $info.NetBiosName -Promoted), $adminSecret)
    # The domain account first: once the server has joined, the local Administrator still answers PowerShell Direct but
    # cannot reach the directory, which Farm needs. Before the join only the local one works, and is tried next.
    $credentials = @($domainCredential, $localCredential)

    $settings = @{
        ComputerName          = $info.ComputerName
        IPAddress             = $IPAddress.ToString()
        PrefixLength          = $PrefixLength
        Gateway               = $Gateway.ToString()
        DnsServer             = $dcMeta.IPAddress
        Domain                = $dcMeta.Domain
        NetBiosName           = $info.NetBiosName
        ServiceName           = $info.ServiceName
        AdministratorPassword = $adminSecret
        VmName                = $Name
        LabRoot               = $guestRoot
    }

    Write-Heading "Building $Name ($($info.Fqdn), federation service $($info.ServiceUrl))"

    # --- The domain controller, for a new server -----------------------------------------------
    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $vm) {
        Write-Heading "Reverting $DomainControllerName to its baseline"
        $snapshot = Get-VMSnapshot -VMName $DomainControllerName -Name (Get-LabCheckpointName -Baseline) | Sort-Object -Property CreationTime -Descending | Select-Object -First 1
        Restore-VMSnapshot -VMSnapshot $snapshot -Confirm:$false
        if ((Get-VM -Name $DomainControllerName).State -ne 'Running') { Start-VM -Name $DomainControllerName }
        $null = Wait-DomainController
        Write-Ok "$DomainControllerName is back at its baseline and its directory answers"
    }

    # --- The VM ---------------------------------------------------------------------------------
    $installPending = $false
    if ($null -eq $vm) {
        $image = Get-LabInstallImage -IsoPath $IsoPath
        Write-Ok "Installation image: $($image.ImageName)$(if ($image.Evaluation) { ' (evaluation media: converted to full Datacenter by the License phase)' })"
        $null = New-Item -ItemType Directory -Path (Join-Path $VhdDirectory $Name) -Force
        New-LabUnattendIso -TemplatePath (Join-Path $guestDirectory 'autounattend.xml') -Image $image -Password $adminSecret `
            -ComputerName $info.ComputerName -UILanguage $UILanguage -Locale $Locale -TimeZone $TimeZone -IsoPath $unattendIso
        try {
            New-LabVirtualMachine -Name $Name -VhdDirectory $VhdDirectory -VhdSizeBytes $VhdSizeBytes -MemoryStartupBytes $MemoryStartupBytes `
                -ProcessorCount $ProcessorCount -SwitchName $SwitchName -IsoPath $IsoPath -UnattendIsoPath $unattendIso `
                -Notes (ConvertTo-LabVmNote -Domain $dcMeta.Domain -ShortName $info.ComputerName -NetBiosName $info.NetBiosName -IPAddress $IPAddress.ToString() -Role FederationServer)
        }
        catch {
            # No VM to install, so the ISO that carries the password goes at once.
            Remove-Item -LiteralPath $unattendIso -Force -ErrorAction SilentlyContinue
            throw
        }
        Write-Ok "Created $Name (Generation 2, $($MemoryStartupBytes / 1GB) GB, $ProcessorCount vCPU, production checkpoints only)"
        $installPending = $true
    }
    else {
        $meta = ConvertFrom-LabVmNote -Notes $vm.Notes
        if ($null -eq $meta -or $meta.Role -ne 'FederationServer') {
            throw "A VM called '$Name' exists but is not this lab's federation server; refusing to touch it."
        }
        Write-Ok "$Name already exists; continuing from where it stands."
        $installPending = [bool](Get-VMDvdDrive -VMName $Name | Where-Object { $_.Path -like '*-unattend.iso' })
    }

    # As for the domain controllers: Time Synchronization so a revert boots with the host's clock, Data Exchange for
    # AVMA, VSS for production checkpoints.
    Enable-VMIntegrationService -VMName $Name -Name 'Time Synchronization', 'Heartbeat', 'Key-Value Pair Exchange', 'Shutdown', 'VSS'

    if ((Get-VM -Name $Name).State -ne 'Running') {
        Start-VM -Name $Name
        if ($installPending) {
            # Windows media asks "Press any key to boot from CD or DVD" and gives up if nobody does.
            Send-LabKeyPress -VMName $Name -Count 15
        }
    }

    if ($installPending) {
        Write-Heading "Waiting for the unattended install (up to $InstallTimeoutMinutes minutes)"
    }
    $guestTimeout = if ($installPending) { $InstallTimeoutMinutes * 60 } else { 600 }
    $script:Credential = Wait-LabGuestReady -VMName $Name -Credential $credentials -TimeoutSeconds $guestTimeout
    Write-Ok "The guest answers PowerShell Direct as $($script:Credential.UserName)"

    if ($installPending) {
        Complete-LabUnattendedInstall -Name $Name -UnattendIsoPath $unattendIso
        Write-Ok 'Ejected the install media and deleted the unattend ISO'
    }

    # --- Drive the guest ------------------------------------------------------------------------
    Write-Heading 'Copying the lab files into the guest'
    Copy-LabAssetToGuest -VMName $Name -Credential $script:Credential -Destination $guestRoot -Path @(
        (Join-Path $guestDirectory 'Initialize-LabFederationServer.ps1'),
        (Join-Path $guestDirectory 'LabDomainController.psm1'),
        (Join-Path $guestDirectory 'LabFederationServer.psm1')
    )

    foreach ($phase in 'License', 'Prepare', 'Join') {
        $null = Invoke-Phase -Phase $phase
    }

    if ($CumulativeUpdatePath) {
        Write-Heading 'Installing the cumulative update'
        Copy-LabAssetToGuest -VMName $Name -Credential $script:Credential -Destination $guestRoot -Path @($CumulativeUpdatePath)
        $guestUpdate = Join-Path $guestRoot (Split-Path $CumulativeUpdatePath -Leaf)
        # DISM, not wusa, which refuses a remote (network-logon) session such as PowerShell Direct.
        Invoke-Command -VMName $Name -Credential $script:Credential -ArgumentList $guestUpdate -ScriptBlock {
            param($package)
            $null = Add-WindowsPackage -Online -PackagePath $package -NoRestart
            Remove-Item -LiteralPath $package -Force
        }
        Write-Ok 'Installed; restarting'
        $script:Credential = Restart-LabGuest -VMName $Name -Credential $credentials
    }

    Write-Heading "On $DomainControllerName`: KDS root key, service account and DNS"
    Invoke-DomainPreparation -Credential (Wait-DomainController)

    $null = Invoke-Phase -Phase 'Farm'
    $verify = Invoke-Phase -Phase 'Verify'
    Write-Ok $verify.Detail

    if (-not (Get-VMIntegrationService -VMName $Name -Name 'Time Synchronization').Enabled) {
        throw 'The Time Synchronization integration service is disabled; a production-checkpoint revert would not boot with the host clock.'
    }
    $build = Get-LabGuestOsBuild -VMName $Name -Credential $script:Credential
    Write-Ok "Guest OS build: $($build.buildString) ($($build.productName) $($build.displayVersion))"

    # --- Baselines ------------------------------------------------------------------------------
    if ($SkipBaselineCheckpoint) {
        Write-Host 'Skipping the baseline checkpoints as requested.' -ForegroundColor Yellow
    }
    else {
        Write-Heading "Taking the baseline checkpoints of $Name and $DomainControllerName"
        Start-Sleep -Seconds 30
        Invoke-Checkpoint -VmName $Name
        Invoke-Checkpoint -VmName $DomainControllerName

        # Taken from the old baseline, so they lack the server's computer account and the gMSA. The runner takes
        # them again on its next run.
        foreach ($stale in @(Get-VMSnapshot -VMName $DomainControllerName | Where-Object { $_.Name -like 'populated-*' })) {
            Remove-VMSnapshot -VMSnapshot $stale -Confirm:$false
            Write-Ok "Removed $DomainControllerName's '$($stale.Name)', taken before $Name joined"
        }
    }

    Write-Heading 'Done'
    Write-Ok "$Name is built: $($info.Fqdn) at $IPAddress, federation service $($info.ServiceUrl), OS build $($build.buildString)."
    Write-Ok "From now on revert $DomainControllerName and $Name together, each to its own baseline."
}
catch {
    [Console]::Error.WriteLine("New-LabFederationServer failed: $($_.Exception.Message)")
    exit 1
}
