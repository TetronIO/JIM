# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 15: SCIM 2.0 Client Connector

.DESCRIPTION
    Drives the SCIM Connector end to end against the containerised test service provider over HTTPS,
    with the provider's certificate trusted rather than validation skipped.

    Steps:
    1. SourceImport - The HR CSV imports and projects, making the Metaverse authoritative
    2. FullImport   - SCIM Users and Groups walk in a page at a time, membership staged as references
    3. Sync         - SCIM Users join the HR-projected Metaverse Objects; Groups project
    4. Flow         - Export evaluation produces one Pending Export per user (Display Name -> displayName)
    5. Export       - The changes reach the provider, through /Bulk where enabled
    6. Confirm      - A second Full Import confirms what the export actually applied
    7. DeltaImport  - Asks the provider only for what changed since the last completed import
    8. Provision    - A joiner appears in HR and is created in the provider (POST, via /Bulk)
    9. Deprovision  - The joiner leaves the export rule's scope and is deleted from the provider

    With the update flow in steps 4-6, steps 8 and 9 complete CRUD: every operation the connector can
    perform against a provider is driven end to end and confirmed by reading the provider back.

    The scenario needs two Connected Systems because JIM deliberately never exports a value back to the
    system it came from (Q3 circular-sync prevention). HR is authoritative for Display Name; SCIM is the
    export target; the export exists only because of that separation.

    What this scenario is for, and what it deliberately leaves to the unit suite: the misbehaviour cases
    (expired cursors, misreported totals, truncated bulk responses, providers that advertise a capability
    and then reject it) are steered per test in JIM.Worker.Tests against the same MockScimProvider this
    container serves. What no stubbed message handler can prove is the part this scenario covers: that
    the connector works over a real socket, against a real certificate, through the whole worker
    pipeline, with the results landing in the database.

.PARAMETER Step
    Which test step to execute (SourceImport, FullImport, Sync, Flow, Export, Confirm, DeltaImport,
    Provision, Deprovision, All)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ExpectedUserCount
    How many users the provider was seeded with (SCIM_USER_COUNT on the container).

.PARAMETER ExpectedGroupCount
    How many groups the provider was seeded with (SCIM_GROUP_COUNT on the container).

.PARAMETER ScimBaseUrl
    The SCIM service provider's base URL. Defaults to the scim-provider container on the Docker
    network, which is where Run-IntegrationTests.ps1 starts it; the sandbox light stack passes its
    native address instead.

.PARAMETER ScimCertificatePath
    The provider's public certificate on the host. When omitted, it is copied out of the scim-provider
    container, which is the containerised path; the sandbox light stack passes the file the native
    provider wrote.

.PARAMETER UseBulkOperations
    Whether the export goes through the provider's /Bulk endpoint. On by default; set false to drive
    the identical scenario down the per-object path.

.PARAMETER SkipPopulate
    Accepted for runner compatibility (snapshot runs pass it to every scenario); this scenario has no
    directory to populate, so it is ignored.

.PARAMETER Template
    Accepted for runner compatibility; this scenario's data comes from the provider and its own CSV.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility; this scenario has no directory target.

.EXAMPLE
    ./Invoke-Scenario15-ScimConnector.ps1 -Step All -ApiKey "jim_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("SourceImport", "FullImport", "Sync", "Flow", "Export", "Confirm", "DeltaImport", "Provision", "Deprovision", "All")]
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
    [string]$ScimBaseUrl = "https://scim-provider:5300",

    [Parameter(Mandatory=$false)]
    [string]$ScimCertificatePath,

    [Parameter(Mandatory=$false)]
    [bool]$UseBulkOperations = $true,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

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
$null = $SkipPopulate

. "$PSScriptRoot/../utils/Test-Helpers.ps1"

$hrSystemName = "SCIM Scenario HR Source"
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

# Rewrites the HR CSV. HR data drives every transition this scenario tests: the update (a suffix on
# every Display Name), the joiner (an extra row in the department the export rule is scoped to), and
# the leaver (the joiner's department changing out of scope).
function Write-HrCsv {
    param([bool]$VerifiedSuffix, [string]$JoinerDepartment)

    $csvLines = [System.Collections.Generic.List[string]]::new()
    $csvLines.Add("accountName,firstName,lastName,displayName,email,department")
    for ($i = 1; $i -le $ExpectedUserCount; $i++) {
        $suffix = if ($VerifiedSuffix) { " (Verified)" } else { "" }
        $csvLines.Add("user$i,User,Number$i,User Number$i$suffix,user$i@example.com,Engineering")
    }
    if ($JoinerDepartment) {
        $csvLines.Add("joiner1,Joiner,One,Joiner One,joiner1@example.com,$JoinerDepartment")
    }

    $localCsvPath = Join-Path ([System.IO.Path]::GetTempPath()) "scenario15-hr-users.csv"
    Set-Content -Path $localCsvPath -Value ($csvLines -join "`n") -NoNewline
    Write-FileToConnectorVolume -SourcePath $localCsvPath -DestinationPath "/connector-files/test-data/scenario15-hr-users.csv"
}

# Runs an HR Full Import then Full Synchronisation, which is how every HR change reaches JIM.
function Invoke-HrImportAndSync {
    param([string]$Label)

    $result = Start-JIMRunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Full Import" -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.activityId -Name "HR Full Import ($Label)"

    $result = Start-JIMRunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Full Synchronisation" -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.activityId -Name "HR Full Synchronisation ($Label)"
}

try {
    if ($Step -eq "All") {
        Write-TestStep "Setup" "Configuring JIM for the scenario"

        if (-not $ScimCertificatePath) {
            # The containerised provider writes its certificate inside the container; copy it out so
            # JIM can be told to trust it. The file is written at startup, so a short wait covers a
            # provider that is still coming up.
            $ScimCertificatePath = Join-Path ([System.IO.Path]::GetTempPath()) "scim-provider.pem"
            $certificateCopied = $false
            for ($attempt = 0; $attempt -lt 12; $attempt++) {
                docker cp scim-provider:/certificates/scim-provider.pem $ScimCertificatePath 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0 -and (Test-Path $ScimCertificatePath)) { $certificateCopied = $true; break }
                Start-Sleep -Seconds 5
            }
            if (-not $certificateCopied) {
                throw "Could not copy the certificate out of the scim-provider container. Is it running? (Run-IntegrationTests.ps1 starts it with the 'scim' compose profile.)"
            }
            Write-Host "  OK Copied the provider's certificate from the scim-provider container" -ForegroundColor Green
        }

        & "$PSScriptRoot/../Setup-Scenario15.ps1" `
            -JIMUrl $JIMUrl -ApiKey $ApiKey `
            -ScimBaseUrl $ScimBaseUrl -ScimCertificatePath $ScimCertificatePath `
            -UserCount $ExpectedUserCount -UseBulkOperations $UseBulkOperations
    }

    Write-TestStep "Step 0" "Connecting to JIM and resolving the Connected Systems"

    if (-not $ApiKey) { throw "API key required for authentication" }

    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $systems = @(Get-JIMConnectedSystem)
    $hrSystem = $systems | Where-Object { $_.name -eq $hrSystemName }
    $scimSystem = $systems | Where-Object { $_.name -eq $scimSystemName }
    if (-not $hrSystem) { throw "Connected System '$hrSystemName' not found. Run Setup-Scenario15.ps1 first." }
    if (-not $scimSystem) { throw "Connected System '$scimSystemName' not found. Run Setup-Scenario15.ps1 first." }

    $objectTypes = Get-JIMConnectedSystem -Id $scimSystem.id -ObjectTypes
    $userTypeId = ($objectTypes | Where-Object { $_.name -eq "User" }).id
    $groupTypeId = ($objectTypes | Where-Object { $_.name -eq "Group" }).id

    Write-Host "  OK HR source (ID: $($hrSystem.id)), SCIM target (ID: $($scimSystem.id))" -ForegroundColor Green

    # --- HR source import and projection ---
    if ($Step -in @("SourceImport", "All")) {
        Write-TestStep "Step 1" "HR CSV import and projection (the authoritative source)"

        $result = Start-JIMRunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Full Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "HR Full Import"

        $result = Start-JIMRunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Full Synchronisation" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "HR Full Synchronisation"

        $metaverseUsers = @(Get-JIMMetaverseObject -ObjectTypeName "User" -All)
        Assert-Condition -Condition ($metaverseUsers.Count -ge $ExpectedUserCount) `
            -Message "Users projected from HR to the Metaverse (found $($metaverseUsers.Count))"

        Add-StepResult -Name "SourceImport" -Passed $true -Detail "$($metaverseUsers.Count) Metaverse Users from HR"
        Write-Host "  OK HR source projected" -ForegroundColor Green
    }

    # --- SCIM Full Import ---
    if ($Step -in @("FullImport", "All")) {
        Write-TestStep "Step 2" "Full Import from the SCIM service provider"

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

    # --- SCIM Full Synchronisation ---
    if ($Step -in @("Sync", "All")) {
        Write-TestStep "Step 3" "SCIM Full Synchronisation (join Users, project Groups)"

        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Full Synchronisation" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "SCIM Full Synchronisation"

        # Joins, not projections: the Metaverse Object count must not have grown. A count of 50 here
        # means matching failed and every SCIM User projected a duplicate.
        $metaverseUsers = @(Get-JIMMetaverseObject -ObjectTypeName "User" -All)
        Assert-Equal -Actual $metaverseUsers.Count -Expected $ExpectedUserCount `
            -Message "Metaverse Users after the SCIM sync (joined, not duplicated)"

        $joinedUsers = @(Get-JIMConnectedSystemObject -ConnectedSystemId $scimSystem.id -ObjectTypeId $userTypeId -All |
            Where-Object { $_.joinType -and $_.joinType -ne "NotJoined" })
        Assert-Equal -Actual $joinedUsers.Count -Expected $ExpectedUserCount `
            -Message "SCIM Users joined to the HR-projected Metaverse Objects"

        Add-StepResult -Name "Sync" -Passed $true -Detail "$($joinedUsers.Count) users joined; groups projected"
        Write-Host "  OK SCIM synchronisation complete" -ForegroundColor Green
    }

    # --- The mover: an HR change flows towards SCIM ---
    if ($Step -in @("Flow", "All")) {
        Write-TestStep "Step 4" "HR change flows towards SCIM (export evaluation)"

        # Export evaluation is driven by Metaverse Object CHANGES, so an already-settled Metaverse and a
        # freshly joined, unchanged target produce nothing: there is no change to flow. That is correct
        # (a join is not an update), so the scenario does what a customer does: HR changes, and the
        # change propagates. Every user's Display Name gains a suffix, the HR import picks it up, and
        # the HR sync evaluates the export rules; HR is the source, so Q3 does not suppress them.
        # The export rule was created disabled and goes live only now, after the SCIM import and join:
        # the provider already held these users, and a provisioning rule live any earlier would have
        # duplicated all of them. This is the order a real brownfield adoption follows.
        $exportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq "SCIM Users Export (MV -> SCIM)" }
        if (-not $exportRule) { throw "Export rule not found; run setup first." }
        Set-JIMSyncRule -Id $exportRule.id -Enable | Out-Null
        Write-Host "  OK Export rule enabled now the existing population is joined" -ForegroundColor Gray

        Write-HrCsv -VerifiedSuffix $true -JoinerDepartment ""
        Write-Host "  OK HR CSV updated: every Display Name now carries a '(Verified)' suffix" -ForegroundColor Gray

        Invoke-HrImportAndSync -Label "changed data"

        $pending = @(Get-JIMPendingExport -ConnectedSystemId $scimSystem.id -All)
        Assert-Equal -Actual $pending.Count -Expected $ExpectedUserCount `
            -Message "Pending Exports produced, one per user (Display Name -> displayName)"

        Add-StepResult -Name "Flow" -Passed $true -Detail "$($pending.Count) Pending Exports from the HR change"
        Write-Host "  OK The HR change produced one Pending Export per user" -ForegroundColor Green
    }

    # --- Export ---
    if ($Step -in @("Export", "All")) {
        Write-TestStep "Step 5" "Export to the SCIM service provider"

        $pendingBefore = @(Get-JIMPendingExport -ConnectedSystemId $scimSystem.id -All)
        Assert-Condition -Condition ($pendingBefore.Count -gt 0) `
            -Message "There are Pending Exports to send (found $($pendingBefore.Count))"

        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Export" -Wait -PassThru
        Assert-ExportSuccess -ActivityId $result.activityId -Name "SCIM Export"

        # A successfully applied Pending Export moves to Exported and is held until the confirming
        # import proves the value landed; only then is it deleted. So the assertion here is that every
        # change was applied and none was left Pending or Failed. This is the assertion that matters
        # most for bulk: an operation the provider never reported on must not be recorded as applied.
        $pendingAfter = @(Get-JIMPendingExport -ConnectedSystemId $scimSystem.id -All)
        $notApplied = @($pendingAfter | Where-Object { $_.status -ne "Exported" })
        Assert-Equal -Actual $notApplied.Count -Expected 0 `
            -Message "Every Pending Export was applied (none left Pending or Failed)"
        Assert-Equal -Actual $pendingAfter.Count -Expected $ExpectedUserCount `
            -Message "Applied exports held for the confirming import"

        Add-StepResult -Name "Export" -Passed $true -Detail "$($pendingBefore.Count) changes applied, awaiting confirmation"
        Write-Host "  OK Exported $($pendingBefore.Count) change(s)" -ForegroundColor Green
    }

    # --- Confirming import ---
    if ($Step -in @("Confirm", "All")) {
        Write-TestStep "Step 6" "Confirming Full Import (what the export actually applied)"

        # An export JIM recorded as applied and the provider never received is the failure this catches,
        # and the only thing that can catch it is reading the provider back.
        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Full Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "SCIM Confirming Full Import"

        $users = @(Get-JIMConnectedSystemObject -ConnectedSystemId $scimSystem.id -ObjectTypeId $userTypeId -All)
        Assert-Equal -Actual $users.Count -Expected $ExpectedUserCount `
            -Message "Users after the confirming import (the export must not have created duplicates)"

        $withDisplayName = 0
        foreach ($user in $users) {
            $values = @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $scimSystem.id -CsoId $user.id -AttributeName "displayName" -All |
                Where-Object { $_.stringValue -like "* (Verified)" })
            if ($values.Count -gt 0) { $withDisplayName++ }
        }

        Assert-Equal -Actual $withDisplayName -Expected $ExpectedUserCount `
            -Message "Users carrying the exact displayName the export sent"

        # Confirmation closes the loop: the import proved every value landed, so the Pending Exports
        # that were held in Exported status are now deleted. One left behind means a change the
        # provider reported as applied that the confirming import could not see.
        $pendingAfterConfirm = @(Get-JIMPendingExport -ConnectedSystemId $scimSystem.id -All)
        Assert-Equal -Actual $pendingAfterConfirm.Count -Expected 0 `
            -Message "Pending Exports remaining after confirmation"

        Add-StepResult -Name "Confirm" -Passed $true -Detail "$withDisplayName users carry the exported displayName; all exports confirmed"
        Write-Host "  OK The provider holds every value the export claimed to apply" -ForegroundColor Green
    }

    # --- Delta Import ---
    if ($Step -in @("DeltaImport", "All")) {
        Write-TestStep "Step 7" "Delta Import (only what changed since the last completed import)"

        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Delta Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "SCIM Delta Import"

        # The watermark is set deliberately behind the point the run started reading, so a delta
        # immediately after a full import re-reads a small overlap rather than nothing. What it must not
        # do is disturb the connector space: a delta that duplicates or drops objects is broken.
        $users = @(Get-JIMConnectedSystemObject -ConnectedSystemId $scimSystem.id -ObjectTypeId $userTypeId -All)
        Assert-Equal -Actual $users.Count -Expected $ExpectedUserCount `
            -Message "Users after the Delta Import (a delta must not duplicate or drop objects)"

        Add-StepResult -Name "DeltaImport" -Passed $true -Detail "Delta Import completed without disturbing the connector space"
        Write-Host "  OK Delta Import complete" -ForegroundColor Green
    }

    # --- Provisioning: a joiner is created in the provider ---
    if ($Step -in @("Provision", "All")) {
        Write-TestStep "Step 8" "Provisioning (a joiner appears in HR and is created in SCIM)"

        Write-HrCsv -VerifiedSuffix $true -JoinerDepartment "Engineering"
        Write-Host "  OK HR CSV gained joiner1 in Engineering, the department the export rule is scoped to" -ForegroundColor Gray

        Invoke-HrImportAndSync -Label "joiner"

        $creates = @(Get-JIMPendingExport -ConnectedSystemId $scimSystem.id -All | Where-Object { $_.changeType -eq "Create" })
        Assert-Equal -Actual $creates.Count -Expected 1 -Message "A Create Pending Export was provisioned for the joiner"

        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Export" -Wait -PassThru
        Assert-ExportSuccess -ActivityId $result.activityId -Name "SCIM Export (provision)"

        # The confirming import is what proves the create actually happened: the provider must now hold
        # one more user, and JIM must have adopted the id the provider assigned rather than creating a
        # duplicate Connected System Object for the same resource.
        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Full Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "SCIM Confirming Full Import (provision)"

        $users = @(Get-JIMConnectedSystemObject -ConnectedSystemId $scimSystem.id -ObjectTypeId $userTypeId -All)
        Assert-Equal -Actual $users.Count -Expected ($ExpectedUserCount + 1) `
            -Message "The provider holds the provisioned joiner (and no duplicates)"

        $joinerValues = @()
        foreach ($user in $users) {
            $userName = @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $scimSystem.id -CsoId $user.id -AttributeName "userName" -All)
            if ($userName.stringValue -contains "joiner1") { $joinerValues = @($user); break }
        }
        Assert-Equal -Actual $joinerValues.Count -Expected 1 -Message "The joiner is readable back from the provider by the userName JIM exported"

        Add-StepResult -Name "Provision" -Passed $true -Detail "joiner1 created in the provider and confirmed"
        Write-Host "  OK Provisioning confirmed" -ForegroundColor Green
    }

    # --- Deprovisioning: the joiner leaves scope and is deleted from the provider ---
    if ($Step -in @("Deprovision", "All")) {
        Write-TestStep "Step 9" "Deprovisioning (the joiner leaves scope and is deleted from SCIM)"

        Write-HrCsv -VerifiedSuffix $true -JoinerDepartment "Alumni"
        Write-Host "  OK HR CSV moved joiner1 to Alumni, outside the export rule's scope" -ForegroundColor Gray

        Invoke-HrImportAndSync -Label "leaver"

        $deletes = @(Get-JIMPendingExport -ConnectedSystemId $scimSystem.id -All | Where-Object { $_.changeType -eq "Delete" })
        Assert-Equal -Actual $deletes.Count -Expected 1 `
            -Message "A Delete Pending Export was staged for the leaver (OutboundDeprovisionAction=Delete)"

        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Export" -Wait -PassThru
        Assert-ExportSuccess -ActivityId $result.activityId -Name "SCIM Export (deprovision)"

        # Only a read-back can prove the delete: the provider must be short one user, and a Full Import
        # is also what lets JIM obsolete the Connected System Object for the departed resource.
        $result = Start-JIMRunProfile -ConnectedSystemId $scimSystem.id -RunProfileName "Full Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "SCIM Confirming Full Import (deprovision)"

        $users = @(Get-JIMConnectedSystemObject -ConnectedSystemId $scimSystem.id -ObjectTypeId $userTypeId -All |
            Where-Object { $_.status -ne "Obsolete" })
        Assert-Equal -Actual $users.Count -Expected $ExpectedUserCount `
            -Message "The provider no longer holds the deprovisioned joiner"

        Add-StepResult -Name "Deprovision" -Passed $true -Detail "joiner1 deleted from the provider and confirmed"
        Write-Host "  OK Deprovisioning confirmed" -ForegroundColor Green
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
