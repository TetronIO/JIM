# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    LDAP helper functions for integration testing

.DESCRIPTION
    Provides functions to interact with LDAP directories (Samba AD, OpenLDAP, 389 Directory Server
    and a real Active Directory domain controller) for test setup, data population, and validation.

    Every LDAP client tool call goes through Invoke-LdapTool: inside the directory's own container
    when the config names one, or in the shared jim-ldap-toolbox container over LDAPS when it does not
    (a real Active Directory lab, whose domain controllers are virtual machines).

    Functions accept either a $DirectoryConfig hashtable (from Get-DirectoryConfig)
    or individual parameters for backward compatibility.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# The shared container that runs LDAP client tools against a directory that has no container of its
# own (a real Active Directory domain controller). See test/integration/docker/ldap-toolbox/.
$script:LdapToolboxContainerName = 'jim-ldap-toolbox'

function Test-LdapToolboxConfig {
    <#
    .SYNOPSIS
        Whether a directory config has no container of its own, so its LDAP client tools run in the
        shared toolbox container instead.

    .DESCRIPTION
        The Samba AD, OpenLDAP and 389 Directory Server labs run the directory in a container and the
        harness runs the client tools inside it. A real Active Directory domain controller (a Hyper-V
        virtual machine) has no container, so its config carries a null ContainerName and the tools
        run in jim-ldap-toolbox, reaching the domain controller over LDAPS.

    .PARAMETER DirectoryConfig
        A config from Get-DirectoryConfig, or any hashtable that may carry a ContainerName.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    return [string]::IsNullOrEmpty([string]$DirectoryConfig['ContainerName'])
}

function Test-ActiveDirectoryConfig {
    <#
    .SYNOPSIS
        Whether a directory config describes a real Active Directory domain controller.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    return ([string]$DirectoryConfig['DirectoryType']) -eq 'ActiveDirectory'
}

function Get-LdapUri {
    <#
    .SYNOPSIS
        The URI an LDAP client tool connects to for a container-less directory config.

    .DESCRIPTION
        <LdapSearchScheme>://<Host>:<LdapSearchPort>, for example ldaps://dc1.panoply.local:636. It is
        used for configs with no container (see Test-LdapToolboxConfig). A config with a container keeps
        the in-container URI its callers have always built, because the tool runs inside that
        container and reaches the directory on localhost.

    .PARAMETER DirectoryConfig
        A config from Get-DirectoryConfig.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    return "$($DirectoryConfig['LdapSearchScheme'])://$($DirectoryConfig['Host']):$($DirectoryConfig['LdapSearchPort'])"
}

function Get-LdapToolInvocation {
    <#
    .SYNOPSIS
        Build the docker argument list that runs an LDAP client tool for a directory config.

    .DESCRIPTION
        The one place that decides where a tool runs. A config with a container gives
        "exec [-i] <ContainerName> <tool> <args>", a config with none gives
        "exec [-i] jim-ldap-toolbox <tool> <args>". The list does not include the docker executable
        itself and is pure, so it is testable with no Docker present. Arguments are passed as separate
        elements straight to docker exec with no shell in between, so a '*' or a space in one is safe.

    .PARAMETER DirectoryConfig
        A config from Get-DirectoryConfig, or a hashtable holding just a ContainerName.

    .PARAMETER Tool
        The LDAP client tool to run.

    .PARAMETER Arguments
        The tool's arguments, one element each.

    .PARAMETER WithInput
        Add -i so the tool can read standard input (ldapmodify and ldapadd reading LDIF).

    .OUTPUTS
        string[]: the arguments to pass to docker.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [ValidateSet('ldapsearch', 'ldapwhoami', 'ldapmodify', 'ldapadd', 'ldapdelete', 'ldappasswd')]
        [string]$Tool,

        [Parameter(Mandatory=$true)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Arguments,

        [Parameter(Mandatory=$false)]
        [switch]$WithInput
    )

    $containerName = if (Test-LdapToolboxConfig -DirectoryConfig $DirectoryConfig) {
        $script:LdapToolboxContainerName
    }
    else {
        [string]$DirectoryConfig['ContainerName']
    }

    $argv = [System.Collections.Generic.List[string]]::new()
    $argv.Add('exec')
    if ($WithInput) { $argv.Add('-i') }
    $argv.Add($containerName)
    $argv.Add($Tool)
    foreach ($argument in $Arguments) { $argv.Add($argument) }

    # Always at least three elements (exec, the container and the tool), so it comes back as an array
    # however a caller consumes it.
    return $argv.ToArray()
}

function Invoke-LdapTool {
    <#
    .SYNOPSIS
        Run an LDAP client tool for a directory config, inside its container or in the toolbox.

    .DESCRIPTION
        The single dispatch every LDAP helper in this file goes through. The argument list comes from
        Get-LdapToolInvocation. Standard error is merged into the output, as the callers have always
        done, so a failing tool's diagnostic message is in what comes back.

        By default the output is returned as the tool printed it (one element per line) and the exit
        code is left in $LASTEXITCODE. With -PassThruExitCode a hashtable of ExitCode and Output is
        returned instead, which is what callers that classify the outcome use.

    .PARAMETER DirectoryConfig
        A config from Get-DirectoryConfig, or a hashtable holding just a ContainerName.

    .PARAMETER Tool
        The LDAP client tool to run.

    .PARAMETER Arguments
        The tool's arguments, one element each. Include -H with the URI: Get-LdapUri for a container-less
        config, the in-container URI otherwise.

    .PARAMETER InputObject
        Text written to the tool's standard input (LDIF for ldapmodify or ldapadd). Adds -i.

    .PARAMETER PassThruExitCode
        Return @{ ExitCode; Output } rather than the bare output.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [ValidateSet('ldapsearch', 'ldapwhoami', 'ldapmodify', 'ldapadd', 'ldapdelete', 'ldappasswd')]
        [string]$Tool,

        [Parameter(Mandatory=$true)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Arguments,

        [Parameter(Mandatory=$false)]
        [string]$InputObject,

        [Parameter(Mandatory=$false)]
        [switch]$PassThruExitCode
    )

    $hasInput = $PSBoundParameters.ContainsKey('InputObject')
    $argv = Get-LdapToolInvocation -DirectoryConfig $DirectoryConfig -Tool $Tool -Arguments $Arguments -WithInput:$hasInput

    if ($hasInput) {
        $output = $InputObject | & docker @argv 2>&1
    }
    else {
        $output = & docker @argv 2>&1
    }
    $exitCode = $LASTEXITCODE

    if ($PassThruExitCode) {
        return @{ ExitCode = $exitCode; Output = $output }
    }

    return $output
}

function ConvertTo-UnicodePwdValue {
    <#
    .SYNOPSIS
        Encode a password as Active Directory's unicodePwd attribute value, base64 for LDIF.

    .DESCRIPTION
        unicodePwd takes the password wrapped in double quotes and encoded UTF-16LE. The result is
        base64 so it can be carried in an LDIF "unicodePwd:: <value>" line. Active Directory accepts a
        write to unicodePwd only over an encrypted connection, so it is always sent over LDAPS.

    .PARAMETER Password
        The clear-text password.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
        Justification = 'Lab passwords are plain-text test fixtures, handed to a native LDAP tool as arguments; a SecureString would only be unwrapped again at once.')]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Password
    )

    return [System.Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes('"' + $Password + '"'))
}

function ConvertFrom-LdapGeneralizedTime {
    <#
    .SYNOPSIS
        Parse an LDAP GeneralizedTime value, such as Active Directory's root DSE currentTime, to UTC.

    .DESCRIPTION
        Accepts yyyyMMddHHmmss with an optional fractional part, followed by Z or a UTC offset
        (+hh, +hhmm, -hh, -hhmm), for example 20260928101530.0Z. Returns a DateTime of kind Utc and
        throws on anything else.

    .PARAMETER Value
        The GeneralizedTime string.
    #>
    [CmdletBinding()]
    [OutputType([datetime])]
    param(
        [Parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [string]$Value
    )

    if ($Value -notmatch '^\s*(\d{14})(?:[.,]\d+)?(Z|[+-]\d{2}(?:\d{2})?)\s*$') {
        throw "'$Value' is not an LDAP GeneralizedTime (expected yyyyMMddHHmmss[.f]Z, for example 20260928101530.0Z)."
    }
    $stamp = $matches[1]
    $zone = $matches[2]

    $parsed = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($stamp, 'yyyyMMddHHmmss', [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::None, [ref]$parsed)) {
        throw "'$Value' is not an LDAP GeneralizedTime (the date and time '$stamp' do not exist)."
    }

    $utc = [datetime]::SpecifyKind($parsed, [System.DateTimeKind]::Utc)
    if ($zone -ne 'Z') {
        $sign = if ($zone.StartsWith('-')) { -1 } else { 1 }
        $hours = [int]$zone.Substring(1, 2)
        $minutes = if ($zone.Length -ge 5) { [int]$zone.Substring(3, 2) } else { 0 }
        # A local time ahead of UTC (+0100) is that much later than the same instant in UTC.
        $utc = $utc.AddMinutes(-$sign * ($hours * 60 + $minutes))
    }

    return $utc
}

function Get-LdapRootDseTime {
    <#
    .SYNOPSIS
        Read a directory's own clock: the currentTime attribute of its root DSE, as UTC.

    .DESCRIPTION
        Runs ldapsearch with -s base -b "" currentTime through Invoke-LdapTool, over the config's URI
        (Get-LdapUri for a container-less config), and parses the GeneralizedTime it returns. Pair it
        with Test-LdapClockSkew to check a domain controller restored from a checkpoint has the right
        time. Throws, with the exit code and output, when the search fails or returns no currentTime.

    .PARAMETER DirectoryConfig
        A directory config from Get-DirectoryConfig.

    .PARAMETER BindDN
        The Distinguished Name to bind as.

    .PARAMETER BindPassword
        The password to bind with.
    #>
    [CmdletBinding()]
    [OutputType([datetime])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
        Justification = 'Lab passwords are plain-text test fixtures, handed to a native LDAP tool as arguments; a SecureString would only be unwrapped again at once.')]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$BindDN,

        [Parameter(Mandatory=$true)]
        [string]$BindPassword
    )

    $ldapUri = if (Test-LdapToolboxConfig -DirectoryConfig $DirectoryConfig) {
        Get-LdapUri -DirectoryConfig $DirectoryConfig
    }
    else {
        "$($DirectoryConfig.LdapSearchScheme)://localhost:$($DirectoryConfig.LdapSearchPort)"
    }

    $invocation = Invoke-LdapTool -DirectoryConfig $DirectoryConfig -Tool ldapsearch -PassThruExitCode -Arguments @(
        '-x', '-LLL', '-H', $ldapUri, '-D', $BindDN, '-w', $BindPassword, '-s', 'base', '-b', '', 'currentTime')
    $outputLines = @($invocation.Output | ForEach-Object { "$_" })

    if ($invocation.ExitCode -ne 0) {
        throw "Reading the root DSE's currentTime failed (exit code $($invocation.ExitCode)): $($outputLines -join ' ')"
    }
    foreach ($line in $outputLines) {
        if ($line -match '^currentTime:\s*(\S+)\s*$') {
            return ConvertFrom-LdapGeneralizedTime -Value $matches[1]
        }
    }

    throw "The root DSE reply carried no currentTime: $($outputLines -join ' ')"
}

function Test-LdapClockSkew {
    <#
    .SYNOPSIS
        Compare a directory's clock with a reference clock (the runner's UTC clock by default).

    .DESCRIPTION
        Kerberos and password-change operations misbehave when clocks drift, and a lab restored from a
        checkpoint boots with whatever time it was saved at until the host's clock is applied. This
        returns the observed skew so a readiness check can fail with the number rather than a guess.

    .PARAMETER DirectoryTime
        The directory's time, for example from ConvertFrom-LdapGeneralizedTime on the root DSE's
        currentTime.

    .PARAMETER ReferenceTime
        The clock to compare with. Defaults to [DateTime]::UtcNow.

    .PARAMETER ToleranceSeconds
        The largest absolute skew accepted. Defaults to 5 seconds.

    .OUTPUTS
        An object with WithinTolerance, SkewSeconds (the directory's time minus the reference, so
        positive means the directory is ahead), ToleranceSeconds, DirectoryTime and ReferenceTime.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [datetime]$DirectoryTime,

        [Parameter(Mandatory=$false)]
        [datetime]$ReferenceTime = [datetime]::UtcNow,

        [Parameter(Mandatory=$false)]
        [double]$ToleranceSeconds = 5
    )

    $skewSeconds = ($DirectoryTime.ToUniversalTime() - $ReferenceTime.ToUniversalTime()).TotalSeconds

    return [pscustomobject]@{
        WithinTolerance  = ([math]::Abs($skewSeconds) -le $ToleranceSeconds)
        SkewSeconds      = $skewSeconds
        ToleranceSeconds = $ToleranceSeconds
        DirectoryTime    = $DirectoryTime.ToUniversalTime()
        ReferenceTime    = $ReferenceTime.ToUniversalTime()
    }
}

function Expand-LDIFFoldedLine {
    <#
    .SYNOPSIS
        Splits raw LDIF search output into logical lines, unfolding RFC 2849
        continuation lines (a line beginning with a single space continues the
        previous line; ldapsearch folds long values, such as DNs, at 78 columns).
        Also strips trailing carriage returns: samba's ldb tooling emits CRLF
        line endings which would otherwise embed \r in parsed values.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$RawLdif
    )

    $logicalLines = [System.Collections.Generic.List[string]]::new()

    foreach ($rawLine in ($RawLdif -split "`n")) {
        # Strip a trailing \r (CRLF from samba's ldb) before the fold check, so a
        # continuation marker on a CRLF-terminated line is still recognised.
        $line = $rawLine.TrimEnd("`r")

        if ($line.StartsWith(' ') -and $logicalLines.Count -gt 0) {
            # Continuation line: append everything after the single leading space
            # to the previous logical line.
            $logicalLines[$logicalLines.Count - 1] += $line.Substring(1)
        }
        else {
            # A leading-space line with no predecessor (defensive) is kept as-is,
            # as are ordinary lines, comments, base64 markers, and blank lines.
            $logicalLines.Add($line)
        }
    }

    # The unary comma forces array output: PowerShell otherwise unwraps a single-element
    # array to a bare scalar on return, which would silently break the [string[]] contract
    # (and turn indexed access like $lines[0] into character indexing on the string).
    return ,$logicalLines.ToArray()
}

function Test-LDAPConnection {
    <#
    .SYNOPSIS
        Test connectivity to an LDAP server
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Server,

        [Parameter(Mandatory=$false)]
        [int]$Port = 389,

        [Parameter(Mandatory=$false)]
        [int]$TimeoutSeconds = 10
    )

    try {
        $tcpClient = New-Object System.Net.Sockets.TcpClient
        $connectTask = $tcpClient.ConnectAsync($Server, $Port)

        if ($connectTask.Wait($TimeoutSeconds * 1000)) {
            $tcpClient.Close()
            return $true
        }
        else {
            $tcpClient.Close()
            return $false
        }
    }
    catch {
        return $false
    }
}

function Invoke-LDAPSearch {
    <#
    .SYNOPSIS
        Execute an LDAP search using ldapsearch command inside a container

    .DESCRIPTION
        Callers throughout this file bind as the directory administrator (Get-DirectoryConfig's
        BindDN/BindPassword), deliberately, not as JimBindDN/JimBindPassword: these are
        out-of-band assertions and population, and need the administrator's unrestricted view to
        prove what JIM actually did to the directory, independent of what JIM itself was permitted
        to see. Only the Connected Systems JIM configures bind as the delegated service account.

        When -DirectoryConfig is given it decides where the tool runs: its container, or the shared
        toolbox over Get-LdapUri when it has none (Server, Port and Scheme are then not used).
        Without it, -ContainerName and the URI parts behave exactly as they always have.
    #>
    param(
        [Parameter(Mandatory=$false)]
        [string]$ContainerName = "samba-ad-primary",

        [Parameter(Mandatory=$false)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$false)]
        [string]$Server = "localhost",

        [Parameter(Mandatory=$false)]
        [int]$Port = 389,

        [Parameter(Mandatory=$false)]
        [string]$Scheme = "ldap",

        [Parameter(Mandatory=$true)]
        [string]$BaseDN,

        [Parameter(Mandatory=$true)]
        [string]$BindDN,

        [Parameter(Mandatory=$true)]
        [string]$BindPassword,

        [Parameter(Mandatory=$true)]
        [string]$Filter,

        [Parameter(Mandatory=$false)]
        [string[]]$Attributes = @("*")
    )

    $toolConfig = if ($DirectoryConfig) { $DirectoryConfig } else { @{ ContainerName = $ContainerName } }
    $ldapUri = if (Test-LdapToolboxConfig -DirectoryConfig $toolConfig) {
        Get-LdapUri -DirectoryConfig $toolConfig
    }
    else {
        "${Scheme}://${Server}:${Port}"
    }

    try {
        # Build ldapsearch arguments array — pass args directly to docker exec
        # to avoid shell glob expansion issues with '*'
        $ldapArgs = @(
            "-x", "-LLL",
            "-H", $ldapUri,
            "-D", $BindDN,
            "-w", $BindPassword,
            "-b", $BaseDN,
            $Filter
        )
        # Only add explicit attribute names — omitting attributes returns all user attributes by default
        # (the LDAP protocol default). Do NOT pass '*' as it gets glob-expanded by shells.
        $explicitAttrs = @($Attributes | Where-Object { $_ -ne "*" })
        foreach ($attr in $explicitAttrs) {
            $ldapArgs += $attr
        }

        $invocation = Invoke-LdapTool -DirectoryConfig $toolConfig -Tool ldapsearch -Arguments $ldapArgs -PassThruExitCode
        $result = $invocation.Output

        if ($invocation.ExitCode -ne 0) {
            Write-Verbose "LDAP search failed: $result"
            return $null
        }

        return $result
    }
    catch {
        Write-Verbose "LDAP search exception: $_"
        return $null
    }
}

function Get-LDAPUser {
    <#
    .SYNOPSIS
        Get a user from LDAP by username attribute

    .DESCRIPTION
        Searches for a user by the appropriate name attribute for the directory type.
        For Samba AD this is sAMAccountName; for OpenLDAP this is uid.
        Pass a $DirectoryConfig hashtable or individual parameters.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$UserIdentifier,

        [Parameter(Mandatory=$false)]
        [hashtable]$DirectoryConfig,

        # Individual parameters (used when DirectoryConfig not provided)
        [Parameter(Mandatory=$false)]
        [string]$ContainerName,

        [Parameter(Mandatory=$false)]
        [string]$Server = "localhost",

        [Parameter(Mandatory=$false)]
        [int]$Port = 389,

        [Parameter(Mandatory=$false)]
        [string]$Scheme = "ldap",

        [Parameter(Mandatory=$false)]
        [string]$BaseDN = "DC=panoply,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindDN = "CN=Administrator,CN=Users,DC=panoply,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindPassword = "Test@123!",

        [Parameter(Mandatory=$false)]
        [string]$UserNameAttr = "sAMAccountName"
    )

    # Resolve config
    if ($DirectoryConfig) {
        $ContainerName = $DirectoryConfig.ContainerName
        $Server = "localhost"
        $Port = $DirectoryConfig.LdapSearchPort
        $Scheme = $DirectoryConfig.LdapSearchScheme
        $BaseDN = $DirectoryConfig.BaseDN
        $BindDN = $DirectoryConfig.BindDN
        $BindPassword = $DirectoryConfig.BindPassword
        $UserNameAttr = $DirectoryConfig.UserNameAttr
    }

    if (-not $ContainerName) { $ContainerName = "samba-ad-primary" }

    $filter = "($UserNameAttr=$UserIdentifier)"

    # A supplied config decides where the search runs (its container, or the toolbox when it has none).
    $configParameter = @{}
    if ($DirectoryConfig) { $configParameter.DirectoryConfig = $DirectoryConfig }

    $result = Invoke-LDAPSearch @configParameter `
        -ContainerName $ContainerName `
        -Server $Server `
        -Port $Port `
        -Scheme $Scheme `
        -BaseDN $BaseDN `
        -BindDN $BindDN `
        -BindPassword $BindPassword `
        -Filter $filter

    if ($null -eq $result -or $result.Length -eq 0) {
        return $null
    }

    # Parse LDIF output. Unfold RFC 2849 continuation lines first (ldapsearch folds long
    # values, such as DNs, at 78 columns) so a folded value is not silently truncated.
    $user = @{}
    $lines = Expand-LDIFFoldedLine -RawLdif ($result -join "`n")

    foreach ($line in $lines) {
        # LDIF comments start with '#' (referrals like '# refldap://...' appear in Samba AD
        # responses even when no objects match the filter). Skip them — otherwise the regex
        # below matches the referral as if it were an attribute line and Test-LDAPUserExists
        # would return true for a non-existent user.
        if ($line -match '^\s*#') {
            continue
        }
        if ($line -match "^([^:]+):\s*(.+)$") {
            $key = $matches[1]
            $value = $matches[2]

            if ($user.ContainsKey($key)) {
                # Multi-valued attribute
                if ($user[$key] -is [array]) {
                    $user[$key] += $value
                }
                else {
                    $user[$key] = @($user[$key], $value)
                }
            }
            else {
                $user[$key] = $value
            }
        }
    }

    # An LDAP search result that didn't match any object still carries referrals; once
    # those are stripped, an empty hashtable means "no user". Require at least a 'dn'
    # attribute to be confident we parsed a real result.
    if (-not $user.ContainsKey('dn')) {
        return $null
    }

    return $user
}

function Test-LDAPUserExists {
    <#
    .SYNOPSIS
        Check if a user exists in LDAP
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$UserIdentifier,

        [Parameter(Mandatory=$false)]
        [hashtable]$DirectoryConfig,

        # Individual parameters (used when DirectoryConfig not provided)
        [Parameter(Mandatory=$false)]
        [string]$ContainerName,

        [Parameter(Mandatory=$false)]
        [string]$Server = "localhost",

        [Parameter(Mandatory=$false)]
        [int]$Port = 389,

        [Parameter(Mandatory=$false)]
        [string]$Scheme = "ldap",

        [Parameter(Mandatory=$false)]
        [string]$BaseDN = "DC=panoply,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindDN = "CN=Administrator,CN=Users,DC=panoply,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindPassword = "Test@123!",

        [Parameter(Mandatory=$false)]
        [string]$UserNameAttr = "sAMAccountName"
    )

    $params = @{ UserIdentifier = $UserIdentifier }
    if ($DirectoryConfig) { $params.DirectoryConfig = $DirectoryConfig }
    else {
        if ($ContainerName) { $params.ContainerName = $ContainerName }
        $params.Server = $Server; $params.Port = $Port; $params.Scheme = $Scheme
        $params.BaseDN = $BaseDN; $params.BindDN = $BindDN; $params.BindPassword = $BindPassword
        $params.UserNameAttr = $UserNameAttr
    }

    $user = Get-LDAPUser @params
    return $null -ne $user
}

function Get-LDAPUserCount {
    <#
    .SYNOPSIS
        Get count of users in LDAP
    #>
    param(
        [Parameter(Mandatory=$false)]
        [hashtable]$DirectoryConfig,

        # Individual parameters (used when DirectoryConfig not provided)
        [Parameter(Mandatory=$false)]
        [string]$ContainerName,

        [Parameter(Mandatory=$false)]
        [string]$Server = "localhost",

        [Parameter(Mandatory=$false)]
        [int]$Port = 389,

        [Parameter(Mandatory=$false)]
        [string]$Scheme = "ldap",

        [Parameter(Mandatory=$false)]
        [string]$BaseDN = "DC=panoply,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindDN = "CN=Administrator,CN=Users,DC=panoply,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindPassword = "Test@123!",

        [Parameter(Mandatory=$false)]
        [string]$Filter
    )

    # Resolve config
    if ($DirectoryConfig) {
        $ContainerName = $DirectoryConfig.ContainerName
        $Server = "localhost"
        $Port = $DirectoryConfig.LdapSearchPort
        $Scheme = $DirectoryConfig.LdapSearchScheme
        $BaseDN = $DirectoryConfig.BaseDN
        $BindDN = $DirectoryConfig.BindDN
        $BindPassword = $DirectoryConfig.BindPassword
        if (-not $Filter) {
            # Use appropriate filter for the directory type
            $objectClass = $DirectoryConfig.UserObjectClass
            if ($objectClass -eq "user") {
                $Filter = "(&(objectClass=user)(!(objectClass=computer)))"
            } else {
                $Filter = "(objectClass=$objectClass)"
            }
        }
    }

    if (-not $ContainerName) { $ContainerName = "samba-ad-primary" }
    if (-not $Filter) { $Filter = "(&(objectClass=user)(!(objectClass=computer)))" }

    # A supplied config decides where the search runs (its container, or the toolbox when it has none).
    $configParameter = @{}
    if ($DirectoryConfig) { $configParameter.DirectoryConfig = $DirectoryConfig }

    $result = Invoke-LDAPSearch @configParameter `
        -ContainerName $ContainerName `
        -Server $Server `
        -Port $Port `
        -Scheme $Scheme `
        -BaseDN $BaseDN `
        -BindDN $BindDN `
        -BindPassword $BindPassword `
        -Filter $Filter `
        -Attributes @("dn")

    if ($null -eq $result) {
        return 0
    }

    # @() guards the single-match case: Where-Object returns a scalar string for one match, and
    # .Count on a scalar fails under Set-StrictMode -Version Latest (which scenario scripts set).
    $count = @($result -split "`n" | Where-Object { $_ -match "^dn:" }).Count
    return $count
}

function Get-LDAPGroup {
    <#
    .SYNOPSIS
        Get a group from LDAP by cn

    .DESCRIPTION
        Searches for a group by cn in the configured group container.
        Returns a hashtable with parsed LDIF attributes, including multi-valued
        member attributes as arrays.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$GroupName,

        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    $result = Invoke-LDAPSearch `
        -DirectoryConfig $DirectoryConfig `
        -ContainerName $DirectoryConfig.ContainerName `
        -Server "localhost" `
        -Port $DirectoryConfig.LdapSearchPort `
        -Scheme $DirectoryConfig.LdapSearchScheme `
        -BaseDN $DirectoryConfig.GroupContainer `
        -BindDN $DirectoryConfig.BindDN `
        -BindPassword $DirectoryConfig.BindPassword `
        -Filter "(cn=$GroupName)"

    if ($null -eq $result -or $result.Length -eq 0) {
        return $null
    }

    # Parse LDIF output, handle multi-valued attributes (e.g. member). Unfold RFC 2849
    # continuation lines first: member DNs routinely exceed the 78-column fold width.
    $group = @{}
    $lines = Expand-LDIFFoldedLine -RawLdif ($result -join "`n")

    foreach ($line in $lines) {
        if ($line -match "^([^:]+):\s*(.+)$") {
            $key = $matches[1]
            $value = $matches[2]

            if ($group.ContainsKey($key)) {
                if ($group[$key] -is [array]) {
                    $group[$key] += $value
                }
                else {
                    $group[$key] = @($group[$key], $value)
                }
            }
            else {
                $group[$key] = $value
            }
        }
    }

    return $group
}

function Test-LDAPGroupExists {
    <#
    .SYNOPSIS
        Check if a group exists in LDAP
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$GroupName,

        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    $group = Get-LDAPGroup -GroupName $GroupName -DirectoryConfig $DirectoryConfig
    return $null -ne $group
}

function Get-LDAPGroupMembers {
    <#
    .SYNOPSIS
        Get the member DNs of a group from LDAP

    .DESCRIPTION
        Returns an array of member DNs for the specified group.
        Filters out placeholder members (e.g. cn=placeholder) used to satisfy
        the groupOfNames MUST member constraint.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$GroupName,

        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$false)]
        [string]$PlaceholderDn = "cn=placeholder"
    )

    $group = Get-LDAPGroup -GroupName $GroupName -DirectoryConfig $DirectoryConfig
    if ($null -eq $group -or -not $group.ContainsKey('member')) {
        return @()
    }

    $members = $group['member']
    if ($members -isnot [array]) {
        $members = @($members)
    }

    # Filter out placeholder member
    $realMembers = @($members | Where-Object {
        -not $_.Equals($PlaceholderDn, [System.StringComparison]::OrdinalIgnoreCase)
    })

    return $realMembers
}

function Get-LDAPGroupCount {
    <#
    .SYNOPSIS
        Get count of groups in LDAP
    #>
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$false)]
        [string]$Filter
    )

    $groupObjectClass = $DirectoryConfig.GroupObjectClass
    if (-not $groupObjectClass) { $groupObjectClass = "groupOfNames" }
    if (-not $Filter) { $Filter = "(objectClass=$groupObjectClass)" }

    $result = Invoke-LDAPSearch `
        -DirectoryConfig $DirectoryConfig `
        -ContainerName $DirectoryConfig.ContainerName `
        -Server "localhost" `
        -Port $DirectoryConfig.LdapSearchPort `
        -Scheme $DirectoryConfig.LdapSearchScheme `
        -BaseDN $DirectoryConfig.GroupContainer `
        -BindDN $DirectoryConfig.BindDN `
        -BindPassword $DirectoryConfig.BindPassword `
        -Filter $Filter `
        -Attributes @("dn")

    if ($null -eq $result) {
        return 0
    }

    # @() guards the single-match case: Where-Object returns a scalar string for one match, and
    # .Count on a scalar fails under Set-StrictMode -Version Latest (which scenario scripts set).
    $count = @($result -split "`n" | Where-Object { $_ -match "^dn:" }).Count
    return $count
}

function Get-LDAPGroupList {
    <#
    .SYNOPSIS
        List all group names (cn values) in LDAP
    #>
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$false)]
        [string]$Filter
    )

    $groupObjectClass = $DirectoryConfig.GroupObjectClass
    if (-not $groupObjectClass) { $groupObjectClass = "groupOfNames" }
    if (-not $Filter) { $Filter = "(objectClass=$groupObjectClass)" }

    $result = Invoke-LDAPSearch `
        -DirectoryConfig $DirectoryConfig `
        -ContainerName $DirectoryConfig.ContainerName `
        -Server "localhost" `
        -Port $DirectoryConfig.LdapSearchPort `
        -Scheme $DirectoryConfig.LdapSearchScheme `
        -BaseDN $DirectoryConfig.GroupContainer `
        -BindDN $DirectoryConfig.BindDN `
        -BindPassword $DirectoryConfig.BindPassword `
        -Filter $Filter `
        -Attributes @("cn")

    if ($null -eq $result) {
        return @()
    }

    $groups = @()
    $lines = Expand-LDIFFoldedLine -RawLdif ($result -join "`n")
    foreach ($line in $lines) {
        if ($line -match "^cn:\s*(.+)$") {
            $groups += $matches[1].Trim()
        }
    }

    return $groups
}

# Functions are automatically available when dot-sourced
# No need for Export-ModuleMember

<#
    Active Directory bind sub-codes.

    Every refused bind comes back as result code 49 (invalidCredentials) with the real reason in a
    hexadecimal sub-code inside the diagnostic message, so the result code alone cannot tell a wrong
    password from a correct one that must be changed. Samba AD emits the same Windows sub-codes.

    Reference: the "data <code>" field of the AcceptSecurityContext error, as documented for Active
    Directory's LDAP bind response and reproduced by Samba's LDAP server.
#>
$script:LDAPBindSubCodes = @{
    '525' = 'UserNotFound'
    '52e' = 'InvalidCredentials'
    '530' = 'LogonTimeRestricted'
    '531' = 'WorkstationRestricted'
    '532' = 'PasswordExpired'
    '533' = 'AccountDisabled'
    '701' = 'AccountExpired'
    '773' = 'MustChangePassword'
    '775' = 'AccountLockedOut'
}

function Get-LDAPBindOutcome {
    <#
    .SYNOPSIS
        Classify the outcome of an LDAP simple bind from a client's exit code and output.

    .DESCRIPTION
        Turns ldapwhoami's exit code and diagnostic text into one word describing what the directory
        actually decided. The distinction that matters is between 'InvalidCredentials' (the password is
        wrong) and 'MustChangePassword' (the password is right, and the directory is insisting the
        account holder chooses a new one). Both arrive as LDAP result code 49 on Active Directory.

        OpenLDAP has no equivalent of Active Directory's hexadecimal sub-code (there is no portable
        "must change" state to distinguish there in the first place; see
        LdapConnectorPassword.BuildNonActiveDirectoryResult), so a bare 'ldap_bind: Invalid credentials'
        message with none of the AD-style sub-codes present is classified as InvalidCredentials directly.

        Anything else unrecognised is reported as 'Failed' rather than being guessed at, so a new failure
        mode surfaces as a test failure instead of being quietly folded into an existing category.

    .PARAMETER ExitCode
        The client's exit code. Zero means the bind succeeded.

    .PARAMETER BindOutput
        The client's combined output, which carries the diagnostic message on failure.

    .OUTPUTS
        One of: Success, MustChangePassword, InvalidCredentials, AccountDisabled, PasswordExpired,
        AccountLockedOut, AccountExpired, UserNotFound, LogonTimeRestricted, WorkstationRestricted,
        Failed.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [int]$ExitCode,

        [Parameter(Mandatory=$false)]
        [AllowEmptyString()]
        [string]$BindOutput = ""
    )

    if ($ExitCode -eq 0) {
        return 'Success'
    }

    if ($BindOutput -match ',\s*data\s+([0-9a-fA-F]+)\s*,') {
        $subCode = $matches[1].ToLowerInvariant()
        if ($script:LDAPBindSubCodes.ContainsKey($subCode)) {
            return $script:LDAPBindSubCodes[$subCode]
        }
    }

    # No AD-style sub-code: OpenLDAP's own diagnostic for a wrong password is exactly this, with nothing
    # further to parse. Checked only once the sub-code match above has failed, so an Active Directory
    # message this does not recognise still falls through to Failed rather than being misread as this.
    if ($BindOutput -match '^ldap_bind:\s*Invalid credentials') {
        return 'InvalidCredentials'
    }

    return 'Failed'
}

function Test-LDAPBind {
    <#
    .SYNOPSIS
        Attempt an LDAP simple bind as a given account and report what the directory decided.

    .DESCRIPTION
        Binds with ldapwhoami inside the directory container, which is how the account holder's own
        credentials are checked without JIM in the path at all. Returns the classified outcome
        alongside the raw output, so a failing assertion can show what the directory actually said.

        Cleartext LDAP is deliberate and sufficient here on the container labs: this reads a credential
        decision, it does not write a password. The password *writes* this scenario depends on go over
        LDAPS, because Active Directory refuses them otherwise. A real Active Directory domain
        controller (a config with no container) enforces LDAP signing and refuses a cleartext simple
        bind, so its bind goes over LDAPS through the toolbox.

    .PARAMETER BindDN
        The Distinguished Name to bind as.

    .PARAMETER BindPassword
        The password to bind with.

    .PARAMETER DirectoryConfig
        Directory configuration hashtable from Get-DirectoryConfig.

    .OUTPUTS
        A hashtable with Outcome (see Get-LDAPBindOutcome), ExitCode and Output.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$BindDN,

        [Parameter(Mandatory=$true)]
        [string]$BindPassword,

        [Parameter(Mandatory=$false)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$false)]
        [string]$ContainerName,

        [Parameter(Mandatory=$false)]
        [int]$Port = 389,

        [Parameter(Mandatory=$false)]
        [string]$Scheme = "ldap"
    )

    if ($DirectoryConfig) {
        $toolConfig = $DirectoryConfig
        $Port = $DirectoryConfig.LdapSearchPort
        $Scheme = $DirectoryConfig.LdapSearchScheme
    }
    else {
        if (-not $ContainerName) { $ContainerName = "samba-ad-primary" }
        $toolConfig = @{ ContainerName = $ContainerName }
    }

    $ldapUri = if (Test-LdapToolboxConfig -DirectoryConfig $toolConfig) {
        Get-LdapUri -DirectoryConfig $toolConfig
    }
    else {
        "${Scheme}://localhost:${Port}"
    }

    $invocation = Invoke-LdapTool -DirectoryConfig $toolConfig -Tool ldapwhoami `
        -Arguments @('-x', '-H', $ldapUri, '-D', $BindDN, '-w', $BindPassword) -PassThruExitCode
    $exitCode = $invocation.ExitCode
    $outputText = ($invocation.Output | Out-String).Trim()

    return @{
        Outcome  = Get-LDAPBindOutcome -ExitCode $exitCode -BindOutput $outputText
        ExitCode = $exitCode
        Output   = $outputText
    }
}

function Set-LDAPUserPasswordAsAccountHolder {
    <#
    .SYNOPSIS
        Change an account's password as the account holder, authenticating with the current one.

    .DESCRIPTION
        Uses smbpasswd's remote mode, which performs the account holder's own password change against
        the domain controller. This is the flow a new starter is put through at first sign-in, and it
        is the only one that proves an initial password is *usable* rather than merely correct: an
        administrative reset would prove nothing about the credential JIM set.

        Note this is a change, not a set: the directory enforces the parts of its password policy that
        only apply to a change (minimum age, history, minimum length), which an administrative set
        such as JIM's own bypasses.

        On a real Active Directory config (DirectoryType ActiveDirectory, no container) the same
        change is made the way Windows makes it: the account binds as itself over LDAPS and, in one
        modify, deletes unicodePwd carrying the old password and adds unicodePwd carrying the new one.
        smbpasswd does not exist there. The account's DN is found with an administrator search when
        -AccountDN is not given.

    .PARAMETER AccountName
        The account's sAMAccountName.

    .PARAMETER CurrentPassword
        The password the account currently holds.

    .PARAMETER NewPassword
        The password to change it to.

    .PARAMETER DirectoryConfig
        Directory configuration hashtable from Get-DirectoryConfig.

    .PARAMETER AccountDN
        The account's Distinguished Name. Only used on Active Directory, where the change is an LDAP
        modify of that entry; looked up by sAMAccountName as the administrator when omitted.

    .OUTPUTS
        A hashtable with Success, ExitCode, Output and Outcome (see Get-LDAPPasswordModifyOutcome).
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$AccountName,

        [Parameter(Mandatory=$true)]
        [string]$CurrentPassword,

        [Parameter(Mandatory=$true)]
        [string]$NewPassword,

        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$false)]
        [string]$AccountDN
    )

    if (Test-ActiveDirectoryConfig -DirectoryConfig $DirectoryConfig) {
        if (-not $AccountDN) {
            $user = Get-LDAPUser -UserIdentifier $AccountName -DirectoryConfig $DirectoryConfig
            if ($null -eq $user -or -not $user.ContainsKey('dn')) {
                throw "Set-LDAPUserPasswordAsAccountHolder: could not find the account '$AccountName' to change its password."
            }
            $AccountDN = [string]$user['dn']
        }

        # A simple bind accepts the NetBIOS domain\account form, so the account holder needs no DN to bind.
        $result = Invoke-LdapUnicodePwdModify -DirectoryConfig $DirectoryConfig `
            -BindDN "$($DirectoryConfig.ShortDomain)\$AccountName" -BindPassword $CurrentPassword `
            -TargetDN $AccountDN -NewPassword $NewPassword -OldPassword $CurrentPassword

        return @{
            Success  = ($result.ExitCode -eq 0)
            ExitCode = $result.ExitCode
            Output   = $result.Output
            Outcome  = $result.Outcome
        }
    }

    # smbpasswd -r reads the current password then the new one twice, from standard input.
    $input = "$CurrentPassword`n$NewPassword`n$NewPassword`n"

    # The domain controller is addressed by the name it advertises as its dNSHostName, which is what
    # its TLS certificate carries and what the kpasswd exchange expects.
    $domainControllerName = "dc1.$($DirectoryConfig.Domain)"

    $output = $input | & docker exec -i $DirectoryConfig.ContainerName `
        smbpasswd -r $domainControllerName -U $AccountName -s 2>&1
    $exitCode = $LASTEXITCODE
    $outputText = ($output | Out-String).Trim()

    return @{
        Success  = ($exitCode -eq 0)
        ExitCode = $exitCode
        Output   = $outputText
        Outcome  = Get-LDAPPasswordModifyOutcome -ExitCode $exitCode -Output $outputText
    }
}

# LDAP result codes an RFC 3062 Password Modify can answer with, as ldappasswd prints them
# ("Result: <text> (<code>)"). The number is what is matched on; the text varies by server.
$script:LDAPPasswordModifyResultCodes = @{
    '19' = 'ConstraintViolation'   # the directory's password policy refused the new value
    '49' = 'InvalidCredentials'    # the bind that carries the operation was refused
    '50' = 'InsufficientAccess'    # bound, but not permitted to write this entry's password
    '53' = 'UnwillingToPerform'    # for example a cleartext operation the directory will not take
}

# Active Directory's own error codes, which lead the "additional info" line of a refused unicodePwd
# write and say more than the LDAP result code can. Both codes arrive as a constraint violation (19),
# and the password policy one can also arrive as unwilling to perform (53), so the code is what is read
# first. 0x52D is ERROR_PASSWORD_RESTRICTION (the new value broke the domain's password policy) and
# 0x56 is ERROR_INVALID_PASSWORD (the old password supplied with the change was wrong).
$script:LDAPPasswordModifyAdCodes = @{
    '0000052D' = 'ConstraintViolation'
    '00000056' = 'InvalidOldPassword'
}

function Get-LDAPPasswordModifyOutcome {
    <#
    .SYNOPSIS
        Classify the outcome of an RFC 3062 Password Modify from ldappasswd's exit code and output.

    .DESCRIPTION
        Turns ldappasswd's exit code and diagnostic text into one word describing what the directory
        decided. The distinction that matters for Scenario 022 is between 'ConstraintViolation' (the
        password policy did its job and refused the value) and everything else: a refusal for want of
        access, or a bind failure, is a test-fixture problem that would otherwise pass as "enforced".

        Active Directory has no RFC 3062 operation; its password writes are an ldapmodify of unicodePwd
        and it explains a refusal with a hexadecimal code at the head of the "additional info" line.
        That code is read before the result code: 0000052D (the password policy refused the value) is
        reported as 'ConstraintViolation' whether Active Directory answered it as constraint violation
        or as unwilling to perform, and 00000056 (the old password was wrong) is reported as
        'InvalidOldPassword' rather than being mistaken for a policy refusal.

        Anything unrecognised is reported as 'Failed' rather than being guessed at, so a new failure
        mode surfaces as a test failure instead of being quietly folded into an existing category.

    .PARAMETER ExitCode
        The client's exit code. Zero means the operation succeeded.

    .PARAMETER Output
        The client's combined output, which carries "Result: ... (<code>)" on failure.

    .OUTPUTS
        One of: Success, ConstraintViolation, InvalidOldPassword, InvalidCredentials,
        InsufficientAccess, UnwillingToPerform, Failed.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [int]$ExitCode,

        [Parameter(Mandatory=$false)]
        [AllowEmptyString()]
        [string]$Output = ""
    )

    if ($ExitCode -eq 0) {
        return 'Success'
    }

    if ($Output -match 'additional info:\s*([0-9A-Fa-f]{8})\b') {
        $adCode = $matches[1].ToUpperInvariant()
        if ($script:LDAPPasswordModifyAdCodes.ContainsKey($adCode)) {
            return $script:LDAPPasswordModifyAdCodes[$adCode]
        }
    }

    if ($Output -match '\((\d+)\)') {
        $resultCode = $matches[1]
        if ($script:LDAPPasswordModifyResultCodes.ContainsKey($resultCode)) {
            return $script:LDAPPasswordModifyResultCodes[$resultCode]
        }
    }

    return 'Failed'
}

function Invoke-LdapUnicodePwdModify {
    <#
    .SYNOPSIS
        Change or set an Active Directory account's password by modifying unicodePwd over LDAPS.

    .DESCRIPTION
        Active Directory takes a password only as a write to unicodePwd on an encrypted connection.
        With -OldPassword the modify is a change, the account holder's operation: one modify that
        deletes unicodePwd carrying the old value and adds it carrying the new one. Without it the
        modify replaces unicodePwd, the administrator's reset. The LDIF goes to ldapmodify on standard
        input, so neither password is in a process argument list other than the bind's own -w.

        The tool runs through Invoke-LdapTool, so it goes to the toolbox for a container-less config,
        over Get-LdapUri (LDAPS).

    .PARAMETER DirectoryConfig
        An ActiveDirectory config from Get-DirectoryConfig.

    .PARAMETER BindDN
        Who to bind as: a Distinguished Name, or the domain\account form for the account holder.

    .PARAMETER BindPassword
        The bind password.

    .PARAMETER TargetDN
        The Distinguished Name of the entry whose password is written.

    .PARAMETER NewPassword
        The password to set.

    .PARAMETER OldPassword
        The account's current password. Present: a change (delete and add). Absent: a reset (replace).

    .OUTPUTS
        A hashtable with Outcome (see Get-LDAPPasswordModifyOutcome), ExitCode and Output.
    #>
    [CmdletBinding()]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
        Justification = 'Lab passwords are plain-text test fixtures, handed to a native LDAP tool as arguments; a SecureString would only be unwrapped again at once.')]
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig,

        [Parameter(Mandatory=$true)]
        [string]$BindDN,

        [Parameter(Mandatory=$true)]
        [string]$BindPassword,

        [Parameter(Mandatory=$true)]
        [string]$TargetDN,

        [Parameter(Mandatory=$true)]
        [string]$NewPassword,

        [Parameter(Mandatory=$false)]
        [string]$OldPassword
    )

    # LDIF carries a value that is not plain printable ASCII (or that starts with a space, colon or
    # less-than sign, or ends with a space) as base64, marked by a second colon.
    $dnLine = if ($TargetDN -match '^[\x21-\x39\x3B\x3D-\x7E]([\x20-\x7E]*[\x21-\x7E])?$') {
        "dn: $TargetDN"
    }
    else {
        "dn:: $([System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($TargetDN)))"
    }

    $newValue = ConvertTo-UnicodePwdValue -Password $NewPassword
    $ldifLines = if ($PSBoundParameters.ContainsKey('OldPassword')) {
        @($dnLine, 'changetype: modify',
          'delete: unicodePwd', "unicodePwd:: $(ConvertTo-UnicodePwdValue -Password $OldPassword)", '-',
          'add: unicodePwd', "unicodePwd:: $newValue", '-')
    }
    else {
        @($dnLine, 'changetype: modify',
          'replace: unicodePwd', "unicodePwd:: $newValue", '-')
    }
    $ldif = ($ldifLines -join "`n") + "`n"

    $invocation = Invoke-LdapTool -DirectoryConfig $DirectoryConfig -Tool ldapmodify `
        -Arguments @('-x', '-H', (Get-LdapUri -DirectoryConfig $DirectoryConfig), '-D', $BindDN, '-w', $BindPassword) `
        -InputObject $ldif -PassThruExitCode
    $exitCode = $invocation.ExitCode
    $outputText = ($invocation.Output | Out-String).Trim()

    return @{
        Outcome  = Get-LDAPPasswordModifyOutcome -ExitCode $exitCode -Output $outputText
        ExitCode = $exitCode
        Output   = $outputText
    }
}

function Set-LDAPUserPasswordWithPasswordModify {
    <#
    .SYNOPSIS
        Change a password with the RFC 3062 Password Modify extended operation, as a given account.

    .DESCRIPTION
        Runs ldappasswd inside the directory container, bound as -BindDN, against -TargetDN (the bound
        account itself when -TargetDN is omitted). This is the operation JIM's LDAP Connector uses to
        set a password on every directory that is not Active Directory, so an outcome observed here is
        an outcome JIM's password channel would see too.

        Two things this proves that a Modify of userPassword would not: that the directory takes the
        extended operation at all over the scheme in use (the Scenario 022 "step 0 spike"), and that a
        password policy overlay sees the cleartext value it needs for a quality check. The rootdn is
        exempt from OpenLDAP's password policy, so bind as an ordinary account to observe enforcement.

        Active Directory has no RFC 3062 Password Modify, and neither does JIM's LDAP Connector use it
        there. On an ActiveDirectory config the same call is made with unicodePwd over LDAPS through
        ldapmodify: replace unicodePwd when the bound account resets another account's password (the
        administrator case), or delete the old value and add the new one in a single modify when it
        changes its own (the account holder case, where the old value is BindPassword).

    .PARAMETER BindDN
        The Distinguished Name to bind as.

    .PARAMETER BindPassword
        The password to bind with.

    .PARAMETER NewPassword
        The password to set.

    .PARAMETER TargetDN
        The entry whose password is set. Defaults to the bound account (a self change). On Active
        Directory a BindDN that is not itself a DN (domain\account, a user principal name) needs
        this, because the modify must name the entry.

    .PARAMETER DirectoryConfig
        Directory configuration hashtable from Get-DirectoryConfig (container, port and scheme).

    .OUTPUTS
        A hashtable with Outcome (see Get-LDAPPasswordModifyOutcome), ExitCode and Output.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$BindDN,

        [Parameter(Mandatory=$true)]
        [string]$BindPassword,

        [Parameter(Mandatory=$true)]
        [string]$NewPassword,

        [Parameter(Mandatory=$false)]
        [string]$TargetDN,

        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

    if (Test-ActiveDirectoryConfig -DirectoryConfig $DirectoryConfig) {
        $selfChange = (-not $TargetDN) -or $TargetDN.Equals($BindDN, [System.StringComparison]::OrdinalIgnoreCase)
        if (-not $TargetDN) {
            if ($BindDN -notmatch '^[^=\\]+=') {
                throw "Set-LDAPUserPasswordWithPasswordModify: on Active Directory the entry to modify must be named, and the BindDN '$BindDN' is not a Distinguished Name. Pass -TargetDN."
            }
            $TargetDN = $BindDN
        }

        $modify = @{
            DirectoryConfig = $DirectoryConfig
            BindDN          = $BindDN
            BindPassword    = $BindPassword
            TargetDN        = $TargetDN
            NewPassword     = $NewPassword
        }
        if ($selfChange) { $modify.OldPassword = $BindPassword }

        return Invoke-LdapUnicodePwdModify @modify
    }

    $ldapUri = "$($DirectoryConfig.LdapSearchScheme)://localhost:$($DirectoryConfig.LdapSearchPort)"

    # -s carries the new password; without a target DN ldappasswd changes the bound account's own.
    $ldapArgs = @("-x", "-H", $ldapUri,
                  "-D", $BindDN, "-w", $BindPassword, "-s", $NewPassword)
    if ($TargetDN) {
        $ldapArgs += $TargetDN
    }

    $invocation = Invoke-LdapTool -DirectoryConfig $DirectoryConfig -Tool ldappasswd -Arguments $ldapArgs -PassThruExitCode
    $exitCode = $invocation.ExitCode
    $outputText = ($invocation.Output | Out-String).Trim()

    return @{
        Outcome  = Get-LDAPPasswordModifyOutcome -ExitCode $exitCode -Output $outputText
        ExitCode = $exitCode
        Output   = $outputText
    }
}
