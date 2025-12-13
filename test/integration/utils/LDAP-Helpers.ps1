<#
.SYNOPSIS
    LDAP helper functions for integration testing

.DESCRIPTION
    Provides functions to interact with LDAP directories (Samba AD, OpenLDAP)
    for test setup, data population, and validation
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
        Execute an LDAP search using ldapsearch command
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Server,

        [Parameter(Mandatory=$false)]
        [int]$Port = 389,

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

    $ldapUri = "ldap://${Server}:${Port}"
    $attrString = $Attributes -join " "

    try {
        $result = docker exec samba-ad-primary ldapsearch `
            -x `
            -H $ldapUri `
            -D $BindDN `
            -w $BindPassword `
            -b $BaseDN `
            $Filter `
            $attrString 2>&1

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
        Get a user from LDAP by sAMAccountName
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$SamAccountName,

        [Parameter(Mandatory=$false)]
        [string]$Server = "localhost",

        [Parameter(Mandatory=$false)]
        [int]$Port = 389,

        [Parameter(Mandatory=$false)]
        [string]$BaseDN = "DC=testdomain,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindDN = "CN=Administrator,CN=Users,DC=testdomain,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindPassword = "Test@123!"
    )

    $filter = "(sAMAccountName=$SamAccountName)"

    $result = Invoke-LDAPSearch `
        -Server $Server `
        -Port $Port `
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
        [string]$SamAccountName,

        [Parameter(Mandatory=$false)]
        [string]$Server = "localhost",

        [Parameter(Mandatory=$false)]
        [int]$Port = 389,

        [Parameter(Mandatory=$false)]
        [string]$BaseDN = "DC=testdomain,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindDN = "CN=Administrator,CN=Users,DC=testdomain,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindPassword = "Test@123!"
    )

    $user = Get-LDAPUser `
        -SamAccountName $SamAccountName `
        -Server $Server `
        -Port $Port `
        -BaseDN $BaseDN `
        -BindDN $BindDN `
        -BindPassword $BindPassword

    return $null -ne $user
}

function Get-LDAPUserCount {
    <#
    .SYNOPSIS
        Get count of users in LDAP
    #>
    param(
        [Parameter(Mandatory=$false)]
        [string]$Server = "localhost",

        [Parameter(Mandatory=$false)]
        [int]$Port = 389,

        [Parameter(Mandatory=$false)]
        [string]$BaseDN = "DC=testdomain,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindDN = "CN=Administrator,CN=Users,DC=testdomain,DC=local",

        [Parameter(Mandatory=$false)]
        [string]$BindPassword = "Test@123!",

        [Parameter(Mandatory=$false)]
        [string]$Filter = "(&(objectClass=user)(!(objectClass=computer)))"
    )

    $result = Invoke-LDAPSearch `
        -Server $Server `
        -Port $Port `
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

# Export functions
Export-ModuleMember -Function @(
    'Test-LDAPConnection',
    'Invoke-LDAPSearch',
    'Get-LDAPUser',
    'Test-LDAPUserExists',
    'Get-LDAPUserCount'
)
