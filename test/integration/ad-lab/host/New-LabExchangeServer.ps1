# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Builds one of the lab's Exchange servers on this Hyper-V host, unattended: an Exchange Server SE Mailbox server,
    a member of a lab domain controller's forest (#2412).

.DESCRIPTION
    Creates the VM and installs Windows exactly as New-LabFederationServer.ps1 does (Generation 2, production
    checkpoints only, the AVMA key), then drives the guest through Initialize-LabExchangeServer.ps1 over PowerShell
    Direct: License, Prepare, Join, Prerequisites, Install, Configure and Verify. Finally it takes the 'baseline'
    checkpoint of both VMs and, unless -LeaveRunning, shuts the server down: the lab runs it only for the scenarios
    that use Exchange.

    The forest must already carry the Exchange organisation, prepared by its domain controller's build
    (settings.json directoryExtensions) from the same media this server installs from. This build never prepares a
    forest; the guest refuses one prepared to another schema version.

    The prerequisites that are not on the Exchange media (the Visual C++ 2012 and 2013 runtimes and IIS URL Rewrite)
    are read from -PrerequisiteDirectory, where README.md says how to download them. Each must carry a valid Microsoft
    Corporation signature, or the build stops before copying anything into the guest.

    An Exchange server depends on its domain controller: its computer account, its server object, its databases and
    every recipient live in the directory, while the mailboxes themselves live on the server. So, as with the AD FS
    server, this build:
      - starts a new server by reverting the domain controller to 'baseline', so no test data from an earlier run
        is captured;
      - when the server is built, takes the domain controller's 'baseline' again, so the server's objects are in every
        checkpoint from now on;
      - removes the domain controller's 'populated-*' checkpoints, which were taken from the old baseline (the runner
        takes them again on its next run).
    From then on the two are reverted together, each to its own baseline, before any scenario that creates or changes
    recipients in that forest. Do not run this while a lab run is in progress.

    It reports how long each phase took and the host's CPU use during Install and Verify, which #2412 asks to be
    recorded: the host has 8 logical processors for every VM.

    Safe to re-run after a failed step: the VM is reused and every guest phase checks what is already done. To start
    again from nothing, run Remove-LabDomainController.ps1 -Name <server> -Force first.

    The unattend ISO contains the Administrator password (encoded, not encrypted; Windows Setup reads it from
    there). It is deleted as soon as the install has finished. Passwords are otherwise never written to disk or
    logged.

.PARAMETER Name
    The VM name, for example exchange-primary.

.PARAMETER DomainControllerName
    The lab domain controller whose forest the server joins, for example dc-primary. Its domain, NetBIOS name and
    address (the server's DNS server) are read from its Notes.

.PARAMETER IsoPath
    Path to a Windows Server 2025 ISO holding a Datacenter image (full or evaluation media).

.PARAMETER ExchangeIsoPath
    The Exchange Server SE ISO, the media the forest was prepared with (settings.json directoryExtensions).

.PARAMETER PrerequisiteDirectory
    The folder holding vcredist_2012_x64.exe, vcredist_2013_x64.exe and rewrite_amd64_en-US.msi
    (settings.json exchangePrerequisiteDirectory).

.PARAMETER VhdDirectory
    Folder that receives the VM's files: <VhdDirectory>\<Name>\.

.PARAMETER SwitchName
    The virtual switch the server attaches to: the Internal Lab switch, beside the domain controllers.

.PARAMETER IPAddress
    Static IPv4 address for the server.

.PARAMETER PrefixLength
    Prefix length of the subnet.

.PARAMETER Gateway
    Default gateway: the host's address on the Lab switch, which leads nowhere by design.

.PARAMETER ComputerName
    The server's host name. Default ex1.

.PARAMETER AdministratorPassword
    The Administrator password, the same for the local account and the domain's. Falls back to the environment
    variable JIM_AD_LAB_ADMIN_PASSWORD.

.PARAMETER CumulativeUpdatePath
    A .msu cumulative update to install after the domain join and before the prerequisites.

.PARAMETER MemoryStartupBytes
    Static memory (Microsoft does not support Dynamic Memory for Exchange). Default 12 GB.

.PARAMETER ProcessorCount
    Virtual processors. Default 4.

.PARAMETER VhdSizeBytes
    Size of the dynamically expanding system disk. Default 150 GB.

.PARAMETER UILanguage
    Windows UI language. Default en-US, the only one the Windows Server media carries.

.PARAMETER Locale
    System locale, user locale and keyboard. Default en-GB.

.PARAMETER TimeZone
    Windows time zone identifier. Default UTC.

.PARAMETER InstallTimeoutMinutes
    How long to wait for the unattended Windows install to finish. Default 90.

.PARAMETER SkipBaselineCheckpoint
    Build and verify, but take no checkpoint of either VM.

.PARAMETER LeaveRunning
    Leave the server running after its baseline is taken, rather than shut it down.

.EXAMPLE
    $admin = Read-Host 'Administrator password' -AsSecureString
    .\New-LabExchangeServer.ps1 -Name exchange-primary -DomainControllerName dc-primary -IsoPath D:\iso\ws2025.iso `
        -ExchangeIsoPath D:\media\ExchangeServerSE-x64.iso -PrerequisiteDirectory D:\media\exchange-prerequisites `
        -VhdDirectory D:\vm -SwitchName Lab -IPAddress 10.99.0.31 -PrefixLength 24 -Gateway 10.99.0.1 `
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
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ExchangeIsoPath,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$PrerequisiteDirectory,

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

    [string]$ComputerName = 'ex1',

    [securestring]$AdministratorPassword,

    [string]$CumulativeUpdatePath,

    [uint64]$MemoryStartupBytes = 12GB,

    [ValidateRange(1, 32)]
    [int]$ProcessorCount = 4,

    [uint64]$VhdSizeBytes = 150GB,

    [string]$UILanguage = 'en-US',

    [string]$Locale = 'en-GB',

    [string]$TimeZone = 'UTC',

    [ValidateRange(10, 480)]
    [int]$InstallTimeoutMinutes = 90,

    [switch]$SkipBaselineCheckpoint,

    [switch]$LeaveRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$guestDirectory = @(
    (Join-Path $PSScriptRoot 'guest'),
    (Join-Path (Join-Path $PSScriptRoot '..') 'guest')
) | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'LabExchangeServer.psm1') } | Select-Object -First 1
if (-not $guestDirectory) {
    [Console]::Error.WriteLine('Cannot find guest/LabExchangeServer.psm1 beside or above this script; see the layout notes in README.md.')
    exit 1
}
Import-Module (Join-Path $guestDirectory 'LabDomainController.psm1') -Force
Import-Module (Join-Path $guestDirectory 'LabExchangeServer.psm1') -Force

$guestRoot = 'C:\jim-ad-lab'
$guestScript = Join-Path $guestRoot 'Initialize-LabExchangeServer.ps1'
$script:Timings = New-Object System.Collections.Generic.List[string]

function Write-Heading {
    param([string]$Text)
    Write-Host ''
    Write-Host "=== $Text ===" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host "  $Text" -ForegroundColor Green
}

function Measure-HostCpu {
    # Starts sampling the host's CPU in the background while a phase runs (Read-HostCpu reads it): all logical
    # processors' run time, and how long this VM's virtual processors wait to be scheduled, which is what an
    # overcommitted host costs a guest. A job's output is serialised only one level deep, which would turn each
    # counter sample into a string, so the job flattens the samples itself.
    $counters = @(
        '\Hyper-V Hypervisor Logical Processor(_Total)\% Total Run Time',
        "\Hyper-V Hypervisor Virtual Processor($($Name):*)\CPU Wait Time Per Dispatch"
    )
    return (Start-Job -ScriptBlock {
            Get-Counter -Counter $using:counters -SampleInterval 10 -Continuous -ErrorAction SilentlyContinue |
                ForEach-Object { $_.CounterSamples } |
                ForEach-Object { [pscustomobject]@{ Path = $_.Path; CookedValue = $_.CookedValue } }
        })
}

function Read-HostCpu {
    param($Job)
    try {
        Stop-Job -Job $Job
        $samples = @(Receive-Job -Job $Job -ErrorAction SilentlyContinue)
        $run = @($samples | Where-Object { $_.Path -like '*logical processor(_total)*' } | ForEach-Object { $_.CookedValue })
        $wait = @($samples | Where-Object { $_.Path -like '*cpu wait time per dispatch*' } | ForEach-Object { $_.CookedValue })
        if ($run.Count -eq 0) { return 'host CPU not sampled' }
        $runAverage = ($run | Measure-Object -Average).Average
        $runPeak = ($run | Measure-Object -Maximum).Maximum
        $text = 'host CPU {0:N0}% on average, {1:N0}% at peak' -f $runAverage, $runPeak
        if ($wait.Count -gt 0) {
            # Reported in 100-nanosecond units.
            $text += '; vCPU wait {0:N1} us per dispatch on average' -f ((($wait | Measure-Object -Average).Average) / 10)
        }
        return $text
    }
    finally {
        Remove-Job -Job $Job -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Phase {
    param([string]$Phase, [switch]$SampleCpu)
    Write-Heading "Phase: $Phase"
    $arguments = Get-LabExchangePhaseArgument -Phase $Phase -Settings $settings
    $job = $null
    if ($SampleCpu) { $job = Measure-HostCpu }
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $result = Invoke-LabGuestPhase -VMName $Name -Credential $script:Credential -Arguments $arguments -GuestScript $guestScript
    }
    finally {
        $clock.Stop()
        $cpu = $null
        if ($job) { $cpu = Read-HostCpu -Job $job }
        $timing = '{0} {1:N1} min' -f $Phase, $clock.Elapsed.TotalMinutes
        if ($cpu) { $timing += " ($cpu)" }
        $script:Timings.Add($timing)
        Write-Ok $timing
    }
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

function Invoke-Checkpoint {
    param([string]$VmName)
    # The script runs in this session and sets an exit code only when it fails, so clear whatever an earlier native
    # command left in LASTEXITCODE first; otherwise a checkpoint that was taken reads as a failure.
    $global:LASTEXITCODE = 0
    & (Join-Path $PSScriptRoot 'Checkpoint-LabDomainController.ps1') -Name $VmName -Checkpoint (Get-LabCheckpointName -Baseline) -Replace
    if ($LASTEXITCODE -ne 0) { throw "Taking the baseline checkpoint of '$VmName' failed." }
}

function Get-PrerequisiteFile {
    # The installers from the prerequisites directory, each checked for a valid Microsoft signature before anything
    # reaches the guest.
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($prerequisite in @(Get-LabExchangePrerequisite | Where-Object { $_.Source -eq 'Directory' })) {
        $path = Join-Path $PrerequisiteDirectory $prerequisite.FileName
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "The $($prerequisite.Name) installer is not at $path. Download it from $($prerequisite.DownloadUrl) (see README.md, Exchange servers)."
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '' }
        if (-not (Test-LabMicrosoftSignature -Status ([string]$signature.Status) -SignerSubject $subject)) {
            throw "$path is not validly signed by Microsoft Corporation (signature $($signature.Status), signer '$subject'). Download it again from $($prerequisite.DownloadUrl)."
        }
        $files.Add($path)
    }
    return $files.ToArray()
}

function Invoke-WithExchangeMedia {
    # Runs a phase with the Exchange media attached, and detaches it afterwards whatever happens, so no media stays
    # in the guest or its checkpoints.
    param([string]$Phase, [switch]$SampleCpu)
    try {
        if (@(Get-VMDvdDrive -VMName $Name | Where-Object { $_.Path -eq $ExchangeIsoPath }).Count -eq 0) {
            $null = Add-VMDvdDrive -VMName $Name -Path $ExchangeIsoPath
            Write-Ok "Attached the Exchange media ($(Split-Path $ExchangeIsoPath -Leaf))"
        }
        $null = Invoke-Phase -Phase $Phase -SampleCpu:$SampleCpu
    }
    finally {
        foreach ($drive in @(Get-VMDvdDrive -VMName $Name | Where-Object { $_.Path -eq $ExchangeIsoPath })) {
            Remove-VMDvdDrive -VMName $Name -ControllerNumber $drive.ControllerNumber -ControllerLocation $drive.ControllerLocation
        }
    }
}

# --- Validate ---------------------------------------------------------------------------------

try {
    Assert-LabWindows -Feature 'Building an Exchange server'
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
    $prerequisiteFiles = Get-PrerequisiteFile

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

    $info = Get-LabExchangeServerInfo -Domain $dcMeta.Domain -NetBiosName $dcMeta.NetBiosName -ServerName $ComputerName
    $adminSecret = Resolve-LabSecret -Value $AdministratorPassword -EnvironmentVariable 'JIM_AD_LAB_ADMIN_PASSWORD'
    $unattendIso = Join-Path (Join-Path $VhdDirectory $Name) "$Name-unattend.iso"

    $localCredential = New-Object System.Management.Automation.PSCredential((Get-LabAdministratorUserName -NetBiosName $info.NetBiosName), $adminSecret)
    $domainCredential = New-Object System.Management.Automation.PSCredential((Get-LabAdministratorUserName -NetBiosName $info.NetBiosName -Promoted), $adminSecret)
    # The domain account first: once the server has joined, the local Administrator still answers PowerShell Direct but
    # cannot reach the directory, which Install needs. Before the join only the local one works, and is tried next.
    $credentials = @($domainCredential, $localCredential)

    $settings = @{
        ComputerName          = $info.ComputerName
        IPAddress             = $IPAddress.ToString()
        PrefixLength          = $PrefixLength
        Gateway               = $Gateway.ToString()
        DnsServer             = $dcMeta.IPAddress
        Domain                = $dcMeta.Domain
        NetBiosName           = $info.NetBiosName
        AdministratorPassword = $adminSecret
        VmName                = $Name
        LabRoot               = $guestRoot
    }

    Write-Heading "Building $Name ($($info.Fqdn), Exchange organisation $($info.NetBiosName))"
    $buildClock = [System.Diagnostics.Stopwatch]::StartNew()

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
    elseif ((Get-VM -Name $DomainControllerName).State -ne 'Running') {
        Start-VM -Name $DomainControllerName
        $null = Wait-DomainController
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
                -Notes (ConvertTo-LabVmNote -Domain $dcMeta.Domain -ShortName $info.ComputerName -NetBiosName $info.NetBiosName -IPAddress $IPAddress.ToString() -Role ExchangeServer)
        }
        catch {
            # No VM to install, so the ISO that carries the password goes at once.
            Remove-Item -LiteralPath $unattendIso -Force -ErrorAction SilentlyContinue
            throw
        }
        Write-Ok "Created $Name (Generation 2, $($MemoryStartupBytes / 1GB) GB static, $ProcessorCount vCPU, production checkpoints only)"
        $installPending = $true
    }
    else {
        $meta = ConvertFrom-LabVmNote -Notes $vm.Notes
        if ($null -eq $meta -or $meta.Role -ne 'ExchangeServer') {
            throw "A VM called '$Name' exists but is not one of this lab's Exchange servers; refusing to touch it."
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
        Write-Heading "Waiting for the unattended Windows install (up to $InstallTimeoutMinutes minutes)"
    }
    $guestTimeout = if ($installPending) { $InstallTimeoutMinutes * 60 } else { 600 }
    $script:Credential = Wait-LabGuestReady -VMName $Name -Credential $credentials -TimeoutSeconds $guestTimeout
    Write-Ok "The guest answers PowerShell Direct as $($script:Credential.UserName)"

    if ($installPending) {
        Complete-LabUnattendedInstall -Name $Name -UnattendIsoPath $unattendIso
        Write-Ok 'Ejected the install media and deleted the unattend ISO'
        $script:Timings.Add(('Windows install {0:N1} min' -f $buildClock.Elapsed.TotalMinutes))
    }

    # --- Drive the guest ------------------------------------------------------------------------
    Write-Heading 'Copying the lab files and the prerequisites into the guest'
    Copy-LabAssetToGuest -VMName $Name -Credential $script:Credential -Destination $guestRoot -Path @(
        (Join-Path $guestDirectory 'Initialize-LabExchangeServer.ps1'),
        (Join-Path $guestDirectory 'LabDomainController.psm1'),
        (Join-Path $guestDirectory 'LabExchangeServer.psm1')
    )
    Copy-LabAssetToGuest -VMName $Name -Credential $script:Credential -Destination (Join-Path $guestRoot 'exchange-prerequisites') -Path $prerequisiteFiles
    Write-Ok ('Copied ' + (($prerequisiteFiles | ForEach-Object { Split-Path $_ -Leaf }) -join ', ') + ', each signed by Microsoft')

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

    Invoke-WithExchangeMedia -Phase 'Prerequisites'
    Invoke-WithExchangeMedia -Phase 'Install' -SampleCpu
    $null = Invoke-Phase -Phase 'Configure'
    $verify = Invoke-Phase -Phase 'Verify' -SampleCpu
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

        # Taken from the old baseline, so they lack the server's objects. The runner takes them again on its next run.
        foreach ($stale in @(Get-VMSnapshot -VMName $DomainControllerName | Where-Object { $_.Name -like 'populated-*' })) {
            Remove-VMSnapshot -VMSnapshot $stale -Confirm:$false
            Write-Ok "Removed $DomainControllerName's '$($stale.Name)', taken before $Name joined"
        }
    }

    if (-not $LeaveRunning) {
        Stop-VM -Name $Name -Force
        Write-Ok "Shut $Name down; the lab starts it only for the scenarios that use Exchange"
    }

    Write-Heading 'Done'
    Write-Ok "$Name is built: $($info.Fqdn) at $IPAddress, Remote PowerShell $($info.PowerShellUri), OS build $($build.buildString)."
    Write-Ok ('Took {0:N0} minutes: {1}' -f $buildClock.Elapsed.TotalMinutes, ($script:Timings -join '; '))
    Write-Ok "From now on revert $DomainControllerName and $Name together, each to its own baseline."
}
catch {
    [Console]::Error.WriteLine("New-LabExchangeServer failed: $($_.Exception.Message)")
    if ($script:Timings.Count -gt 0) { [Console]::Error.WriteLine('Timings so far: ' + ($script:Timings -join '; ')) }
    exit 1
}
