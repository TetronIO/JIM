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

    # Parse LDIF output
    $user = @{}
    $lines = $result -split "`n"

    foreach ($line in $lines) {
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

    $count = ($result -split "`n" | Where-Object { $_ -match "^dn:" }).Count
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

    # Parse LDIF output — handle multi-valued attributes (e.g. member)
    $group = @{}
    $lines = $result -split "`n"

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

    $count = ($result -split "`n" | Where-Object { $_ -match "^dn:" }).Count
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
    $lines = $result -split "`n"
    foreach ($line in $lines) {
        if ($line -match "^cn:\s*(.+)$") {
            $groups += $matches[1].Trim()
        }
    }

    return $groups
}

# Functions are automatically available when dot-sourced
# No need for Export-ModuleMember
