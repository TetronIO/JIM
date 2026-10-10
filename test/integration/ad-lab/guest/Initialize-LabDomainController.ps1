# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Builds a lab domain controller from the inside, one phase at a time. Runs in the guest, under Windows
    PowerShell 5.1, driven by New-LabDomainController.ps1 over PowerShell Direct.

.DESCRIPTION
    Phases, run in this order with a restart between them where a phase reports RebootRequired:

      License    On evaluation media only: convert the evaluation edition to full Datacenter with the AVMA key (one
                 restart), after which the guest activates through Automatic Virtual Machine Activation. It must run
                 before Promote: Microsoft does not support converting an evaluation domain controller. On full media
                 there is nothing to do.
      Prepare    Rename the computer, set the static IP address, install the AD DS and DNS roles.
      Promote    Install-ADDSForest (a new single-domain forest, Windows Server 2016 functional level, with DNS).
      Extend     Only when -DirectoryExtension lists products: extend the forest with each, in order, from its media,
                 which the host has attached. Exchange: Setup /PrepareSchema, /PrepareAD (the organisation, named after
                 the forest) and /PrepareAllDomains, the only supported way to add Exchange's schema. Skips whatever a
                 previous run already did.
      Configure  Everything the directory carries after the build, identical to
                 test/integration/docker/samba-ad-prebuilt/post-provision.sh: DNS forwarder, w32time, Windows
                 Update off, Administrator password never expires, the OUs, svc-jim and the JIM Connectors group,
                 the delegation from jim-ad-delegation.acl (unchanged), read over Deleted Objects, the LDAPS
                 certificate, and (when asked) the Recycle Bin.
      Verify     Read it all back, then prove the delegation as svc-jim over LDAPS, as post-provision.sh does.
                 Throws if any required check fails.

    Every phase is idempotent: it checks for what it would create (role installed, domain exists, OU exists,
    certificate with that subject exists) and does nothing when it is already there, so the host can re-run the
    whole sequence after a failed step. Each phase ends by emitting one result object,
    { Phase, Changed, RebootRequired, Detail }, which the host reads.

    Secrets arrive as SecureString parameters and are never written to disk or logged. The domain password
    policy is left at the Windows defaults (complexity on); the Samba image relaxes it, this does not.

    Windows Server 2025 defaults that stay on: LDAP signing is enforced (a simple bind over plain LDAP is
    refused, which Verify confirms), so everything talks LDAPS on 636.

.PARAMETER Phase
    License, Prepare, Promote, Extend, Configure or Verify.

.PARAMETER DirectoryExtension
    The products extending the forest, in order (Extend applies them; Verify checks them). Exchange only, for now.

.PARAMETER ComputerName
    The domain controller's host name, for example dc1 (giving dc1.panoply.local).

.PARAMETER Domain
    The forest root domain, for example PANOPLY.LOCAL.

.PARAMETER NetBiosName
    The NetBIOS domain name, for example PANOPLY.

.PARAMETER IPAddress
    Static IPv4 address (Prepare).

.PARAMETER PrefixLength
    Prefix length of the subnet (Prepare).

.PARAMETER Gateway
    Default gateway (Prepare).

.PARAMETER DnsForwarder
    Optional. The DNS server this domain controller forwards to (Prepare sets it as the resolver before promotion;
    Configure sets it as the forwarder after). The lab network has no uplink, so normally there is none: with no
    value, Prepare points the resolver at this machine's own address and Configure adds no forwarder.

.PARAMETER NtpServer
    The NTP source w32time follows (Configure sets it; Verify checks it).

.PARAMETER SafeModePassword
    Directory Services Restore Mode password (Promote).

.PARAMETER ServiceAccountPassword
    The svc-jim password (Configure, Verify).

.PARAMETER EnableRecycleBin
    Enable the Active Directory Recycle Bin (Configure); Verify then expects it on, and otherwise off.

.PARAMETER ExtraCertificateNames
    Extra DNS names for the LDAPS certificate, for example the Compose service name.

.PARAMETER VmName
    The VM's name on the host, used to name the exported certificate file.

.PARAMETER LabRoot
    Where the lab files live in the guest. Default C:\jim-ad-lab.

.EXAMPLE
    .\Initialize-LabDomainController.ps1 -Phase Prepare -ComputerName dc1 -IPAddress 10.99.0.11 -PrefixLength 24 -Gateway 10.99.0.1

    No forwarder: the lab network has no uplink, so the resolver is the machine itself.

.EXAMPLE
    .\Initialize-LabDomainController.ps1 -Phase Prepare -ComputerName dc1 -IPAddress 10.20.30.41 -PrefixLength 24 -Gateway 10.20.30.1 -DnsForwarder 10.20.30.2

.NOTES
    Deliberate departures from the obvious, each explained where it happens: the forest is created with
    -NoRebootOnCompletion so the host, not the guest, restarts it; the LDAPS certificate lives in the machine
    store only, not also in the NTDS store.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'The phases narrate progress to the operator watching the host; Write-Host is forwarded through PowerShell Direct.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'The parameters are read by the phase functions below, which see them through the script scope.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal helpers of a build script; the phase is the unit of change and every helper checks for existing state first.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('License', 'Prepare', 'Promote', 'Extend', 'Configure', 'Verify')]
    [string]$Phase,

    [string]$ComputerName,
    [string]$Domain,
    [string]$NetBiosName,
    [string]$IPAddress,
    [ValidateRange(0, 32)]
    [int]$PrefixLength = 0,
    [string]$Gateway,
    [string]$DnsForwarder,
    [string]$NtpServer,
    [securestring]$SafeModePassword,
    [securestring]$ServiceAccountPassword,
    [switch]$EnableRecycleBin,
    [string[]]$ExtraCertificateNames = @(),
    [ValidateSet('Exchange')]
    [string[]]$DirectoryExtension = @(),
    [string]$VmName,
    [string]$LabRoot = 'C:\jim-ad-lab'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'LabDomainController.psm1') -Force

$script:Changed = $false
$script:RebootRequired = $false
$script:Detail = New-Object System.Collections.Generic.List[string]

function Write-Step {
    param([string]$Message)
    Write-Host ("[{0}] {1}" -f $Phase, $Message)
}

function Set-Changed {
    param([string]$Message)
    $script:Changed = $true
    $script:Detail.Add($Message)
    Write-Step $Message
}

function Assert-Setting {
    param([string[]]$Name)
    foreach ($item in $Name) {
        $value = Get-Variable -Name $item -ValueOnly -ErrorAction SilentlyContinue
        if (($null -eq $value) -or (($value -is [string]) -and [string]::IsNullOrWhiteSpace($value)) -or (($value -is [int]) -and ($value -eq 0))) {
            throw "Phase $Phase needs -$item."
        }
    }
}

function Resolve-DnsForwarder {
    # The address to use as the DNS forwarder, or $null when there is none: an empty value means none, and nothing
    # else does. The lab network has no uplink, so normally there is none.
    param([string]$Forwarder)
    if ([string]::IsNullOrWhiteSpace($Forwarder)) { return $null }
    return $Forwarder.Trim()
}

function Get-DomainRole {
    # 0 standalone workstation ... 3 member server, 4 backup domain controller, 5 primary domain controller.
    return [int](Get-CimInstance -ClassName Win32_ComputerSystem).DomainRole
}

function Test-IsDomainController {
    return ((Get-DomainRole) -ge 4)
}

function Get-EditionId {
    return [string](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
}

function Wait-ActiveDirectory {
    # After the promotion restart, Active Directory Web Services takes a while to answer. Wait for it.
    param([int]$TimeoutSeconds = 900)

    # The ActiveDirectory module talks to Active Directory Web Services, and promotion normally sets that service to
    # start automatically. On the first real build (Windows Server 2025 build 26100.32230, from evaluation media) it was
    # left Disabled with no start-type change ever logged, so the wait below ran out; the likely cause was the host
    # hard-resetting the guest straight after promotion (Restart-LabGuest now restarts cleanly). Own the dependency
    # anyway: make sure it starts automatically and is running, whatever left it otherwise.
    $webServices = Get-Service -Name 'ADWS' -ErrorAction Stop
    if ($webServices.StartType -ne 'Automatic') {
        Set-Service -Name 'ADWS' -StartupType Automatic
        Set-Changed "Set Active Directory Web Services to start automatically (it was $($webServices.StartType))"
    }
    if ((Get-Service -Name 'ADWS').Status -ne 'Running') {
        Start-Service -Name 'ADWS'
        Write-Step 'Started Active Directory Web Services'
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = 'no attempt was made'
    while ((Get-Date) -lt $deadline) {
        try {
            # Loading the module is part of the wait, not a step before it: loaded while Web Services is still starting
            # it either warns ("Error initializing default drive") or, part-way up, fails outright ("Attempting to
            # perform the InitializeDefaultDrives operation on the 'ActiveDirectory' provider failed"). Retried here.
            if (-not (Get-Module -Name ActiveDirectory)) {
                Import-Module ActiveDirectory -ErrorAction Stop -WarningAction SilentlyContinue
            }
            $null = Get-ADDomain -ErrorAction Stop
            # The module creates its AD: drive only at import, and only if Web Services answered then; imported before
            # they were up ("Error initializing default drive"), it never does, and the delegation (Get-Acl AD:\...)
            # would fail later. Create it once they answer.
            if (-not (Get-PSDrive -Name AD -ErrorAction SilentlyContinue)) {
                $null = New-PSDrive -Name AD -PSProvider ActiveDirectory -Root '//RootDSE/' -Scope Global -ErrorAction Stop
            }
            return
        }
        catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Seconds 10
        }
    }
    throw "Active Directory did not answer within $TimeoutSeconds seconds. Last error: $lastError"
}

# ---------------------------------------------------------------------------------------------
# License
# ---------------------------------------------------------------------------------------------

function Invoke-LicensePhase {
    $edition = Get-EditionId
    if (-not (Test-LabEvaluationEdition -EditionId $edition)) {
        Write-Step "Edition $edition is not an evaluation edition; nothing to convert."
        return
    }
    if (Test-IsDomainController) {
        throw "This is an evaluation edition ($edition) that is already a domain controller, which Microsoft does not support converting. Remove the VM and build it again."
    }

    # https://learn.microsoft.com/windows-server/get-started/upgrade-conversion-options#convert-an-evaluation-version-to-a-retail-version
    # The AVMA key converts ServerDatacenterEval to ServerDatacenter (proven on Windows Server 2025 build 26100), and
    # once the restart applies the new edition the guest activates through AVMA against the Datacenter host, with no
    # further step. DISM returns 3010 when the change needs the restart, which the host performs.
    $output = & dism.exe /Online /Set-Edition:ServerDatacenter "/ProductKey:$(Get-LabAvmaKey -Edition Datacenter)" /AcceptEula /NoRestart /English 2>&1
    if ($LASTEXITCODE -notin 0, 3010) {
        throw ("DISM could not convert $edition to ServerDatacenter (exit $LASTEXITCODE): " + (($output | Select-Object -Last 5) -join ' '))
    }
    $script:RebootRequired = $true
    Set-Changed "Converted $edition to ServerDatacenter with the AVMA key (applies on restart)"
}

# ---------------------------------------------------------------------------------------------
# Prepare
# ---------------------------------------------------------------------------------------------

function Invoke-PreparePhase {
    Assert-Setting -Name 'ComputerName', 'IPAddress', 'PrefixLength', 'Gateway'

    # Rename. The unattend file already names the machine, so this normally does nothing; it makes a re-run
    # after a manual change converge.
    if ($env:COMPUTERNAME -ne $ComputerName) {
        Rename-Computer -NewName $ComputerName -Force
        $script:RebootRequired = $true
        Set-Changed "Renamed the computer to $ComputerName"
    }

    # Static address. Skipped once the address is right, so a re-run on a promoted controller does not touch the
    # network (and never rewrites the resolver, which promotion points at the loopback address).
    $adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Sort-Object -Property ifIndex | Select-Object -First 1
    if ($null -eq $adapter) {
        throw 'No network adapter is up. Is the virtual machine connected to a virtual switch?'
    }
    $current = @(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -eq $IPAddress -and $_.PrefixLength -eq $PrefixLength })
    if ($current.Count -eq 0) {
        Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
        Get-NetRoute -InterfaceIndex $adapter.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
        Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -Dhcp Disabled
        $null = New-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress $IPAddress -PrefixLength $PrefixLength -DefaultGateway $Gateway
        Set-Changed "Set the static address $IPAddress/$PrefixLength via $Gateway"
    }
    if (-not (Test-IsDomainController)) {
        # With no forwarder (the lab has no uplink) the resolver is this machine itself, which is what it becomes on
        # promotion anyway.
        $forwarder = Resolve-DnsForwarder -Forwarder $DnsForwarder
        $resolver = if ($forwarder) { $forwarder } else { $IPAddress }
        Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses $resolver
    }

    # Roles.
    $wanted = @('AD-Domain-Services', 'DNS')
    $missing = @(Get-WindowsFeature -Name $wanted | Where-Object { -not $_.Installed })
    if ($missing.Count -gt 0) {
        $install = Install-WindowsFeature -Name $wanted -IncludeManagementTools
        if (-not $install.Success) {
            throw 'Installing the AD DS and DNS roles did not succeed.'
        }
        if ([string]$install.RestartNeeded -eq 'Yes') { $script:RebootRequired = $true }
        Set-Changed ('Installed the roles: ' + (($missing | ForEach-Object { $_.Name }) -join ', '))
    }
}

# ---------------------------------------------------------------------------------------------
# Promote
# ---------------------------------------------------------------------------------------------

function Invoke-PromotePhase {
    Assert-Setting -Name 'Domain', 'NetBiosName', 'SafeModePassword'

    if (Test-IsDomainController) {
        Write-Step 'Already a domain controller; nothing to promote.'
        return
    }

    # The last point at which an evaluation edition can still be converted. Promoting one would leave a domain
    # controller that expires in real time and can never be converted, so refuse rather than build it.
    $edition = Get-EditionId
    if (Test-LabEvaluationEdition -EditionId $edition) {
        throw "Refusing to promote an evaluation edition ($edition): run the License phase and restart first."
    }

    Import-Module ADDSDeployment -ErrorAction Stop

    # -ForestMode and -DomainMode WinThreshold are the Windows Server 2016 functional level: the Install-ADDSForest
    # parameter accepts Win2008, Win2008R2, Win2012, Win2012R2, WinThreshold and Default, WinThreshold being the
    # name the 2016 level kept from its pre-release naming, and Windows2016Forest / Windows2016Domain being what
    # Get-ADForest and Get-ADDomain report afterwards (Verify checks that):
    #   https://learn.microsoft.com/powershell/module/addsdeployment/install-addsforest
    #   https://learn.microsoft.com/windows-server/identity/ad-ds/plan/raise-domain-forest-functional-levels
    #
    # -NoRebootOnCompletion: the host restarts the VM itself (Restart-VM -Type Reboot, a clean restart, never a hard
    # reset, which can lose promotion's last writes), which a PowerShell Direct session survives better than a
    # restart from inside the guest. RebootRequired tells it to.
    # -CreateDnsDelegation:$false: there is no parent zone. -Force skips the prompts and the warnings about
    # delegation and the static address, which are expected here.
    $result = Install-ADDSForest `
        -DomainName $Domain `
        -DomainNetbiosName $NetBiosName `
        -ForestMode WinThreshold `
        -DomainMode WinThreshold `
        -InstallDns `
        -CreateDnsDelegation:$false `
        -SafeModeAdministratorPassword $SafeModePassword `
        -NoRebootOnCompletion `
        -Force

    if (($null -ne $result) -and ($result.PSObject.Properties['Status']) -and ([string]$result.Status -eq 'Error')) {
        throw ('Install-ADDSForest failed: ' + [string]$result.Message)
    }
    $script:RebootRequired = $true
    Set-Changed "Promoted this server to the first domain controller of $Domain"
}

# ---------------------------------------------------------------------------------------------
# Extend: directory extensions (Exchange; room for Skype for Business)
# ---------------------------------------------------------------------------------------------

function Get-AdObjectProperty {
    # One property of an AD object, or $null when the object or the attribute is not there (StrictMode-safe).
    param([object]$Object, [string]$Name)
    if (($null -eq $Object) -or ($null -eq $Object.PSObject.Properties[$Name])) { return $null }
    return $Object.$Name
}

function Get-ExchangeForestState {
    # Where Exchange stands in this forest: the schema version (rangeUpper of ms-Exch-Schema-Version-Pt), the
    # organisation (name and objectVersion under CN=Microsoft Exchange,CN=Services) and the domain preparation
    # (objectVersion of CN=Microsoft Exchange System Objects). Each is $null when absent.
    $root = Get-ADRootDSE
    $version = Get-ADObject -SearchBase $root.schemaNamingContext -SearchScope OneLevel -LDAPFilter '(cn=ms-Exch-Schema-Version-Pt)' -Properties rangeUpper | Select-Object -First 1
    $organization = $null
    try {
        $organization = Get-ADObject -SearchBase "CN=Microsoft Exchange,CN=Services,$($root.configurationNamingContext)" -SearchScope OneLevel -LDAPFilter '(objectClass=msExchOrganizationContainer)' -Properties objectVersion -ErrorAction Stop | Select-Object -First 1
    }
    catch {
        # No CN=Microsoft Exchange container: the organisation has not been created.
        $organization = $null
    }
    $systemObjects = Get-ADObject -SearchBase $root.defaultNamingContext -SearchScope OneLevel -LDAPFilter '(cn=Microsoft Exchange System Objects)' -Properties objectVersion | Select-Object -First 1

    $rangeUpper = Get-AdObjectProperty -Object $version -Name 'rangeUpper'
    return [pscustomobject][ordered]@{
        SchemaRangeUpper    = $(if ($null -ne $rangeUpper) { [int]$rangeUpper } else { $null })
        OrganizationName    = $(if ($null -ne $organization) { [string]$organization.Name } else { $null })
        OrganizationVersion = (Get-AdObjectProperty -Object $organization -Name 'objectVersion')
        DomainVersion       = (Get-AdObjectProperty -Object $systemObjects -Name 'objectVersion')
    }
}

function Find-ExchangeMedia {
    # The root of the Exchange media the host attached: a DVD drive holding Setup.exe and Setup\Data\SchemaVersion.ldf.
    foreach ($volume in @(Get-Volume | Where-Object { ($_.DriveType -eq 'CD-ROM') -and $_.DriveLetter })) {
        $root = "$($volume.DriveLetter):"
        if ((Test-Path -LiteralPath "$root\Setup.exe") -and (Test-Path -LiteralPath "$root\Setup\Data\SchemaVersion.ldf")) {
            return $root
        }
    }
    throw 'No Exchange media is attached: no DVD drive holds Setup.exe and Setup\Data\SchemaVersion.ldf. The host attaches -ExchangeIsoPath before Extend.'
}

function Invoke-ExchangeExtension {
    $currentVersion = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    if (-not (Test-LabExchangeSchemaMasterBuild -CurrentBuild ([int]$currentVersion.CurrentBuild) -Ubr ([int]$currentVersion.UBR))) {
        throw "Windows build $($currentVersion.CurrentBuild).$($currentVersion.UBR) is older than 26100.7171: Microsoft requires the November 2025 update on a Windows Server 2025 schema master before Exchange extends the schema. Pass -CumulativeUpdatePath."
    }

    $media = Find-ExchangeMedia
    $target = Get-LabExchangeSchemaTarget -LdfText (Get-Content -LiteralPath "$media\Setup\Data\SchemaVersion.ldf" -Raw)
    $organizationName = Get-LabExchangeOrganizationName -NetBiosName $NetBiosName
    $state = Get-ExchangeForestState
    if ($state.OrganizationName -and ($state.OrganizationName -ne $organizationName)) {
        throw "This forest already has the Exchange organisation '$($state.OrganizationName)', not '$organizationName'; an organisation cannot be renamed."
    }

    $steps = @(Get-LabExchangePreparationStep -SchemaRangeUpper $state.SchemaRangeUpper -TargetRangeUpper $target `
            -OrganizationPresent ([bool]$state.OrganizationName) -DomainPrepared ($null -ne $state.DomainVersion))
    if ($steps.Count -eq 0) {
        Write-Step "Exchange is already prepared: schema $($state.SchemaRangeUpper), organisation '$($state.OrganizationName)' $($state.OrganizationVersion), domain $($state.DomainVersion)."
        return
    }

    # Setup runs from the media and writes its log to C:\ExchangeSetupLogs\ExchangeSetup.log.
    $setup = "$media\Setup.exe"
    foreach ($step in $steps) {
        Write-Step "Exchange Setup /$step (this takes several minutes)"
        $arguments = Get-LabExchangeSetupArgument -Step $step -OrganizationName $organizationName
        $output = & $setup @arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            $log = 'C:\ExchangeSetupLogs\ExchangeSetup.log'
            $tail = if (Test-Path -LiteralPath $log) { (Get-Content -LiteralPath $log -Tail 15) -join ' | ' } else { (($output | Select-Object -Last 15) -join ' | ') }
            throw "Exchange Setup /$step failed (exit $LASTEXITCODE): $tail"
        }
        Set-Changed "Exchange Setup /$step completed"
    }

    $after = Get-ExchangeForestState
    if (($after.SchemaRangeUpper -ne $target) -or ($after.OrganizationName -ne $organizationName) -or ($null -eq $after.DomainVersion)) {
        throw "Exchange Setup reported success but the forest reads schema $($after.SchemaRangeUpper) (want $target), organisation '$($after.OrganizationName)' (want '$organizationName'), domain $($after.DomainVersion)."
    }
    Set-Changed "Exchange organisation '$organizationName' prepared: schema $($after.SchemaRangeUpper), organisation $($after.OrganizationVersion), domain $($after.DomainVersion)"
    # A fresh boot before Configure, so nothing downstream reads a schema cache from before the extension.
    $script:RebootRequired = $true
}

function Invoke-ExtendPhase {
    Assert-Setting -Name 'Domain', 'NetBiosName'
    if (@($DirectoryExtension).Count -eq 0) {
        Write-Step 'No directory extensions requested; nothing to do.'
        return
    }
    if (-not (Test-IsDomainController)) {
        throw 'Extend runs after Promote: this server is not a domain controller yet.'
    }
    Wait-ActiveDirectory
    foreach ($product in $DirectoryExtension) {
        switch ($product) {
            'Exchange' { Invoke-ExchangeExtension }
            default { throw "No Extend handler for the directory extension '$product'." }
        }
    }
}

# ---------------------------------------------------------------------------------------------
# Configure
# ---------------------------------------------------------------------------------------------

function Set-DnsForwarderTo {
    param([string]$Address)
    $existing = @(Get-DnsServerForwarder).IPAddress | ForEach-Object { $_.IPAddressToString }
    if ($existing -notcontains $Address) {
        Set-DnsServerForwarder -IPAddress $Address
        Set-Changed "Set the DNS forwarder to $Address"
    }
}

function Set-TimeSource {
    param([string]$Server)
    # The Hyper-V Time Synchronization integration service stays enabled on the host, so a production-checkpoint
    # revert boots with the host's clock; w32time then follows the same NTP source as the host and the runner.
    $parameters = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Parameters'
    $configured = ''
    if ($parameters.PSObject.Properties['NtpServer']) { $configured = [string]$parameters.NtpServer }
    if (-not $configured.StartsWith($Server, [System.StringComparison]::OrdinalIgnoreCase)) {
        $null = & w32tm.exe /config /manualpeerlist:$Server /syncfromflags:manual /reliable:yes /update
        if ($LASTEXITCODE -ne 0) { throw "w32tm /config failed with exit code $LASTEXITCODE." }
        Restart-Service -Name w32time
        Set-Changed "Pointed w32time at $Server"
    }
}

function Set-WindowsUpdatePolicy {
    # Patching is by rebuild only, so automatic installation is switched off. Nothing else is stopped or disabled.
    $key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
    if (-not (Test-Path -LiteralPath $key)) {
        $null = New-Item -Path $key -Force
    }
    $value = $null
    $existing = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    if (($null -ne $existing) -and $existing.PSObject.Properties['NoAutoUpdate']) { $value = $existing.NoAutoUpdate }
    if ($value -ne 1) {
        Set-ItemProperty -LiteralPath $key -Name NoAutoUpdate -Value 1 -Type DWord
        Set-Changed 'Switched automatic Windows Update installation off (NoAutoUpdate=1)'
    }
}

function New-LabOrganizationalUnit {
    param([string]$Name, [string]$ParentDn)
    $dn = "OU=$Name,$ParentDn"
    $found = Get-ADOrganizationalUnit -Filter "DistinguishedName -eq '$dn'" -ErrorAction SilentlyContinue
    if ($null -eq $found) {
        # Not protected from accidental deletion, matching the Samba OUs: scenarios create and remove things here.
        New-ADOrganizationalUnit -Name $Name -Path $ParentDn -ProtectedFromAccidentalDeletion $false
        Set-Changed "Created $dn"
    }
}

function Set-ServiceAccount {
    param([string]$BaseDn, [securestring]$Password)
    $description = "The account JIM's Connected Systems bind as"
    $dn = "CN=svc-jim,OU=Services,$BaseDn"
    $user = Get-ADUser -Filter "SamAccountName -eq 'svc-jim'" -ErrorAction SilentlyContinue
    if ($null -eq $user) {
        New-ADUser -Name 'svc-jim' -SamAccountName 'svc-jim' -Path "OU=Services,$BaseDn" -Description $description `
            -AccountPassword $Password -Enabled $true -PasswordNeverExpires $true
        Set-Changed "Created $dn"
    }
    else {
        # Converge: same password every run, so the credential the harness holds is the one that works.
        Set-ADAccountPassword -Identity $user -Reset -NewPassword $Password
        Set-ADUser -Identity $user -Description $description -Enabled $true -PasswordNeverExpires $true
    }

    $group = Get-ADGroup -Filter "Name -eq 'JIM Connectors'" -ErrorAction SilentlyContinue
    if ($null -eq $group) {
        New-ADGroup -Name 'JIM Connectors' -SamAccountName 'JIM Connectors' -GroupScope Global -GroupCategory Security -Path "OU=Services,$BaseDn"
        Set-Changed "Created CN=JIM Connectors,OU=Services,$BaseDn"
    }
    $member = @(Get-ADGroupMember -Identity 'JIM Connectors' | Where-Object { $_.SamAccountName -eq 'svc-jim' })
    if ($member.Count -eq 0) {
        Add-ADGroupMember -Identity 'JIM Connectors' -Members 'svc-jim'
        Set-Changed 'Added svc-jim to JIM Connectors'
    }
}

function Set-LdapsCertificate {
    param([object]$Info, [string[]]$Extra, [string]$ExportName)

    $names = @(Get-LabCertificateName -Fqdn $Info.Fqdn -ShortName $Info.ShortName -Domain $Info.DnsDomain -ExtraName $Extra)
    $subject = "CN=$($Info.Fqdn), O=JIM Integration Testing"
    $store = 'Cert:\LocalMachine\My'

    $reusable = @(Get-ChildItem -LiteralPath $store | Where-Object {
            ($_.Subject -eq $subject) -and $_.HasPrivateKey -and ($_.NotAfter -gt (Get-Date).AddDays(30)) -and
            ((($_.DnsNameList | ForEach-Object { $_.Unicode.ToLowerInvariant() } | Sort-Object) -join ',') -eq (($names | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object) -join ','))
        })

    if ($reusable.Count -gt 0) {
        $certificate = $reusable | Sort-Object -Property NotAfter -Descending | Select-Object -First 1
    }
    else {
        # Anything with the same subject is stale (different names, near expiry). Remove it, so there is exactly
        # one server-authentication certificate for AD DS to choose from.
        Get-ChildItem -LiteralPath $store | Where-Object { $_.Subject -eq $subject } | Remove-Item -Force

        # The subject and names mirror post-provision.sh. The Schannel provider and key exchange key spec are what
        # Microsoft's LDAPS certificate requirements list; the server-authentication EKU (1.3.6.1.5.5.7.3.1) is
        # required too:
        #   https://learn.microsoft.com/windows-server/identity/ad-ds/configure-ldap-signing-certificates
        $certificate = New-SelfSignedCertificate `
            -DnsName $names `
            -Subject $subject `
            -CertStoreLocation $store `
            -Provider 'Microsoft RSA SChannel Cryptographic Provider' `
            -KeySpec KeyExchange `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -NotAfter (Get-Date).AddYears(10) `
            -KeyExportPolicy NonExportable `
            -KeyUsage DigitalSignature, KeyEncipherment `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1')
        Set-Changed ('Created the LDAPS certificate for ' + ($names -join ', '))
    }

    # AD DS looks in the NTDS service's Personal store first and, if that holds nothing, in the machine's Personal
    # store, and it picks the certificate up without a restart. The brief for this lab suggested copying the
    # certificate into the NTDS store as well. That is deliberately not done: the store is not exposed by the
    # certificate provider, and copying only the public certificate (certutil -addstore of a .cer) would leave a
    # certificate with no private key in the store AD DS prefers, which breaks LDAPS. With exactly one
    # server-authentication certificate in the machine store, the choice is already deterministic.
    #   https://learn.microsoft.com/windows-server/identity/ad-ds/configure-ldap-signing-certificates

    $exportPath = Join-Path $LabRoot ("{0}-ca.cer" -f $ExportName)
    $null = Export-Certificate -Cert $certificate -FilePath $exportPath -Type CERT -Force

    # AD DS only uses a certificate whose chain the domain controller itself trusts. A self-signed one is its own root,
    # so its public half has to be in the machine's Trusted Root store too; without it Schannel logs 36886 ("No
    # suitable default server credential exists"), the directory logs 1220 (error 8009030e) and LDAPS drops every
    # handshake. Copies of an earlier certificate with the same subject are removed, so only the current one is trusted.
    $root = 'Cert:\LocalMachine\Root'
    Get-ChildItem -LiteralPath $root | Where-Object { ($_.Subject -eq $subject) -and ($_.Thumbprint -ne $certificate.Thumbprint) } | Remove-Item -Force
    if (-not (Get-ChildItem -LiteralPath $root | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint })) {
        $null = Import-Certificate -FilePath $exportPath -CertStoreLocation $root
        Set-Changed 'Trusted the LDAPS certificate on the domain controller itself (machine Trusted Root store)'
    }
    return $certificate
}

function Enable-RecycleBinFeature {
    $forest = (Get-ADForest).Name
    $feature = Get-ADOptionalFeature -Filter "Name -eq 'Recycle Bin Feature'"
    if (@($feature.EnabledScopes).Count -eq 0) {
        # Irreversible, which is why it is only ever done when asked for (dc-primary).
        Enable-ADOptionalFeature -Identity $feature -Scope ForestOrConfigurationSet -Target $forest -Confirm:$false
        Set-Changed 'Enabled the Active Directory Recycle Bin'
    }
}

function Invoke-ConfigurePhase {
    Assert-Setting -Name 'ComputerName', 'Domain', 'NetBiosName', 'NtpServer', 'ServiceAccountPassword'
    if ([string]::IsNullOrEmpty($VmName)) { $script:VmName = $ComputerName }

    Wait-ActiveDirectory
    $info = Get-LabDomainInfo -Domain $Domain -ShortName $ComputerName -NetBiosName $NetBiosName
    $base = $info.BaseDn
    $aclPath = Join-Path $LabRoot 'jim-ad-delegation.acl'

    # No forwarder unless one was given: the lab network has no uplink to forward to.
    $forwarder = Resolve-DnsForwarder -Forwarder $DnsForwarder
    if ($forwarder) { Set-DnsForwarderTo -Address $forwarder }
    Set-TimeSource -Server $NtpServer
    Set-WindowsUpdatePolicy

    # Belt and braces, as post-provision.sh: the credential must never expire.
    $administrator = Get-ADUser -Identity 'Administrator' -Properties PasswordNeverExpires
    if (-not $administrator.PasswordNeverExpires) {
        Set-ADUser -Identity $administrator -PasswordNeverExpires $true
        Set-Changed 'Set Administrator to never expire its password'
    }

    # OU structure: Corp (the partition container JIM selects), its Users and Groups, the two legacy test OUs, and
    # Services, where JIM's own account lives.
    New-LabOrganizationalUnit -Name 'Corp' -ParentDn $base
    New-LabOrganizationalUnit -Name 'Users' -ParentDn "OU=Corp,$base"
    New-LabOrganizationalUnit -Name 'Groups' -ParentDn "OU=Corp,$base"
    New-LabOrganizationalUnit -Name 'TestUsers' -ParentDn $base
    New-LabOrganizationalUnit -Name 'TestGroups' -ParentDn $base
    New-LabOrganizationalUnit -Name 'Services' -ParentDn $base

    Set-ServiceAccount -BaseDn $base -Password $ServiceAccountPassword

    # The delegation, from the same file the Samba image and the connector documentation use, and the read over
    # Deleted Objects. The same functions Grant-LabDelegation.ps1 uses at run time.
    foreach ($container in @("OU=Corp,$base", "OU=TestUsers,$base", "OU=TestGroups,$base")) {
        $outcome = Grant-LabContainerDelegation -ContainerDn $container -AclPath $aclPath
        if ($outcome.Outcome -eq 'Delegated') { Set-Changed "Delegated JIM's access over $container" }
    }
    $tombstones = Grant-LabTombstoneRead -NetBiosName $info.NetBiosName
    if ($tombstones.Outcome -eq 'Delegated') { Set-Changed "Granted read over $($tombstones.ContainerDn)" }

    $null = Set-LdapsCertificate -Info $info -Extra $ExtraCertificateNames -ExportName $VmName

    if ($EnableRecycleBin) {
        Enable-RecycleBinFeature
    }

    # The host restarts after Configure regardless: the certificate and the time source are cleanest on a fresh boot.
    $script:RebootRequired = $true
}

# ---------------------------------------------------------------------------------------------
# Verify
# ---------------------------------------------------------------------------------------------

$script:Checks = New-Object System.Collections.Generic.List[object]

function Add-Check {
    # Runs one check; an exception fails it. -Advisory checks are reported but do not fail the phase.
    param([string]$Name, [scriptblock]$Test, [switch]$Advisory)
    try {
        $outcome = & $Test
        $passed = [bool]$outcome.Passed
        $detail = [string]$outcome.Detail
    }
    catch {
        $passed = $false
        $detail = $_.Exception.Message
    }
    $script:Checks.Add([pscustomobject][ordered]@{ Name = $Name; Passed = $passed; Advisory = [bool]$Advisory; Detail = $detail })
    if ($passed) { $mark = 'ok  ' } elseif ($Advisory) { $mark = 'warn' } else { $mark = 'FAIL' }
    Write-Step ("{0} {1}: {2}" -f $mark, $Name, $detail)
}

function Invoke-VerifyPhase {
    Assert-Setting -Name 'ComputerName', 'Domain', 'NetBiosName', 'NtpServer', 'ServiceAccountPassword'
    if ([string]::IsNullOrEmpty($VmName)) { $script:VmName = $ComputerName }

    Wait-ActiveDirectory
    $info = Get-LabDomainInfo -Domain $Domain -ShortName $ComputerName -NetBiosName $NetBiosName
    $base = $info.BaseDn
    $names = @(Get-LabCertificateName -Fqdn $info.Fqdn -ShortName $info.ShortName -Domain $info.DnsDomain -ExtraName $ExtraCertificateNames)

    Add-Check 'Forest and domain are at the Windows Server 2016 functional level' {
        $forestMode = [string](Get-ADForest).ForestMode
        $domainMode = [string](Get-ADDomain).DomainMode
        [pscustomobject]@{ Passed = (($forestMode -eq 'Windows2016Forest') -and ($domainMode -eq 'Windows2016Domain')); Detail = "$forestMode, $domainMode" }
    }

    Add-Check 'Domain password policy is at the Windows defaults (complexity on)' {
        $policy = Get-ADDefaultDomainPasswordPolicy
        [pscustomobject]@{ Passed = [bool]$policy.ComplexityEnabled; Detail = "complexity=$($policy.ComplexityEnabled), min length=$($policy.MinPasswordLength)" }
    }

    Add-Check 'Recycle Bin matches the request' {
        $feature = Get-ADOptionalFeature -Filter "Name -eq 'Recycle Bin Feature'"
        $enabled = (@($feature.EnabledScopes).Count -gt 0)
        [pscustomobject]@{ Passed = ($enabled -eq [bool]$EnableRecycleBin); Detail = "enabled=$enabled, wanted=$([bool]$EnableRecycleBin)" }
    }

    Add-Check 'The OU structure exists' {
        $missing = @()
        foreach ($dn in @("OU=Corp,$base", "OU=Users,OU=Corp,$base", "OU=Groups,OU=Corp,$base", "OU=TestUsers,$base", "OU=TestGroups,$base", "OU=Services,$base")) {
            if ($null -eq (Get-ADOrganizationalUnit -Filter "DistinguishedName -eq '$dn'" -ErrorAction SilentlyContinue)) { $missing += $dn }
        }
        [pscustomobject]@{ Passed = ($missing.Count -eq 0); Detail = ('missing: ' + ($missing -join '; ')) }
    }

    Add-Check 'svc-jim is enabled, never expires, and is in JIM Connectors' {
        $user = Get-ADUser -Identity "CN=svc-jim,OU=Services,$base" -Properties PasswordNeverExpires, Enabled, Description
        $member = @(Get-ADGroupMember -Identity 'JIM Connectors' | Where-Object { $_.SamAccountName -eq 'svc-jim' })
        $ok = $user.Enabled -and $user.PasswordNeverExpires -and ($member.Count -eq 1)
        [pscustomobject]@{ Passed = [bool]$ok; Detail = "enabled=$($user.Enabled), neverExpires=$($user.PasswordNeverExpires), member=$($member.Count -eq 1)" }
    }

    Add-Check 'Administrator never expires its password' {
        $administrator = Get-ADUser -Identity 'Administrator' -Properties PasswordNeverExpires
        [pscustomobject]@{ Passed = [bool]$administrator.PasswordNeverExpires; Detail = "neverExpires=$($administrator.PasswordNeverExpires)" }
    }

    Add-Check 'Windows Update automatic installation is off' {
        $value = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' -ErrorAction Stop).NoAutoUpdate
        [pscustomobject]@{ Passed = ($value -eq 1); Detail = "NoAutoUpdate=$value" }
    }

    Add-Check 'w32time follows the configured NTP source' {
        $configured = [string](Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Parameters').NtpServer
        [pscustomobject]@{ Passed = $configured.StartsWith($NtpServer, [System.StringComparison]::OrdinalIgnoreCase); Detail = "NtpServer=$configured" }
    }

    Add-Check 'The Hyper-V time provider is enabled in the guest' -Advisory {
        $enabled = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\VMICTimeProvider').Enabled
        [pscustomobject]@{ Passed = ($enabled -eq 1); Detail = "VMICTimeProvider Enabled=$enabled" }
    }

    $script:expectedThumbprint = ''
    Add-Check 'LDAPS serves the lab certificate with the expected names' {
        $subject = "CN=$($info.Fqdn), O=JIM Integration Testing"
        $local = @(Get-ChildItem -LiteralPath 'Cert:\LocalMachine\My' | Where-Object { $_.Subject -eq $subject })
        if ($local.Count -ne 1) { throw "expected exactly one certificate with subject '$subject' in the machine store, found $($local.Count)" }
        $served = $null
        $deadline = (Get-Date).AddMinutes(5)
        while ($null -eq $served) {
            try { $served = Test-LabLdapsEndpoint -HostName 'localhost' -Port 636 -TargetName $info.Fqdn }
            catch { if ((Get-Date) -gt $deadline) { throw }; Start-Sleep -Seconds 10 }
        }
        $script:expectedThumbprint = $local[0].Thumbprint
        $missingNames = @($names | Where-Object { $served.DnsNames -notcontains $_ })
        $ok = ($served.Thumbprint -eq $local[0].Thumbprint) -and ($missingNames.Count -eq 0) -and ($served.Subject -like "CN=$($info.Fqdn),*")
        [pscustomobject]@{ Passed = $ok; Detail = "served $($served.Thumbprint); names missing: $($missingNames -join ', ')" }
    }

    Add-Check 'A simple bind over plain LDAP is refused (LDAP signing enforced)' {
        $refused = Test-LabPlainLdapRefused -DomainDn $base -ServiceAccountPassword $ServiceAccountPassword
        [pscustomobject]@{ Passed = [bool]$refused; Detail = "refused=$refused" }
    }

    Add-Check 'The delegation is on every managed container' {
        $sid = Get-LabGroupSid
        $missing = @()
        foreach ($dn in @("OU=Corp,$base", "OU=TestUsers,$base", "OU=TestGroups,$base")) {
            # -Path, escaped: Get-Acl -LiteralPath does not work on the AD: drive (see Get-LabAdProviderPath).
            $sddl = (Get-Acl -Path (Get-LabAdProviderPath -DistinguishedName $dn) -ErrorAction Stop).GetSecurityDescriptorSddlForm([System.Security.AccessControl.AccessControlSections]::Access)
            if (-not (Test-LabSddlContainsSid -Sddl $sddl -Sid $sid)) { $missing += $dn }
        }
        [pscustomobject]@{ Passed = ($missing.Count -eq 0); Detail = ('missing on: ' + ($missing -join '; ')) }
    }

    Add-Check 'JIM Connectors can read the Deleted Objects container' {
        $sid = Get-LabGroupSid
        $ok = Test-LabAclGrant -Sddl (Get-LabTombstoneDescriptor -DomainDn $base) -Sid $sid -AccessMask 0x20014
        [pscustomobject]@{ Passed = [bool]$ok; Detail = "LC+RP+RC granted=$ok" }
    }

    # As post-provision.sh: prove the delegation by doing what JIM does, as svc-jim over LDAPS.
    $probes = @(Test-LabServiceAccountDelegation -DomainDn $base -ServiceAccountPassword $ServiceAccountPassword -ExpectedThumbprint $script:expectedThumbprint)
    foreach ($probe in $probes) {
        $script:Checks.Add([pscustomobject][ordered]@{ Name = $probe.Name; Passed = [bool]$probe.Passed; Advisory = $false; Detail = [string]$probe.Detail })
        if ($probe.Passed) { $mark = 'ok  ' } else { $mark = 'FAIL' }
        Write-Step ("{0} {1}: {2}" -f $mark, $probe.Name, $probe.Detail)
    }

    if (@($DirectoryExtension) -contains 'Exchange') {
        Add-Check 'The Exchange organisation is prepared (schema, organisation named after the forest, domain)' {
            $exchange = Get-ExchangeForestState
            $ok = ($null -ne $exchange.SchemaRangeUpper) -and ($exchange.OrganizationName -eq $NetBiosName) -and ($null -ne $exchange.OrganizationVersion) -and ($null -ne $exchange.DomainVersion)
            [pscustomobject]@{ Passed = [bool]$ok; Detail = "schema rangeUpper $($exchange.SchemaRangeUpper), organisation '$($exchange.OrganizationName)' objectVersion $($exchange.OrganizationVersion), domain objectVersion $($exchange.DomainVersion)" }
        }
    }

    Add-Check 'Windows is a full Datacenter edition, not evaluation' {
        $edition = Get-EditionId
        [pscustomobject]@{ Passed = ($edition -eq 'ServerDatacenter'); Detail = "EditionID=$edition" }
    }

    Add-Check 'Windows is activated through Automatic Virtual Machine Activation' -Advisory {
        $product = Get-CimInstance -ClassName SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL AND Name LIKE 'Windows%'" | Select-Object -First 1
        $ok = ($null -ne $product) -and ($product.LicenseStatus -eq 1)
        [pscustomobject]@{ Passed = [bool]$ok; Detail = "status=$($product.LicenseStatus), $($product.Description)" }
    }

    $failed = @($script:Checks | Where-Object { (-not $_.Passed) -and (-not $_.Advisory) })
    $script:Detail.Add(("{0} checks, {1} failed" -f $script:Checks.Count, $failed.Count))
    if ($failed.Count -gt 0) {
        throw ("Verification failed: " + (($failed | ForEach-Object { "$($_.Name) ($($_.Detail))" }) -join '; '))
    }
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------

Write-Step ('Starting; ' + (ConvertTo-LabArgumentString -Argument ([ordered]@{
                Domain       = $Domain
                ComputerName = $ComputerName
                VmName       = $VmName
            })))

switch ($Phase) {
    'License' { Invoke-LicensePhase }
    'Prepare' { Invoke-PreparePhase }
    'Promote' { Invoke-PromotePhase }
    'Extend' { Invoke-ExtendPhase }
    'Configure' { Invoke-ConfigurePhase }
    'Verify' { Invoke-VerifyPhase }
}

Write-Step ("Done; changed={0}, reboot required={1}" -f $script:Changed, $script:RebootRequired)

[pscustomobject][ordered]@{
    Phase          = $Phase
    Changed        = $script:Changed
    RebootRequired = $script:RebootRequired
    Detail         = ($script:Detail -join '; ')
}
