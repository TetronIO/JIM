# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 14: Attribute Priority (multi-source winner resolution)

.DESCRIPTION
    Validates Attribute Priority resolution (#91): when two import Synchronisation Rules
    contribute to the same Metaverse attribute for the same joined Metaverse Object, the
    higher-priority contributor's value wins outright (winner-takes-all for scalars and
    references, winner-takes-all-values for multi-valued attributes).

    Topology (configured by Setup-Scenario14.ps1, seeded by Populate-OpenLDAP-Scenario14.ps1):
    - "Scenario 14 Primary" (OpenLDAP suffix dc=yellowstone,dc=local) and "Scenario 14
      Secondary" (dc=glitterband,dc=local), the same OpenLDAP container's two suffixes.
    - Six users, sharing Employee ID across both suffixes so each pair joins to a single
      Metaverse Object (Simple Mode matching on Employee ID).
    - Both systems flow Description, Job Title, Manager (Reference) and Other Telephones
      (multi-valued) into the same Metaverse attributes, with Primary = priority 1 and
      Secondary = priority 2 for every one of them.

    This scenario is OpenLDAP only (see Setup-Scenario14.ps1 header); Run-IntegrationTests.ps1
    hard-fails a Samba AD or "All" -DirectoryType request before this script is invoked.

    Tests:
    1. BaselineResolution - full import + full sync both systems, then assert that every
       contested attribute on a sample user carries the Primary contributor's value (and,
       where obtainable, its provenance), the Manager reference resolves to Primary's
       referent, and the multi-valued Other Telephones set is exactly Primary's two numbers
       (Secondary's are completely absent: winner-takes-all-values).

    -- INSERT NEW STEPS HERE: add the ValidateSet entry above, a $testResults.Steps-tracked
       block below (mirroring BaselineResolution's structure), and update .PARAMETER Step. --

.PARAMETER Step
    Which test step to execute (BaselineResolution, All)

.PARAMETER Template
    Accepted for runner compatibility. This scenario seeds its own small, fixed, deterministic
    user set (see Populate-OpenLDAP-Scenario14.ps1) and ignores the template.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait between steps for JIM processing (default: 0)

.PARAMETER SkipPopulate
    Skip re-seeding OpenLDAP (used when the runner already populated via a snapshot). Scenario
    14 is currently excluded from OpenLDAP snapshot handling in Run-IntegrationTests.ps1 (its
    dataset is small and bespoke), so the runner never sets this automatically; it exists for
    manual re-runs against an already-populated environment (e.g. with -SkipReset).

.PARAMETER DirectoryConfig
    Directory-specific configuration hashtable from Get-DirectoryConfig. Must be OpenLDAP.

.EXAMPLE
    ./Invoke-Scenario14-AttributePriority.ps1 -Step All -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario14-AttributePriority.ps1 -Step BaselineResolution -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("BaselineResolution", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
}
if ($DirectoryConfig.UserObjectClass -ne "inetOrgPerson") {
    throw "Scenario 14 (Attribute Priority) is OpenLDAP only. Run-IntegrationTests.ps1 should have rejected this combination before this script was invoked."
}

$primarySystemName = "Scenario 14 Primary"
$secondarySystemName = "Scenario 14 Secondary"

Write-TestSection "Scenario 14: Attribute Priority"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template (ignored - fixed six-user dataset)" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Attribute Priority"
    Template = $Template
    Steps = @()
    Success = $false
}

try {
    # ========================================================================
    # Step 0: Setup and Verification
    # ========================================================================
    Write-TestSection "Step 0: Setup and Verification"

    if (-not $ApiKey) {
        throw "API key required for authentication"
    }

    Write-Host "Waiting for OpenLDAP to be healthy..." -ForegroundColor Gray
    $maxWaitSeconds = 120
    $elapsed = 0
    $interval = 5
    $containerStatus = ""
    while ($elapsed -lt $maxWaitSeconds) {
        $containerStatus = docker inspect --format='{{.State.Health.Status}}' $DirectoryConfig.ContainerName 2>&1
        if ($containerStatus -eq "healthy") { break }
        Start-Sleep -Seconds $interval
        $elapsed += $interval
    }
    if ($containerStatus -ne "healthy") {
        throw "$($DirectoryConfig.ContainerName) container did not become healthy within ${maxWaitSeconds}s (status: $containerStatus)"
    }
    Write-Host "  OK OpenLDAP is healthy" -ForegroundColor Green

    if (-not $SkipPopulate) {
        Write-Host "Populating test data (both suffixes)..." -ForegroundColor Gray
        & "$PSScriptRoot/../Populate-OpenLDAP-Scenario14.ps1"
        Write-Host "  OK Test data populated" -ForegroundColor Green
    }
    else {
        Write-Host "  Using pre-populated data - skipping population" -ForegroundColor Green
    }

    Write-Host "Running Scenario 14 setup..." -ForegroundColor Gray
    & "$PSScriptRoot/../Setup-Scenario14.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template -DirectoryConfig $DirectoryConfig
    Write-Host "  OK JIM configured for Scenario 14" -ForegroundColor Green

    # Re-import module to ensure we have a live connection after Setup-Scenario14.ps1 ran in a
    # separate invocation.
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $connectedSystems = Get-JIMConnectedSystem
    $primarySystem = $connectedSystems | Where-Object { $_.name -eq $primarySystemName }
    $secondarySystem = $connectedSystems | Where-Object { $_.name -eq $secondarySystemName }
    if (-not $primarySystem -or -not $secondarySystem) {
        throw "Connected Systems not found. Ensure Setup-Scenario14.ps1 completed successfully."
    }

    $primaryProfiles = Get-JIMRunProfile -ConnectedSystemId $primarySystem.id
    $secondaryProfiles = Get-JIMRunProfile -ConnectedSystemId $secondarySystem.id
    $primaryFullImport = $primaryProfiles | Where-Object { $_.name -eq "Full Import" }
    $secondaryFullImport = $secondaryProfiles | Where-Object { $_.name -eq "Full Import" }
    $primaryFullSync = $primaryProfiles | Where-Object { $_.name -eq "Full Synchronisation" }
    $secondaryFullSync = $secondaryProfiles | Where-Object { $_.name -eq "Full Synchronisation" }

    if (-not $primaryFullImport -or -not $secondaryFullImport -or -not $primaryFullSync -or -not $secondaryFullSync) {
        throw "Required Run Profiles not found. Ensure Setup-Scenario14.ps1 completed successfully."
    }

    # ========================================================================
    # Test 1: BaselineResolution
    # ========================================================================
    if ($Step -eq "BaselineResolution" -or $Step -eq "All") {
        Write-TestSection "Test 1: Baseline Resolution (Primary wins every contested attribute)"

        $baselineSuccess = $true
        $baselineNotes = @()

        try {
            Write-Host "Running Full Import (Primary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary)"

            Write-Host "Running Full Import (Secondary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Secondary)"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary)"

            Write-Host "Running Full Synchronisation (Secondary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Secondary)"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            # Sample subject: Alice (Employee ID S14-0). Her Primary-suffix manager (rotation
            # offset 1) is Bob (S14-1); her Secondary-suffix manager (offset 3) is Dave (S14-3).
            # Baseline resolution must show Bob, never Dave, per Populate-OpenLDAP-Scenario14.ps1.
            Write-Host "Looking up sample Metaverse Objects..." -ForegroundColor Gray
            $aliceMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-0" -PageSize 5) | Select-Object -First 1
            $bobMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-1" -PageSize 5) | Select-Object -First 1
            $daveMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-3" -PageSize 5) | Select-Object -First 1

            if (-not $aliceMvo -or -not $bobMvo -or -not $daveMvo) {
                throw "Could not resolve sample Metaverse Objects for Alice (S14-0), Bob (S14-1) and/or Dave (S14-3). Check the join on Employee ID succeeded for both systems."
            }
            Write-Host "  OK Alice=$($aliceMvo.id), Bob=$($bobMvo.id), Dave=$($daveMvo.id)" -ForegroundColor Green

            $primaryImportRuleName = "$primarySystemName Import Users"
            $secondaryImportRuleName = "$secondarySystemName Import Users"
            $null = $secondaryImportRuleName  # documents the losing rule name; not asserted directly

            Assert-MvoAttributeValue -MvoId $aliceMvo.id -AttributeName "Description" `
                -ExpectedValue "Primary-sourced description for Alice Anderson (S14)" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Alice's Description (Primary wins)"

            Assert-MvoAttributeValue -MvoId $aliceMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Engineer (Primary)" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Alice's Job Title (Primary wins)"

            Assert-MvoAttributeValue -MvoId $aliceMvo.id -AttributeName "Manager" `
                -ExpectedReferenceMvoId $bobMvo.id `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Alice's Manager (Primary's referent, Bob, not Secondary's, Dave)"

            Assert-MvoAttributeValue -MvoId $aliceMvo.id -AttributeName "Other Telephones" `
                -ExpectedValues @("+44 20 7946 1000", "+44 20 7946 1001") `
                -Name "Alice's Other Telephones (Primary's full value set, Secondary's absent)"

            Assert-MvoAttributeValue -MvoId $aliceMvo.id -AttributeName "Email" `
                -ExpectedValue "alice14@yellowstone.local" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Alice's Email (Primary's domain)"

            Assert-MvoAttributeValue -MvoId $aliceMvo.id -AttributeName "Employee ID" `
                -ExpectedValue "S14-0" `
                -Name "Alice's Employee ID (join key sanity check)"

            $baselineNotes += "Primary won Description, Job Title, Manager, Other Telephones and Email for Alice"
        }
        catch {
            $baselineSuccess = $false
            $baselineNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "BaselineResolution"
                Success = $baselineSuccess
                Note = ($baselineNotes -join "; ")
            }
        }
    }

    # -- INSERT NEW STEP DISPATCH BLOCKS HERE (mirror the "if ($Step -eq ... -or $Step -eq 'All')" shape above) --

    # Calculate overall success
    $failedSteps = @($testResults.Steps | Where-Object { $_.Success -eq $false })
    $testResults.Success = ($failedSteps.Count -eq 0)
}
catch {
    Write-Host ""
    Write-Host "FAIL Test failed with error:" -ForegroundColor Red
    Write-Host "  $_" -ForegroundColor Red
    Write-Host ""
    if (@($testResults.Steps | Where-Object { $_.Success -eq $false }).Count -eq 0) {
        $testResults.Steps += @{ Name = "Setup"; Success = $false; Error = $_.ToString() }
    }
}

# ========================================================================
# Summary
# ========================================================================
Write-TestSection "Test Results Summary"

$passedCount = @($testResults.Steps | Where-Object { $_.Success -eq $true }).Count
$failedCount = @($testResults.Steps | Where-Object { $_.Success -eq $false }).Count
$totalCount = @($testResults.Steps).Count

Write-Host "Scenario: $($testResults.Scenario)" -ForegroundColor Cyan
Write-Host ""

foreach ($testStep in $testResults.Steps) {
    $icon = if ($testStep.Success) { "OK" } else { "FAIL" }
    $color = if ($testStep.Success) { "Green" } else { "Red" }

    Write-Host "  $icon $($testStep.Name)" -ForegroundColor $color

    if ($testStep.ContainsKey('Note') -and $testStep.Note) {
        Write-Host "    $($testStep.Note)" -ForegroundColor Gray
    }
    if (-not $testStep.Success -and $testStep.ContainsKey('Error') -and $testStep.Error) {
        Write-Host "    Error: $($testStep.Error)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Results: $passedCount passed, $failedCount failed (of $totalCount tests)" -ForegroundColor $(if ($failedCount -eq 0) { "Green" } else { "Red" })

if ($testResults.Success) {
    Write-Host ""
    Write-Host "OK All Scenario 14 tests passed!" -ForegroundColor Green
    exit 0
}
else {
    Write-Host ""
    Write-Host "FAIL Some Scenario 14 tests failed" -ForegroundColor Red
    exit 1
}
