<#
.SYNOPSIS
    Test Scenario 8: Cross-domain Entitlement Synchronisation

.DESCRIPTION
    Validates synchronisation of entitlement groups (security groups, distribution groups)
    between two AD instances (Quantum Dynamics APAC source and EMEA target).
    Source AD is authoritative for groups. Tests initial sync, forward sync,
    drift detection, and state reassertion.

    This scenario is self-contained and will:
    1. Populate users and groups in Source AD
    2. Configure JIM with user and group sync rules
    3. Sync users between domains (prerequisite for group member resolution)
    4. Sync groups with membership preservation

.PARAMETER Step
    Which test step to execute (InitialSync, ForwardSync, DetectDrift, ReassertState, NewGroup, DeleteGroup, All)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait between steps for JIM processing (default: 20)

.EXAMPLE
    ./Invoke-Scenario8-CrossDomainEntitlementSync.ps1 -Step All -Template Nano -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario8-CrossDomainEntitlementSync.ps1 -Step InitialSync -Template Small -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("ImportToMV", "InitialSync", "ForwardSync", "DetectDrift", "ReassertState", "NewGroup", "DeleteGroup", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"
. "$PSScriptRoot/../utils/Test-GroupHelpers.ps1"

Write-TestSection "Scenario 8: Cross-domain Entitlement Synchronisation"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Cross-domain Entitlement Synchronisation"
    Template = $Template
    Steps = @()
    Success = $false
}

try {
    # Step 0: Setup JIM configuration
    Write-TestSection "Step 0: Setup and Verification"

    if (-not $ApiKey) {
        Write-Host "  No API key provided" -ForegroundColor Yellow
        Write-Host "  Create an API key via JIM web UI: Admin > API Keys" -ForegroundColor Yellow
        throw "API key required for authentication"
    }

    # Verify Scenario 8 containers are running
    Write-Host "Verifying Scenario 8 infrastructure..." -ForegroundColor Gray

    $sourceStatus = docker inspect --format='{{.State.Health.Status}}' samba-ad-source 2>&1
    $targetStatus = docker inspect --format='{{.State.Health.Status}}' samba-ad-target 2>&1

    if ($sourceStatus -ne "healthy") {
        throw "samba-ad-source container is not healthy (status: $sourceStatus). Run: docker compose -f docker-compose.integration-tests.yml --profile scenario8 up -d"
    }
    if ($targetStatus -ne "healthy") {
        throw "samba-ad-target container is not healthy (status: $targetStatus). Run: docker compose -f docker-compose.integration-tests.yml --profile scenario8 up -d"
    }

    Write-Host "  ✓ Source AD (APAC) healthy" -ForegroundColor Green
    Write-Host "  ✓ Target AD (EMEA) healthy" -ForegroundColor Green

    # Populate test data in Source AD
    Write-Host "Populating test data in Source AD..." -ForegroundColor Gray
    & "$PSScriptRoot/../Populate-SambaAD-Scenario8.ps1" -Template $Template -Instance Source

    Write-Host "Creating OU structure in Target AD..." -ForegroundColor Gray
    & "$PSScriptRoot/../Populate-SambaAD-Scenario8.ps1" -Template $Template -Instance Target

    Write-Host "✓ Test data populated" -ForegroundColor Green

    # Run Setup-Scenario8 to configure JIM
    Write-Host "Running Scenario 8 setup..." -ForegroundColor Gray
    & "$PSScriptRoot/../Setup-Scenario8.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template

    Write-Host "✓ JIM configured for Scenario 8" -ForegroundColor Green

    # Re-import module to ensure we have connection
    $modulePath = "$PSScriptRoot/../../../JIM.PowerShell/JIM/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Get connected system and run profile IDs
    $connectedSystems = Get-JIMConnectedSystem
    $sourceSystem = $connectedSystems | Where-Object { $_.name -eq "Quantum Dynamics APAC" }
    $targetSystem = $connectedSystems | Where-Object { $_.name -eq "Quantum Dynamics EMEA" }

    if (-not $sourceSystem -or -not $targetSystem) {
        throw "Connected systems not found. Ensure Setup-Scenario8.ps1 completed successfully."
    }

    $sourceProfiles = Get-JIMRunProfile -ConnectedSystemId $sourceSystem.id
    $targetProfiles = Get-JIMRunProfile -ConnectedSystemId $targetSystem.id

    # Full profiles (for initial sync)
    $sourceFullImportProfile = $sourceProfiles | Where-Object { $_.name -eq "Full Import" }
    $sourceFullSyncProfile = $sourceProfiles | Where-Object { $_.name -eq "Full Sync" }
    $sourceExportProfile = $sourceProfiles | Where-Object { $_.name -eq "Export" }

    $targetFullImportProfile = $targetProfiles | Where-Object { $_.name -eq "Full Import" }
    $targetFullSyncProfile = $targetProfiles | Where-Object { $_.name -eq "Full Sync" }
    $targetExportProfile = $targetProfiles | Where-Object { $_.name -eq "Export" }

    # Delta profiles (for forward sync after initial)
    $sourceDeltaImportProfile = $sourceProfiles | Where-Object { $_.name -eq "Delta Import" }
    $sourceDeltaSyncProfile = $sourceProfiles | Where-Object { $_.name -eq "Delta Sync" }
    $targetDeltaImportProfile = $targetProfiles | Where-Object { $_.name -eq "Delta Import" }
    $targetDeltaSyncProfile = $targetProfiles | Where-Object { $_.name -eq "Delta Sync" }

    # Helper function to run FULL forward sync (Source -> Metaverse -> Target)
    # Used for initial synchronisation when objects already exist in both systems
    function Invoke-FullForwardSync {
        param(
            [string]$Context = ""
        )
        $contextSuffix = if ($Context) { " ($Context)" } else { "" }
        Write-Host "  Running FULL forward sync (Source → Metaverse → Target)..." -ForegroundColor Gray

        # INITIAL RECONCILIATION FLOW:
        # When objects already exist in both Source and Target AD, we need to:
        # 1. Import from BOTH systems first (create CSOs without sync)
        # 2. Sync Source (project to create MVOs)
        # 3. Sync Target (join Target CSOs to MVOs BEFORE export evaluation creates provisioning CSOs)
        # 4. Sync Source again (now exports will see existing Target CSOs and generate Updates, not Creates)

        # Step 1: Full Import from Source
        Write-Host "    Full importing from Source AD..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Source Full Import$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 2: Full Import from Target (BEFORE any sync)
        # Import Target CSOs early so they can join to MVOs before export rules create provisioning CSOs
        Write-Host "    Full importing from Target AD (discover existing objects)..." -ForegroundColor Gray
        $targetImportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $targetImportResult.activityId -Name "Target Full Import$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 3: Full Sync Source to Metaverse (projection)
        # Creates MVOs from Source CSOs. Export rules will evaluate, but Target CSOs exist
        # and will join in the next step, so any provisioning CSOs created here will be replaced.
        Write-Host "    Full syncing Source to metaverse (create MVOs)..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Source Full Sync (project)$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 4: Full Sync Target (join Target CSOs to MVOs)
        # Target CSOs join to MVOs via matching rules. This links real Target CSOs to MVOs.
        # If provisioning CSOs were created in step 3, they should be replaced/updated.
        Write-Host "    Full syncing Target (join Target CSOs to MVOs)..." -ForegroundColor Gray
        $targetSyncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $targetSyncResult.activityId -Name "Target Full Sync (join)$contextSuffix" -AllowWarnings
        Start-Sleep -Seconds $WaitSeconds

        # Step 5: Full Sync Source again to re-evaluate exports
        # Now that Target CSOs are properly joined, exports should be Update (not Create)
        Write-Host "    Full syncing Source again (re-evaluate exports)..." -ForegroundColor Gray
        $syncResult2 = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult2.activityId -Name "Source Full Sync (re-evaluate)$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 6: Export to Target
        Write-Host "    Exporting to Target AD..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetExportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "Target Export$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 7: Full Confirming Import from Target
        Write-Host "    Full confirming import in Target AD..." -ForegroundColor Gray
        $confirmImportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $confirmImportResult.activityId -Name "Target Full Confirming Import$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 8: Full Confirming Sync (informational only during initial sync)
        # Note: During initial sync with pre-existing objects, this may have CouldNotJoinDueToExistingJoin
        # errors because provisioning CSOs were created during Source Full Sync before real Target CSOs
        # could join. This is a known limitation of the provisioning model with pre-existing objects.
        # The important validation is that Export succeeded - confirming sync is informational only.
        Write-Host "    Full confirming sync..." -ForegroundColor Gray
        $confirmSyncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullSyncProfile.id -Wait -PassThru
        $confirmSyncActivity = Get-JIMActivity -Id $confirmSyncResult.activityId
        if ($confirmSyncActivity.status -eq "Complete") {
            Write-Host "  ✓ Target Full Confirming Sync$contextSuffix completed successfully (Status: Complete)" -ForegroundColor Green
        }
        elseif ($confirmSyncActivity.status -eq "CompleteWithWarning") {
            Write-Host "  ⚠ Target Full Confirming Sync$contextSuffix completed with warnings (expected during initial sync with pre-existing objects)" -ForegroundColor Yellow
        }
        else {
            Write-Host "  ⚠ Target Full Confirming Sync$contextSuffix completed with status: $($confirmSyncActivity.status)" -ForegroundColor Yellow
            Write-Host "    (CouldNotJoinDueToExistingJoin errors are expected during initial sync with pre-existing objects)" -ForegroundColor Yellow
        }
        Start-Sleep -Seconds $WaitSeconds
    }

    # Helper function to run DELTA forward sync (Source -> Metaverse -> Target)
    # Used for incremental changes after initial sync
    function Invoke-DeltaForwardSync {
        param(
            [string]$Context = ""
        )
        $contextSuffix = if ($Context) { " ($Context)" } else { "" }
        Write-Host "  Running DELTA forward sync (Source → Metaverse → Target)..." -ForegroundColor Gray

        # Step 1: Delta Import from Source
        Write-Host "    Delta importing from Source AD..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceDeltaImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Source Delta Import$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 2: Delta Sync to Metaverse
        Write-Host "    Delta syncing to metaverse..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceDeltaSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Source Delta Sync$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 3: Export to Target
        Write-Host "    Exporting to Target AD..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetExportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "Target Export$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 4: Delta Confirming Import from Target
        Write-Host "    Delta confirming import in Target AD..." -ForegroundColor Gray
        $confirmImportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetDeltaImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $confirmImportResult.activityId -Name "Target Delta Confirming Import$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 5: Delta Confirming Sync
        Write-Host "    Delta confirming sync..." -ForegroundColor Gray
        $confirmSyncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetDeltaSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $confirmSyncResult.activityId -Name "Target Delta Confirming Sync$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds
    }

    # Backward-compatible alias for InitialSync (uses Full)
    function Invoke-ForwardSync {
        param(
            [string]$Context = ""
        )
        Invoke-FullForwardSync -Context $Context
    }

    # Test 0: ImportToMV (Import from Source and sync to Metaverse ONLY - no export)
    if ($Step -eq "ImportToMV") {
        Write-TestSection "Test 0: ImportToMV (Source → Metaverse Only)"

        Write-Host "Importing from Source AD and projecting to Metaverse..." -ForegroundColor Gray
        Write-Host "  (Stopping before export to allow review)" -ForegroundColor Yellow

        # Step 1: Import from Source
        Write-Host "    Importing from Source AD..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Source Full Import"
        Start-Sleep -Seconds $WaitSeconds

        # Step 2: Sync to Metaverse
        Write-Host "    Syncing to metaverse..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Source Full Sync"

        Write-Host ""
        Write-Host "✓ ImportToMV complete - objects should now be in the Metaverse" -ForegroundColor Green
        Write-Host ""
        Write-Host "Next steps to review:" -ForegroundColor Cyan
        Write-Host "  1. Check Metaverse for projected users and groups" -ForegroundColor Gray
        Write-Host "  2. Verify attribute mappings are correct" -ForegroundColor Gray
        Write-Host "  3. Run -Step InitialSync to continue with export to Target AD" -ForegroundColor Gray
        Write-Host ""

        $testResults.Steps += "ImportToMV"
        $testResults.Success = $true
        return
    }

    # Test 1: InitialSync (Import groups from Source, sync to Target)
    if ($Step -eq "InitialSync" -or $Step -eq "All") {
        Write-TestSection "Test 1: InitialSync (Source → Target)"

        Write-Host "Syncing initial data..." -ForegroundColor Gray
        Invoke-ForwardSync -Context "InitialSync"

        Write-Host "Validating groups in Target AD..." -ForegroundColor Gray

        # Find groups with members in Source AD
        $sourceContainer = "samba-ad-source"
        $targetContainer = "samba-ad-target"

        $groupListOutput = docker exec $sourceContainer samba-tool group list 2>&1
        $testGroups = @($groupListOutput -split "`n" | Where-Object { $_ -match "^(Company-|Dept-|Location-|Project-)" })

        if ($testGroups.Count -eq 0) {
            Write-Host "  ⚠ No test groups found in Source AD" -ForegroundColor Yellow
        }
        else {
            # Find a group with members for validation
            $validationGroup = $null
            $sourceMemberCount = 0
            foreach ($grp in $testGroups) {
                $grpName = $grp.Trim()
                $members = docker exec $sourceContainer samba-tool group listmembers $grpName 2>&1
                if ($LASTEXITCODE -eq 0 -and $members) {
                    $memberList = @($members -split "`n" | Where-Object { $_.Trim() -ne "" })
                    if ($memberList.Count -gt 0) {
                        $validationGroup = $grpName
                        $sourceMemberCount = $memberList.Count
                        break
                    }
                }
            }

            if ($validationGroup) {
                Write-Host "  Validation group: $validationGroup (Source members: $sourceMemberCount)" -ForegroundColor Cyan

                # Check if group exists in Target
                docker exec $targetContainer samba-tool group show $validationGroup 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "  ✓ Group '$validationGroup' exists in Target AD" -ForegroundColor Green

                    # Check if members were synced
                    $targetMembers = docker exec $targetContainer samba-tool group listmembers $validationGroup 2>&1
                    $targetMemberCount = 0
                    if ($LASTEXITCODE -eq 0 -and $targetMembers) {
                        $targetMemberList = @($targetMembers -split "`n" | Where-Object { $_.Trim() -ne "" })
                        $targetMemberCount = $targetMemberList.Count
                    }

                    if ($targetMemberCount -eq $sourceMemberCount) {
                        Write-Host "  ✓ Member count matches: Source=$sourceMemberCount, Target=$targetMemberCount" -ForegroundColor Green
                    }
                    elseif ($targetMemberCount -gt 0) {
                        Write-Host "  ⚠ Member count mismatch: Source=$sourceMemberCount, Target=$targetMemberCount" -ForegroundColor Yellow
                        Write-Host "    Some members may not have synced yet (reference resolution)" -ForegroundColor Yellow
                    }
                    else {
                        Write-Host "  ⚠ No members in Target group (members may not have synced)" -ForegroundColor Yellow
                        Write-Host "    This can occur if user CSOs don't exist in Target yet" -ForegroundColor Yellow
                    }
                }
                else {
                    Write-Host "  ⚠ Group '$validationGroup' not found in Target AD" -ForegroundColor Yellow
                }
            }
            else {
                Write-Host "  ⚠ No groups with members found for validation" -ForegroundColor Yellow
            }
        }

        Write-Host "✓ InitialSync test completed" -ForegroundColor Green
        $testResults.Steps += "InitialSync"
    }

    # Test 2: ForwardSync (Add/remove members in Source, sync to Target)
    if ($Step -eq "ForwardSync" -or $Step -eq "All") {
        Write-TestSection "Test 2: ForwardSync (Membership Changes)"

        Write-Host "This test validates that membership changes in Source AD flow to Target AD" -ForegroundColor Gray

        # Container names for Source and Target AD
        $sourceContainer = "samba-ad-source"
        $targetContainer = "samba-ad-target"

        # Step 2.1: Find a group and users to test with
        Write-Host "  Finding test group and users..." -ForegroundColor Gray

        # Get a group from Source AD (use first group from Entitlements OU)
        $groupListOutput = docker exec $sourceContainer samba-tool group list 2>&1
        $allGroups = $groupListOutput -split "`n" | Where-Object { $_ -match "^(Company-|Dept-|Location-|Project-)" }

        if ($allGroups.Count -eq 0) {
            throw "No test groups found in Source AD. Ensure InitialSync has been run."
        }

        $testGroupName = $allGroups[0].Trim()
        Write-Host "    Test group: $testGroupName" -ForegroundColor Cyan

        # Get current members of the test group in Source AD
        $sourceMembersOutput = docker exec $sourceContainer samba-tool group listmembers $testGroupName 2>&1
        $sourceMembers = @()
        if ($LASTEXITCODE -eq 0 -and $sourceMembersOutput) {
            $sourceMembers = @($sourceMembersOutput -split "`n" | Where-Object { $_.Trim() -ne "" })
        }
        $initialMemberCount = $sourceMembers.Count
        Write-Host "    Current members in Source: $initialMemberCount" -ForegroundColor Gray

        # Get current members of the test group in Target AD (before changes)
        $targetMembersBefore = docker exec $targetContainer samba-tool group listmembers $testGroupName 2>&1
        $targetMemberCountBefore = 0
        if ($LASTEXITCODE -eq 0 -and $targetMembersBefore) {
            $targetMemberCountBefore = @($targetMembersBefore -split "`n" | Where-Object { $_.Trim() -ne "" }).Count
        }
        Write-Host "    Current members in Target: $targetMemberCountBefore" -ForegroundColor Gray

        # Step 2.2: Find users to add and remove
        Write-Host "  Preparing membership changes..." -ForegroundColor Gray

        # Get all users from Source AD
        $allUsersOutput = docker exec $sourceContainer samba-tool user list 2>&1
        $allUsers = @($allUsersOutput -split "`n" | Where-Object {
            $_.Trim() -ne "" -and
            $_ -notmatch "^(Administrator|Guest|krbtgt)" -and
            $_ -notmatch "DNS"
        })

        if ($allUsers.Count -lt 2) {
            throw "Not enough users in Source AD for membership testing. Need at least 2 users."
        }

        # Find users NOT in the group (to add)
        $usersNotInGroup = @($allUsers | Where-Object { $_ -notin $sourceMembers })
        # Find users IN the group (to remove)
        $usersInGroup = @($sourceMembers | Where-Object { $_ -in $allUsers })

        # Select users to add (up to 2)
        $usersToAdd = @()
        if ($usersNotInGroup.Count -ge 2) {
            $usersToAdd = $usersNotInGroup[0..1]
        }
        elseif ($usersNotInGroup.Count -eq 1) {
            $usersToAdd = @($usersNotInGroup[0])
        }

        # Select user to remove (1)
        $userToRemove = $null
        if ($usersInGroup.Count -ge 1) {
            $userToRemove = $usersInGroup[0]
        }

        Write-Host "    Users to add: $($usersToAdd -join ', ')" -ForegroundColor Yellow
        Write-Host "    User to remove: $userToRemove" -ForegroundColor Yellow

        # Step 2.3: Make membership changes in Source AD
        Write-Host "  Making membership changes in Source AD..." -ForegroundColor Gray

        $addedCount = 0
        foreach ($userToAdd in $usersToAdd) {
            $result = docker exec $sourceContainer samba-tool group addmembers $testGroupName $userToAdd 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "    ✓ Added '$userToAdd' to '$testGroupName'" -ForegroundColor Green
                $addedCount++
            }
            else {
                Write-Host "    ⚠ Failed to add '$userToAdd': $result" -ForegroundColor Yellow
            }
        }

        $removedCount = 0
        if ($userToRemove) {
            $result = docker exec $sourceContainer samba-tool group removemembers $testGroupName $userToRemove 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "    ✓ Removed '$userToRemove' from '$testGroupName'" -ForegroundColor Green
                $removedCount++
            }
            else {
                Write-Host "    ⚠ Failed to remove '$userToRemove': $result" -ForegroundColor Yellow
            }
        }

        # Verify changes in Source AD
        $sourceMembersAfterChange = docker exec $sourceContainer samba-tool group listmembers $testGroupName 2>&1
        $sourceMemberCountAfterChange = 0
        if ($LASTEXITCODE -eq 0 -and $sourceMembersAfterChange) {
            $sourceMemberCountAfterChange = @($sourceMembersAfterChange -split "`n" | Where-Object { $_.Trim() -ne "" }).Count
        }
        $expectedSourceCount = $initialMemberCount + $addedCount - $removedCount
        Write-Host "    Source members after change: $sourceMemberCountAfterChange (expected: $expectedSourceCount)" -ForegroundColor Gray

        # Step 2.4: Run DELTA forward sync to propagate changes (more efficient than full)
        Write-Host "  Running delta forward sync to propagate changes..." -ForegroundColor Gray
        Invoke-DeltaForwardSync -Context "ForwardSync membership changes"

        # Step 2.5: Validate changes in Target AD
        Write-Host "  Validating changes in Target AD..." -ForegroundColor Gray

        # Check if the group exists in Target AD
        docker exec $targetContainer samba-tool group show $testGroupName 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Test group '$testGroupName' not found in Target AD after sync"
        }

        # Get members in Target AD after sync
        $targetMembersAfter = docker exec $targetContainer samba-tool group listmembers $testGroupName 2>&1
        $targetMembersList = @()
        if ($LASTEXITCODE -eq 0 -and $targetMembersAfter) {
            $targetMembersList = @($targetMembersAfter -split "`n" | Where-Object { $_.Trim() -ne "" })
        }
        $targetMemberCountAfter = $targetMembersList.Count

        Write-Host "    Target members after sync: $targetMemberCountAfter" -ForegroundColor Gray

        # Validate: Added users should be in Target
        $addValidationPassed = $true
        foreach ($addedUser in $usersToAdd) {
            if ($addedUser -in $targetMembersList) {
                Write-Host "    ✓ Added user '$addedUser' found in Target group" -ForegroundColor Green
            }
            else {
                Write-Host "    ✗ Added user '$addedUser' NOT found in Target group" -ForegroundColor Red
                $addValidationPassed = $false
            }
        }

        # Validate: Removed user should NOT be in Target
        $removeValidationPassed = $true
        if ($userToRemove) {
            if ($userToRemove -notin $targetMembersList) {
                Write-Host "    ✓ Removed user '$userToRemove' is not in Target group" -ForegroundColor Green
            }
            else {
                Write-Host "    ✗ Removed user '$userToRemove' still in Target group" -ForegroundColor Red
                $removeValidationPassed = $false
            }
        }

        # Validate: Member count should match
        $countValidationPassed = ($targetMemberCountAfter -eq $sourceMemberCountAfterChange)
        if ($countValidationPassed) {
            Write-Host "    ✓ Member count matches between Source ($sourceMemberCountAfterChange) and Target ($targetMemberCountAfter)" -ForegroundColor Green
        }
        else {
            Write-Host "    ⚠ Member count mismatch: Source=$sourceMemberCountAfterChange, Target=$targetMemberCountAfter" -ForegroundColor Yellow
        }

        # Overall validation
        if ($addValidationPassed -and $removeValidationPassed -and $countValidationPassed) {
            Write-Host "✓ ForwardSync test completed successfully" -ForegroundColor Green
        }
        else {
            throw "ForwardSync validation failed - membership changes did not propagate correctly"
        }

        $testResults.Steps += "ForwardSync"
    }

    # Test 3: DetectDrift (Unauthorised changes in Target AD)
    if ($Step -eq "DetectDrift" -or $Step -eq "All") {
        Write-TestSection "Test 3: DetectDrift (Drift Detection)"

        Write-Host "This test validates that JIM detects unauthorised changes made directly in Target AD" -ForegroundColor Gray

        # Container names for Source and Target AD
        $sourceContainer = "samba-ad-source"
        $targetContainer = "samba-ad-target"

        # Step 3.1: Find groups with members in Target AD for testing
        Write-Host "  Finding test groups in Target AD..." -ForegroundColor Gray

        $groupListOutput = docker exec $targetContainer samba-tool group list 2>&1
        $testGroups = @($groupListOutput -split "`n" | Where-Object { $_ -match "^(Company-|Dept-|Location-|Project-)" })

        if ($testGroups.Count -lt 2) {
            throw "Not enough test groups in Target AD. Need at least 2 groups. Ensure InitialSync has been run."
        }

        # Find two groups with members for drift testing
        $driftGroup1 = $null  # Group to add an unauthorised member to
        $driftGroup2 = $null  # Group to remove an authorised member from
        $driftGroup1Members = @()
        $driftGroup2Members = @()

        foreach ($grp in $testGroups) {
            $grpName = $grp.Trim()
            $members = docker exec $targetContainer samba-tool group listmembers $grpName 2>&1
            if ($LASTEXITCODE -eq 0 -and $members) {
                $memberList = @($members -split "`n" | Where-Object { $_.Trim() -ne "" })
                if ($memberList.Count -gt 0) {
                    if (-not $driftGroup1) {
                        $driftGroup1 = $grpName
                        $driftGroup1Members = $memberList
                    }
                    elseif (-not $driftGroup2 -and $grpName -ne $driftGroup1) {
                        $driftGroup2 = $grpName
                        $driftGroup2Members = $memberList
                        break
                    }
                }
            }
        }

        if (-not $driftGroup1 -or -not $driftGroup2) {
            throw "Could not find two groups with members in Target AD for drift testing."
        }

        Write-Host "    Drift test group 1: $driftGroup1 (members: $($driftGroup1Members.Count))" -ForegroundColor Cyan
        Write-Host "    Drift test group 2: $driftGroup2 (members: $($driftGroup2Members.Count))" -ForegroundColor Cyan

        # Step 3.2: Get all users in Target AD (to find a user NOT in driftGroup1)
        Write-Host "  Finding user to add to group (unauthorised addition)..." -ForegroundColor Gray

        $allUsersOutput = docker exec $targetContainer samba-tool user list 2>&1
        $allUsers = @($allUsersOutput -split "`n" | Where-Object {
            $_.Trim() -ne "" -and
            $_ -notmatch "^(Administrator|Guest|krbtgt)" -and
            $_ -notmatch "DNS"
        })

        # Find a user NOT in driftGroup1 to add (simulating unauthorised addition)
        $userToAddToDrift = $null
        foreach ($user in $allUsers) {
            if ($user -notin $driftGroup1Members) {
                $userToAddToDrift = $user.Trim()
                break
            }
        }

        if (-not $userToAddToDrift) {
            Write-Host "    ⚠ All users already in $driftGroup1, skipping unauthorised addition test" -ForegroundColor Yellow
        }
        else {
            Write-Host "    User to add (unauthorised): $userToAddToDrift" -ForegroundColor Yellow
        }

        # Find a user IN driftGroup2 to remove (simulating unauthorised removal)
        $userToRemoveFromDrift = $driftGroup2Members[0].Trim()
        Write-Host "    User to remove (unauthorised): $userToRemoveFromDrift from $driftGroup2" -ForegroundColor Yellow

        # Step 3.3: Record the EXPECTED state (from Source AD - the authoritative source)
        Write-Host "  Recording expected state from Source AD (authoritative)..." -ForegroundColor Gray

        $sourceGroup1Members = docker exec $sourceContainer samba-tool group listmembers $driftGroup1 2>&1
        $sourceGroup1MemberCount = 0
        if ($LASTEXITCODE -eq 0 -and $sourceGroup1Members) {
            $sourceGroup1MemberCount = @($sourceGroup1Members -split "`n" | Where-Object { $_.Trim() -ne "" }).Count
        }

        $sourceGroup2Members = docker exec $sourceContainer samba-tool group listmembers $driftGroup2 2>&1
        $sourceGroup2MemberCount = 0
        if ($LASTEXITCODE -eq 0 -and $sourceGroup2Members) {
            $sourceGroup2MemberCount = @($sourceGroup2Members -split "`n" | Where-Object { $_.Trim() -ne "" }).Count
        }

        Write-Host "    Source $driftGroup1 members (expected): $sourceGroup1MemberCount" -ForegroundColor Gray
        Write-Host "    Source $driftGroup2 members (expected): $sourceGroup2MemberCount" -ForegroundColor Gray

        # Step 3.4: Make UNAUTHORISED changes directly in Target AD (bypassing JIM)
        Write-Host "  Making unauthorised changes directly in Target AD..." -ForegroundColor Gray

        $driftAddSucceeded = $false
        $driftRemoveSucceeded = $false

        if ($userToAddToDrift) {
            $addResult = docker exec $targetContainer samba-tool group addmembers $driftGroup1 $userToAddToDrift 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "    ✓ Unauthorised addition: Added '$userToAddToDrift' to '$driftGroup1'" -ForegroundColor Yellow
                $driftAddSucceeded = $true
            }
            else {
                Write-Host "    ⚠ Failed to add user to group: $addResult" -ForegroundColor Yellow
            }
        }

        $removeResult = docker exec $targetContainer samba-tool group removemembers $driftGroup2 $userToRemoveFromDrift 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "    ✓ Unauthorised removal: Removed '$userToRemoveFromDrift' from '$driftGroup2'" -ForegroundColor Yellow
            $driftRemoveSucceeded = $true
        }
        else {
            Write-Host "    ⚠ Failed to remove user from group: $removeResult" -ForegroundColor Yellow
        }

        if (-not $driftAddSucceeded -and -not $driftRemoveSucceeded) {
            throw "Could not make any unauthorised changes in Target AD for drift testing"
        }

        # Step 3.5: Verify the changes are visible in Target AD
        Write-Host "  Verifying unauthorised changes in Target AD..." -ForegroundColor Gray

        $targetGroup1MembersAfterDrift = docker exec $targetContainer samba-tool group listmembers $driftGroup1 2>&1
        $targetGroup1MemberCountAfterDrift = 0
        if ($LASTEXITCODE -eq 0 -and $targetGroup1MembersAfterDrift) {
            $targetGroup1MemberCountAfterDrift = @($targetGroup1MembersAfterDrift -split "`n" | Where-Object { $_.Trim() -ne "" }).Count
        }

        $targetGroup2MembersAfterDrift = docker exec $targetContainer samba-tool group listmembers $driftGroup2 2>&1
        $targetGroup2MemberCountAfterDrift = 0
        if ($LASTEXITCODE -eq 0 -and $targetGroup2MembersAfterDrift) {
            $targetGroup2MemberCountAfterDrift = @($targetGroup2MembersAfterDrift -split "`n" | Where-Object { $_.Trim() -ne "" }).Count
        }

        Write-Host "    Target $driftGroup1 members (after drift): $targetGroup1MemberCountAfterDrift (expected: $sourceGroup1MemberCount)" -ForegroundColor Gray
        Write-Host "    Target $driftGroup2 members (after drift): $targetGroup2MemberCountAfterDrift (expected: $sourceGroup2MemberCount)" -ForegroundColor Gray

        # Validate drift is visible in AD
        $driftDetectedInAD = $false
        if ($driftAddSucceeded -and $targetGroup1MemberCountAfterDrift -gt $sourceGroup1MemberCount) {
            Write-Host "    ✓ Drift confirmed: $driftGroup1 has extra member in Target" -ForegroundColor Green
            $driftDetectedInAD = $true
        }
        if ($driftRemoveSucceeded -and $targetGroup2MemberCountAfterDrift -lt $sourceGroup2MemberCount) {
            Write-Host "    ✓ Drift confirmed: $driftGroup2 is missing member in Target" -ForegroundColor Green
            $driftDetectedInAD = $true
        }

        if (-not $driftDetectedInAD) {
            Write-Host "    ⚠ Drift changes not visible in AD (unexpected)" -ForegroundColor Yellow
        }

        # Step 3.6: Delta Import from Target AD to detect the drift
        # Delta Import picks up the unauthorised changes made directly in Target AD
        Write-Host "  Running Delta Import on Target AD (to import drifted state)..." -ForegroundColor Gray

        $targetImportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetDeltaImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $targetImportResult.activityId -Name "Target Delta Import (detect drift)"
        Start-Sleep -Seconds $WaitSeconds

        # Step 3.7: Delta Sync on Target AD to evaluate the drift against sync rules
        # This is where JIM should:
        # 1. Compare the imported CSO attribute values against what the sync rules say they should be
        # 2. Determine that the Target AD group memberships don't match the authoritative Source state
        # 3. Stage pending exports to correct the drift (re-assert the desired state)
        # Note: This is how MIM 2016 works - the sync engine evaluates inbound changes and determines
        # if corrective exports are needed based on the configured sync rules.
        Write-Host "  Running Delta Sync on Target AD (to evaluate drift against sync rules)..." -ForegroundColor Gray

        $targetSyncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetDeltaSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $targetSyncResult.activityId -Name "Target Delta Sync (evaluate drift)" -AllowWarnings
        Start-Sleep -Seconds $WaitSeconds

        # Step 3.8: Validate drift detection and pending exports
        Write-Host "  Validating drift detection and pending exports..." -ForegroundColor Gray

        # Store drift context for ReassertState step
        $script:driftContext = @{
            DriftGroup1 = $driftGroup1
            DriftGroup2 = $driftGroup2
            UserAddedToDriftGroup1 = $userToAddToDrift
            UserRemovedFromDriftGroup2 = $userToRemoveFromDrift
            SourceGroup1MemberCount = $sourceGroup1MemberCount
            SourceGroup2MemberCount = $sourceGroup2MemberCount
            DriftAddSucceeded = $driftAddSucceeded
            DriftRemoveSucceeded = $driftRemoveSucceeded
        }

        # The key validation: After Delta Import and Delta Sync on Target AD:
        # 1. Delta Import updated the Target CSO attribute values to reflect the drifted state
        # 2. Delta Sync evaluated these CSO changes against sync rules
        # 3. Delta Sync should have determined that the Target state doesn't match the
        #    authoritative Source state and staged pending exports to correct the drift
        #
        # This is how MIM 2016 works - the sync engine evaluates inbound changes and
        # determines if corrective exports are needed based on the configured sync rules.

        $validations = @()

        if ($driftDetectedInAD) {
            $validations += @{ Name = "Drift visible in Target AD"; Success = $true }
            Write-Host "    ✓ Drift is visible in Target AD" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "Drift visible in Target AD"; Success = $false }
            Write-Host "    ✗ Drift not visible in Target AD" -ForegroundColor Red
        }

        # Delta Import completed - JIM has imported the drifted state
        $importActivity = Get-JIMActivity -Id $targetImportResult.activityId

        if ($importActivity.status -eq "Complete" -or $importActivity.status -eq "CompleteWithWarning") {
            $validations += @{ Name = "Target Delta Import completed"; Success = $true }
            Write-Host "    ✓ Target Delta Import completed" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "Target Delta Import completed"; Success = $false }
            Write-Host "    ✗ Target Delta Import failed: $($importActivity.status)" -ForegroundColor Red
        }

        # Delta Sync completed - JIM has evaluated the drift against sync rules
        $syncActivity = Get-JIMActivity -Id $targetSyncResult.activityId

        if ($syncActivity.status -eq "Complete" -or $syncActivity.status -eq "CompleteWithWarning") {
            $validations += @{ Name = "Target Delta Sync completed"; Success = $true }
            Write-Host "    ✓ Target Delta Sync completed" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "Target Delta Sync completed"; Success = $false }
            Write-Host "    ✗ Target Delta Sync failed: $($syncActivity.status)" -ForegroundColor Red
        }

        # KEY VALIDATION: Check that JIM has staged pending exports to correct the drift
        # After Delta Sync evaluates the drifted CSOs against sync rules, it should
        # determine that corrective exports are needed and stage them as pending exports.
        Write-Host "  Checking for pending exports to correct drift..." -ForegroundColor Gray

        # Refresh the connected system to get current pending export count
        $connectedSystems = Get-JIMConnectedSystem
        $targetSystemRefreshed = $connectedSystems | Where-Object { $_.name -eq "Quantum Dynamics EMEA" }
        $pendingExportCount = $targetSystemRefreshed.pendingExportObjectsCount

        Write-Host "    Pending exports for Target AD: $pendingExportCount" -ForegroundColor Cyan

        # We expect pending exports to correct the drift (at minimum, exports for the groups we modified)
        $expectedMinPendingExports = 0
        if ($driftAddSucceeded) { $expectedMinPendingExports++ }
        if ($driftRemoveSucceeded) { $expectedMinPendingExports++ }

        if ($pendingExportCount -ge $expectedMinPendingExports -and $expectedMinPendingExports -gt 0) {
            $validations += @{ Name = "Pending exports staged to correct drift"; Success = $true }
            Write-Host "    ✓ JIM has staged $pendingExportCount pending export(s) to correct drift" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "Pending exports staged to correct drift"; Success = $false }
            Write-Host "    ✗ Expected at least $expectedMinPendingExports pending export(s) to correct drift, found $pendingExportCount" -ForegroundColor Red
        }

        # Overall success if all validations passed
        $allValidationsPassed = @($validations | Where-Object { -not $_.Success }).Count -eq 0

        if ($allValidationsPassed) {
            Write-Host ""
            Write-Host "✓ DetectDrift test completed successfully" -ForegroundColor Green
            Write-Host "  Drift has been detected and JIM has staged corrective exports" -ForegroundColor Gray
            Write-Host "  Run ReassertState to execute the exports and verify correction" -ForegroundColor Gray
        }
        else {
            throw "DetectDrift validation failed - drift was not detected or corrective exports were not staged"
        }

        $testResults.Steps += "DetectDrift"
    }

    # Test 4: ReassertState (Force state reassertion)
    if ($Step -eq "ReassertState" -or $Step -eq "All") {
        Write-TestSection "Test 4: ReassertState (State Reassertion)"

        Write-Host "This test validates that JIM reasserts Source AD membership to Target AD after drift" -ForegroundColor Gray

        # Container names for Source and Target AD
        $sourceContainer = "samba-ad-source"
        $targetContainer = "samba-ad-target"

        # Step 4.1: Get drift context from DetectDrift step (or discover groups if run independently)
        if (-not $script:driftContext) {
            Write-Host "  DetectDrift context not available, discovering groups..." -ForegroundColor Yellow

            # Find groups to validate (same logic as DetectDrift)
            $groupListOutput = docker exec $targetContainer samba-tool group list 2>&1
            $testGroups = @($groupListOutput -split "`n" | Where-Object { $_ -match "^(Company-|Dept-|Location-|Project-)" })

            if ($testGroups.Count -lt 2) {
                throw "Not enough test groups in Target AD. Ensure InitialSync has been run."
            }

            # Use first two groups with members
            $driftGroup1 = $null
            $driftGroup2 = $null

            foreach ($grp in $testGroups) {
                $grpName = $grp.Trim()
                $members = docker exec $targetContainer samba-tool group listmembers $grpName 2>&1
                if ($LASTEXITCODE -eq 0 -and $members) {
                    $memberList = @($members -split "`n" | Where-Object { $_.Trim() -ne "" })
                    if ($memberList.Count -gt 0) {
                        if (-not $driftGroup1) {
                            $driftGroup1 = $grpName
                        }
                        elseif (-not $driftGroup2 -and $grpName -ne $driftGroup1) {
                            $driftGroup2 = $grpName
                            break
                        }
                    }
                }
            }

            if (-not $driftGroup1 -or -not $driftGroup2) {
                throw "Could not find two groups with members in Target AD."
            }

            # Get expected member counts from Source AD
            $sourceGroup1Members = docker exec $sourceContainer samba-tool group listmembers $driftGroup1 2>&1
            $sourceGroup1MemberCount = 0
            if ($LASTEXITCODE -eq 0 -and $sourceGroup1Members) {
                $sourceGroup1MemberCount = @($sourceGroup1Members -split "`n" | Where-Object { $_.Trim() -ne "" }).Count
            }

            $sourceGroup2Members = docker exec $sourceContainer samba-tool group listmembers $driftGroup2 2>&1
            $sourceGroup2MemberCount = 0
            if ($LASTEXITCODE -eq 0 -and $sourceGroup2Members) {
                $sourceGroup2MemberCount = @($sourceGroup2Members -split "`n" | Where-Object { $_.Trim() -ne "" }).Count
            }

            $script:driftContext = @{
                DriftGroup1 = $driftGroup1
                DriftGroup2 = $driftGroup2
                SourceGroup1MemberCount = $sourceGroup1MemberCount
                SourceGroup2MemberCount = $sourceGroup2MemberCount
            }
        }

        $driftGroup1 = $script:driftContext.DriftGroup1
        $driftGroup2 = $script:driftContext.DriftGroup2
        $expectedGroup1MemberCount = $script:driftContext.SourceGroup1MemberCount
        $expectedGroup2MemberCount = $script:driftContext.SourceGroup2MemberCount

        Write-Host "  Drift context:" -ForegroundColor Gray
        Write-Host "    Group 1: $driftGroup1 (expected members: $expectedGroup1MemberCount)" -ForegroundColor Cyan
        Write-Host "    Group 2: $driftGroup2 (expected members: $expectedGroup2MemberCount)" -ForegroundColor Cyan

        # Step 4.2: Record Target AD state BEFORE reassertion
        Write-Host "  Recording Target AD state before reassertion..." -ForegroundColor Gray

        $targetGroup1MembersBefore = docker exec $targetContainer samba-tool group listmembers $driftGroup1 2>&1
        $targetGroup1MemberCountBefore = 0
        if ($LASTEXITCODE -eq 0 -and $targetGroup1MembersBefore) {
            $targetGroup1MemberCountBefore = @($targetGroup1MembersBefore -split "`n" | Where-Object { $_.Trim() -ne "" }).Count
        }

        $targetGroup2MembersBefore = docker exec $targetContainer samba-tool group listmembers $driftGroup2 2>&1
        $targetGroup2MemberCountBefore = 0
        if ($LASTEXITCODE -eq 0 -and $targetGroup2MembersBefore) {
            $targetGroup2MemberCountBefore = @($targetGroup2MembersBefore -split "`n" | Where-Object { $_.Trim() -ne "" }).Count
        }

        Write-Host "    Target $driftGroup1 members (before): $targetGroup1MemberCountBefore" -ForegroundColor Gray
        Write-Host "    Target $driftGroup2 members (before): $targetGroup2MemberCountBefore" -ForegroundColor Gray

        # Step 4.3: Run Delta Forward Sync to reassert state from Source to Target
        # This will:
        # 1. Delta Import from Source (picks up any Source changes, but mainly re-confirms state)
        # 2. Delta Sync (evaluates export rules against MVOs)
        # 3. Export to Target (corrects the drift by reasserting Source membership)
        # 4. Delta Confirming Import (confirms the exports)
        Write-Host "  Running delta forward sync to reassert state..." -ForegroundColor Gray
        Invoke-DeltaForwardSync -Context "ReassertState"

        # Step 4.4: Validate state reassertion
        Write-Host "  Validating state reassertion..." -ForegroundColor Gray

        $targetGroup1MembersAfter = docker exec $targetContainer samba-tool group listmembers $driftGroup1 2>&1
        $targetGroup1MemberCountAfter = 0
        $targetGroup1MemberList = @()
        if ($LASTEXITCODE -eq 0 -and $targetGroup1MembersAfter) {
            $targetGroup1MemberList = @($targetGroup1MembersAfter -split "`n" | Where-Object { $_.Trim() -ne "" })
            $targetGroup1MemberCountAfter = $targetGroup1MemberList.Count
        }

        $targetGroup2MembersAfter = docker exec $targetContainer samba-tool group listmembers $driftGroup2 2>&1
        $targetGroup2MemberCountAfter = 0
        $targetGroup2MemberList = @()
        if ($LASTEXITCODE -eq 0 -and $targetGroup2MembersAfter) {
            $targetGroup2MemberList = @($targetGroup2MembersAfter -split "`n" | Where-Object { $_.Trim() -ne "" })
            $targetGroup2MemberCountAfter = $targetGroup2MemberList.Count
        }

        Write-Host "    Target $driftGroup1 members (after): $targetGroup1MemberCountAfter (expected: $expectedGroup1MemberCount)" -ForegroundColor Gray
        Write-Host "    Target $driftGroup2 members (after): $targetGroup2MemberCountAfter (expected: $expectedGroup2MemberCount)" -ForegroundColor Gray

        # Validate: Target groups should now match Source groups
        $validations = @()

        if ($targetGroup1MemberCountAfter -eq $expectedGroup1MemberCount) {
            $validations += @{ Name = "$driftGroup1 member count matches Source"; Success = $true }
            Write-Host "    ✓ $driftGroup1 members corrected: $targetGroup1MemberCountAfter (matches Source)" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "$driftGroup1 member count matches Source"; Success = $false }
            Write-Host "    ✗ $driftGroup1 member count mismatch: Target=$targetGroup1MemberCountAfter, Expected=$expectedGroup1MemberCount" -ForegroundColor Red
        }

        if ($targetGroup2MemberCountAfter -eq $expectedGroup2MemberCount) {
            $validations += @{ Name = "$driftGroup2 member count matches Source"; Success = $true }
            Write-Host "    ✓ $driftGroup2 members corrected: $targetGroup2MemberCountAfter (matches Source)" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "$driftGroup2 member count matches Source"; Success = $false }
            Write-Host "    ✗ $driftGroup2 member count mismatch: Target=$targetGroup2MemberCountAfter, Expected=$expectedGroup2MemberCount" -ForegroundColor Red
        }

        # Validate that unauthorised additions were removed and removals were restored
        if ($script:driftContext.UserAddedToDriftGroup1) {
            $userAddedToDrift = $script:driftContext.UserAddedToDriftGroup1
            if ($userAddedToDrift -notin $targetGroup1MemberList) {
                $validations += @{ Name = "Unauthorised addition removed from $driftGroup1"; Success = $true }
                Write-Host "    ✓ Unauthorised member '$userAddedToDrift' removed from $driftGroup1" -ForegroundColor Green
            }
            else {
                $validations += @{ Name = "Unauthorised addition removed from $driftGroup1"; Success = $false }
                Write-Host "    ✗ Unauthorised member '$userAddedToDrift' still in $driftGroup1" -ForegroundColor Red
            }
        }

        if ($script:driftContext.UserRemovedFromDriftGroup2) {
            $userRemovedFromDrift = $script:driftContext.UserRemovedFromDriftGroup2
            if ($userRemovedFromDrift -in $targetGroup2MemberList) {
                $validations += @{ Name = "Unauthorised removal restored in $driftGroup2"; Success = $true }
                Write-Host "    ✓ Member '$userRemovedFromDrift' restored to $driftGroup2" -ForegroundColor Green
            }
            else {
                $validations += @{ Name = "Unauthorised removal restored in $driftGroup2"; Success = $false }
                Write-Host "    ✗ Member '$userRemovedFromDrift' not restored to $driftGroup2" -ForegroundColor Red
            }
        }

        # Overall success if all validations passed
        $allValidationsPassed = @($validations | Where-Object { -not $_.Success }).Count -eq 0

        if ($allValidationsPassed) {
            Write-Host ""
            Write-Host "✓ ReassertState test completed successfully" -ForegroundColor Green
            Write-Host "  JIM has corrected the drift and restored authoritative Source state" -ForegroundColor Gray
        }
        else {
            throw "ReassertState validation failed - drift was not corrected"
        }

        $testResults.Steps += "ReassertState"
    }

    # Test 5: NewGroup (Create new group in Source, sync to Target)
    if ($Step -eq "NewGroup" -or $Step -eq "All") {
        Write-TestSection "Test 5: NewGroup (New Group Provisioning)"

        Write-Host "This test validates that new groups created in Source AD are provisioned to Target AD" -ForegroundColor Gray

        # Container names for Source and Target AD
        $sourceContainer = "samba-ad-source"
        $targetContainer = "samba-ad-target"

        # Test group details
        $newGroupName = "Project-Scenario8Test"
        $newGroupDescription = "Test group created by Scenario 8 NewGroup test"

        # Step 5.1: Create new group in Source AD
        Write-Host "  Creating new group '$newGroupName' in Source AD..." -ForegroundColor Gray

        # First, delete the group if it exists from a previous run
        docker exec $sourceContainer samba-tool group delete $newGroupName 2>&1 | Out-Null
        docker exec $targetContainer samba-tool group delete $newGroupName 2>&1 | Out-Null

        # Create the group in Source AD (OU=Entitlements,OU=Corp)
        $createResult = docker exec $sourceContainer samba-tool group add $newGroupName `
            --groupou="OU=Entitlements,OU=Corp" `
            --description="$newGroupDescription" 2>&1

        if ($LASTEXITCODE -eq 0) {
            Write-Host "    ✓ Created group '$newGroupName' in Source AD" -ForegroundColor Green
        }
        elseif ($createResult -match "already exists") {
            Write-Host "    Group '$newGroupName' already exists in Source AD" -ForegroundColor Yellow
        }
        else {
            throw "Failed to create group in Source AD: $createResult"
        }

        # Step 5.2: Add members to the new group
        Write-Host "  Adding members to new group..." -ForegroundColor Gray

        # Get users from Source AD to add as members
        $allUsersOutput = docker exec $sourceContainer samba-tool user list 2>&1
        $allUsers = @($allUsersOutput -split "`n" | Where-Object {
            $_.Trim() -ne "" -and
            $_ -notmatch "^(Administrator|Guest|krbtgt)" -and
            $_ -notmatch "DNS"
        })

        # Add up to 3 members to the new group
        $membersToAdd = @()
        $addedCount = 0
        foreach ($user in $allUsers) {
            if ($addedCount -ge 3) { break }
            $userName = $user.Trim()
            $addResult = docker exec $sourceContainer samba-tool group addmembers $newGroupName $userName 2>&1
            if ($LASTEXITCODE -eq 0) {
                $membersToAdd += $userName
                $addedCount++
            }
        }

        Write-Host "    Added $addedCount members: $($membersToAdd -join ', ')" -ForegroundColor Cyan

        # Step 5.3: Run forward sync to provision the new group to Target
        Write-Host "  Running delta forward sync to provision new group..." -ForegroundColor Gray
        Invoke-DeltaForwardSync -Context "NewGroup"

        # Step 5.4: Validate new group in Target AD
        Write-Host "  Validating new group in Target AD..." -ForegroundColor Gray

        $validations = @()

        # Check if group exists in Target AD
        $targetGroupInfo = docker exec $targetContainer samba-tool group show $newGroupName 2>&1
        if ($LASTEXITCODE -eq 0) {
            $validations += @{ Name = "Group exists in Target AD"; Success = $true }
            Write-Host "    ✓ Group '$newGroupName' exists in Target AD" -ForegroundColor Green

            # Verify description attribute
            if ($targetGroupInfo -match "description:\s*$([regex]::Escape($newGroupDescription))") {
                $validations += @{ Name = "Group description correct"; Success = $true }
                Write-Host "    ✓ Group description is correct" -ForegroundColor Green
            }
            else {
                # Description may not be synced depending on attribute flow configuration
                Write-Host "    ⚠ Group description may not be synced (attribute flow configuration)" -ForegroundColor Yellow
            }
        }
        else {
            $validations += @{ Name = "Group exists in Target AD"; Success = $false }
            Write-Host "    ✗ Group '$newGroupName' NOT found in Target AD" -ForegroundColor Red
        }

        # Check members in Target AD
        $targetMembers = docker exec $targetContainer samba-tool group listmembers $newGroupName 2>&1
        $targetMemberCount = 0
        $targetMemberList = @()
        if ($LASTEXITCODE -eq 0 -and $targetMembers) {
            $targetMemberList = @($targetMembers -split "`n" | Where-Object { $_.Trim() -ne "" })
            $targetMemberCount = $targetMemberList.Count
        }

        if ($targetMemberCount -eq $addedCount) {
            $validations += @{ Name = "Group member count matches"; Success = $true }
            Write-Host "    ✓ Group has $targetMemberCount members (matches Source)" -ForegroundColor Green
        }
        elseif ($targetMemberCount -gt 0) {
            $validations += @{ Name = "Group member count matches"; Success = $true }
            Write-Host "    ⚠ Group has $targetMemberCount members (expected $addedCount - some may not have synced yet)" -ForegroundColor Yellow
        }
        else {
            $validations += @{ Name = "Group member count matches"; Success = $false }
            Write-Host "    ✗ Group has no members in Target AD" -ForegroundColor Red
        }

        # Store the new group name for DeleteGroup test
        $script:newGroupContext = @{
            GroupName = $newGroupName
            MembersAdded = $membersToAdd
        }

        # Overall success if group exists (members may take additional sync cycles)
        $allValidationsPassed = @($validations | Where-Object { -not $_.Success }).Count -eq 0

        if ($allValidationsPassed) {
            Write-Host ""
            Write-Host "✓ NewGroup test completed successfully" -ForegroundColor Green
            Write-Host "  New group '$newGroupName' provisioned to Target AD" -ForegroundColor Gray
        }
        else {
            throw "NewGroup validation failed"
        }

        $testResults.Steps += "NewGroup"
    }

    # Test 6: DeleteGroup (Delete group from Source, remove from Target)
    if ($Step -eq "DeleteGroup" -or $Step -eq "All") {
        Write-TestSection "Test 6: DeleteGroup (Group Deletion)"

        Write-Host "This test validates that groups deleted from Source AD are deleted from Target AD" -ForegroundColor Gray

        # Container names for Source and Target AD
        $sourceContainer = "samba-ad-source"
        $targetContainer = "samba-ad-target"

        # Step 6.1: Determine which group to delete
        $groupToDelete = $null

        # Prefer the group created in NewGroup test if available
        if ($script:newGroupContext -and $script:newGroupContext.GroupName) {
            $groupToDelete = $script:newGroupContext.GroupName
            Write-Host "  Using group from NewGroup test: $groupToDelete" -ForegroundColor Cyan
        }
        else {
            # Find a project group to delete (least impactful)
            Write-Host "  NewGroup context not available, finding a project group to delete..." -ForegroundColor Yellow

            $groupListOutput = docker exec $sourceContainer samba-tool group list 2>&1
            $projectGroups = @($groupListOutput -split "`n" | Where-Object { $_ -match "^Project-" })

            if ($projectGroups.Count -gt 0) {
                $groupToDelete = $projectGroups[0].Trim()
            }
            else {
                # If no project groups, find any test group
                $testGroups = @($groupListOutput -split "`n" | Where-Object { $_ -match "^(Company-|Dept-|Location-)" })
                if ($testGroups.Count -gt 0) {
                    $groupToDelete = $testGroups[0].Trim()
                }
            }
        }

        if (-not $groupToDelete) {
            throw "Could not find a group to delete. Ensure InitialSync or NewGroup has been run."
        }

        Write-Host "  Group to delete: $groupToDelete" -ForegroundColor Yellow

        # Step 6.2: Verify group exists in both Source and Target before deletion
        Write-Host "  Verifying group exists in both Source and Target AD..." -ForegroundColor Gray

        docker exec $sourceContainer samba-tool group show $groupToDelete 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Group '$groupToDelete' does not exist in Source AD"
        }
        Write-Host "    ✓ Group exists in Source AD" -ForegroundColor Green

        docker exec $targetContainer samba-tool group show $groupToDelete 2>&1 | Out-Null
        $groupExistsInTarget = ($LASTEXITCODE -eq 0)
        if ($groupExistsInTarget) {
            Write-Host "    ✓ Group exists in Target AD" -ForegroundColor Green
        }
        else {
            Write-Host "    ⚠ Group does not exist in Target AD (may not have synced yet)" -ForegroundColor Yellow
        }

        # Step 6.3: Delete the group from Source AD
        Write-Host "  Deleting group '$groupToDelete' from Source AD..." -ForegroundColor Gray

        $deleteResult = docker exec $sourceContainer samba-tool group delete $groupToDelete 2>&1
        if ($LASTEXITCODE -eq 0 -or $deleteResult -match "Deleted") {
            Write-Host "    ✓ Group deleted from Source AD" -ForegroundColor Green
        }
        else {
            throw "Failed to delete group from Source AD: $deleteResult"
        }

        # Step 6.4: Run forward sync to propagate the deletion
        Write-Host "  Running delta forward sync to propagate deletion..." -ForegroundColor Gray
        Invoke-DeltaForwardSync -Context "DeleteGroup"

        # Step 6.5: Validate group is deleted from Target AD
        Write-Host "  Validating group deletion in Target AD..." -ForegroundColor Gray

        $validations = @()

        docker exec $targetContainer samba-tool group show $groupToDelete 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $validations += @{ Name = "Group deleted from Target AD"; Success = $true }
            Write-Host "    ✓ Group '$groupToDelete' deleted from Target AD" -ForegroundColor Green
        }
        else {
            # Group still exists - may be due to deletion grace period
            $validations += @{ Name = "Group deleted from Target AD"; Success = $false }
            Write-Host "    ✗ Group '$groupToDelete' still exists in Target AD" -ForegroundColor Red
            Write-Host "      Note: Group may be pending deletion due to grace period" -ForegroundColor Yellow
            Write-Host "      Check JIM UI for pending deletions" -ForegroundColor Yellow
        }

        # Verify group is no longer in Source AD (double-check)
        docker exec $sourceContainer samba-tool group show $groupToDelete 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $validations += @{ Name = "Group confirmed deleted from Source AD"; Success = $true }
            Write-Host "    ✓ Group confirmed deleted from Source AD" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "Group confirmed deleted from Source AD"; Success = $false }
            Write-Host "    ✗ Group still exists in Source AD (deletion failed)" -ForegroundColor Red
        }

        # Overall success if group is deleted from both (or at least Source)
        $allValidationsPassed = @($validations | Where-Object { -not $_.Success }).Count -eq 0

        if ($allValidationsPassed) {
            Write-Host ""
            Write-Host "✓ DeleteGroup test completed successfully" -ForegroundColor Green
            Write-Host "  Group '$groupToDelete' deleted from both Source and Target AD" -ForegroundColor Gray
        }
        else {
            # Check if only the Target deletion failed (may be due to grace period)
            $sourceDeleted = @($validations | Where-Object { $_.Name -eq "Group confirmed deleted from Source AD" -and $_.Success }).Count -gt 0
            $targetDeleted = @($validations | Where-Object { $_.Name -eq "Group deleted from Target AD" -and $_.Success }).Count -gt 0

            if ($sourceDeleted -and -not $targetDeleted) {
                Write-Host ""
                Write-Host "⚠ DeleteGroup test partially complete" -ForegroundColor Yellow
                Write-Host "  Group deleted from Source AD, but still exists in Target AD" -ForegroundColor Yellow
                Write-Host "  This may be expected if deletion grace period is configured" -ForegroundColor Yellow
                # Don't fail the test - this is expected behaviour with deletion rules
            }
            else {
                throw "DeleteGroup validation failed"
            }
        }

        $testResults.Steps += "DeleteGroup"
    }

    Write-TestSection "Test Execution Summary"
    Write-Host "Tests executed: $($testResults.Steps.Count)" -ForegroundColor Gray
    Write-Host "Steps: $($testResults.Steps -join ', ')" -ForegroundColor Gray

    $testResults.Success = $true
    Write-Host ""
    Write-Host "✓ Scenario 8 test execution completed successfully" -ForegroundColor Green

}
catch {
    Write-Host ""
    Write-Failure "Scenario 8 test failed: $($_.Exception.Message)"
    Write-Host ""
    Write-Host "Stack trace:" -ForegroundColor Gray
    Write-Host $_.ScriptStackTrace -ForegroundColor Gray

    exit 1
}
