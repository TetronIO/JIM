# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The names and HR data Scenario 028 (the SCIM Connector against Keycloak, #2349) shares between its Setup and
    scenario scripts.

.DESCRIPTION
    The scenario rewrites its HR CSV between steps (a membership change, a leaver), so the shape of that file is
    defined once here rather than in both scripts. Dot-source after utils/Test-Helpers.ps1, which provides
    Write-FileToConnectorVolume.
#>

$script:Scenario028Realm = "jim-scim"
$script:Scenario028ClientId = "jim-scim"
# A throwaway secret for a realm that lives only as long as this test container.
$script:Scenario028ClientSecret = "scenario-028-client-secret"
$script:Scenario028ProtectedUserName = "kc.protected"

$script:Scenario028HrSystemName = "Keycloak Scenario HR Source"
$script:Scenario028KeycloakSystemName = "Keycloak SCIM"
$script:Scenario028HrCsvPath = "/connector-files/test-data/scenario-028-hr.csv"

$script:Scenario028HrPeopleRuleName = "Keycloak HR People Import (CSV -> MV)"
$script:Scenario028HrGroupsRuleName = "Keycloak HR Groups Import (CSV -> MV)"
$script:Scenario028KeycloakJoinRuleName = "Keycloak Users Join (SCIM -> MV)"
$script:Scenario028UserExportRuleName = "Keycloak Users Export (MV -> SCIM)"
$script:Scenario028GroupExportRuleName = "Keycloak Groups Export (MV -> SCIM)"

$script:Scenario028EnabledMvAttribute = "Account Enabled"
$script:Scenario028InScopeDepartment = "Engineering"

# Two groups: one ordinary, and one whose membership is large enough that both its first export and a later change
# take more than Keycloak's 100 PATCH operations, unless JIM merges and splits them as it should.
$script:Scenario028Groups = @(
    @{ AccountName = "kc-small"; DisplayName = "Keycloak Small Group"; FirstMember = 1; LastMember = 2 },
    @{ AccountName = "kc-large"; DisplayName = "Keycloak Large Group"; FirstMember = 1; LastMember = 110 }
)

function Get-Scenario028AccountName {
    <#
    .SYNOPSIS
        The HR accountName of person $Number. Mixed case on purpose: Keycloak stores userName in lower case, and the
        export lowercases it, so the case has to survive JIM and be gone in Keycloak.
    #>
    param([Parameter(Mandatory)][int]$Number)
    return "Kc.User{0:D3}" -f $Number
}

function New-Scenario028People {
    <#
    .SYNOPSIS
        The scenario's people, all in the in-scope department and enabled.
    #>
    param([Parameter(Mandatory)][int]$Count)

    return @(1..$Count | ForEach-Object {
        $accountName = Get-Scenario028AccountName $_
        [pscustomobject]@{
            AccountName = $accountName
            FirstName   = "Kc"
            LastName    = ("User{0:D3}" -f $_)
            Email       = "$accountName@Example.COM"
            Department  = $script:Scenario028InScopeDepartment
            Active      = $true
        }
    })
}

function New-Scenario028Groups {
    <#
    .SYNOPSIS
        The scenario's groups, each with the accountNames of its members.
    #>
    param([Parameter(Mandatory)][int]$UserCount)

    return @($script:Scenario028Groups | ForEach-Object {
        [pscustomobject]@{
            AccountName = $_.AccountName
            DisplayName = $_.DisplayName
            Members     = @($_.FirstMember..([Math]::Min($_.LastMember, $UserCount)) | ForEach-Object { Get-Scenario028AccountName $_ })
        }
    })
}

function Write-Scenario028HrCsv {
    <#
    .SYNOPSIS
        Writes the HR CSV to the connector-files volume: people and groups in one file, told apart by objectType.

    .DESCRIPTION
        The members column holds every member, separated by the connector's default multi-value delimiter (|). A
        header names columns and nothing more, so the setup marks the column multi-valued on the schema.
    #>
    param(
        [Parameter(Mandatory)][object[]]$People,
        [Parameter(Mandatory)][object[]]$Groups
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("objectType,accountName,firstName,lastName,email,department,active,displayName,members")
    foreach ($person in $People) {
        $lines.Add("person,$($person.AccountName),$($person.FirstName),$($person.LastName),$($person.Email),$($person.Department),$($person.Active.ToString().ToLowerInvariant()),,")
    }
    foreach ($group in $Groups) {
        $lines.Add("group,$($group.AccountName),,,,,,$($group.DisplayName),$($group.Members -join '|')")
    }

    $localPath = Join-Path ([System.IO.Path]::GetTempPath()) "scenario-028-hr.csv"
    Set-Content -Path $localPath -Value ($lines -join "`n") -NoNewline
    Write-FileToConnectorVolume -SourcePath $localPath -DestinationPath $script:Scenario028HrCsvPath
}
