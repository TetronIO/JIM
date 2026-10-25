# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The AD FS server of the Active Directory lab: names, settings, guest phase arguments and the checks that prove a
    federation service works.

.DESCRIPTION
    Pure functions only (but Read-LabModuleFile, which reads the module files the digest covers), tested on any
    platform by LabFederationServer.Tests.ps1. The host (New-LabFederationServer.ps1)
    and the guest (Initialize-LabFederationServer.ps1, under Windows PowerShell 5.1) both import it, so it keeps to
    syntax 5.1 understands. Everything the domain controllers share with the AD FS server (VM creation, PowerShell
    Direct, checkpoints) stays in LabDomainController.psm1.
#>

Set-StrictMode -Version Latest

$script:DnsLabelPattern = '^[A-Za-z0-9]([A-Za-z0-9-]{0,13}[A-Za-z0-9])?$'

function Get-LabFederationInfo {
    <#
    .SYNOPSIS
        Derives the names the AD FS server needs: its own, the federation service's, and its group managed service
        account's.

    .DESCRIPTION
        The federation service name must differ from the computer name. The computer account holds HOST/<name>,
        which covers HTTP, so a service named after the computer would have its HTTP SPN claimed by the computer and
        Kerberos to AD FS would break. The service account is a gMSA named gmsa-<service>, whose sAMAccountName
        (with its trailing $) must fit in 15 characters.
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
        [string]$ServerName,

        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z0-9]([A-Za-z0-9-]{0,13}[A-Za-z0-9])?$')]
        [string]$ServiceName
    )

    $dnsDomain = $Domain.ToLowerInvariant()
    $computer = $ServerName.ToLowerInvariant()
    $service = $ServiceName.ToLowerInvariant()

    if ($computer -eq $service) {
        throw "The federation service name '$service' is the computer's own name, whose HOST SPN would claim the service's HTTP SPN. Choose a different service name."
    }

    $gmsa = "gmsa-$service"
    if (($gmsa.Length + 1) -gt 15) {
        throw "The service account '$gmsa`$' would be longer than 15 characters, the sAMAccountName limit. Choose a shorter service name."
    }

    $netBios = $NetBiosName.ToUpperInvariant()
    $serviceFqdn = "$service.$dnsDomain"
    return [pscustomobject][ordered]@{
        DnsDomain       = $dnsDomain
        NetBiosName     = $netBios
        ComputerName    = $computer
        Fqdn            = "$computer.$dnsDomain"
        ServiceName     = $service
        ServiceFqdn     = $serviceFqdn
        ServiceUrl      = "https://$serviceFqdn/adfs"
        GmsaName        = $gmsa
        GmsaAccount     = "$netBios\$gmsa`$"
        ComputerAccount = "$computer`$"
    }
}

function Get-LabFederationCertificateName {
    <#
    .SYNOPSIS
        The DNS names on the AD FS TLS certificate: the federation service, which AD FS binds and browsers check,
        then the server itself.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [object]$Info
    )

    return @($Info.ServiceFqdn, $Info.Fqdn)
}

function ConvertFrom-LabFederationSetting {
    <#
    .SYNOPSIS
        Maps one entry of settings.json federationServers to the build's parameters, taking the domain and the DNS
        server from the domain controller the server joins.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Settings,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $servers = $null
    if ($Settings.PSObject.Properties['federationServers']) { $servers = $Settings.federationServers }
    if ($null -eq $servers -or -not $servers.PSObject.Properties[$Name]) {
        throw "settings.json has no federation server called '$Name' under federationServers."
    }
    $server = $servers.$Name

    $dcName = [string]$server.domainController
    $controllers = $null
    if ($Settings.PSObject.Properties['domainControllers']) { $controllers = $Settings.domainControllers }
    if ([string]::IsNullOrEmpty($dcName) -or $null -eq $controllers -or -not $controllers.PSObject.Properties[$dcName]) {
        throw "Federation server '$Name' joins the domain controller '$dcName', which settings.json does not describe under domainControllers."
    }
    $dc = $controllers.$dcName

    $computerName = 'adfs1'
    if ($server.PSObject.Properties['computerName'] -and -not [string]::IsNullOrEmpty([string]$server.computerName)) {
        $computerName = [string]$server.computerName
    }
    $serviceName = 'adfs'
    if ($server.PSObject.Properties['serviceName'] -and -not [string]::IsNullOrEmpty([string]$server.serviceName)) {
        $serviceName = [string]$server.serviceName
    }

    return [pscustomobject][ordered]@{
        Name                 = $Name
        DomainControllerName = $dcName
        Domain               = [string]$dc.domain
        DnsServer            = [string]$dc.ipAddress
        IPAddress            = [string]$server.ipAddress
        PrefixLength         = [int]$server.prefixLength
        Gateway              = [string]$server.gateway
        ComputerName         = $computerName
        ServiceName          = $serviceName
    }
}

function Get-LabFederationPhaseArgument {
    <#
    .SYNOPSIS
        Builds the named parameters for one phase of Initialize-LabFederationServer.ps1, from the full settings.

    .DESCRIPTION
        Each phase gets only what it uses, so the Administrator password reaches only Join and Farm, the two phases
        that act as the domain Administrator. The result is an ordered dictionary for splatting.
    #>
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('License', 'Prepare', 'Join', 'Farm', 'Verify')]
        [string]$Phase,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Settings
    )

    $required = @{
        License = @()
        Prepare = @('ComputerName', 'IPAddress', 'PrefixLength', 'Gateway', 'DnsServer')
        Join    = @('Domain', 'NetBiosName', 'AdministratorPassword')
        Farm    = @('Domain', 'NetBiosName', 'ComputerName', 'ServiceName', 'AdministratorPassword', 'VmName', 'LabRoot')
        Verify  = @('Domain', 'NetBiosName', 'ComputerName', 'ServiceName', 'VmName', 'LabRoot')
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

function Test-LabFederationUrl {
    # True when a URL is HTTPS on the given host (case-insensitive) and its path starts with the given prefix.
    param([string]$Url, [string]$HostName, [string]$PathPrefix)

    $uri = $null
    if (-not [System.Uri]::TryCreate($Url, [System.UriKind]::Absolute, [ref]$uri)) {
        return $false
    }
    return (($uri.Scheme -eq 'https') -and ($uri.Host -ieq $HostName) -and $uri.AbsolutePath.StartsWith($PathPrefix, [System.StringComparison]::OrdinalIgnoreCase))
}

function Test-LabFederationDiscovery {
    <#
    .SYNOPSIS
        Checks AD FS's OpenID Connect discovery document against what JIM relies on, and returns one line per
        problem (nothing when it is right).

    .DESCRIPTION
        The issuer must be the federation service (https://<service>/adfs); the authorisation, token, key-set and
        sign-out endpoints must be on it over HTTPS; and a published list of PKCE methods must include S256, because
        Connect-JIM signs in with it. access_token_issuer is read by the caller and reported, not judged: which issuer
        AD FS puts in access tokens is one of the things the lab exists to find out (#2355).
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [object]$Document,

        [Parameter(Mandatory)]
        [string]$ServiceFqdn
    )

    $problems = New-Object System.Collections.Generic.List[string]
    $expectedIssuer = "https://$ServiceFqdn/adfs"

    $issuer = $null
    if ($Document.PSObject.Properties['issuer']) { $issuer = [string]$Document.issuer }
    if (-not (Test-LabFederationUrl -Url $issuer -HostName $ServiceFqdn -PathPrefix '/adfs') -or ($issuer.TrimEnd('/') -ine $expectedIssuer)) {
        $problems.Add("issuer is '$issuer', expected '$expectedIssuer'.")
    }

    foreach ($member in 'authorization_endpoint', 'token_endpoint', 'jwks_uri', 'end_session_endpoint') {
        if (-not $Document.PSObject.Properties[$member] -or [string]::IsNullOrEmpty([string]$Document.$member)) {
            $problems.Add("$member is missing.")
            continue
        }
        $url = [string]$Document.$member
        if (-not (Test-LabFederationUrl -Url $url -HostName $ServiceFqdn -PathPrefix '/adfs/')) {
            $problems.Add("$member is '$url', which is not on $expectedIssuer over HTTPS.")
        }
    }

    # AD FS supports PKCE (2019 and later) without publishing code_challenge_methods_supported, so only a list that
    # leaves S256 out is a problem. Connect-JIM's sign-in is what proves PKCE works.
    if ($Document.PSObject.Properties['code_challenge_methods_supported'] -and (@($Document.code_challenge_methods_supported) -notcontains 'S256')) {
        $problems.Add('code_challenge_methods_supported does not offer S256, which Connect-JIM signs in with.')
    }

    return $problems.ToArray()
}

function Test-LabFederationKeySet {
    <#
    .SYNOPSIS
        Checks AD FS's published key set (jwks_uri) holds at least one signing key JIM could validate a token
        with, and returns one line per problem (nothing when it is right).
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [object]$Document
    )

    $keys = @()
    if ($Document.PSObject.Properties['keys']) { $keys = @($Document.keys) }
    $usable = @($keys | Where-Object {
            $null -ne $_ -and $_.PSObject.Properties['kty'] -and $_.kty -eq 'RSA' -and
            $_.PSObject.Properties['kid'] -and -not [string]::IsNullOrEmpty([string]$_.kid) -and
            (-not $_.PSObject.Properties['use'] -or $_.use -eq 'sig')
        })
    if ($usable.Count -eq 0) {
        return @('The key set holds no signing key: no RSA key with a key ID (kid) for signatures.')
    }
    return @()
}

function Test-LabModuleFile {
    <#
    .SYNOPSIS
        Whether a path, relative to src/JIM.PowerShell, is part of the JIM PowerShell module as it is loaded: the
        manifest, the root module, and the scripts under Public and Private. Not the tests, nor the notes beside them.

    .DESCRIPTION
        JIM is registered on the AD FS server by the module's own Register-JIMIdentityProvider, run on the host from
        the copy Install-LabScripts.ps1 deploys (#2441). These are the files that copy holds, and the files
        Get-LabModuleDigest covers. Either separator is accepted.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    $path = $RelativePath.Replace('\', '/')
    if ($path -ceq 'JIM.psd1' -or $path -ceq 'JIM.psm1') { return $true }
    return ($path -cmatch '^(Public|Private)/' -and $path -match '\.ps1$' -and $path -notmatch '(^|/)\.\.?(/|$)')
}

function Get-LabModuleDigest {
    <#
    .SYNOPSIS
        One SHA-256 digest, in lower-case hex, over the JIM PowerShell module's files: each file's path and content.

    .DESCRIPTION
        The runner computes it over the commit under test, and the host over the copy it holds, so the AD FS
        registration refuses to run from a copy that is not the commit's (#2441). Pure: the callers read the files.
        It is the same whatever the line endings (a Windows checkout has CRLF where Linux has LF), the path separator
        or the order the entries arrive in, and changes when any file is added, removed, renamed or edited.

    .PARAMETER Entry
        One object per file, with Path (relative to src/JIM.PowerShell) and Text (its content).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Entry
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $lines = [System.Collections.Generic.List[string]]::new()
        foreach ($file in $Entry) {
            $text = ([string]$file.Text).Replace("`r`n", "`n")
            $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))
            $lines.Add(('{0} {1}' -f ([string]$file.Path).Replace('\', '/'), (($hash | ForEach-Object { $_.ToString('x2') }) -join '')))
        }
        $sorted = $lines.ToArray()
        [Array]::Sort($sorted, [StringComparer]::Ordinal)
        $digest = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(($sorted -join "`n")))
        return (($digest | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally {
        $sha.Dispose()
    }
}

function ConvertTo-LabIdentityProviderRegistration {
    <#
    .SYNOPSIS
        Turns what Register-JIMIdentityProvider returned into the object Register-LabIdentityProvider.ps1 prints for
        the runner: camel-case members, the client secret as plain text, and the digest of the module that made it.

    .DESCRIPTION
        The client secret leaves the host as text on SSH standard output, the only way to the runner; the runner masks
        it before it does anything else, and gives it to JIM through the environment, never a file (#2441). It is
        made new on each run, after the AD FS server is reverted, and dies with the next revert. Throws when a value
        JIM needs is missing, rather than letting the run start JIM without it.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [object]$Registration,

        [Parameter(Mandatory)]
        [string]$ModuleDigest
    )

    foreach ($name in 'Authority', 'ClientId', 'PublicClientId', 'ApiScope', 'ClaimType', 'MvAttribute') {
        if (-not $Registration.PSObject.Properties[$name] -or [string]::IsNullOrEmpty([string]$Registration.$name)) {
            throw "Register-JIMIdentityProvider returned no $name."
        }
    }
    $secret = $null
    if ($Registration.PSObject.Properties['ClientSecret'] -and $Registration.ClientSecret -is [securestring]) {
        $secret = (New-Object System.Net.NetworkCredential('', $Registration.ClientSecret)).Password
    }
    if ([string]::IsNullOrEmpty($secret)) {
        throw 'Register-JIMIdentityProvider returned no client secret, so JIM could not sign anyone in. Was the AD FS server reverted first?'
    }

    return [pscustomobject][ordered]@{
        authority      = [string]$Registration.Authority
        clientId       = [string]$Registration.ClientId
        clientSecret   = $secret
        publicClientId = [string]$Registration.PublicClientId
        apiScope       = [string]$Registration.ApiScope
        claimType      = [string]$Registration.ClaimType
        mvAttribute    = [string]$Registration.MvAttribute
        changes        = @($Registration.Changes)
        moduleDigest   = $ModuleDigest
    }
}

function Read-LabModuleFile {
    <#
    .SYNOPSIS
        Reads the JIM PowerShell module's files under a folder (src/JIM.PowerShell, or the host's deployed copy), as
        the entries Get-LabModuleDigest takes: Path, relative with forward slashes, and Text.

    .DESCRIPTION
        The one function here that touches the file system, so the runner and the host read the module the same way.
        Only the files Test-LabModuleFile counts are read. Fails when the folder does not exist.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "The module folder '$Root' does not exist."
    }
    $full = (Resolve-Path -LiteralPath $Root).ProviderPath.TrimEnd('\', '/')
    foreach ($file in (Get-ChildItem -LiteralPath $full -File -Recurse)) {
        $relative = $file.FullName.Substring($full.Length + 1).Replace('\', '/')
        if (Test-LabModuleFile -RelativePath $relative) {
            [pscustomobject]@{ Path = $relative; Text = [System.IO.File]::ReadAllText($file.FullName) }
        }
    }
}

Export-ModuleMember -Function @(
    'Get-LabFederationInfo', 'Get-LabFederationCertificateName', 'ConvertFrom-LabFederationSetting',
    'Get-LabFederationPhaseArgument', 'Test-LabFederationDiscovery', 'Test-LabFederationKeySet',
    'Test-LabModuleFile', 'Get-LabModuleDigest', 'ConvertTo-LabIdentityProviderRegistration', 'Read-LabModuleFile'
)
