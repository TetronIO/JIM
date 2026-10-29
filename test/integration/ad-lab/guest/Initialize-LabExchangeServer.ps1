# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Builds one of the lab's Exchange servers from the inside, one phase at a time. Runs in the guest, under Windows
    PowerShell 5.1, driven by New-LabExchangeServer.ps1 over PowerShell Direct.

.DESCRIPTION
    Phases, run in this order with a restart between them where a phase reports RebootRequired:

      License        On evaluation media only: convert the evaluation edition to full Datacenter with the AVMA key.
      Prepare        Rename the computer, set the static IP address, resolve through the domain controller, switch
                     automatic Windows Update off, stop the machine account password changing, set Remote Registry to
                     Automatic (Exchange requires it) and fix the page file at a quarter of memory.
      Join           Join the domain as its Administrator.
      Prerequisites  Install the Windows features and the software Microsoft lists for a Mailbox server: the Visual
                     C++ 2012 and 2013 runtimes and IIS URL Rewrite (copied in by the host) and UCMA 4.0 (from the
                     Exchange media the host attaches).
      Install        Run Exchange Setup unattended to install the Mailbox role into the forest's existing
                     organisation, naming the first database.
      Configure      Through Exchange Remote PowerShell: the second database, excluded from automatic distribution;
                     svc-jim in Recipient Management; the GAL sync contacts OU; and the export and local trust of
                     Exchange's own certificate.
      Verify         Read it all back, then prove the recipient cmdlets JIM will drive: a mailbox enabled and removed on
                     a throwaway user, a mail contact enabled and removed in the GAL sync OU, and Remote PowerShell over
                     HTTPS with Kerberos, as JIM connects. Throws if any required check fails.

    Every phase is idempotent, so the host can re-run the whole sequence after a failed step. Each phase ends by
    emitting one result object, { Phase, Changed, RebootRequired, Detail }, which the host reads.

    The forest's schema and organisation are not touched here: the domain controller's build prepared them
    (/PrepareSchema, /PrepareAD, /PrepareAllDomains) from the same media, and Install refuses a forest that it did not
    prepare to this media's schema, rather than let Setup extend the schema from a member server.

    The machine account password is never changed (Netlogon DisablePasswordChange=1): the server is reverted to its
    baseline together with its domain controller, and a password changed after the baseline would match neither.

    The Administrator password arrives as a SecureString parameter and is never written to disk or logged.

.PARAMETER Phase
    License, Prepare, Join, Prerequisites, Install, Configure or Verify.

.PARAMETER ComputerName
    The server's host name, for example ex1.

.PARAMETER IPAddress
    Static IPv4 address (Prepare).

.PARAMETER PrefixLength
    Prefix length of the subnet (Prepare).

.PARAMETER Gateway
    Default gateway (Prepare).

.PARAMETER DnsServer
    The domain controller's address, the only DNS server (Prepare).

.PARAMETER Domain
    The domain to join, for example PANOPLY.LOCAL.

.PARAMETER NetBiosName
    The NetBIOS domain name, for example PANOPLY, which is also the Exchange organisation's name.

.PARAMETER AdministratorPassword
    The domain Administrator's password (Join, Configure, Verify).

.PARAMETER VmName
    The VM's name on the host, used to name the exported certificate file.

.PARAMETER LabRoot
    Where the lab files live in the guest. Default C:\jim-ad-lab. The host copies the prerequisites into its
    exchange-prerequisites folder.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'The phases narrate progress to the operator watching the host; Write-Host is forwarded through PowerShell Direct.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'The parameters are read by the phase functions below, which see them through the script scope.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal helpers of a build script; the phase is the unit of change and every helper checks for existing state first.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('License', 'Prepare', 'Join', 'Prerequisites', 'Install', 'Configure', 'Verify')]
    [string]$Phase,

    [string]$ComputerName,
    [string]$IPAddress,
    [ValidateRange(0, 32)]
    [int]$PrefixLength = 0,
    [string]$Gateway,
    [string]$DnsServer,
    [string]$Domain,
    [string]$NetBiosName,
    [securestring]$AdministratorPassword,
    [string]$VmName,
    [string]$LabRoot = 'C:\jim-ad-lab'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'LabDomainController.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'LabExchangeServer.psm1') -Force

$script:Changed = $false
$script:RebootRequired = $false
$script:Detail = New-Object System.Collections.Generic.List[string]

# The cmdlets Configure and Verify import from Exchange Remote PowerShell. Nothing else crosses the session.
$script:ExchangeCommands = @(
    'Get-ExchangeServer', 'Get-OrganizationConfig', 'Test-ServiceHealth', 'Get-MailboxDatabase', 'New-MailboxDatabase',
    'Mount-Database', 'Set-MailboxDatabase', 'Get-RoleGroupMember', 'Add-RoleGroupMember',
    'Enable-Mailbox', 'Get-Mailbox', 'Remove-Mailbox', 'Get-MailboxStatistics', 'Enable-MailContact', 'Get-MailContact',
    'Remove-MailContact'
)

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

function Get-EditionId {
    return [string](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
}

function Get-ComputerSystem {
    return (Get-CimInstance -ClassName Win32_ComputerSystem)
}

function Get-Info {
    return (Get-LabExchangeServerInfo -Domain $Domain -NetBiosName $NetBiosName -ServerName $ComputerName)
}

function Get-DomainCredential {
    $user = Get-LabAdministratorUserName -NetBiosName $NetBiosName -Promoted
    return (New-Object System.Management.Automation.PSCredential($user, $AdministratorPassword))
}

function Get-NetlogonPasswordChangeSetting {
    $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\Netlogon\Parameters'
    $existing = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    if (($null -ne $existing) -and $existing.PSObject.Properties['DisablePasswordChange']) { return [int]$existing.DisablePasswordChange }
    return 0
}

function Get-PageFileTarget {
    # A quarter of the memory the VM has (static memory, so the installed modules are the whole of it).
    $bytes = [uint64]((Get-CimInstance -ClassName Win32_PhysicalMemory | Measure-Object -Property Capacity -Sum).Sum)
    return (Get-LabExchangePageFileSize -MemoryBytes $bytes)
}

function Get-PageFileState {
    $system = Get-ComputerSystem
    $setting = @(Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction SilentlyContinue)
    return [pscustomobject]@{
        Automatic = [bool]$system.AutomaticManagedPagefile
        Settings  = $setting
    }
}

function Get-RoleKey {
    return (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\ExchangeServer\v15\MailboxRole' -ErrorAction SilentlyContinue)
}

function Get-InstalledSoftwareName {
    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    return [string[]]@(Get-ItemProperty -Path $keys -ErrorAction SilentlyContinue |
            Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName } |
            ForEach-Object { [string]$_.DisplayName })
}

function Find-ExchangeMedia {
    # The root of the Exchange media the host attached: a DVD drive holding Setup.exe and Setup\Data\SchemaVersion.ldf.
    foreach ($volume in @(Get-Volume | Where-Object { ($_.DriveType -eq 'CD-ROM') -and $_.DriveLetter })) {
        $root = "$($volume.DriveLetter):"
        if ((Test-Path -LiteralPath "$root\Setup.exe") -and (Test-Path -LiteralPath "$root\Setup\Data\SchemaVersion.ldf")) {
            return $root
        }
    }
    throw 'No Exchange media is attached: no DVD drive holds Setup.exe and Setup\Data\SchemaVersion.ldf. The host attaches it before Prerequisites and Install.'
}

function Connect-Exchange {
    <#
        Opens Exchange Remote PowerShell on this server, as the Exchange Management Shell does, and imports the
        cmdlets the build uses. Exchange's services take minutes to start after a restart, so this waits for the
        endpoint to answer, and returns how long after boot it did.
    #>
    param([int]$TimeoutSeconds = 1800)

    $info = Get-Info
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = 'no attempt was made'
    while ((Get-Date) -lt $deadline) {
        try {
            $session = New-PSSession -ConfigurationName Microsoft.Exchange -ConnectionUri $info.ManagementUri -Authentication Kerberos -Credential (Get-DomainCredential) -ErrorAction Stop
            $null = Import-PSSession -Session $session -CommandName $script:ExchangeCommands -AllowClobber -DisableNameChecking -ErrorAction Stop
            $boot = (Get-CimInstance -ClassName Win32_OperatingSystem).LastBootUpTime
            return [int]((Get-Date) - $boot).TotalSeconds
        }
        catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Seconds 15
        }
    }
    throw "Exchange Remote PowerShell at $($info.ManagementUri) did not answer within $TimeoutSeconds seconds. Last error: $lastError"
}

function Wait-ExchangeService {
    # Test-ServiceHealth until every role's required services run, which some reach minutes after the endpoint answers.
    param([int]$TimeoutSeconds = 1200)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $problems = @(Get-LabExchangeServiceHealthProblem -Health @(Test-ServiceHealth))
        if ($problems.Count -eq 0) {
            $boot = (Get-CimInstance -ClassName Win32_OperatingSystem).LastBootUpTime
            return [int]((Get-Date) - $boot).TotalSeconds
        }
        if ((Get-Date) -gt $deadline) { return $null }
        Start-Sleep -Seconds 15
    }
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
    # As for the domain controllers: DISM returns 3010 when the change needs the restart, which the host performs.
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
    if ($env:COMPUTERNAME -ne $ComputerName) {
        Rename-Computer -NewName $ComputerName -Force
        $script:RebootRequired = $true
        Set-Changed "Renamed the computer to $ComputerName"
    }

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

    $resolvers = @((Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4).ServerAddresses)
    if (($resolvers.Count -ne 1) -or ($resolvers[0] -ne $DnsServer)) {
        Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses $DnsServer
        Set-Changed "Resolve through the domain controller at $DnsServer"
    }

    # Patching is by rebuild only, as on the domain controllers.
    $key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
    if (-not (Test-Path -LiteralPath $key)) {
        $null = New-Item -Path $key -Force
    }
    $existing = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    $value = $null
    if (($null -ne $existing) -and $existing.PSObject.Properties['NoAutoUpdate']) { $value = $existing.NoAutoUpdate }
    if ($value -ne 1) {
        Set-ItemProperty -LiteralPath $key -Name NoAutoUpdate -Value 1 -Type DWord
        Set-Changed 'Switched automatic Windows Update installation off (NoAutoUpdate=1)'
    }

    # Before the join, so the password the domain records at the join is the one that never changes.
    if ((Get-NetlogonPasswordChangeSetting) -ne 1) {
        Set-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\Netlogon\Parameters' -Name DisablePasswordChange -Value 1 -Type DWord
        Set-Changed 'Stopped the machine account password changing (DisablePasswordChange=1), so a revert never breaks the secure channel'
    }

    # Exchange requires Remote Registry set to Automatic (Exchange Server 2019 and SE prerequisites).
    $remoteRegistry = Get-Service -Name 'RemoteRegistry'
    if ([string]$remoteRegistry.StartType -ne 'Automatic') {
        Set-Service -Name 'RemoteRegistry' -StartupType Automatic
        Set-Changed 'Set Remote Registry to start automatically, as Exchange requires'
    }
    if ($remoteRegistry.Status -ne 'Running') {
        Start-Service -Name 'RemoteRegistry'
    }

    # A fixed page file, a quarter of memory (Exchange's system requirements). Takes effect on restart.
    $target = Get-PageFileTarget
    $state = Get-PageFileState
    if ($state.Automatic) {
        $null = Get-CimInstance -ClassName Win32_ComputerSystem | Set-CimInstance -Property @{ AutomaticManagedPagefile = $false } -PassThru
    }
    $settings = @(Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction SilentlyContinue)
    $right = @($settings | Where-Object { ($_.Name -ieq 'C:\pagefile.sys') -and ($_.InitialSize -eq $target) -and ($_.MaximumSize -eq $target) })
    if ($state.Automatic -or ($settings.Count -ne 1) -or ($right.Count -ne 1)) {
        foreach ($other in @($settings | Where-Object { $_.Name -ine 'C:\pagefile.sys' })) { Remove-CimInstance -InputObject $other }
        $pageFile = @(Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction SilentlyContinue | Where-Object { $_.Name -ieq 'C:\pagefile.sys' })
        if ($pageFile.Count -eq 0) {
            $null = New-CimInstance -ClassName Win32_PageFileSetting -Property @{ Name = 'C:\pagefile.sys' }
            $pageFile = @(Get-CimInstance -ClassName Win32_PageFileSetting | Where-Object { $_.Name -ieq 'C:\pagefile.sys' })
        }
        $null = $pageFile[0] | Set-CimInstance -Property @{ InitialSize = [uint32]$target; MaximumSize = [uint32]$target } -PassThru
        $script:RebootRequired = $true
        Set-Changed "Fixed the page file at $target MB, a quarter of memory (applies on restart)"
    }
}

# ---------------------------------------------------------------------------------------------
# Join
# ---------------------------------------------------------------------------------------------

function Invoke-JoinPhase {
    $system = Get-ComputerSystem
    if ($system.PartOfDomain) {
        if ($system.Domain -ine $Domain) {
            throw "This server is already a member of '$($system.Domain)', not '$Domain'. Remove the VM and build it again."
        }
        Write-Step "Already a member of $($system.Domain); nothing to join."
        return
    }

    $edition = Get-EditionId
    if (Test-LabEvaluationEdition -EditionId $edition) {
        throw "Refusing to join an evaluation edition ($edition): run the License phase and restart first."
    }

    Add-Computer -DomainName $Domain -Credential (Get-DomainCredential) -Force
    $script:RebootRequired = $true
    Set-Changed "Joined $Domain (applies on restart)"
}

# ---------------------------------------------------------------------------------------------
# Prerequisites
# ---------------------------------------------------------------------------------------------

function Install-Prerequisite {
    param([object]$Prerequisite, [string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The installer for $($Prerequisite.Name) is not at $Path. The host copies it from settings.json exchangePrerequisiteDirectory before this phase."
    }
    if ($Path -like '*.msi') {
        $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList (@('/i', "`"$Path`"") + @($Prerequisite.Arguments)) -Wait -PassThru
    }
    else {
        $process = Start-Process -FilePath $Path -ArgumentList @($Prerequisite.Arguments) -Wait -PassThru
    }
    # 3010 and 1641: installed, restart needed.
    if ($process.ExitCode -notin 0, 3010, 1641) {
        throw "Installing $($Prerequisite.Name) failed (exit $($process.ExitCode))."
    }
    if ($process.ExitCode -ne 0) { $script:RebootRequired = $true }
    if (-not (Test-LabExchangePrerequisiteInstalled -Prerequisite $Prerequisite -InstalledName (Get-InstalledSoftwareName))) {
        throw "$($Prerequisite.Name) reported success (exit $($process.ExitCode)) but is not listed as installed software."
    }
    Set-Changed "Installed $($Prerequisite.Name)"
}

function Invoke-PrerequisitesPhase {
    $features = @(Get-LabExchangeWindowsFeature)
    $missing = @(Get-WindowsFeature -Name $features | Where-Object { -not $_.Installed })
    $unknown = @($features | Where-Object { $_ -notin @(Get-WindowsFeature -Name $features | ForEach-Object { $_.Name }) })
    if ($unknown.Count -gt 0) {
        throw ('Windows does not know these features Exchange needs: ' + ($unknown -join ', '))
    }
    if ($missing.Count -gt 0) {
        Write-Step ("Installing {0} Windows features (several minutes)" -f $missing.Count)
        $install = Install-WindowsFeature -Name ($missing | ForEach-Object { $_.Name })
        if (-not $install.Success) {
            throw 'Installing the Windows features Exchange needs did not succeed.'
        }
        if ([string]$install.RestartNeeded -eq 'Yes') { $script:RebootRequired = $true }
        Set-Changed ('Installed {0} Windows features: {1}' -f $missing.Count, (($missing | ForEach-Object { $_.Name }) -join ', '))
    }

    $media = $null
    foreach ($prerequisite in @(Get-LabExchangePrerequisite)) {
        if (Test-LabExchangePrerequisiteInstalled -Prerequisite $prerequisite -InstalledName (Get-InstalledSoftwareName)) {
            Write-Step "$($prerequisite.Name) is already installed."
            continue
        }
        if ($prerequisite.Source -eq 'Media') {
            if ($null -eq $media) { $media = Find-ExchangeMedia }
            $path = Join-Path $media $prerequisite.FileName
        }
        else {
            $path = Join-Path (Join-Path $LabRoot 'exchange-prerequisites') $prerequisite.FileName
        }
        Write-Step "Installing $($prerequisite.Name)"
        Install-Prerequisite -Prerequisite $prerequisite -Path $path
    }

    # A clean start before Setup, whose readiness checks refuse a server with a restart pending.
    if ($script:Changed) { $script:RebootRequired = $true }
}

# ---------------------------------------------------------------------------------------------
# Install
# ---------------------------------------------------------------------------------------------

function Assert-ForestPrepared {
    # The domain controller's build prepared the forest from the same media; refuse anything else rather than let
    # Setup prepare the schema or the organisation from here.
    param([string]$Media)

    Import-Module ActiveDirectory -ErrorAction Stop -WarningAction SilentlyContinue
    $rootDse = Get-ADRootDSE
    $target = Get-LabExchangeSchemaTarget -LdfText (Get-Content -LiteralPath "$Media\Setup\Data\SchemaVersion.ldf" -Raw)
    $schema = Get-ADObject -Identity "CN=ms-Exch-Schema-Version-Pt,$($rootDse.schemaNamingContext)" -Properties rangeUpper -ErrorAction SilentlyContinue
    $rangeUpper = $null
    if ($null -ne $schema) { $rangeUpper = $schema.rangeUpper }
    if ($rangeUpper -ne $target) {
        throw "The forest's Exchange schema is '$rangeUpper' but this media's is $target. Rebuild the domain controller with this media in settings.json directoryExtensions; this build does not prepare forests."
    }

    $expected = Get-LabExchangeOrganizationName -NetBiosName $NetBiosName
    $organizations = @(Get-ADObject -SearchBase "CN=Microsoft Exchange,CN=Services,$($rootDse.configurationNamingContext)" -SearchScope OneLevel -LDAPFilter '(objectClass=msExchOrganizationContainer)' -ErrorAction SilentlyContinue)
    if (($organizations.Count -ne 1) -or ($organizations[0].Name -ne $expected)) {
        throw ("The forest should hold the one Exchange organisation '$expected' that its domain controller's build prepared; it holds: " + $(if ($organizations.Count) { ($organizations | ForEach-Object { $_.Name }) -join ', ' } else { 'none' }))
    }
    Write-Step "The forest is prepared: schema $rangeUpper, organisation '$expected'."
}

function Invoke-InstallPhase {
    $info = Get-Info
    $state = Get-LabExchangeRoleState -RoleKey (Get-RoleKey)
    if ($state -eq 'Installed') {
        Write-Step "The Mailbox role is already installed ($((Get-RoleKey).ConfiguredVersion))."
        return
    }

    $media = Find-ExchangeMedia
    Assert-ForestPrepared -Media $media
    if ($state -eq 'Partial') {
        Write-Step 'An earlier Setup left the Mailbox role part-installed; running Setup again resumes it.'
    }

    # Setup writes its log to C:\ExchangeSetupLogs\ExchangeSetup.log.
    $arguments = Get-LabExchangeSetupInstallArgument -DatabaseName $info.ProvisioningDatabase
    Write-Step "Exchange Setup $($arguments -join ' ') (typically 45 minutes or more)"
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $output = & "$media\Setup.exe" @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $clock.Stop()
    if ($exitCode -ne 0) {
        $log = 'C:\ExchangeSetupLogs\ExchangeSetup.log'
        $tail = if (Test-Path -LiteralPath $log) { (Get-Content -LiteralPath $log -Tail 20) -join ' | ' } else { (($output | Select-Object -Last 20) -join ' | ') }
        throw "Exchange Setup failed (exit $exitCode) after $([int]$clock.Elapsed.TotalMinutes) minutes: $tail"
    }
    $after = Get-LabExchangeRoleState -RoleKey (Get-RoleKey)
    if ($after -ne 'Installed') {
        throw "Exchange Setup exited 0 but the Mailbox role reads $after. See C:\ExchangeSetupLogs\ExchangeSetup.log."
    }
    $script:RebootRequired = $true
    Set-Changed ("Installed the Exchange Mailbox role {0} in {1} minutes" -f (Get-RoleKey).ConfiguredVersion, [int]$clock.Elapsed.TotalMinutes)
}

# ---------------------------------------------------------------------------------------------
# Configure
# ---------------------------------------------------------------------------------------------

function Set-ExcludedDatabase {
    param([object]$Info)

    $name = $Info.ExcludedDatabase
    $database = @(Get-MailboxDatabase -Server $Info.ComputerName | Where-Object { [string]$_.Name -eq $name })
    if ($database.Count -eq 0) {
        $paths = Get-LabExchangeDatabasePath -Name $name
        $null = New-MailboxDatabase -Name $name -Server $Info.ComputerName -EdbFilePath $paths.EdbFilePath -LogFolderPath $paths.LogFolderPath
        Set-Changed "Created the mailbox database $name"
    }

    $status = Get-MailboxDatabase -Identity $name -Status
    if (-not $status.Mounted) {
        # A new database can take a moment to be known to the Information Store.
        $deadline = (Get-Date).AddMinutes(5)
        while ($true) {
            try {
                Mount-Database -Identity $name -Confirm:$false -ErrorAction Stop
                break
            }
            catch {
                if ((Get-Date) -gt $deadline) { throw }
                Start-Sleep -Seconds 15
            }
        }
        Set-Changed "Mounted $name"
    }
    if (-not $status.IsExcludedFromProvisioning) {
        Set-MailboxDatabase -Identity $name -IsExcludedFromProvisioning $true
        Set-Changed "Excluded $name from automatic mailbox distribution"
    }

    $first = Get-MailboxDatabase -Identity $Info.ProvisioningDatabase
    if ($first.IsExcludedFromProvisioning) {
        Set-MailboxDatabase -Identity $Info.ProvisioningDatabase -IsExcludedFromProvisioning $false
        Set-Changed "Opened $($Info.ProvisioningDatabase) to automatic mailbox distribution"
    }
}

function Set-ExchangeCertificateTrust {
    # Exchange's own self-signed certificate, which Setup binds to IIS for the server's names: exported for the runner
    # as the LDAPS and AD FS certificates are, and trusted on the server itself so Verify can reach it over HTTPS.
    param([object]$Info)

    # The certificate IIS serves on every address, read from the store: Get-ExchangeCertificate through Remote
    # PowerShell loses the properties that would say which one it is.
    Import-Module WebAdministration
    $binding = @(Get-ChildItem -Path 'IIS:\SslBindings' | Where-Object { $_.Port -eq 443 -and [string]$_.IPAddress -eq '0.0.0.0' })
    if ($binding.Count -eq 0) {
        throw 'IIS has no certificate bound to port 443 on all addresses.'
    }
    $thumbprint = [string]$binding[0].Thumbprint
    $certificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$thumbprint" -ErrorAction SilentlyContinue
    if ($null -eq $certificate -or -not (Test-LabExchangeIisCertificate -Certificate $certificate -Fqdn $Info.Fqdn)) {
        throw "The certificate IIS serves on port 443 ($thumbprint) is not Exchange's self-signed one for $($Info.Fqdn)."
    }

    $exportPath = Join-Path $LabRoot ("{0}-ca.cer" -f $VmName)
    $null = Export-Certificate -Cert "Cert:\LocalMachine\My\$thumbprint" -FilePath $exportPath -Type CERT -Force

    $root = 'Cert:\LocalMachine\Root'
    if (-not (Get-ChildItem -LiteralPath $root | Where-Object { $_.Thumbprint -eq $thumbprint })) {
        $null = Import-Certificate -FilePath $exportPath -CertStoreLocation $root
        Set-Changed "Trusted Exchange's certificate for $($Info.Fqdn) on the server itself (machine Trusted Root store)"
    }
    return $thumbprint
}

function Invoke-ConfigurePhase {
    $info = Get-Info
    Write-Step 'Waiting for Exchange Remote PowerShell (its services start minutes after boot)'
    $seconds = Connect-Exchange
    Write-Step "Exchange Remote PowerShell answered $seconds seconds after boot."

    Set-ExcludedDatabase -Info $info

    $members = @(Get-RoleGroupMember -Identity 'Recipient Management' | ForEach-Object { [string]$_.DistinguishedName })
    if ($members -notcontains $info.ServiceAccountDn) {
        Add-RoleGroupMember -Identity 'Recipient Management' -Member $info.ServiceAccountDn -BypassSecurityGroupManagerCheck
        Set-Changed "Added svc-jim to Recipient Management"
    }

    Import-Module ActiveDirectory -ErrorAction Stop -WarningAction SilentlyContinue
    if (-not (Get-ADOrganizationalUnit -LDAPFilter "(ou=$($info.ContactOuName))" -SearchBase "OU=Corp,$($info.DomainDn)" -SearchScope OneLevel)) {
        New-ADOrganizationalUnit -Name $info.ContactOuName -Path "OU=Corp,$($info.DomainDn)" -ProtectedFromAccidentalDeletion $false -Description 'Mail contacts for the other forest''s recipients (GAL sync)'
        Set-Changed "Created $($info.ContactOuDn)"
    }

    $null = Set-ExchangeCertificateTrust -Info $info
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

function Test-MailboxRoundTrip {
    # A throwaway user, mail-enabled as JIM will, then removed with its mailbox purged, so nothing is left in the
    # database the baseline captures. With MDB02 excluded, automatic distribution must choose MDB01.
    param([object]$Info)

    $sam = 'lab-verify-mbx'
    $existing = Get-ADUser -Filter "SamAccountName -eq '$sam'" -ErrorAction SilentlyContinue
    if ($existing) { Remove-ADUser -Identity $existing -Confirm:$false }
    try {
        New-ADUser -Name 'Lab Verify Mailbox' -SamAccountName $sam -Path "CN=Users,$($Info.DomainDn)" -Enabled $false
        $null = Enable-Mailbox -Identity $sam
        $mailbox = Get-Mailbox -Identity $sam
        $database = [string]$mailbox.Database
        Remove-Mailbox -Identity $sam -Permanent $true -Confirm:$false
        $gone = -not (Get-ADUser -Filter "SamAccountName -eq '$sam'")
        return [pscustomobject]@{
            Passed = (($database -eq $Info.ProvisioningDatabase) -and $gone)
            Detail = "Enable-Mailbox put it in $database; Remove-Mailbox -Permanent removed it$(if (-not $gone) { ', but the user remains' })"
        }
    }
    finally {
        $left = Get-ADUser -Filter "SamAccountName -eq '$sam'" -ErrorAction SilentlyContinue
        if ($left) { Remove-ADUser -Identity $left -Confirm:$false }
    }
}

function Test-MailContactRoundTrip {
    # A throwaway contact in the GAL sync OU, mail-enabled as GAL sync will, then removed.
    param([object]$Info)

    $name = 'Lab Verify Contact'
    $dn = "CN=$name,$($Info.ContactOuDn)"
    $existing = Get-ADObject -Filter "distinguishedName -eq '$dn'" -ErrorAction SilentlyContinue
    if ($existing) { Remove-ADObject -Identity $existing -Confirm:$false }
    try {
        New-ADObject -Type contact -Name $name -Path $Info.ContactOuDn
        $null = Enable-MailContact -Identity $dn -ExternalEmailAddress 'lab-verify@example.invalid'
        $contact = Get-MailContact -Identity $dn
        $address = [string]$contact.ExternalEmailAddress
        Remove-MailContact -Identity $dn -Confirm:$false
        $gone = -not (Get-ADObject -Filter "distinguishedName -eq '$dn'")
        return [pscustomobject]@{
            Passed = (($address -match 'lab-verify@example\.invalid') -and $gone)
            Detail = "Enable-MailContact gave $address in $($Info.ContactOuName); Remove-MailContact removed it$(if (-not $gone) { ', but the contact remains' })"
        }
    }
    finally {
        $left = Get-ADObject -Filter "distinguishedName -eq '$dn'" -ErrorAction SilentlyContinue
        if ($left) { Remove-ADObject -Identity $left -Confirm:$false }
    }
}

function Invoke-VerifyPhase {
    $info = Get-Info
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Import-Module ActiveDirectory -ErrorAction Stop -WarningAction SilentlyContinue

    Write-Step 'Waiting for Exchange Remote PowerShell and Exchange''s services to come up on their own'
    $answered = Connect-Exchange
    $healthy = Wait-ExchangeService
    $script:Detail.Add("Remote PowerShell answered ${answered}s after boot; services healthy $(if ($null -ne $healthy) { "${healthy}s" } else { 'never' }) after boot")
    Write-Step "info start-up: Remote PowerShell ${answered}s after boot, services healthy $(if ($null -ne $healthy) { "${healthy}s" } else { 'not within 20 minutes' })"

    Add-Check 'Windows is a full Datacenter edition, not evaluation' {
        $edition = Get-EditionId
        [pscustomobject]@{ Passed = ($edition -eq 'ServerDatacenter'); Detail = "EditionID=$edition" }
    }

    Add-Check 'Windows is activated through Automatic Virtual Machine Activation' -Advisory {
        $product = Get-CimInstance -ClassName SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL AND Name LIKE 'Windows%'" | Select-Object -First 1
        $ok = ($null -ne $product) -and ($product.LicenseStatus -eq 1)
        [pscustomobject]@{ Passed = [bool]$ok; Detail = "status=$($product.LicenseStatus), $($product.Description)" }
    }

    Add-Check "The server is a member of $Domain with a working secure channel" {
        $system = Get-ComputerSystem
        $channel = Test-ComputerSecureChannel
        [pscustomobject]@{ Passed = ($system.PartOfDomain -and ($system.Domain -ieq $Domain) -and $channel); Detail = "domain=$($system.Domain), secure channel=$channel" }
    }

    Add-Check 'The machine account password never changes' {
        $value = Get-NetlogonPasswordChangeSetting
        [pscustomobject]@{ Passed = ($value -eq 1); Detail = "DisablePasswordChange=$value" }
    }

    Add-Check 'Automatic Windows Update installation is off' {
        $value = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' -ErrorAction Stop).NoAutoUpdate
        [pscustomobject]@{ Passed = ($value -eq 1); Detail = "NoAutoUpdate=$value" }
    }

    Add-Check 'Remote Registry starts automatically' {
        $service = Get-Service -Name 'RemoteRegistry'
        [pscustomobject]@{ Passed = ([string]$service.StartType -eq 'Automatic'); Detail = "$($service.Status), start $($service.StartType)" }
    }

    Add-Check 'The page file is fixed at a quarter of memory' {
        $target = Get-PageFileTarget
        $state = Get-PageFileState
        $sizes = @($state.Settings | ForEach-Object { "$($_.Name) $($_.InitialSize)-$($_.MaximumSize) MB" }) -join ', '
        $right = @($state.Settings | Where-Object { ($_.InitialSize -eq $target) -and ($_.MaximumSize -eq $target) })
        [pscustomobject]@{ Passed = ((-not $state.Automatic) -and (@($state.Settings).Count -eq 1) -and ($right.Count -eq 1)); Detail = "want $target MB; $sizes" }
    }

    Add-Check 'The prerequisites are installed' {
        $names = Get-InstalledSoftwareName
        $missing = @(Get-LabExchangePrerequisite | Where-Object { -not (Test-LabExchangePrerequisiteInstalled -Prerequisite $_ -InstalledName $names) } | ForEach-Object { $_.Name })
        [pscustomobject]@{ Passed = ($missing.Count -eq 0); Detail = $(if ($missing.Count) { 'missing: ' + ($missing -join ', ') } else { 'all four' }) }
    }

    Add-Check 'The Exchange Mailbox role is installed' {
        $key = Get-RoleKey
        $state = Get-LabExchangeRoleState -RoleKey $key
        [pscustomobject]@{ Passed = ($state -eq 'Installed'); Detail = "$state $(if ($key) { $key.ConfiguredVersion })" }
    }

    Add-Check 'Every role''s required Exchange services are running' {
        $problems = @(Get-LabExchangeServiceHealthProblem -Health @(Test-ServiceHealth))
        [pscustomobject]@{ Passed = ($problems.Count -eq 0); Detail = $(if ($problems.Count) { $problems -join '; ' } else { 'Test-ServiceHealth: all running' }) }
    }

    Add-Check "Exchange knows $($info.ComputerName) as a Mailbox server" {
        $server = Get-ExchangeServer -Identity $info.ComputerName
        [pscustomobject]@{ Passed = ([string]$server.ServerRole -match 'Mailbox'); Detail = "role $($server.ServerRole), $($server.AdminDisplayVersion)" }
    }

    Add-Check "The organisation is the forest's own" {
        $expected = Get-LabExchangeOrganizationName -NetBiosName $NetBiosName
        $name = [string](Get-OrganizationConfig).Name
        [pscustomobject]@{ Passed = ($name -eq $expected); Detail = "organisation '$name'" }
    }

    Add-Check "The databases are $($info.ProvisioningDatabase) and $($info.ExcludedDatabase), the second excluded from distribution" {
        $databases = @(Get-MailboxDatabase -Server $info.ComputerName -Status)
        $problems = @(Test-LabExchangeDatabaseLayout -Database $databases -Info $info)
        [pscustomobject]@{ Passed = ($problems.Count -eq 0); Detail = $(if ($problems.Count) { $problems -join ' ' } else { (($databases | ForEach-Object { "$($_.Name) mounted, excluded=$($_.IsExcludedFromProvisioning)" }) -join '; ') }) }
    }

    Add-Check 'svc-jim is in Recipient Management' {
        $members = @(Get-RoleGroupMember -Identity 'Recipient Management' | ForEach-Object { [string]$_.DistinguishedName })
        [pscustomobject]@{ Passed = ($members -contains $info.ServiceAccountDn); Detail = "$($members.Count) member(s)" }
    }

    Add-Check "The GAL sync contacts OU exists" {
        $ou = Get-ADOrganizationalUnit -Identity $info.ContactOuDn
        [pscustomobject]@{ Passed = ($null -ne $ou); Detail = $info.ContactOuDn }
    }

    Add-Check 'A mailbox can be enabled on a user and removed, and automatic distribution honours the exclusion' {
        Test-MailboxRoundTrip -Info $info
    }

    Add-Check 'A mail contact can be enabled in the GAL sync OU and removed' {
        Test-MailContactRoundTrip -Info $info
    }

    Add-Check 'No disconnected mailbox is left in either database' {
        $left = @(foreach ($name in $info.ProvisioningDatabase, $info.ExcludedDatabase) {
                Get-MailboxStatistics -Database $name | Where-Object { $_.PSObject.Properties['DisconnectReason'] -and $_.DisconnectReason }
            })
        [pscustomobject]@{ Passed = ($left.Count -eq 0); Detail = "$($left.Count) disconnected" }
    }

    Add-Check "Remote PowerShell answers over HTTPS with Kerberos at $($info.PowerShellUri), as JIM connects" {
        # Bounded: without timeouts an HTTPS connection that never completes held Verify for hours.
        $options = New-PSSessionOption -OpenTimeout 120000 -OperationTimeout 300000 -CancelTimeout 60000
        $session = New-PSSession -ConfigurationName Microsoft.Exchange -ConnectionUri $info.PowerShellUri -Authentication Kerberos -Credential (Get-DomainCredential) -SessionOption $options -ErrorAction Stop
        try {
            $server = Invoke-Command -Session $session -ScriptBlock { Get-ExchangeServer } | Select-Object -First 1
            [pscustomobject]@{ Passed = ([string]$server.Name -ieq $info.ComputerName); Detail = "Get-ExchangeServer answered $($server.Name)" }
        }
        finally {
            Remove-PSSession -Session $session -ErrorAction SilentlyContinue
        }
    }

    Add-Check "The certificate is exported as $VmName-ca.cer for the runner" {
        $path = Join-Path $LabRoot ("{0}-ca.cer" -f $VmName)
        [pscustomobject]@{ Passed = (Test-Path -LiteralPath $path -PathType Leaf); Detail = $path }
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
    'Join' { Invoke-JoinPhase }
    'Prerequisites' { Invoke-PrerequisitesPhase }
    'Install' { Invoke-InstallPhase }
    'Configure' { Invoke-ConfigurePhase }
    'Verify' { Invoke-VerifyPhase }
}

Get-PSSession | Where-Object { $_.ConfigurationName -eq 'Microsoft.Exchange' } | Remove-PSSession -ErrorAction SilentlyContinue

Write-Step ("Done; changed={0}, reboot required={1}" -f $script:Changed, $script:RebootRequired)

[pscustomobject][ordered]@{
    Phase          = $Phase
    Changed        = $script:Changed
    RebootRequired = $script:RebootRequired
    Detail         = ($script:Detail -join '; ')
}
