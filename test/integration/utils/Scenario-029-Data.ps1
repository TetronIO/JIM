# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The names and HR data Scenario 029 (JIM provisioning its own SCIM endpoint) shares between its Setup and
    scenario scripts.

.DESCRIPTION
    The scenario rewrites its HR CSV between steps (a surname change, a cleared department, a leaver), so the
    shape of that file is defined once here rather than in both scripts. Dot-source after utils/Test-Helpers.ps1,
    which provides Write-FileToConnectorVolume.
#>

$script:Scenario029HrSystemName = "SCIM Round Trip HR Source"
$script:Scenario029ClientSystemName = "SCIM Round Trip Client"
$script:Scenario029ServiceProviderSystemName = "SCIM Round Trip Service Provider"
$script:Scenario029HrCsvPath = "/connector-files/test-data/scenario-029-hr.csv"

$script:Scenario029ClientKeyName = "SCIM Round Trip Client"
$script:Scenario029IdentityProviderKeyName = "SCIM Round Trip Identity Provider"
# Where the client's key is kept between Setup and the scenario, which mints nothing itself: a key's secret is
# shown once.
$script:Scenario029KeyFile = Join-Path ([System.IO.Path]::GetTempPath()) "scenario-029-keys.json"

$script:Scenario029HrPeopleRuleName = "SCIM Round Trip HR People (CSV -> MV)"
$script:Scenario029HrGroupsRuleName = "SCIM Round Trip HR Groups (CSV -> MV)"
$script:Scenario029UserExportRuleName = "SCIM Round Trip Users (MV -> SCIM client)"
$script:Scenario029GroupExportRuleName = "SCIM Round Trip Groups (MV -> SCIM client)"
$script:Scenario029InboundUsersRuleName = "SCIM Round Trip Users (Service Provider -> MV)"
$script:Scenario029InboundGroupsRuleName = "SCIM Round Trip Groups (Service Provider -> MV)"
$script:Scenario029ScheduleName = "SCIM Round Trip Inbound"

# Whether an account is enabled has no seeded Metaverse attribute of its own here, so the scenario adds one. It is
# also what puts a person in the export rule's scope, which leaves Department free to be cleared without that
# deprovisioning anyone.
$script:Scenario029EnabledMvAttribute = "Account Enabled"
$script:Scenario029Department = "Engineering"

$script:Scenario029GroupAccountName = "rt-engineers"
$script:Scenario029GroupDisplayName = "Round Trip Engineers"
$script:Scenario029GroupMemberCount = 5

function Get-Scenario029AccountName {
    <#
    .SYNOPSIS
        The HR accountName of person $Number.
    #>
    param([Parameter(Mandatory)][int]$Number)
    return "rt.user{0:D2}" -f $Number
}

function New-Scenario029People {
    <#
    .SYNOPSIS
        The scenario's people, all enabled and in one department.
    #>
    param([Parameter(Mandatory)][int]$Count)

    return @(1..$Count | ForEach-Object {
        $accountName = Get-Scenario029AccountName $_
        [pscustomobject]@{
            AccountName = $accountName
            FirstName   = "Round"
            LastName    = ("Trip{0:D2}" -f $_)
            Email       = "$accountName@example.com"
            Department  = $script:Scenario029Department
            Active      = $true
        }
    })
}

function New-Scenario029Group {
    <#
    .SYNOPSIS
        The scenario's one group, holding the first few people.
    #>
    return [pscustomobject]@{
        AccountName = $script:Scenario029GroupAccountName
        DisplayName = $script:Scenario029GroupDisplayName
        Members     = @(1..$script:Scenario029GroupMemberCount | ForEach-Object { Get-Scenario029AccountName $_ })
    }
}

function Write-Scenario029HrCsv {
    <#
    .SYNOPSIS
        Writes the HR CSV to the connector-files volume: people and the group in one file, told apart by
        objectType.

    .DESCRIPTION
        An empty department is written as an empty field, which the File Connector reads as no value, so clearing a
        person's department in the CSV clears it in the Metaverse.
    #>
    param(
        [Parameter(Mandatory)][object[]]$People,
        [Parameter(Mandatory)][object]$Group
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("objectType,accountName,firstName,lastName,email,department,active,displayName,members")
    foreach ($person in $People) {
        $lines.Add("person,$($person.AccountName),$($person.FirstName),$($person.LastName),$($person.Email),$($person.Department),$($person.Active.ToString().ToLowerInvariant()),,")
    }
    $lines.Add("group,$($Group.AccountName),,,,,,$($Group.DisplayName),$($Group.Members -join '|')")

    $localPath = Join-Path ([System.IO.Path]::GetTempPath()) "scenario-029-hr.csv"
    Set-Content -Path $localPath -Value ($lines -join "`n") -NoNewline
    Write-FileToConnectorVolume -SourcePath $localPath -DestinationPath $script:Scenario029HrCsvPath
}
