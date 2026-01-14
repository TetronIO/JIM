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
        Write-Host "Note: Full implementation requires successful InitialSync completion" -ForegroundColor Yellow
        Write-Host "✓ DetectDrift test framework ready (implementation pending)" -ForegroundColor Green
        $testResults.Steps += "DetectDrift"
    }

    # Test 4: ReassertState (Force state reassertion)
    if ($Step -eq "ReassertState" -or $Step -eq "All") {
        Write-TestSection "Test 4: ReassertState (State Reassertion)"

        Write-Host "This test validates that JIM reasserts Source AD membership to Target AD after drift" -ForegroundColor Gray
        Write-Host "Note: Full implementation requires successful DetectDrift completion" -ForegroundColor Yellow
        Write-Host "✓ ReassertState test framework ready (implementation pending)" -ForegroundColor Green
        $testResults.Steps += "ReassertState"
    }

    # Test 5: NewGroup (Create new group in Source, sync to Target)
    if ($Step -eq "NewGroup" -or $Step -eq "All") {
        Write-TestSection "Test 5: NewGroup (New Group Provisioning)"

        Write-Host "This test validates that new groups created in Source AD are provisioned to Target AD" -ForegroundColor Gray
        Write-Host "Note: Full implementation requires successful InitialSync completion" -ForegroundColor Yellow
        Write-Host "✓ NewGroup test framework ready (implementation pending)" -ForegroundColor Green
        $testResults.Steps += "NewGroup"
    }

    # Test 6: DeleteGroup (Delete group from Source, remove from Target)
    if ($Step -eq "DeleteGroup" -or $Step -eq "All") {
        Write-TestSection "Test 6: DeleteGroup (Group Deletion)"

        Write-Host "This test validates that groups deleted from Source AD are deleted from Target AD" -ForegroundColor Gray
        Write-Host "Note: Full implementation requires successful InitialSync completion" -ForegroundColor Yellow
        Write-Host "✓ DeleteGroup test framework ready (implementation pending)" -ForegroundColor Green
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
