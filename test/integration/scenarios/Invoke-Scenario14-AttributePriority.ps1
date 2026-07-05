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
    2. RecallReElection - Alice's (S14-0) Primary-suffix entry is deleted outright. A Full
       Import (Primary) marks her Primary CSO Obsolete; a Full Synchronisation (Primary) then
       recalls every attribute Primary contributed and, in the SAME run, re-elects the
       surviving Secondary contribution: Description, Job Title, the Manager reference (to
       Dave, S14-3, Secondary's rotation-offset-3 target) and the full Other Telephones MVA
       all hand over together.
    3. IdenticalValueHandOver - Bob's (S14-1) Secondary description is first edited to match
       Primary's exact string while Primary still wins (proving the loser matching values does
       not steal the win), then Bob's Primary entry is deleted outright. The identical value
       survives unchanged but its provenance hands over to Secondary, and Job Title hands over
       to Secondary's distinct value: no value flap on the shared string.
    4. WithdrawalReElection - Carol's (S14-2) Primary entry keeps existing but only her
       `description` attribute is withdrawn (LDIF attribute delete, not an entry/CSO deletion).
       A Full Synchronisation (Primary) re-elects Secondary's Description in the SAME run,
       while Job Title, Manager and Other Telephones stay on Primary: no collateral hand-over
       of attributes Primary still supplies.
    5. NoContributorCleared - Erin's (S14-4) Secondary description is withdrawn first (the
       loser leaving changes nothing), then her Primary description is withdrawn too (the
       winner leaving with no surviving contributor). Description ends up with no value at all,
       and the Primary Full Synchronisation Activity records a NoContributor sync outcome
       ("MVO No Contributor" in the UI).

    -- INSERT NEW STEPS HERE (Phase C: NullIsValue tri-state; Phase D: authority/propagation):
       add the ValidateSet entry above, a $testResults.Steps-tracked block below (mirroring
       BaselineResolution's structure), and update .PARAMETER Step. --

    Step composition under -Step All: RecallReElection through NoContributorCleared were each
    given a distinct subject (Alice, Bob, Carol, Erin) precisely so they compose safely when run
    back-to-back after BaselineResolution; none of them touches a user another step depends on.
    Full Import/Full Synchronisation are always run per-system (Primary or Secondary, whichever
    that step mutated), so a later step's full-system re-sync of an earlier step's mutated user is
    idempotent (no further change, no repeat recall/withdrawal outcome).

.PARAMETER Step
    Which test step to execute (BaselineResolution, RecallReElection, IdenticalValueHandOver,
    WithdrawalReElection, NoContributorCleared, All). Run-IntegrationTests.ps1 resets and
    repopulates OpenLDAP for every scenario invocation, so a single named -Step run starts from a
    fresh environment with no synchronised state; the script therefore establishes the baseline
    (both systems fully imported and synchronised) before dispatching any non-baseline step, and
    the step then mutates from that baseline rather than from a state left by an earlier step.

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
    [ValidateSet("BaselineResolution", "RecallReElection", "IdenticalValueHandOver", "WithdrawalReElection", "NoContributorCleared", "All")]
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

# Re-derive Primary (Yellowstone) and Secondary (Glitterband) directory configuration independently
# of whichever single OpenLDAP instance was passed in as -DirectoryConfig, mirroring
# Setup-Scenario14.ps1. RecallReElection, IdenticalValueHandOver, WithdrawalReElection and
# NoContributorCleared mutate LDAP directly (ldapmodify/ldapdelete against the container), so both
# suffixes' bind credentials are needed regardless of which one -DirectoryConfig pointed at.
$primaryLdapConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
$secondaryLdapConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Target
$primaryLdapUri = "ldap://localhost:$($primaryLdapConfig.Port)"
$secondaryLdapUri = "ldap://localhost:$($secondaryLdapConfig.Port)"

function Invoke-Scenario14LdapModify {
    <#
    .SYNOPSIS
        Runs an LDIF payload through ldapmodify against a Scenario 14 OpenLDAP suffix.

    .DESCRIPTION
        Mirrors the bash/docker-exec pattern established by Populate-OpenLDAP-Scenario8.ps1: write
        the LDIF to a temp file, cat it into "docker exec -i <container> ldapmodify -c" (the -c
        continues past non-fatal per-entry errors, matching the established batch-tolerant
        pattern), then remove the temp file.
    #>
    param(
        [Parameter(Mandatory=$true)] [string]$ContainerName,
        [Parameter(Mandatory=$true)] [string]$LdapUri,
        [Parameter(Mandatory=$true)] [string]$BindDN,
        [Parameter(Mandatory=$true)] [string]$BindPassword,
        [Parameter(Mandatory=$true)] [string]$Ldif
    )

    $ldifPath = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $ldifPath -Value $Ldif -NoNewline
    try {
        $result = bash -c "cat '$ldifPath' | docker exec -i $ContainerName ldapmodify -x -H '$LdapUri' -D '$BindDN' -w '$BindPassword' -c" 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "ldapmodify failed (exit code $LASTEXITCODE): $result"
        }
    }
    finally {
        Remove-Item -Path $ldifPath -Force -ErrorAction SilentlyContinue
    }
}

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

    # Establishes the baseline synchronised state every test step builds on: both systems fully
    # imported and synchronised, so all six users are joined with Primary winning each contested
    # attribute. BaselineResolution runs (and asserts) this; a single named -Step run on a freshly
    # reset environment has no prior sync state, so the dispatch below also runs it before any
    # non-baseline step.
    function Invoke-Scenario14BaselineRuns {
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
    }

    if ($Step -notin @("All", "BaselineResolution")) {
        Write-TestSection "Step 0b: Establishing baseline synchronised state for -Step $Step"
        Invoke-Scenario14BaselineRuns
    }

    # ========================================================================
    # Test 1: BaselineResolution
    # ========================================================================
    if ($Step -eq "BaselineResolution" -or $Step -eq "All") {
        Write-TestSection "Test 1: Baseline Resolution (Primary wins every contested attribute)"

        $baselineSuccess = $true
        $baselineNotes = @()

        try {
            Invoke-Scenario14BaselineRuns

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

    # ========================================================================
    # Test 2: RecallReElection
    # ========================================================================
    if ($Step -eq "RecallReElection" -or $Step -eq "All") {
        Write-TestSection "Test 2: Recall Re-Election (Alice's Primary CSO is deleted; Secondary re-elects, same run)"

        $recallSuccess = $true
        $recallNotes = @()

        try {
            # Alice (S14-0) is removed from the Primary suffix only. A subsequent Full Import (Primary)
            # marks her Primary CSO Obsolete (missing from the source); Full Synchronisation (Primary)
            # then recalls every attribute her Primary CSO contributed and, in the SAME run, re-elects
            # the still-joined Secondary contribution: scalars, the Manager reference, and the full
            # Other Telephones MVA hand over together (docs/concepts/attribute-priority.md, "When the
            # winning source disconnects or withdraws").
            Write-Host "Deleting Alice from the Primary suffix only..." -ForegroundColor Gray
            $aliceDn = "uid=alice14,$($primaryLdapConfig.UserContainer)"
            $deleteOutput = docker exec $primaryLdapConfig.ContainerName ldapdelete -x -H $primaryLdapUri -D $primaryLdapConfig.BindDN -w $primaryLdapConfig.BindPassword "$aliceDn" 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "ldapdelete failed for '$aliceDn' (exit $LASTEXITCODE): $deleteOutput"
            }
            Write-Host "  OK Deleted $aliceDn" -ForegroundColor Green

            Write-Host "Running Full Import (Primary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after Alice's deletion"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after Alice's deletion"

            $aliceMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-0" -PageSize 5) | Select-Object -First 1
            $daveMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-3" -PageSize 5) | Select-Object -First 1
            if (-not $aliceMvo -or -not $daveMvo) {
                throw "Could not resolve Alice (S14-0) and/or Dave (S14-3) Metaverse Objects after recall. Check the Primary CSO was actually obsoleted and recalled."
            }
            Write-Host "  OK Alice's Metaverse Object survived the recall (ID: $($aliceMvo.id))" -ForegroundColor Green

            $secondaryImportRuleName = "$secondarySystemName Import Users"

            Assert-MvoAttributeValue -MvoId $aliceMvo.id -AttributeName "Description" `
                -ExpectedValue "Secondary-sourced description for Alice Anderson (S14)" `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Alice's Description (re-elected to Secondary, same run)"

            Assert-MvoAttributeValue -MvoId $aliceMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Engineer (Secondary)" `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Alice's Job Title (re-elected to Secondary)"

            Assert-MvoAttributeValue -MvoId $aliceMvo.id -AttributeName "Manager" `
                -ExpectedReferenceMvoId $daveMvo.id `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Alice's Manager (re-elected reference: Dave, Secondary's rotation offset 3, not Bob)"

            Assert-MvoAttributeValue -MvoId $aliceMvo.id -AttributeName "Other Telephones" `
                -ExpectedValues @("+44 20 7946 2000", "+44 20 7946 2001") `
                -Name "Alice's Other Telephones (full MVA hand-over to Secondary's set)"

            Assert-MvoAttributeValue -MvoId $aliceMvo.id -AttributeName "Email" `
                -ExpectedValue "alice14@glitterband.local" `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Alice's Email (re-elected to Secondary's domain)"

            $recallNotes += "Alice's Primary CSO was recalled; Secondary re-elected Description, Job Title, the Manager reference, Other Telephones and Email in the same run"
        }
        catch {
            $recallSuccess = $false
            $recallNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "RecallReElection"
                Success = $recallSuccess
                Note = ($recallNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 3: IdenticalValueHandOver
    # ========================================================================
    if ($Step -eq "IdenticalValueHandOver" -or $Step -eq "All") {
        Write-TestSection "Test 3: Identical-Value Hand-Over (Bob, no value flap when the winner departs)"

        $identicalSuccess = $true
        $identicalNotes = @()

        try {
            $primaryImportRuleName = "$primarySystemName Import Users"
            $secondaryImportRuleName = "$secondarySystemName Import Users"
            $bobPrimaryDescription = "Primary-sourced description for Bob Baker (S14)"

            # Phase 1: make the Secondary (losing) contributor's Description identical to Primary's
            # while Primary still wins. A Full Import + Full Synchronisation of Secondary alone must
            # not steal the win merely because the values now match.
            Write-Host "Updating Bob's Secondary description to match Primary's value..." -ForegroundColor Gray
            $bobSecondaryDn = "uid=bob14,$($secondaryLdapConfig.UserContainer)"
            $matchLdif = "dn: $bobSecondaryDn`nchangetype: modify`nreplace: description`ndescription: $bobPrimaryDescription`n"
            Invoke-Scenario14LdapModify -ContainerName $secondaryLdapConfig.ContainerName -LdapUri $secondaryLdapUri `
                -BindDN $secondaryLdapConfig.BindDN -BindPassword $secondaryLdapConfig.BindPassword -Ldif $matchLdif

            Write-Host "Running Full Import (Secondary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Secondary) after Bob's identical-value update"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Secondary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Secondary) after Bob's identical-value update"

            $bobMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-1" -PageSize 5) | Select-Object -First 1
            if (-not $bobMvo) {
                throw "Could not resolve Bob (S14-1) Metaverse Object."
            }

            Assert-MvoAttributeValue -MvoId $bobMvo.id -AttributeName "Description" `
                -ExpectedValue $bobPrimaryDescription `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Bob's Description (Primary still wins; the loser matching values must not steal the win)"

            # Phase 2: Primary's CSO is deleted outright. The identical Secondary value should hand
            # over (same value, new provenance, no flap), and Job Title should hand over to
            # Secondary's distinct value.
            Write-Host "Deleting Bob from the Primary suffix..." -ForegroundColor Gray
            $bobPrimaryDn = "uid=bob14,$($primaryLdapConfig.UserContainer)"
            $deleteOutput = docker exec $primaryLdapConfig.ContainerName ldapdelete -x -H $primaryLdapUri -D $primaryLdapConfig.BindDN -w $primaryLdapConfig.BindPassword "$bobPrimaryDn" 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "ldapdelete failed for '$bobPrimaryDn' (exit $LASTEXITCODE): $deleteOutput"
            }
            Write-Host "  OK Deleted $bobPrimaryDn" -ForegroundColor Green

            Write-Host "Running Full Import (Primary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after Bob's Primary deletion"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after Bob's Primary deletion"

            Assert-MvoAttributeValue -MvoId $bobMvo.id -AttributeName "Description" `
                -ExpectedValue $bobPrimaryDescription `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Bob's Description (value unchanged, provenance handed to Secondary, no flap)"

            Assert-MvoAttributeValue -MvoId $bobMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Manager (Secondary)" `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Bob's Job Title (handed over to Secondary's distinct value)"

            $identicalNotes += "Bob's identical-value Description did not steal the win while Primary was joined, then handed over to Secondary provenance without a value change when Primary departed"
        }
        catch {
            $identicalSuccess = $false
            $identicalNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "IdenticalValueHandOver"
                Success = $identicalSuccess
                Note = ($identicalNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 4: WithdrawalReElection
    # ========================================================================
    if ($Step -eq "WithdrawalReElection" -or $Step -eq "All") {
        Write-TestSection "Test 4: Withdrawal Re-Election (Carol, winner withdraws in place, no collateral hand-over)"

        $withdrawalSuccess = $true
        $withdrawalNotes = @()

        try {
            $primaryImportRuleName = "$primarySystemName Import Users"
            $secondaryImportRuleName = "$secondarySystemName Import Users"

            # Carol's Primary entry stays; only her description attribute is withdrawn ("delete:
            # description" with no value in the LDIF removes every value of that attribute, per RFC
            # 4511). This is an in-place withdrawal, not a CSO deletion: the winner stays joined but
            # simply stops supplying a value, which re-elects the surviving Secondary contributor in
            # the SAME run exactly as a disconnection would (docs/concepts/attribute-priority.md,
            # "When the winning source disconnects or withdraws").
            Write-Host "Withdrawing Carol's Primary description (attribute removed, entry remains)..." -ForegroundColor Gray
            $carolPrimaryDn = "uid=carol14,$($primaryLdapConfig.UserContainer)"
            $withdrawLdif = "dn: $carolPrimaryDn`nchangetype: modify`ndelete: description`n"
            Invoke-Scenario14LdapModify -ContainerName $primaryLdapConfig.ContainerName -LdapUri $primaryLdapUri `
                -BindDN $primaryLdapConfig.BindDN -BindPassword $primaryLdapConfig.BindPassword -Ldif $withdrawLdif

            Write-Host "Running Full Import (Primary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after Carol's description withdrawal"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after Carol's description withdrawal"

            $carolMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-2" -PageSize 5) | Select-Object -First 1
            $daveMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-3" -PageSize 5) | Select-Object -First 1
            if (-not $carolMvo -or -not $daveMvo) {
                throw "Could not resolve Carol (S14-2) and/or Dave (S14-3) Metaverse Objects."
            }

            Assert-MvoAttributeValue -MvoId $carolMvo.id -AttributeName "Description" `
                -ExpectedValue "Secondary-sourced description for Carol Clarke (S14)" `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Carol's Description (re-elected to Secondary, same run)"

            Assert-MvoAttributeValue -MvoId $carolMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Analyst (Primary)" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Carol's Job Title (still Primary's; no collateral hand-over)"

            Assert-MvoAttributeValue -MvoId $carolMvo.id -AttributeName "Manager" `
                -ExpectedReferenceMvoId $daveMvo.id `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Carol's Manager (still Primary's referent, Dave; no collateral hand-over)"

            Assert-MvoAttributeValue -MvoId $carolMvo.id -AttributeName "Other Telephones" `
                -ExpectedValues @("+44 20 7946 1020", "+44 20 7946 1021") `
                -Name "Carol's Other Telephones (still Primary's set; no collateral hand-over)"

            $withdrawalNotes += "Carol's withdrawn Description re-elected to Secondary in the same run; Job Title, Manager and Other Telephones stayed on Primary"
        }
        catch {
            $withdrawalSuccess = $false
            $withdrawalNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "WithdrawalReElection"
                Success = $withdrawalSuccess
                Note = ($withdrawalNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 5: NoContributorCleared
    # ========================================================================
    if ($Step -eq "NoContributorCleared" -or $Step -eq "All") {
        Write-TestSection "Test 5: No Contributor Cleared (Erin, both sources withdraw Description)"

        $noContributorSuccess = $true
        $noContributorNotes = @()

        try {
            $primaryImportRuleName = "$primarySystemName Import Users"
            $erinPrimaryDescription = "Primary-sourced description for Erin Ellis (S14)"

            # Phase 1: the LOSING (Secondary) contributor withdraws first. Primary still contributes,
            # so Description must be untouched.
            Write-Host "Withdrawing Erin's Secondary description..." -ForegroundColor Gray
            $erinSecondaryDn = "uid=erin14,$($secondaryLdapConfig.UserContainer)"
            $withdrawSecondaryLdif = "dn: $erinSecondaryDn`nchangetype: modify`ndelete: description`n"
            Invoke-Scenario14LdapModify -ContainerName $secondaryLdapConfig.ContainerName -LdapUri $secondaryLdapUri `
                -BindDN $secondaryLdapConfig.BindDN -BindPassword $secondaryLdapConfig.BindPassword -Ldif $withdrawSecondaryLdif

            Write-Host "Running Full Import (Secondary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Secondary) after Erin's Secondary description withdrawal"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Secondary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Secondary) after Erin's Secondary description withdrawal"

            $erinMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-4" -PageSize 5) | Select-Object -First 1
            if (-not $erinMvo) {
                throw "Could not resolve Erin (S14-4) Metaverse Object."
            }

            Assert-MvoAttributeValue -MvoId $erinMvo.id -AttributeName "Description" `
                -ExpectedValue $erinPrimaryDescription `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Erin's Description (unaffected by the losing Secondary contributor withdrawing)"

            # Phase 2: the WINNING (Primary) contributor also withdraws. No contributor remains, so
            # Description must clear to no value, and the Full Synchronisation (Primary) Activity must
            # record a NoContributor sync outcome (docs/concepts/attribute-priority.md, "MVO No
            # Contributor"; ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor in
            # src/JIM.Models/Activities/ActivityEnums.cs).
            #
            # Evidence gathered before writing this assertion (see task instructions):
            # - The RPEI's denormalised OutcomeSummary is built by SyncOutcomeBuilder.BuildOutcomeSummary
            #   as "OutcomeType:count" pairs using the raw enum name (e.g. "NoContributor:1"), not the
            #   JIM.Web/Helpers.cs display name ("MVO No Contributor"); Assert-ActivityItemsHaveOutcomeSummary
            #   matches "$ExpectedOutcomeType`:" against that raw string, so -ExpectedOutcomeType
            #   "NoContributor" (not the display name) is the correct argument.
            # - Sync outcome tracking level (ChangeTracking.SyncOutcomes.Level) defaults to Detailed
            #   (src/JIM.Models/Core/Constants.cs doc comment; seeded default in
            #   src/JIM.Application/Servers/SeedingServer.cs; fallback in
            #   ServiceSettingsServer.GetSyncOutcomeTrackingLevelAsync is also Detailed). Only Detailed
            #   mode emits the NoContributor child outcome (src/JIM.Worker/Processors/SyncTaskProcessorBase.cs);
            #   Scenario 14's setup never changes this setting, so no extra service-setting call is
            #   required here.
            # - This is an in-place withdrawal on an already-joined CSO (entry remains, attribute
            #   removed), so it takes the "AttributeFlow root + NoContributor child" path in
            #   SyncTaskProcessorBase.cs (not the CSO-obsoletion "Disconnected root" path used by
            #   RecallReElection), matching AttributePriorityRecallWorkflowTests.Withdrawal_WinnerWithdrawsValueInPlace_ReElectsSurvivorInSameRunAsync.
            Write-Host "Withdrawing Erin's Primary description..." -ForegroundColor Gray
            $erinPrimaryDn = "uid=erin14,$($primaryLdapConfig.UserContainer)"
            $withdrawPrimaryLdif = "dn: $erinPrimaryDn`nchangetype: modify`ndelete: description`n"
            Invoke-Scenario14LdapModify -ContainerName $primaryLdapConfig.ContainerName -LdapUri $primaryLdapUri `
                -BindDN $primaryLdapConfig.BindDN -BindPassword $primaryLdapConfig.BindPassword -Ldif $withdrawPrimaryLdif

            Write-Host "Running Full Import (Primary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after Erin's Primary description withdrawal"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after Erin's Primary description withdrawal"

            Assert-MvoAttributeValue -MvoId $erinMvo.id -AttributeName "Description" `
                -ExpectNoValue `
                -Name "Erin's Description (cleared: no contributor remains on either side)"

            Assert-ActivityItemsHaveOutcomeSummary -ActivityId $syncResult.activityId `
                -Name "Full Synchronisation (Primary) after Erin's Primary description withdrawal" `
                -ExpectedOutcomeType "NoContributor"

            $noContributorNotes += "Erin's Description survived the losing Secondary withdrawal, then cleared with a NoContributor sync outcome once the winning Primary also withdrew"
        }
        catch {
            $noContributorSuccess = $false
            $noContributorNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "NoContributorCleared"
                Success = $noContributorSuccess
                Note = ($noContributorNotes -join "; ")
            }
        }
    }

    # -- INSERT NEW STEP DISPATCH BLOCKS HERE (Phase C: NullIsValue tri-state; Phase D: authority/propagation).
    #    Mirror the "if ($Step -eq ... -or $Step -eq 'All')" shape above. --

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
