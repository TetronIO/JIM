# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The names and seed data Scenario 038 (hybrid and Source of Authority, #3029) shares between its Setup and scenario
    scripts.

.DESCRIPTION
    The harness creates every user and group in the lab's hybrid forest, through LDAP, in an organisational unit of the
    run's own under the one Entra Cloud Sync synchronises. Cloud Sync copies them into the hybrid test tenant, and JIM
    reads them from there through the Microsoft Entra ID Connector. Nothing is written to Entra ID directly except the
    Source of Authority switches, which Entra ID alone holds.

    Every object carries the run's prefix (jimit-<run id>-, Get-EntraIdConnectorLabRunPrefix) in its display name, so
    the inbound Synchronisation Rules can scope on it and the snapshot tells what a run made from everything else.
    Active Directory caps sAMAccountName at 20 characters, which a GitHub run prefix alone nearly fills, so account names
    are built from a shorter tag of the run id (Get-Scenario038AccountTag).

    The seed is small and shaped by what it proves:

      p01        stays synchronised from Active Directory throughout. The export rule writes its job title, which
                 Entra ID refuses.
      p02        has its Source of Authority switched to Entra ID, and back.
      p03        a member of both groups.
      Staff      a security group of p01, p02 and p03, synchronised throughout.
      Switched   a security group of p03, whose Source of Authority is switched to Entra ID, and back.

    Dot-source after utils/EntraIdConnectorLab-Helpers.ps1.
#>

$script:Scenario038EntraSystemName = "Entra ID (Scenario 038)"
# The permanent app registration in the hybrid test tenant, made once by its administrator with
# Register-JIMEntraIdConnector -TrackSourceOfAuthority (test/integration/ad-lab/README.md, The hybrid forest).
$script:Scenario038ApplicationName = "JIM Entra ID Connector (CI)"

$script:Scenario038UserImportRuleName = "Entra ID Users Import (Entra ID -> MV)"
$script:Scenario038GroupImportRuleName = "Entra ID Groups Import (Entra ID -> MV)"
$script:Scenario038UserExportRuleName = "Entra ID Users Export (MV -> Entra ID)"

# The job title the export rule writes to p01, which Entra ID must refuse because Active Directory owns it.
$script:Scenario038ExportedJobTitle = "Written by JIM integration Scenario 038"

function Get-Scenario038AccountTag {
    <#
    .SYNOPSIS
        A short tag of the run, for names Active Directory limits: "j" and the last ten characters of the run id.

    .DESCRIPTION
        sAMAccountName holds at most 20 characters. The tag is at most 11, leaving room for a hyphen and a name of up
        to eight (p01, staff, switched). The run id's last characters vary most between runs (GitHub's run ids rise by
        one, and the attempt is last), so two runs' tags differ.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Prefix
    )

    Assert-EntraIdConnectorLabRunPrefix -Prefix $Prefix
    $runId = $Prefix.Substring($script:EntraIdConnectorLabPrefixRoot.Length).TrimEnd('-')
    $tail = if ($runId.Length -gt 10) { $runId.Substring($runId.Length - 10) } else { $runId }
    return "j$tail"
}

function Get-Scenario038SamAccountName {
    <#
    .SYNOPSIS
        An object's sAMAccountName: the run's tag, a hyphen, and the object's short name. At most 20 characters.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]{1,8}$')][string]$Name
    )

    $samAccountName = "$(Get-Scenario038AccountTag -Prefix $Prefix)-$Name"
    if ($samAccountName.Length -gt 20) { throw "'$samAccountName' is longer than the 20 characters Active Directory allows." }
    return $samAccountName
}

function Get-Scenario038RunOuDn {
    <#
    .SYNOPSIS
        The run's own organisational unit, under the one Cloud Sync synchronises: OU=<prefix without its hyphen>.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][string]$SyncOuDn
    )

    Assert-EntraIdConnectorLabRunPrefix -Prefix $Prefix
    return "OU=$($Prefix.TrimEnd('-')),$SyncOuDn"
}

function New-Scenario038People {
    <#
    .SYNOPSIS
        The scenario's three people, as the harness creates them in Active Directory and JIM is expected to project them.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][string]$RunOuDn
    )

    $roles = @('Stays synchronised', 'Switched and back', 'Member of both')
    $people = @()
    for ($number = 1; $number -le 3; $number++) {
        $displayName = "{0}Person {1:D2}" -f $Prefix, $number
        $people += [PSCustomObject]@{
            Key            = "p{0:D2}" -f $number
            DisplayName    = $displayName
            SamAccountName = Get-Scenario038SamAccountName -Prefix $Prefix -Name ("p{0:D2}" -f $number)
            GivenName      = "Person"
            Surname        = "Hybrid{0:D2}" -f $number
            Dn             = "CN=$displayName,$RunOuDn"
            Role           = $roles[$number - 1]
        }
    }
    return $people
}

function New-Scenario038Groups {
    <#
    .SYNOPSIS
        The scenario's two groups, each listing its members by person key.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][string]$RunOuDn
    )

    return @(
        foreach ($group in @(@{ Key = 'staff'; Name = 'Staff'; Members = @('p01', 'p02', 'p03') },
                             @{ Key = 'switched'; Name = 'Switched'; Members = @('p03') })) {
            $displayName = "$Prefix$($group.Name)"
            [PSCustomObject]@{
                Key            = $group.Key
                DisplayName    = $displayName
                SamAccountName = Get-Scenario038SamAccountName -Prefix $Prefix -Name $group.Key
                Dn             = "CN=$displayName,$RunOuDn"
                Members        = $group.Members
            }
        })
}
