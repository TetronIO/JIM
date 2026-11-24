# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The population of Scenario 039, the Microsoft Entra ID Connector at scale (#3053): people and groups sized by the
    run's template, provisioned to the JIM test tenant from an HR CSV.

.DESCRIPTION
    The population is deterministic for a prefix and size, so two runs of one template measure the same work:
      - person i reports to person floor(i / 10), so managers form a tree and everyone but the first has one;
      - the first group holds everyone (a group far beyond Graph's 20 members a request), and each person joins the
        others in turn until they hold the template's average number of memberships, never one group twice.

    It reuses Scenario 036's configuration and CSV (Setup-Scenario-036.ps1, Write-Scenario036HrCsv), without the Initial
    Password: a password is another write per user, and Scenario 036 proves it.
#>

# The Connected System Register-JIMEntraIdConnector creates for the run.
$script:Scenario039EntraSystemName = "Entra ID (Scenario 039)"

# Each step's timings are written here, under the results the workflows upload.
$script:Scenario039ResultsFolder = Join-Path $PSScriptRoot '..' 'results'

# Microsoft Graph allows each application 20 writes a second in a tenant; a scale run measures the Connector at that.
$script:Scenario039WritesPerSecond = 20

# The share of the tenant's object limit a run leaves free.
$script:Scenario039Headroom = 0.1

function New-Scenario039Population {
    <#
    .SYNOPSIS
        The run's people and groups: objects shaped as Write-Scenario036HrCsv writes them.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][string]$Domain,
        [Parameter(Mandatory)][ValidateRange(1, 1000000)][int]$Users,
        [Parameter(Mandatory)][ValidateRange(1, 100000)][int]$Groups,
        [Parameter(Mandatory)][ValidateRange(1, 1000)][int]$AverageMemberships
    )

    $width = [Math]::Max(5, "$Users".Length)
    $people = [System.Collections.Generic.List[object]]::new($Users)
    for ($i = 0; $i -lt $Users; $i++) {
        $number = "$i".PadLeft($width, '0')
        $accountName = "${Prefix}u$number"
        $people.Add([PSCustomObject]@{
            AccountName = $accountName
            FirstName = "Scale"
            LastName = "User $number"
            DisplayName = "${Prefix}Scale User $number"
            Upn = "$accountName@$Domain"
            Department = "Department $($i % 20)"
            JobTitle = "Analyst"
            EmployeeId = "E$number"
            Manager = if ($i -eq 0) { "" } else { "${Prefix}u$("$([Math]::Floor($i / 10))".PadLeft($width, '0'))" }
            Active = $true
        })
    }

    $groupWidth = [Math]::Max(3, "$Groups".Length)
    $groupList = [System.Collections.Generic.List[object]]::new($Groups)
    for ($g = 0; $g -lt $Groups; $g++) {
        $number = "$g".PadLeft($groupWidth, '0')
        $name = if ($g -eq 0) { "All Staff" } else { "Team $number" }
        $groupList.Add([PSCustomObject]@{
            Key = $name
            AccountName = "${Prefix}g$number"
            DisplayName = "$Prefix$name"
            Members = [System.Collections.Generic.List[string]]::new()
        })
    }

    # Everyone is in All Staff; the rest of a person's memberships walk the other groups from an offset of their own.
    $perPerson = [Math]::Min($AverageMemberships, $Groups)
    for ($i = 0; $i -lt $Users; $i++) {
        $groupList[0].Members.Add($people[$i].AccountName)
        for ($m = 1; $m -lt $perPerson; $m++) {
            $g = 1 + (($i + $m - 1) % ($Groups - 1))
            $groupList[$g].Members.Add($people[$i].AccountName)
        }
    }

    return [PSCustomObject]@{ People = $people; Groups = $groupList }
}

function Get-Scenario039Fit {
    <#
    .SYNOPSIS
        Whether the run's users and groups fit in the tenant (its directorySizeQuota), leaving a tenth of the limit
        free, and if not, why.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][long]$Used,
        [Parameter(Mandatory)][long]$Total,
        [Parameter(Mandatory)][int]$Users,
        [Parameter(Mandatory)][int]$Groups
    )

    $needed = [long]$Users + $Groups
    $room = [long][Math]::Floor($Total * (1 - $script:Scenario039Headroom)) - $Used
    if ($needed -le $room) {
        return [PSCustomObject]@{ Fits = $true; Reason = "$needed objects fit: the tenant holds $Used of $Total, and keeps a tenth free" }
    }
    $reason = "the run needs $needed objects ($Users users, $Groups groups), but the tenant holds $Used of $Total and keeps a tenth free, " +
        "leaving room for $([Math]::Max(0, $room)). Purged users still count for a quarter for 30 days, so an earlier run's may be what is taking the room; choose a smaller template, or wait."
    return [PSCustomObject]@{ Fits = $false; Reason = $reason }
}
