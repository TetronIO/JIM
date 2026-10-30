# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The Exchange servers of the Active Directory lab: names, settings, prerequisites, guest phase arguments and the
    checks that prove a Mailbox server works.

.DESCRIPTION
    Pure functions only, tested on any platform by LabExchangeServer.Tests.ps1. The host (New-LabExchangeServer.ps1)
    and the guest (Initialize-LabExchangeServer.ps1, under Windows PowerShell 5.1) both import it, so it keeps to
    syntax 5.1 understands. Everything the Exchange servers share with the domain controllers (VM creation, PowerShell
    Direct, checkpoints) stays in LabDomainController.psm1.
#>

Set-StrictMode -Version Latest

function Get-LabExchangeServerInfo {
    <#
    .SYNOPSIS
        Derives the names an Exchange server needs: its own, its endpoints, its databases, the GAL sync contacts OU
        and JIM's service account in its forest.

    .DESCRIPTION
        JIM reaches Exchange through Remote PowerShell over HTTPS (PowerShellUri); the build manages the server through
        the same endpoint over HTTP (ManagementUri), as the Exchange Management Shell does, so it never depends on the
        certificate it is still setting up. The first database is the one Setup creates; the second is excluded from
        automatic mailbox distribution, so #2021 can prove Exchange honours that. The databases belong to the forest's
        own organisation, so every forest can use the same names.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$')]
        [string]$Domain,

        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z0-9-]{1,15}$')]
        [string]$NetBiosName,

        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9-]{0,13}[A-Za-z0-9])?$')]
        [string]$ServerName
    )

    $dnsDomain = $Domain.ToLowerInvariant()
    $computer = $ServerName.ToLowerInvariant()
    $fqdn = "$computer.$dnsDomain"
    $domainDn = (($dnsDomain -split '\.') | ForEach-Object { "DC=$_" }) -join ','
    $contactOu = 'GALSync'

    return [pscustomobject][ordered]@{
        DnsDomain            = $dnsDomain
        NetBiosName          = $NetBiosName.ToUpperInvariant()
        ComputerName         = $computer
        Fqdn                 = $fqdn
        ComputerAccount      = "$computer`$"
        DomainDn             = $domainDn
        PowerShellUri        = "https://$fqdn/PowerShell/"
        ManagementUri        = "http://$fqdn/PowerShell/"
        ProvisioningDatabase = 'MDB01'
        ExcludedDatabase     = 'MDB02'
        ContactOuName        = $contactOu
        ContactOuDn          = "OU=$contactOu,OU=Corp,$domainDn"
        ServiceAccountDn     = "CN=svc-jim,OU=Services,$domainDn"
    }
}

function Get-LabExchangeDatabasePath {
    <#
    .SYNOPSIS
        Where a mailbox database and its transaction logs live: a folder of their own under C:\ExchangeDatabases.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z0-9-]+$')]
        [string]$Name
    )

    $folder = "C:\ExchangeDatabases\$Name"
    return [pscustomobject][ordered]@{
        EdbFilePath   = "$folder\$Name.edb"
        LogFolderPath = "$folder\Logs"
    }
}

function ConvertFrom-LabExchangeSetting {
    <#
    .SYNOPSIS
        Maps one entry of settings.json exchangeServers to the build's parameters, taking the domain and the DNS
        server from the domain controller the server joins.

    .DESCRIPTION
        The server installs from the Exchange media the forests were prepared with (directoryExtensions), because
        Setup refuses media older than the organisation and a newer build would need the forest prepared again. The
        prerequisites Microsoft does not ship on that media come from exchangePrerequisiteDirectory.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Settings,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $servers = $null
    if ($Settings.PSObject.Properties['exchangeServers']) { $servers = $Settings.exchangeServers }
    if ($null -eq $servers -or -not $servers.PSObject.Properties[$Name]) {
        throw "settings.json has no Exchange server called '$Name' under exchangeServers."
    }
    $server = $servers.$Name

    $dcName = [string]$server.domainController
    $controllers = $null
    if ($Settings.PSObject.Properties['domainControllers']) { $controllers = $Settings.domainControllers }
    if ([string]::IsNullOrEmpty($dcName) -or $null -eq $controllers -or -not $controllers.PSObject.Properties[$dcName]) {
        throw "Exchange server '$Name' joins the domain controller '$dcName', which settings.json does not describe under domainControllers."
    }
    $dc = $controllers.$dcName

    $isoPath = $null
    if ($Settings.PSObject.Properties['directoryExtensions']) {
        foreach ($extension in @($Settings.directoryExtensions)) {
            if ($null -ne $extension -and [string]$extension.product -eq 'Exchange') { $isoPath = [string]$extension.isoPath }
        }
    }
    if ([string]::IsNullOrEmpty($isoPath)) {
        throw "settings.json directoryExtensions has no Exchange entry: the forests have no Exchange organisation for '$Name' to join, and the server installs from the same media."
    }

    $prerequisites = $null
    if ($Settings.PSObject.Properties['exchangePrerequisiteDirectory']) { $prerequisites = [string]$Settings.exchangePrerequisiteDirectory }
    if ([string]::IsNullOrEmpty($prerequisites)) {
        throw 'settings.json has no exchangePrerequisiteDirectory, the folder holding the Exchange prerequisites that are not on its media (see README.md).'
    }

    $computerName = 'ex1'
    if ($server.PSObject.Properties['computerName'] -and -not [string]::IsNullOrEmpty([string]$server.computerName)) {
        $computerName = [string]$server.computerName
    }

    return [pscustomobject][ordered]@{
        Name                  = $Name
        DomainControllerName  = $dcName
        Domain                = [string]$dc.domain
        DnsServer             = [string]$dc.ipAddress
        IPAddress             = [string]$server.ipAddress
        PrefixLength          = [int]$server.prefixLength
        Gateway               = [string]$server.gateway
        ComputerName          = $computerName
        ExchangeIsoPath       = $isoPath
        PrerequisiteDirectory = $prerequisites
    }
}

function Get-LabExchangePhaseArgument {
    <#
    .SYNOPSIS
        Builds the named parameters for one phase of Initialize-LabExchangeServer.ps1, from the full settings.

    .DESCRIPTION
        Each phase gets only what it uses. The Administrator password reaches Join, which joins the domain with it, and
        Configure and Verify, which open the Exchange Remote PowerShell session with it. Install runs Setup as the
        session's own user, the domain Administrator. The result is an ordered dictionary for splatting.
    #>
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('License', 'Prepare', 'Join', 'Prerequisites', 'Install', 'Configure', 'Verify')]
        [string]$Phase,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Settings
    )

    $required = @{
        License       = @()
        Prepare       = @('ComputerName', 'IPAddress', 'PrefixLength', 'Gateway', 'DnsServer')
        Join          = @('Domain', 'NetBiosName', 'AdministratorPassword')
        Prerequisites = @('LabRoot')
        Install       = @('Domain', 'NetBiosName', 'ComputerName', 'LabRoot')
        Configure     = @('Domain', 'NetBiosName', 'ComputerName', 'AdministratorPassword', 'VmName', 'LabRoot')
        Verify        = @('Domain', 'NetBiosName', 'ComputerName', 'AdministratorPassword', 'VmName', 'LabRoot')
    }

    $arguments = [ordered]@{ Phase = $Phase }
    foreach ($name in $required[$Phase]) {
        if ((-not $Settings.Contains($name)) -or ($null -eq $Settings[$name]) -or ([string]$Settings[$name] -eq '' -and $Settings[$name] -isnot [securestring])) {
            throw "Phase $Phase needs the setting '$name'."
        }
        $arguments[$name] = $Settings[$name]
    }
    return $arguments
}

function Get-LabExchangeWindowsFeature {
    <#
    .SYNOPSIS
        The Windows features a Mailbox server needs, as Microsoft lists them for Windows Server with the Desktop
        Experience, plus the Active Directory module the build itself uses.

    .DESCRIPTION
        Copied from "Exchange Server 2019 and SE prerequisites", Mailbox server role, Desktop Experience:
        https://learn.microsoft.com/exchange/plan-and-deploy/prerequisites
        Message Queuing is no longer needed and is left out. Installed by the Prerequisites phase rather than by
        Setup's /InstallWindowsComponents, so a re-run finds them in place and Setup's readiness checks see a server
        that has already restarted.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param()

    return [string[]]@(
        'Server-Media-Foundation', 'NET-Framework-45-Core', 'NET-Framework-45-ASPNET', 'NET-WCF-HTTP-Activation45',
        'NET-WCF-Pipe-Activation45', 'NET-WCF-TCP-Activation45', 'NET-WCF-TCP-PortSharing45', 'RPC-over-HTTP-proxy',
        'RSAT-Clustering', 'RSAT-Clustering-CmdInterface', 'RSAT-Clustering-Mgmt', 'RSAT-Clustering-PowerShell',
        'WAS-Process-Model', 'Web-Asp-Net45', 'Web-Basic-Auth', 'Web-Client-Auth', 'Web-Digest-Auth', 'Web-Dir-Browsing',
        'Web-Dyn-Compression', 'Web-Http-Errors', 'Web-Http-Logging', 'Web-Http-Redirect', 'Web-Http-Tracing',
        'Web-ISAPI-Ext', 'Web-ISAPI-Filter', 'Web-Metabase', 'Web-Mgmt-Console', 'Web-Mgmt-Service', 'Web-Net-Ext45',
        'Web-Request-Monitor', 'Web-Server', 'Web-Stat-Compression', 'Web-Static-Content', 'Web-Windows-Auth', 'Web-WMI',
        'Windows-Identity-Foundation', 'RSAT-ADDS',
        'RSAT-AD-PowerShell'
    )
}

function Get-LabExchangePrerequisite {
    <#
    .SYNOPSIS
        The software a Mailbox server needs beyond Windows features, in the order the Prerequisites phase installs it.

    .DESCRIPTION
        From "Exchange Server 2019 and SE prerequisites" (https://learn.microsoft.com/exchange/plan-and-deploy/prerequisites):
        the Visual C++ 2012 and 2013 runtimes, the IIS URL Rewrite Module (after IIS, which it extends), and Unified
        Communications Managed API 4.0, which Microsoft ships in UCMARedist on the Exchange media. .NET Framework 4.8.1
        is part of Windows Server 2025.

        The lab network has no route out, so the other three are downloaded once on the host, from DownloadUrl, into
        settings.json exchangePrerequisiteDirectory under FileName (see README.md). The host checks each carries a valid
        Microsoft signature before copying it into the guest. DisplayNamePattern recognises the installed software
        among the names in the guest's Uninstall registry keys, so a re-run installs nothing twice.
    #>
    [CmdletBinding()]
    param()

    return @(
        [pscustomobject][ordered]@{
            Name               = 'Visual C++ 2012 Redistributable (x64)'
            Source             = 'Directory'
            FileName           = 'vcredist_2012_x64.exe'
            DownloadUrl        = 'https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe'
            Arguments          = @('/install', '/quiet', '/norestart')
            DisplayNamePattern = '^Microsoft Visual C\+\+ 2012 x64 (Minimum|Additional) Runtime'
        },
        [pscustomobject][ordered]@{
            Name               = 'Visual C++ 2013 Redistributable (x64)'
            Source             = 'Directory'
            FileName           = 'vcredist_2013_x64.exe'
            DownloadUrl        = 'https://aka.ms/highdpimfc2013x64enu'
            Arguments          = @('/install', '/quiet', '/norestart')
            DisplayNamePattern = '^Microsoft Visual C\+\+ 2013 x64 (Minimum|Additional) Runtime'
        },
        [pscustomobject][ordered]@{
            Name               = 'IIS URL Rewrite Module 2.1'
            Source             = 'Directory'
            FileName           = 'rewrite_amd64_en-US.msi'
            DownloadUrl        = 'https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi'
            Arguments          = @('/qn', '/norestart')
            DisplayNamePattern = '^IIS URL Rewrite Module 2$'
        },
        [pscustomobject][ordered]@{
            Name               = 'Unified Communications Managed API 4.0'
            Source             = 'Media'
            FileName           = 'UCMARedist\Setup.exe'
            DownloadUrl        = $null
            Arguments          = @('/q', '/norestart')
            DisplayNamePattern = '^Microsoft Unified Communications Managed API 4\.0, Runtime'
        }
    )
}

function Test-LabExchangePrerequisiteInstalled {
    <#
    .SYNOPSIS
        Whether one prerequisite is installed, from the display names of the installed software.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [object]$Prerequisite,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$InstalledName
    )

    return (@($InstalledName | Where-Object { $_ -match $Prerequisite.DisplayNamePattern }).Count -gt 0)
}

function Test-LabMicrosoftSignature {
    <#
    .SYNOPSIS
        Whether an Authenticode signature is valid and Microsoft Corporation's: the status Get-AuthenticodeSignature
        reports, and its signer certificate's subject, whose organisation must be exactly Microsoft Corporation.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$Status,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$SignerSubject
    )

    if ($Status -ne 'Valid') { return $false }
    $organisations = @($SignerSubject -split ',\s*' | Where-Object { $_ -like 'O=*' } | ForEach-Object { $_.Substring(2) })
    return (($organisations.Count -eq 1) -and ($organisations[0] -ceq 'Microsoft Corporation'))
}

function Get-LabExchangePageFileSize {
    <#
    .SYNOPSIS
        The page file size, in megabytes, for a Mailbox server with the given memory: a quarter of it, with the
        minimum and maximum the same, as Microsoft requires
        (https://learn.microsoft.com/exchange/plan-and-deploy/system-requirements#hardware-requirements-for-exchange-server).
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)]
        [uint64]$MemoryBytes
    )

    return [int][math]::Round(($MemoryBytes / 1MB) * 0.25)
}

function Get-LabExchangeSetupInstallArgument {
    <#
    .SYNOPSIS
        Exchange Setup's arguments to install the Mailbox role, joining the forest's existing organisation.

    .DESCRIPTION
        The forest was prepared (/PrepareSchema, /PrepareAD, /PrepareAllDomains) when its domain controller was built,
        so Setup is given no organisation name. The first database is named and placed here; Configure adds the second.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [string]$DatabaseName
    )

    $paths = Get-LabExchangeDatabasePath -Name $DatabaseName
    return [string[]]@(
        '/Mode:Install',
        '/Roles:Mailbox',
        '/IAcceptExchangeServerLicenseTerms_DiagnosticDataOFF',
        "/MdbName:$DatabaseName",
        "/DbFilePath:$($paths.EdbFilePath)",
        "/LogFolderPath:$($paths.LogFolderPath)"
    )
}

function Get-LabExchangeRoleState {
    <#
    .SYNOPSIS
        Whether Setup has installed the Mailbox role, from its registry key
        (HKLM:\SOFTWARE\Microsoft\ExchangeServer\v15\MailboxRole): NotInstalled, Partial or Installed.

    .DESCRIPTION
        Setup writes a Watermark while it works and removes it when the role is done, recording ConfiguredVersion. A
        watermark left behind marks an install that failed or was interrupted; running Setup /Mode:Install again
        resumes it.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowNull()]
        [object]$RoleKey
    )

    if ($null -eq $RoleKey) { return 'NotInstalled' }
    $hasWatermark = $RoleKey.PSObject.Properties['Watermark'] -and -not [string]::IsNullOrEmpty([string]$RoleKey.Watermark)
    $hasVersion = $RoleKey.PSObject.Properties['ConfiguredVersion'] -and -not [string]::IsNullOrEmpty([string]$RoleKey.ConfiguredVersion)
    if ($hasVersion -and -not $hasWatermark) { return 'Installed' }
    return 'Partial'
}

function Get-LabExchangeServiceHealthProblem {
    <#
    .SYNOPSIS
        Reads Test-ServiceHealth's output and returns one line per role whose required services are not all running
        (nothing when they are).
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Health
    )

    if ($Health.Count -eq 0) {
        return @('Test-ServiceHealth reported no role at all.')
    }
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($role in $Health) {
        if (-not [bool]$role.RequiredServicesRunning) {
            $problems.Add(("{0}: not running: {1}" -f $role.Role, (@($role.ServicesNotRunning) -join ', ')))
        }
    }
    return $problems.ToArray()
}

function Test-LabExchangeDatabaseLayout {
    <#
    .SYNOPSIS
        Checks the server's mailbox databases against what the lab promises, and returns one line per problem
        (nothing when it is right): both databases on this server and mounted, the first open to automatic
        distribution and the second excluded from it.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Database,

        [Parameter(Mandatory)]
        [object]$Info
    )

    $problems = New-Object System.Collections.Generic.List[string]
    $expected = [ordered]@{ $Info.ProvisioningDatabase = $false; $Info.ExcludedDatabase = $true }
    foreach ($name in $expected.Keys) {
        $found = @($Database | Where-Object { [string]$_.Name -eq $name })
        if ($found.Count -eq 0) {
            $problems.Add("The database $name does not exist.")
            continue
        }
        $db = $found[0]
        if ([string]$db.Server -ine $Info.ComputerName) {
            $problems.Add("The database $name is on $($db.Server), not $($Info.ComputerName).")
        }
        if (-not [bool]$db.Mounted) {
            $problems.Add("The database $name is not mounted.")
        }
        if ([bool]$db.IsExcludedFromProvisioning -ne $expected[$name]) {
            $problems.Add(("The database $name is {0} automatic mailbox distribution; it should be {1}." -f $(if ($db.IsExcludedFromProvisioning) { 'excluded from' } else { 'open to' }), $(if ($expected[$name]) { 'excluded' } else { 'open to it' })))
        }
    }
    return $problems.ToArray()
}

function Test-LabExchangeIisCertificate {
    <#
    .SYNOPSIS
        Says whether a certificate from the server's own store is the self-signed one Setup makes for IIS: issued by
        itself, and naming the server's full name.

    .DESCRIPTION
        Read from the certificate store, not from Get-ExchangeCertificate: through Remote PowerShell that returns a
        plain certificate, without IsSelfSigned, Services or CertificateDomains.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [object]$Certificate,

        [Parameter(Mandatory)]
        [string]$Fqdn
    )

    $names = @($Certificate.DnsNameList | ForEach-Object { [string]$_.Unicode })
    return ([string]$Certificate.Subject -eq [string]$Certificate.Issuer) -and ($names -contains $Fqdn)
}

function Test-LabKerberosChallenge {
    <#
    .SYNOPSIS
        Says whether an HTTP response is a challenge a Kerberos client can answer: a 401 offering Kerberos, or
        Negotiate, which carries it.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [int]$StatusCode,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$WwwAuthenticate
    )

    $schemes = @($WwwAuthenticate | ForEach-Object { ($_ -split '\s+', 2)[0] })
    return ($StatusCode -eq 401) -and (@($schemes | Where-Object { $_ -in 'Kerberos', 'Negotiate' }).Count -gt 0)
}

Export-ModuleMember -Function @(
    'Get-LabExchangeServerInfo', 'Get-LabExchangeDatabasePath', 'ConvertFrom-LabExchangeSetting', 'Get-LabExchangePhaseArgument',
    'Get-LabExchangeWindowsFeature', 'Get-LabExchangePrerequisite', 'Test-LabExchangePrerequisiteInstalled',
    'Test-LabMicrosoftSignature', 'Get-LabExchangePageFileSize', 'Get-LabExchangeSetupInstallArgument',
    'Get-LabExchangeRoleState', 'Get-LabExchangeServiceHealthProblem', 'Test-LabExchangeDatabaseLayout',
    'Test-LabExchangeIisCertificate', 'Test-LabKerberosChallenge'
)
