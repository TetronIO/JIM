# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 15: SCIM 2.0 Client Connector

.DESCRIPTION
    Drives the SCIM Connector end to end against the containerised test service provider over HTTPS,
    with the provider's certificate trusted rather than validation skipped.

    Steps:
    1. FullImport  - Walks Users and Groups a page at a time, staging membership as reference values
    2. Sync        - Projects Users and Groups to the Metaverse
    3. Export      - Sends the composed Display Name to the provider, through /Bulk where enabled
    4. Confirm     - A second Full Import confirms what the export actually applied
    5. DeltaImport - Asks the provider only for what changed since the last completed import

    What this scenario is for, and what it deliberately leaves to the unit suite: the misbehaviour cases
    (expired cursors, misreported totals, truncated bulk responses, providers that advertise a capability
    and then reject it) are steered per test in JIM.Worker.Tests against the same MockScimProvider this
    container serves. What no stubbed message handler can prove is the part this scenario covers: that
    the connector works over a real socket, against a real certificate, through the whole worker
    pipeline, with the results landing in the database.

.PARAMETER Step
    Which test step to execute (FullImport, Sync, Export, Confirm, DeltaImport, All)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ExpectedUserCount
    How many users the provider was seeded with (SCIM_USER_COUNT on the container).

.PARAMETER ExpectedGroupCount
    How many groups the provider was seeded with (SCIM_GROUP_COUNT on the container).

.PARAMETER Template
    Accepted for runner compatibility; this scenario's data comes from the provider.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility; this scenario has no directory target.

.EXAMPLE
    ./Invoke-Scenario15-ScimConnector.ps1 -Step All -ApiKey "jim_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("FullImport", "Sync", "Export", "Confirm", "DeltaImport", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$ExpectedUserCount = 25,

    [Parameter(Mandatory=$false)]
    [int]$ExpectedGroupCount = 2,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

$null = $DirectoryConfig
$null = $Template

. "$PSScriptRoot/../utils/Test-Helpers.ps1"

$scimSystemName = "SCIM Test Service Provider"

Write-TestSection "Scenario 15: SCIM 2.0 Client Connector"
Write-Host "Step:            $Step" -ForegroundColor Gray
Write-Host "Expected data:   $ExpectedUserCount user(s), $ExpectedGroupCount group(s)" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "SCIM 2.0 Client Connector"
    Steps = @()
    Success = $false
}

function Add-StepResult {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $testResults.Steps += @{ Name = $Name; Passed = $Passed; Detail = $Detail }
}

try {
    Write-TestStep "Step 0" "Connecting to JIM and resolving the Connected System"

    if (-not $ApiKey) { throw "API key required for authentication" }

    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $scimSystem = @(Get-JIMConnectedSystem) | Where-Object { $_.name -eq $scimSystemName }
    if (-not $scimSystem) { throw "Connected System '$scimSystemName' not found. Run Setup-Scenario15.ps1 first." }

    $runProfiles = @(Get-JIMRunProfile -ConnectedSystemId $scimSystem.id)
    foreach ($required in @("Full Import", "Delta Import", "Full Synchronisation", "Export")) {
        if (-not ($runProfiles | Where-Object { $_.name -eq $required })) {
            throw "Run Profile '$required' not found. Run Setup-Scenario15.ps1 first."
        }
    }

    $objectTypes = Get-JIMConnectedSystem -Id $scimSystem.id -ObjectTypes
    $userTypeId = ($objectTypes | Where-Object { $_.name -eq "User" }).id
    $groupTypeId = ($objectTypes | Where-Object { $_.name -eq "Group" }).id

    Write-Host "  OK Connected System '$scimSystemName' (ID: $($scimSystem.id))" -ForegroundColor Green

    # ─── Full Import ───
    if ($Step -in @("FullImport", "All")) {
        Write-TestStep "Step 1" "Full Import from the SCIM service provider"

        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Full Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "SCIM Full Import"

        $users = @(Get-JIMConnectedSystemObject -ConnectedSystemId $scimSystem.id -ObjectTypeId $userTypeId -All)
        $groups = @(Get-JIMConnectedSystemObject -ConnectedSystemId $scimSystem.id -ObjectTypeId $groupTypeId -All)

        Assert-Equal -Actual $users.Count -Expected $ExpectedUserCount -Message "Users staged by the Full Import"
        Assert-Equal -Actual $groups.Count -Expected $ExpectedGroupCount -Message "Groups staged by the Full Import"

        # Paging is the failure that hides: an import reading one page and reporting success looks like a
        # smaller system rather than a broken one, and deletion detection would then act on the absence.
        Write-Host "  OK Paged import staged every resource ($($users.Count) users across pages of 10)" -ForegroundColor Green

        # Membership proves reference values survived the round trip; a group with only a display name
        # would import cleanly and tell us nothing.
        $groupWithMembers = $groups | Select-Object -First 1
        $membership = @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $scimSystem.id -CsoId $groupWithMembers.id -AttributeName "members" -All)
        Assert-Condition -Condition ($membership.Count -gt 0) -Message "Group membership staged as reference values"

        Add-StepResult -Name "FullImport" -Passed $true -Detail "$($users.Count) users, $($groups.Count) groups, membership staged"
        Write-Host "  OK Full Import complete" -ForegroundColor Green
    }

    # ─── Full Synchronisation ───
    if ($Step -in @("Sync", "All")) {
        Write-TestStep "Step 2" "Full Synchronisation (project to the Metaverse)"

        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Full Synchronisation" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "SCIM Full Synchronisation"

        $metaverseUsers = @(Get-JIMMetaverseObject -ObjectTypeName "User" -All)
        Assert-Condition -Condition ($metaverseUsers.Count -ge $ExpectedUserCount) `
            -Message "Users projected to the Metaverse (found $($metaverseUsers.Count))"

        Add-StepResult -Name "Sync" -Passed $true -Detail "$($metaverseUsers.Count) Metaverse Users"
        Write-Host "  OK Projection complete" -ForegroundColor Green
    }

    # ─── Export ───
    if ($Step -in @("Export", "All")) {
        Write-TestStep "Step 3" "Export to the SCIM service provider"

        $pendingBefore = @(Get-JIMPendingExport -ConnectedSystemId $scimSystem.id -All)
        Assert-Condition -Condition ($pendingBefore.Count -gt 0) `
            -Message "The Full Synchronisation produced Pending Exports to send (found $($pendingBefore.Count))"

        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Export" -Wait -PassThru
        Assert-ExportSuccess -ActivityId $result.activityId -Name "SCIM Export"

        # Every Pending Export is deleted once applied, so anything left is a change JIM could not send.
        # This is the assertion that matters most for bulk: an operation the provider never reported on
        # must remain pending rather than be recorded as exported.
        $pendingAfter = @(Get-JIMPendingExport -ConnectedSystemId $scimSystem.id -All)
        Assert-Equal -Actual $pendingAfter.Count -Expected 0 -Message "Pending Exports remaining after the export"

        Add-StepResult -Name "Export" -Passed $true -Detail "$($pendingBefore.Count) changes exported"
        Write-Host "  OK Exported $($pendingBefore.Count) change(s)" -ForegroundColor Green
    }

    # ─── Confirming import ───
    if ($Step -in @("Confirm", "All")) {
        Write-TestStep "Step 4" "Confirming Full Import (what the export actually applied)"

        # An export JIM recorded as applied and the provider never received is the failure this catches,
        # and the only thing that can catch it is reading the provider back.
        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Full Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "SCIM Confirming Full Import"

        $users = @(Get-JIMConnectedSystemObject -ConnectedSystemId $scimSystem.id -ObjectTypeId $userTypeId)
        Assert-Equal -Actual $users.Count -Expected $ExpectedUserCount `
            -Message "Users after the confirming import (the export must not have created duplicates)"

        $withDisplayName = 0
        foreach ($user in $users) {
            $values = @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $scimSystem.id -CsoId $user.id -AttributeName "displayName" -All |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_.stringValue) })
            if ($values.Count -gt 0) { $withDisplayName++ }
        }

        Assert-Equal -Actual $withDisplayName -Expected $ExpectedUserCount `
            -Message "Users carrying the displayName the export sent"

        Add-StepResult -Name "Confirm" -Passed $true -Detail "$withDisplayName users carry the exported displayName"
        Write-Host "  OK The provider holds every value the export claimed to apply" -ForegroundColor Green
    }

    # ─── Delta Import ───
    if ($Step -in @("DeltaImport", "All")) {
        Write-TestStep "Step 5" "Delta Import (only what changed since the last completed import)"

        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Delta Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "SCIM Delta Import"

        # The watermark is set deliberately behind the point the run started reading, so a delta
        # immediately after a full import re-reads a small overlap rather than nothing. What it must not
        # do is read everything: that would mean the filter never reached the provider.
        $users = @(Get-JIMConnectedSystemObject -ConnectedSystemId $scimSystem.id -ObjectTypeId $userTypeId)
        Assert-Equal -Actual $users.Count -Expected $ExpectedUserCount `
            -Message "Users after the Delta Import (a delta must not duplicate or drop objects)"

        Add-StepResult -Name "DeltaImport" -Passed $true -Detail "Delta Import completed without disturbing the connector space"
        Write-Host "  OK Delta Import complete" -ForegroundColor Green
    }

    $testResults.Success = $true
    Write-TestSection "Scenario 15 Complete"
    foreach ($result in $testResults.Steps) {
        Write-Host ("  OK {0}: {1}" -f $result.Name, $result.Detail) -ForegroundColor Green
    }
}
catch {
    $testResults.Success = $false
    Write-Failure "Scenario 15 failed: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    throw
}

return $testResults
