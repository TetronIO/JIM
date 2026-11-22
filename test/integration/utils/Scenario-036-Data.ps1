# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The names and HR data Scenario 036 (Entra ID as a target, #361) shares between its Setup and scenario scripts.

.DESCRIPTION
    Every person and group is named with the run's prefix (jimit-<run id>-, Get-EntraIdConnectorLabRunPrefix): the
    accountName that becomes the mail nickname, the display name, and the user principal name. Runs never collide, and
    the harness's cleanup and snapshot tell what a run made from everything else in the tenant by that prefix.

    The scenario rewrites its HR CSV between steps (an update, a membership change, two leavers), so the shape of that
    file is defined once here. Dot-source after utils/Test-Helpers.ps1, which provides Write-FileToConnectorVolume.
#>

$script:Scenario036HrSystemName = "Entra ID Scenario HR Source"
$script:Scenario036EntraSystemName = "Entra ID (Scenario 036)"
# The app registration an administrator created once with Register-JIMEntraIdConnector, which the CI app owns.
$script:Scenario036ApplicationName = "JIM Entra ID Connector (CI)"
$script:Scenario036HrCsvPath = "/connector-files/test-data/scenario-036-hr.csv"

$script:Scenario036HrPeopleRuleName = "Entra ID HR People Import (CSV -> MV)"
$script:Scenario036HrGroupsRuleName = "Entra ID HR Groups Import (CSV -> MV)"
$script:Scenario036UserExportRuleName = "Entra ID Users Export (MV -> Entra ID)"
$script:Scenario036GroupExportRuleName = "Entra ID Groups Export (MV -> Entra ID)"

# The JIM test tenant's default domain, which every user principal name the scenario makes is in.
$script:Scenario036DefaultDomain = "tetronjim.onmicrosoft.com"

# A person in this department is out of the user export rule's scope, so JIM deprovisions them.
$script:Scenario036LeaverDepartment = "Alumni"

# 24 people: enough that the Staff group, created with every one of them, holds more than the 20 members Graph takes
# on a group's create, so the rest go in follow-up requests.
$script:Scenario036PeopleCount = 24

function Get-Scenario036AccountName {
    <#
    .SYNOPSIS
        Person $Number's accountName, which is also their mail nickname in Entra ID.
    #>
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][int]$Number
    )
    return "{0}p{1:D2}" -f $Prefix, $Number
}

function New-Scenario036People {
    <#
    .SYNOPSIS
        The scenario's people: the first half in Engineering, the second in Finance, each half managed by its first
        person, all enabled.
    #>
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [string]$Domain = $script:Scenario036DefaultDomain,
        [int]$Count = $script:Scenario036PeopleCount
    )

    $half = [Math]::Ceiling($Count / 2)
    return @(1..$Count | ForEach-Object {
        $number = $_
        $firstOfHalf = if ($number -le $half) { 1 } else { $half + 1 }
        [pscustomobject]@{
            Number      = $number
            AccountName = Get-Scenario036AccountName -Prefix $Prefix -Number $number
            FirstName   = "Person"
            LastName    = ("{0:D2}" -f $number)
            DisplayName = ("{0}Person {1:D2}" -f $Prefix, $number)
            Upn         = ("{0}person.{1:D2}@{2}" -f $Prefix, $number, $Domain)
            Department  = if ($number -le $half) { "Engineering" } else { "Finance" }
            JobTitle    = if ($number -eq $firstOfHalf) { "Head of Department" } else { "Analyst" }
            EmployeeId  = ("E{0:D5}" -f $number)
            Manager     = if ($number -eq $firstOfHalf) { "" } else { Get-Scenario036AccountName -Prefix $Prefix -Number $firstOfHalf }
            Active      = $true
        }
    })
}

function New-Scenario036Groups {
    <#
    .SYNOPSIS
        The scenario's groups: Staff (everyone, so more than 20 members), a group per department, and All Departments,
        whose members are the two department groups.
    #>
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][object[]]$People
    )

    $engineering = "${Prefix}g-engineering"
    $finance = "${Prefix}g-finance"
    return @(
        [pscustomobject]@{ Key = "Staff"; AccountName = "${Prefix}g-staff"; DisplayName = "${Prefix}Staff"
            Members = [System.Collections.Generic.List[string]]@($People | ForEach-Object { $_.AccountName }) },
        [pscustomobject]@{ Key = "Engineering"; AccountName = $engineering; DisplayName = "${Prefix}Engineering"
            Members = [System.Collections.Generic.List[string]]@($People | Where-Object { $_.Department -eq "Engineering" } | ForEach-Object { $_.AccountName }) },
        [pscustomobject]@{ Key = "Finance"; AccountName = $finance; DisplayName = "${Prefix}Finance"
            Members = [System.Collections.Generic.List[string]]@($People | Where-Object { $_.Department -eq "Finance" } | ForEach-Object { $_.AccountName }) },
        [pscustomobject]@{ Key = "AllDepartments"; AccountName = "${Prefix}g-all-departments"; DisplayName = "${Prefix}All Departments"
            Members = [System.Collections.Generic.List[string]]@($engineering, $finance) }
    )
}

function Write-Scenario036HrCsv {
    <#
    .SYNOPSIS
        Writes the HR CSV to the connector-files volume: people and groups in one file, told apart by objectType.

    .DESCRIPTION
        The manager and members columns hold accountNames of objects in the same file; members are separated by the
        File Connector's default multi-value delimiter (|). The setup marks both columns as References on the schema.
    #>
    param(
        [Parameter(Mandatory)][object[]]$People,
        [Parameter(Mandatory)][object[]]$Groups
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("objectType,accountName,firstName,lastName,displayName,upn,department,jobTitle,employeeId,manager,active,members")
    foreach ($person in $People) {
        $lines.Add(("person,{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}," -f $person.AccountName, $person.FirstName, $person.LastName,
            $person.DisplayName, $person.Upn, $person.Department, $person.JobTitle, $person.EmployeeId, $person.Manager,
            $person.Active.ToString().ToLowerInvariant()))
    }
    foreach ($group in $Groups) {
        $lines.Add("group,$($group.AccountName),,,$($group.DisplayName),,,,,,,$($group.Members -join '|')")
    }

    $localPath = Join-Path ([System.IO.Path]::GetTempPath()) "scenario-036-hr.csv"
    Set-Content -Path $localPath -Value ($lines -join "`n") -NoNewline
    Write-FileToConnectorVolume -SourcePath $localPath -DestinationPath $script:Scenario036HrCsvPath
}
