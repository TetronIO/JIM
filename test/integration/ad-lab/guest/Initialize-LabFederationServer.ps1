# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Builds the lab's AD FS server from the inside, one phase at a time. Runs in the guest, under Windows PowerShell
    5.1, driven by New-LabFederationServer.ps1 over PowerShell Direct.

.DESCRIPTION
    Phases, run in this order with a restart between them where a phase reports RebootRequired:

      License  On evaluation media only: convert the evaluation edition to full Datacenter with the AVMA key (one
               restart), after which the guest activates through Automatic Virtual Machine Activation.
      Prepare  Rename the computer, set the static IP address, resolve through the domain controller, switch
               automatic Windows Update off, and stop the machine account password changing (see below).
      Join     Join the domain as its Administrator.
      Farm     Install the AD FS role, create and trust the TLS certificate for the federation service, and create
               the farm (Windows Internal Database) running as the group managed service account the host created on
               the domain controller. Sign-in is set to forms for intranet and extranet alike.
      Verify   Read it all back, then fetch the discovery document and the key set over HTTPS by the federation
               service's name, as JIM will. Throws if any required check fails.

    Every phase is idempotent, so the host can re-run the whole sequence after a failed step. Each phase ends by
    emitting one result object, { Phase, Changed, RebootRequired, Detail }, which the host reads.

    The machine account password is never changed (Netlogon DisablePasswordChange=1). This server is reverted to its
    baseline together with dc-primary, every time. A password changed after the baseline would match neither
    checkpoint, and the server would lose its secure channel to the domain. The gMSA needs nothing similar: its
    password is derived from the KDS root key and the time, not stored.

    Forms sign-in everywhere: AD FS otherwise offers Windows Integrated Authentication on the intranet to browsers it
    recognises, which a headless browser on a machine outside the domain cannot complete.

    The Administrator password arrives as a SecureString parameter and is never written to disk or logged.

.PARAMETER Phase
    License, Prepare, Join, Farm or Verify.

.PARAMETER ComputerName
    The server's host name, for example adfs1.

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
    The NetBIOS domain name, for example PANOPLY.

.PARAMETER ServiceName
    The federation service's host label, for example adfs (giving adfs.panoply.local).

.PARAMETER AdministratorPassword
    The domain Administrator's password (Join, Farm).

.PARAMETER VmName
    The VM's name on the host, used to name the exported certificate file.

.PARAMETER LabRoot
    Where the lab files live in the guest. Default C:\jim-ad-lab.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'The phases narrate progress to the operator watching the host; Write-Host is forwarded through PowerShell Direct.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'The parameters are read by the phase functions below, which see them through the script scope.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal helpers of a build script; the phase is the unit of change and every helper checks for existing state first.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('License', 'Prepare', 'Join', 'Farm', 'Verify')]
    [string]$Phase,

    [string]$ComputerName,
    [string]$IPAddress,
    [ValidateRange(0, 32)]
    [int]$PrefixLength = 0,
    [string]$Gateway,
    [string]$DnsServer,
    [string]$Domain,
    [string]$NetBiosName,
    [string]$ServiceName,
    [securestring]$AdministratorPassword,
    [string]$VmName,
    [string]$LabRoot = 'C:\jim-ad-lab'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'LabDomainController.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'LabFederationServer.psm1') -Force

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

function Get-EditionId {
    return [string](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
}

function Get-ComputerSystem {
    return (Get-CimInstance -ClassName Win32_ComputerSystem)
}

function Get-Info {
    return (Get-LabFederationInfo -Domain $Domain -NetBiosName $NetBiosName -ServerName $ComputerName -ServiceName $ServiceName)
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

# ---------------------------------------------------------------------------------------------
# License
# ---------------------------------------------------------------------------------------------

function Invoke-LicensePhase {
    $edition = Get-EditionId
    if (-not (Test-LabEvaluationEdition -EditionId $edition)) {
        Write-Step "Edition $edition is not an evaluation edition; nothing to convert."
        return
    }
    # As for the domain controllers (Initialize-LabDomainController.ps1): the AVMA key converts ServerDatacenterEval to
    # ServerDatacenter, and DISM returns 3010 when the change needs the restart, which the host performs.
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
# Farm
# ---------------------------------------------------------------------------------------------

function Set-FederationCertificate {
    param([object]$Info)

    $names = @(Get-LabFederationCertificateName -Info $Info)
    $subject = "CN=$($Info.ServiceFqdn), O=JIM Integration Testing"
    $store = 'Cert:\LocalMachine\My'

    $reusable = @(Get-ChildItem -LiteralPath $store | Where-Object {
            ($_.Subject -eq $subject) -and $_.HasPrivateKey -and ($_.NotAfter -gt (Get-Date).AddDays(30)) -and
            ((($_.DnsNameList | ForEach-Object { $_.Unicode.ToLowerInvariant() } | Sort-Object) -join ',') -eq (($names | Sort-Object) -join ','))
        })
    if ($reusable.Count -gt 0) {
        $certificate = $reusable | Sort-Object -Property NotAfter -Descending | Select-Object -First 1
    }
    else {
        Get-ChildItem -LiteralPath $store | Where-Object { $_.Subject -eq $subject } | Remove-Item -Force
        # A self-signed server-authentication certificate, as for LDAPS on the domain controllers: the runner
        # trusts the exported public half, the way it trusts theirs. AD FS reads the private key as its service
        # account; Install-AdfsFarm grants the gMSA access to it.
        $certificate = New-SelfSignedCertificate `
            -DnsName $names `
            -Subject $subject `
            -CertStoreLocation $store `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -NotAfter (Get-Date).AddYears(10) `
            -KeyExportPolicy NonExportable `
            -KeyUsage DigitalSignature, KeyEncipherment `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1')
        Set-Changed ('Created the federation service TLS certificate for ' + ($names -join ', '))
    }

    $exportPath = Join-Path $LabRoot ("{0}-ca.cer" -f $VmName)
    $null = Export-Certificate -Cert $certificate -FilePath $exportPath -Type CERT -Force

    # Self-signed, so its own root: the server must trust it to call itself (Verify does) and for AD FS's own
    # health checks.
    $root = 'Cert:\LocalMachine\Root'
    Get-ChildItem -LiteralPath $root | Where-Object { ($_.Subject -eq $subject) -and ($_.Thumbprint -ne $certificate.Thumbprint) } | Remove-Item -Force
    if (-not (Get-ChildItem -LiteralPath $root | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint })) {
        $null = Import-Certificate -FilePath $exportPath -CertStoreLocation $root
        Set-Changed 'Trusted the federation service certificate on the server itself (machine Trusted Root store)'
    }
    return $certificate
}

function Test-AdfsFarm {
    try {
        $null = Get-AdfsProperties -ErrorAction Stop
        return $true
    }
    catch {
        return $false
    }
}

function Invoke-FarmPhase {
    $info = Get-Info

    $wanted = @('ADFS-Federation', 'RSAT-AD-PowerShell')
    $missing = @(Get-WindowsFeature -Name $wanted | Where-Object { -not $_.Installed })
    if ($missing.Count -gt 0) {
        $install = Install-WindowsFeature -Name $wanted -IncludeManagementTools
        if (-not $install.Success) {
            throw 'Installing the AD FS role did not succeed.'
        }
        if ([string]$install.RestartNeeded -eq 'Yes') { $script:RebootRequired = $true }
        Set-Changed ('Installed: ' + (($missing | ForEach-Object { $_.Name }) -join ', '))
    }

    $certificate = Set-FederationCertificate -Info $info

    Import-Module ADFS -ErrorAction Stop
    if (-not (Test-AdfsFarm)) {
        # The host created the gMSA on the domain controller, allowed this computer to retrieve its password, and
        # registered the federation service's name in DNS. Test-ADServiceAccount proves the first two from here.
        Import-Module ActiveDirectory -ErrorAction Stop
        if (-not (Test-ADServiceAccount -Identity $info.GmsaName)) {
            throw "This server cannot use the service account $($info.GmsaAccount). The host creates it on the domain controller before this phase; check it allows $($info.ComputerAccount) to retrieve its password."
        }

        # Windows Internal Database: a single-server farm, the simplest that works.
        #   https://learn.microsoft.com/windows-server/identity/ad-fs/deployment/deploying-a-federation-server-farm
        $null = Install-AdfsFarm `
            -CertificateThumbprint $certificate.Thumbprint `
            -FederationServiceName $info.ServiceFqdn `
            -FederationServiceDisplayName 'JIM Integration Testing' `
            -GroupServiceAccountIdentifier $info.GmsaAccount `
            -Credential (Get-DomainCredential) `
            -OverwriteConfiguration
        $script:RebootRequired = $true
        Set-Changed "Created the AD FS farm $($info.ServiceFqdn), running as $($info.GmsaAccount)"
    }
    else {
        $properties = Get-AdfsProperties
        if ($properties.HostName -ine $info.ServiceFqdn) {
            throw "This server already runs the AD FS farm '$($properties.HostName)', not '$($info.ServiceFqdn)'. Remove the VM and build it again."
        }
        Write-Step "The AD FS farm $($properties.HostName) already exists."
    }

    # Forms sign-in on the intranet as well as the extranet (see the description above).
    $policy = Get-AdfsGlobalAuthenticationPolicy
    $intranet = @($policy.PrimaryIntranetAuthenticationProvider)
    $extranet = @($policy.PrimaryExtranetAuthenticationProvider)
    if (($intranet.Count -ne 1) -or ($intranet[0] -ne 'FormsAuthentication') -or ($extranet -notcontains 'FormsAuthentication')) {
        Set-AdfsGlobalAuthenticationPolicy -PrimaryIntranetAuthenticationProvider @('FormsAuthentication') -PrimaryExtranetAuthenticationProvider @('FormsAuthentication')
        Set-Changed 'Set sign-in to forms for intranet and extranet'
    }
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
    $info = Get-Info
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Import-Module ADFS -ErrorAction Stop

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

    Add-Check 'The AD FS service is running' {
        $service = Get-Service -Name 'adfssrv'
        [pscustomobject]@{ Passed = ($service.Status -eq 'Running'); Detail = "$($service.Status), start $($service.StartType)" }
    }

    Add-Check "The farm is $($info.ServiceFqdn), running as $($info.GmsaAccount)" {
        $properties = Get-AdfsProperties
        $account = (Get-CimInstance -ClassName Win32_Service -Filter "Name='adfssrv'").StartName
        [pscustomobject]@{ Passed = (($properties.HostName -ieq $info.ServiceFqdn) -and ($account -ieq $info.GmsaAccount)); Detail = "host=$($properties.HostName), account=$account" }
    }

    Add-Check 'Sign-in is forms for intranet and extranet' {
        $policy = Get-AdfsGlobalAuthenticationPolicy
        $intranet = @($policy.PrimaryIntranetAuthenticationProvider) -join ','
        $extranet = @($policy.PrimaryExtranetAuthenticationProvider) -join ','
        [pscustomobject]@{ Passed = (($intranet -eq 'FormsAuthentication') -and ($extranet -match 'FormsAuthentication')); Detail = "intranet=$intranet, extranet=$extranet" }
    }

    Add-Check "$($info.ServiceFqdn) resolves to this server" {
        $addresses = @(Resolve-DnsName -Name $info.ServiceFqdn -Type A -ErrorAction Stop | Where-Object { $_.Type -eq 'A' } | ForEach-Object { $_.IPAddress })
        $mine = @(Get-NetIPAddress -AddressFamily IPv4 | ForEach-Object { $_.IPAddress })
        [pscustomobject]@{ Passed = (($addresses.Count -eq 1) -and ($mine -contains $addresses[0])); Detail = ($addresses -join ', ') }
    }

    $script:Discovery = $null
    Add-Check 'The OpenID Connect discovery document is what JIM relies on' {
        $script:Discovery = Invoke-RestMethod -Uri "$($info.ServiceUrl)/.well-known/openid-configuration" -UseBasicParsing
        $problems = @(Test-LabFederationDiscovery -Document $script:Discovery -ServiceFqdn $info.ServiceFqdn)
        [pscustomobject]@{ Passed = ($problems.Count -eq 0); Detail = $(if ($problems.Count -eq 0) { "issuer $($script:Discovery.issuer)" } else { $problems -join ' ' }) }
    }
    $discovery = $script:Discovery

    if ($null -ne $discovery) {
        # Not judged: which issuer AD FS puts in access tokens is one of the questions the lab answers (#2355).
        $accessTokenIssuer = '(none published)'
        if ($discovery.PSObject.Properties['access_token_issuer']) { $accessTokenIssuer = [string]$discovery.access_token_issuer }
        $script:Detail.Add("access_token_issuer=$accessTokenIssuer")
        Write-Step "info access_token_issuer: $accessTokenIssuer"

        Add-Check 'The key set holds a signing key' {
            $keys = Invoke-RestMethod -Uri ([string]$discovery.jwks_uri) -UseBasicParsing
            $problems = @(Test-LabFederationKeySet -Document $keys)
            [pscustomobject]@{ Passed = ($problems.Count -eq 0); Detail = $(if ($problems.Count -eq 0) { "$(@($keys.keys).Count) key(s)" } else { $problems -join ' ' }) }
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
                ServiceName  = $ServiceName
                VmName       = $VmName
            })))

switch ($Phase) {
    'License' { Invoke-LicensePhase }
    'Prepare' { Invoke-PreparePhase }
    'Join' { Invoke-JoinPhase }
    'Farm' { Invoke-FarmPhase }
    'Verify' { Invoke-VerifyPhase }
}

Write-Step ("Done; changed={0}, reboot required={1}" -f $script:Changed, $script:RebootRequired)

[pscustomobject][ordered]@{
    Phase          = $Phase
    Changed        = $script:Changed
    RebootRequired = $script:RebootRequired
    Detail         = ($script:Detail -join '; ')
}
