# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The names and seed data Scenario 037 (Entra ID as a source, #361) shares between its Setup and scenario scripts.

.DESCRIPTION
    The harness seeds every user and group itself, through Microsoft Graph, and JIM reads them: the reverse of Scenario
    036. Every object carries the run's prefix (jimit-<run id>-, Get-EntraIdConnectorLabRunPrefix) in its display name,
    mail nickname and user principal name, so the inbound Synchronisation Rules can scope on it and the harness's cleanup
    and snapshot tell what a run made from everything else in the tenant.

    The seed is small and shaped by what it proves, not by volume:

      p01 to p06    members, with a department and a job title. p06 is deleted part way through.
      p07           a guest (userType Guest), read as externalEntraDirectory.
      Staff         a security group of p01 to p04.
      Engineering   a security group of p01 and p02, which the Delta Import step changes to p01 and p05.
      All Teams     a security group whose one member is Engineering: a nested group.
      Mixed         a security group of p05 and one of the tenant's own users, who is out of every rule's scope.
      Temporary     a security group of p03, deleted part way through.
      Hidden        a Microsoft 365 group with hidden membership, of p03 and p04, read through Member.Read.Hidden.

    Dot-source after utils/EntraIdConnectorLab-Helpers.ps1.
#>

$script:Scenario037EntraSystemName = "Entra ID (Scenario 037)"
# The app registration Scenario 036 also uses; each run gives it a new certificate, so the two never run at once.
$script:Scenario037ApplicationName = "JIM Entra ID Connector (CI)"

$script:Scenario037UserImportRuleName = "Entra ID Users Import (Entra ID -> MV)"
$script:Scenario037GroupImportRuleName = "Entra ID Groups Import (Entra ID -> MV)"

# The JIM test tenant's default domain, which every user principal name the scenario makes is in.
$script:Scenario037DefaultDomain = "tetronjim.onmicrosoft.com"

function Get-Scenario037AccountName {
    <#
    .SYNOPSIS
        Person $Number's mail nickname, which JIM projects as their Account Name.
    #>
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][int]$Number
    )
    return "{0}p{1:D2}" -f $Prefix, $Number
}

function New-Scenario037People {
    <#
    .SYNOPSIS
        The scenario's seven people, as the harness creates them and as JIM is expected to project them.
    #>
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][string]$Domain
    )

    $departments = @('Engineering', 'Engineering', 'Finance', 'Finance', 'Operations', 'Operations', 'Partners')
    $titles = @('Engineer', 'Senior Engineer', 'Accountant', 'Finance Manager', 'Analyst', 'Operations Lead', 'Consultant')
    $people = @()
    for ($number = 1; $number -le 7; $number++) {
        $accountName = Get-Scenario037AccountName -Prefix $Prefix -Number $number
        $people += [PSCustomObject]@{
            Number      = $number
            AccountName = $accountName
            DisplayName = "{0}Person {1:D2}" -f $Prefix, $number
            GivenName   = "Person"
            Surname     = "Reader{0:D2}" -f $number
            Upn         = "$accountName@$Domain"
            Department  = $departments[$number - 1]
            JobTitle    = $titles[$number - 1]
            IsGuest     = ($number -eq 7)
        }
    }
    return $people
}

function New-Scenario037Groups {
    <#
    .SYNOPSIS
        The scenario's groups. Members are account names of the run's people, or a group's key (GroupMembers); the
        tenant's own user in Mixed is added by the scenario, since only it knows who that is.
    #>
    param(
        [Parameter(Mandatory)][string]$Prefix
    )

    $group = {
        param([string]$Key, [string[]]$Members, [string[]]$GroupMembers = @(), [bool]$Unified = $false, [bool]$Hidden = $false)
        [PSCustomObject]@{
            Key          = $Key
            AccountName  = "$Prefix$($Key.ToLowerInvariant() -replace ' ', '-')"
            DisplayName  = "$Prefix$Key"
            Members      = [System.Collections.Generic.List[string]]@($Members | ForEach-Object { "{0}{1}" -f $Prefix, $_ })
            GroupMembers = [System.Collections.Generic.List[string]]@($GroupMembers)
            Unified      = $Unified
            Hidden       = $Hidden
        }
    }

    return @(
        (& $group 'Staff' @('p01', 'p02', 'p03', 'p04')),
        (& $group 'Engineering' @('p01', 'p02')),
        (& $group 'All Teams' @() @('Engineering')),
        (& $group 'Mixed' @('p05')),
        (& $group 'Temporary' @('p03')),
        (& $group 'Hidden' @('p03', 'p04') @() $true $true)
    )
}

function New-Scenario037Password {
    <#
    .SYNOPSIS
        A password for a seeded user that nobody needs to know: nobody signs in as them. Random, from the system's
        cryptographic generator, and never written anywhere.
    #>
    [OutputType([string])]
    param()

    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)
    $random = -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
    # Each of the four character classes, whatever the random part held.
    return "Aa9!$random"
}
