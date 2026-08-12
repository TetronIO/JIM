# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    LDAP helper functions for integration testing

.DESCRIPTION
    Provides functions to interact with LDAP directories (Samba AD, OpenLDAP)
    for test setup, data population, and validation.

    Functions accept either a $DirectoryConfig hashtable (from Get-DirectoryConfig)
    or individual parameters for backward compatibility.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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
    #>
    param(
        [Parameter(Mandatory=$false)]
        [string]$ContainerName = "samba-ad-primary",

        [Parameter(Mandatory=$true)]
        [string]$Server,

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

    $ldapUri = "${Scheme}://${Server}:${Port}"

    try {
        # Build ldapsearch arguments array — pass args directly to docker exec
        # to avoid shell glob expansion issues with '*'
        $ldapArgs = @(
            "exec", $ContainerName, "ldapsearch",
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

        $result = & docker @ldapArgs 2>&1

        if ($LASTEXITCODE -ne 0) {
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

    $result = Invoke-LDAPSearch `
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

    $result = Invoke-LDAPSearch `
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
        account holder chooses a new one). Both arrive as LDAP result code 49.

        Anything unrecognised is reported as 'Failed' rather than being guessed at, so a new failure
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

        Cleartext LDAP is deliberate and sufficient here: this reads a credential decision, it does not
        write a password. The password *writes* this scenario depends on go over LDAPS, because Active
        Directory refuses them otherwise.

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
        $ContainerName = $DirectoryConfig.ContainerName
        $Port = $DirectoryConfig.LdapSearchPort
        $Scheme = $DirectoryConfig.LdapSearchScheme
    }
    if (-not $ContainerName) { $ContainerName = "samba-ad-primary" }

    $output = & docker exec $ContainerName ldapwhoami -x `
        -H "${Scheme}://localhost:${Port}" -D $BindDN -w $BindPassword 2>&1
    $exitCode = $LASTEXITCODE
    $outputText = ($output | Out-String).Trim()

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

    .PARAMETER AccountName
        The account's sAMAccountName.

    .PARAMETER CurrentPassword
        The password the account currently holds.

    .PARAMETER NewPassword
        The password to change it to.

    .PARAMETER DirectoryConfig
        Directory configuration hashtable from Get-DirectoryConfig.

    .OUTPUTS
        A hashtable with Success, ExitCode and Output.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$AccountName,

        [Parameter(Mandatory=$true)]
        [string]$CurrentPassword,

        [Parameter(Mandatory=$true)]
        [string]$NewPassword,

        [Parameter(Mandatory=$true)]
        [hashtable]$DirectoryConfig
    )

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
    }
}
