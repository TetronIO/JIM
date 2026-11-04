# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 009: Partition-scoped import run profiles

.DESCRIPTION
    Validates that partition-scoped import run profiles correctly filter to the specified partition,
    and that unscoped import run profiles import from all selected partitions.

    For Samba AD (single domain partition): scoped and unscoped imports see the same data,
    proving the scoped code path works without regressions.

    For OpenLDAP (two suffixes — Yellowstone + Glitterband): true partition filtering is tested.
    Scoped imports to each partition return only that partition's users, while unscoped import
    returns users from both partitions.

    Tests:
    1. ScopedImport   - Full Import scoped to primary partition
    2. ScopedImport2  - (OpenLDAP only) Full Import scoped to second partition
    3. UnscopedImport - Full Import without PartitionId (all selected partitions)
    4. Comparison     - Verify counts are consistent (scoped subsets sum to unscoped total)

    Then the Delta Import steps (#2715: each partition keeps its own Delta Import watermark). Every run they make
    is checked against the watermarks JIM holds before and after it: the run starts each partition it reads from
    that partition's watermark, records one for each, and leaves every other partition's exactly as it was.
    5. DeltaBaseline       - after the Full Imports, both Delta Import Run Profiles read changes, with no warning
    6. CrossPartitionDelta - change users in each partition; Delta Import (Scoped) imports the first partition's
                             changes only, and Delta Import (Scoped - Second) then imports the second's (the #2715
                             regression: it used to start from where the first run ended, past them)
    7. ScopedFullThenDelta - change users in the second partition; Full Import (Scoped); the second partition's
                             Delta Import still imports them
    8. UnscopedDelta       - change users in each partition; Delta Import (Unscoped) imports both, with a read range
                             point per partition; the scoped Delta Imports then import nothing
    9. PartitionDeletion   - delete a user in the second partition; only the second partition's Delta Import
                             marks it deleted
    10. ReselectedPartition - deselect and reselect the second partition, which clears its watermark; Delta Import
                             (Unscoped) reads every object instead, warns naming that partition and deletes nothing;
                             the second partition's Delta Import then reads changes again

    OpenLDAP and 389 Directory Server run every Delta Import step. Samba AD (and the Active Directory lab) has one
    domain partition, so it runs DeltaBaseline, CrossPartitionDelta and UnscopedDelta only, with Delta Import
    (Unscoped) as the second Run Profile: both target the one partition and share its watermark, which proves the
    Update Sequence Number path end to end.

    Last, on every directory type (#2723: an object is recorded in the partition it is in, not the one its first
    import's Run Profile targeted):
    11. UnscopedCreatedDeletion - add a user to each partition; Full Import (Unscoped) imports them, and each
                             partition's object count then includes its user; delete them in the directory; Full Import
                             (Scoped) marks only its own partition's user deleted, and Full Import (Scoped - Second)
                             the other (Samba AD: one user, one partition, one scoped Full Import)

.PARAMETER Step
    Which test step to execute. A Delta Import step, or UnscopedCreatedDeletion, on its own first runs the scoped Full
    Imports it builds on.

.PARAMETER Template
    Data scale template. The Delta Import steps on OpenLDAP and 389 Directory Server need five users in each
    partition, so Micro or larger; Samba AD uses fixed test users whatever the template.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait between steps (default: 0)

.PARAMETER DirectoryConfig
    Directory-specific configuration hashtable from Get-DirectoryConfig

.EXAMPLE
    ./Invoke-Scenario-009-PartitionScopedImports.ps1 -Step All -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario-009-PartitionScopedImports.ps1 -Step All -ApiKey "jim_..." -DirectoryConfig (Get-DirectoryConfig -DirectoryType OpenLDAP)
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("ScopedImport", "UnscopedImport", "Comparison", "DeltaBaseline", "CrossPartitionDelta", "ScopedFullThenDelta",
        "UnscopedDelta", "PartitionDeletion", "ReselectedPartition", "UnscopedCreatedDeletion", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Hard-fail: the long-tail templates are Scenario 008 only.
$longTailTemplates = @("Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")
if ($Template -in $longTailTemplates) {
    throw "Template '$Template' is only valid for Scenario 008 (Cross-Domain Entitlement Sync). Use 'Scale100k50Groups' or smaller for this scenario."
}

# Default to SambaAD Primary if no config provided
if (-not $DirectoryConfig) {
    . "$PSScriptRoot/../utils/Test-Helpers.ps1"
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"
. "$PSScriptRoot/../utils/Directory-Helpers.ps1"

$isRfcDirectory = Test-IsRfcDirectory $DirectoryConfig
$systemName = if ($isRfcDirectory) { "Partition Test OpenLDAP" } else { "Partition Test AD" }

Write-TestSection "Scenario 009: Partition-Scoped Imports ($systemName)"
Write-Host "Step:          $Step" -ForegroundColor Gray
Write-Host "Directory:     $($DirectoryConfig.DirectoryType)" -ForegroundColor Gray
Write-Host "Template:      $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Partition-Scoped Imports"
    Template = $Template
    Steps = @()
    Success = $false
}

# For SambaAD, we create fixed test users. For OpenLDAP, we use the pre-populated data.
# Eight, so CrossPartitionDelta can change two disjoint sets of four, one before each of its two Delta Imports.
$sambaTestUsers = @(
    @{ Sam = "partition.test1"; FirstName = "Partition"; LastName = "TestOne"; Department = "Engineering" },
    @{ Sam = "partition.test2"; FirstName = "Partition"; LastName = "TestTwo"; Department = "Marketing" },
    @{ Sam = "partition.test3"; FirstName = "Partition"; LastName = "TestThree"; Department = "Finance" },
    @{ Sam = "partition.test4"; FirstName = "Partition"; LastName = "TestFour"; Department = "Engineering" },
    @{ Sam = "partition.test5"; FirstName = "Partition"; LastName = "TestFive"; Department = "Marketing" },
    @{ Sam = "partition.test6"; FirstName = "Partition"; LastName = "TestSix"; Department = "Finance" },
    @{ Sam = "partition.test7"; FirstName = "Partition"; LastName = "TestSeven"; Department = "Engineering" },
    @{ Sam = "partition.test8"; FirstName = "Partition"; LastName = "TestEight"; Department = "Marketing" }
)

$testUsersOU = "OU=TestUsers"

# The Delta Import steps (#2715) each directory type runs. OpenLDAP and 389 Directory Server host two partitions
# (Yellowstone and Glitterband), each with its own scoped Run Profiles, so they run every one. Samba AD and Active
# Directory have one domain partition: the second Delta Import Run Profile there is Delta Import (Unscoped), which
# targets that same partition, so the two share one watermark, and the steps that need a second partition do not
# apply.
$deltaStepsByDirectoryFamily = @{
    Rfc      = @("DeltaBaseline", "CrossPartitionDelta", "ScopedFullThenDelta", "UnscopedDelta", "PartitionDeletion", "ReselectedPartition")
    AdFamily = @("DeltaBaseline", "CrossPartitionDelta", "UnscopedDelta")
}
$deltaSteps = if ($isRfcDirectory) { $deltaStepsByDirectoryFamily.Rfc } else { $deltaStepsByDirectoryFamily.AdFamily }
if ($Step -in $deltaStepsByDirectoryFamily.Rfc -and $Step -notin $deltaSteps) {
    throw "Step '$Step' needs a second partition, which $($DirectoryConfig.DirectoryType) does not have in this scenario. It runs these Delta Import steps: $($deltaSteps -join ', ')."
}
$runsDeltaSteps = ($Step -eq "All") -or ($Step -in $deltaSteps)

# The steps after the Full Import ones: the Delta Import steps this directory type runs, then UnscopedCreatedDeletion
# (#2723), which every directory type runs, one user per partition.
$partitionSteps = @($deltaSteps) + @("UnscopedCreatedDeletion")
$runsPartitionSteps = ($Step -eq "All") -or ($Step -in $partitionSteps)

# How many users each Delta Import step changes in a partition: one more than the Delta Import Run Profiles' page size
# (Setup-Scenario-009.ps1), so the change read of every Delta Import crosses a Connector page boundary (on Samba AD,
# which the LDAP Connector reads unpaged, every read comes back in one page).
$changedUsersPerPartition = 4

# Calculate expected user counts for OpenLDAP multi-partition testing
$expectedYellowstoneUsers = $null
$expectedGlitterbandUsers = $null
$expectedTotalUsers = $null

if ($isRfcDirectory) {
    $scale = Get-TemplateScale -Template $Template
    $expectedYellowstoneUsers = [Math]::Ceiling($scale.Users / 2)
    $expectedGlitterbandUsers = [Math]::Floor($scale.Users / 2)
    $expectedTotalUsers = $scale.Users
    Write-Host "Expected users: Yellowstone=$expectedYellowstoneUsers, Glitterband=$expectedGlitterbandUsers, Total=$expectedTotalUsers" -ForegroundColor Gray
    Write-Host ""
}

# The users the Delta Import steps change, by partition. On OpenLDAP and 389 Directory Server the scenario adds five of
# its own to each partition (four to change, a fifth for PartitionDeletion to delete), so the steps work at any
# template, Nano included, and change only objects the scenario owns; on Samba AD they are the fixed test users.
$rfcDeltaUsersPerPartition = $changedUsersPerPartition + 1
$deltaUsers = @{ Primary = @(); Second = @() }
$secondPartitionConfig = $null
if ($isRfcDirectory) {
    # Glitterband's administrator, for the second partition's users: on OpenLDAP each suffix has its own rootDN; on
    # 389 Directory Server both are the Directory Manager.
    $secondPartitionConfig = $DirectoryConfig.Clone()
    $secondPartitionConfig.BindDN = $DirectoryConfig.SecondBindDN
    $secondPartitionConfig.BaseDN = $DirectoryConfig.SecondSuffix
}
$deltaUserPartitions = @(
    @{ Key = "Primary"; Label = "yellowstone"; Config = $DirectoryConfig; Suffix = $DirectoryConfig.BaseDN }
    @{ Key = "Second"; Label = "glitterband"; Config = $secondPartitionConfig; Suffix = $DirectoryConfig['SecondSuffix'] }
)

# The users UnscopedCreatedDeletion has added to the directory and not yet deleted, for the cleanup to remove should
# the step stop part-way.
$unscopedCreatedUsers = [System.Collections.Generic.List[hashtable]]::new()

# ---------------------------------------------------------------------------------------------
# Delta Import step helpers (#2715)
# ---------------------------------------------------------------------------------------------

function Get-PartitionUserDn {
    <#
    .SYNOPSIS
        The distinguished names of the users in an RFC directory partition's People container, sorted, read as that
        partition's administrator.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$Suffix
    )

    $entries = @(Find-DirectoryEntry -DirectoryConfig $Config -BaseDn "ou=People,$Suffix" -Scope sub `
        -Filter "(objectClass=$($DirectoryConfig.UserObjectClass))" -Attributes @('dn'))
    return @($entries | ForEach-Object { [string]$_['dn'] } | Sort-Object)
}

function Set-PartitionUserDisplayName {
    <#
    .SYNOPSIS
        Writes "<Prefix> <n>" to each user's displayName in one ldapmodify (one modify per user, in the order given)
        and returns the values written, sorted: the names the next import of their partition must report as updated.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string[]]$Dn,

        [Parameter(Mandatory=$true)]
        [string]$Prefix
    )

    $values = [System.Collections.Generic.List[string]]::new()
    $ldif = [System.Text.StringBuilder]::new()
    for ($index = 0; $index -lt $Dn.Count; $index++) {
        $value = "$Prefix $($index + 1)"
        $values.Add($value)
        [void]$ldif.Append("dn: $($Dn[$index])`nchangetype: modify`nreplace: displayName`ndisplayName: $value`n-`n`n")
    }

    $result = Invoke-DirectoryLdif -DirectoryConfig $Config -Ldif $ldif.ToString() -Operation modify
    if (-not $result.Success) {
        throw "Could not change the display names of $($Dn.Count) user(s) for '$Prefix': $($result.Output)"
    }
    Write-Host "  Changed $($Dn.Count) user(s) in the directory: $Prefix 1..$($Dn.Count)" -ForegroundColor Gray
    return @($values | Sort-Object)
}

function Invoke-ImportRunProfile {
    <#
    .SYNOPSIS
        Runs one of the Connected System's import Run Profiles and returns what it did, with the partition watermarks
        JIM held immediately before and after it.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$ProfileName
    )

    $runProfile = $profiles | Where-Object { $_.name -eq $ProfileName } | Select-Object -First 1
    if (-not $runProfile) {
        throw "Run Profile '$ProfileName' not found. Ensure Setup-Scenario-009.ps1 completed successfully."
    }

    $before = Get-PartitionDeltaImportWatermark -ConnectedSystemId $ldapSystem.id
    Write-Host "Running $ProfileName..." -ForegroundColor Gray
    $run = Start-JIMRunProfile -ConnectedSystemId $ldapSystem.id -RunProfileId $runProfile.id -Wait -PassThru
    Start-Sleep -Seconds $WaitSeconds
    $after = Get-PartitionDeltaImportWatermark -ConnectedSystemId $ldapSystem.id

    $activityId = "$($run.activityId)"
    $counts = Get-ImportObjectCount -ActivityId $activityId
    Write-Host "  Added $($counts.Added), updated $($counts.Updated), deleted $($counts.Deleted), unchanged $($counts.Unchanged)" -ForegroundColor Gray

    return @{
        Name       = $ProfileName
        ActivityId = $activityId
        Activity   = Get-JIMActivity -Id $activityId
        Before     = $before
        After      = $after
        Counts     = $counts
    }
}

function Format-PartitionName {
    param([int]$PartitionId)
    $partition = $partitionsById[$PartitionId]
    return $(if ($partition) { "'$($partition.name)'" } else { "partition $PartitionId" })
}

function Assert-RunKeptToItsPartitions {
    <#
    .SYNOPSIS
        The #2715 contract for one run: it started each partition it read from the watermark JIM held for it, recorded
        a watermark for each of them (what JIM now holds), and left every other partition's watermark as it was.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Run,

        [Parameter(Mandatory=$true)]
        [int[]]$TargetPartitionIds
    )

    $label = $Run.Name
    $expectedIds = ($TargetPartitionIds | Sort-Object) -join ','

    $from = Get-ActivityPartitionWatermark -Activity $Run.Activity -Direction From
    if ($null -eq $from) { throw "$label recorded no partition it started from (importPartitionWatermarksFrom is empty)" }
    $fromIds = (@($from.Keys) | Sort-Object) -join ','
    if ($fromIds -ne $expectedIds) { throw "$label started from the watermarks of partitions [$fromIds]; expected [$expectedIds]" }

    $to = Get-ActivityPartitionWatermark -Activity $Run.Activity -Direction To
    if ($null -eq $to) { throw "$label recorded no partition watermark (importPartitionWatermarksTo is empty)" }
    $toIds = (@($to.Keys) | Sort-Object) -join ','
    if ($toIds -ne $expectedIds) { throw "$label recorded watermarks for partitions [$toIds]; expected [$expectedIds]" }

    foreach ($partitionId in $TargetPartitionIds) {
        $name = Format-PartitionName -PartitionId $partitionId
        if ($from[$partitionId] -ne $Run.Before[$partitionId]) {
            throw "$label started $name from '$($from[$partitionId])', but JIM held '$($Run.Before[$partitionId])' for it"
        }
        if ([string]::IsNullOrEmpty($to[$partitionId])) {
            throw "$label recorded no watermark for $name"
        }
        if ($Run.After[$partitionId] -ne $to[$partitionId]) {
            throw "$label recorded '$($to[$partitionId])' for $name, but JIM now holds '$($Run.After[$partitionId])'"
        }
    }

    foreach ($partitionId in @($Run.Before.Keys | Where-Object { $_ -notin $TargetPartitionIds })) {
        if ($Run.After[$partitionId] -ne $Run.Before[$partitionId]) {
            throw "$label does not read $(Format-PartitionName -PartitionId $partitionId), but moved its watermark from '$($Run.Before[$partitionId])' to '$($Run.After[$partitionId])' (#2715)"
        }
    }

    Write-Host "  OK $label started from and recorded the watermark of $(($TargetPartitionIds | ForEach-Object { Format-PartitionName -PartitionId $_ }) -join ' and ') only; every other partition's is unchanged" -ForegroundColor Green
}

function Assert-ImportedExactly {
    <#
    .SYNOPSIS
        What a run imported, exactly: the objects it added, updated and deleted by display name (none unless named),
        and how many it read unchanged (0 for a Delta Import that read only changes; every object for one that read in
        full).

    .PARAMETER AnyUnchanged
        For a Full Import, which reads every object: how many it read unchanged is not checked.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Run,

        [Parameter(Mandatory=$false)]
        [string[]]$Added = @(),

        [Parameter(Mandatory=$false)]
        [string[]]$Updated = @(),

        [Parameter(Mandatory=$false)]
        [string[]]$Deleted = @(),

        [Parameter(Mandatory=$false)]
        [int]$Unchanged = 0,

        [Parameter(Mandatory=$false)]
        [switch]$AnyUnchanged
    )

    $label = $Run.Name
    $counts = $Run.Counts
    if ($counts.Added -ne @($Added).Count) { throw "$label added $($counts.Added) object(s); expected $(@($Added).Count) ($(@($Added) -join ', '))" }
    if ($counts.Updated -ne @($Updated).Count) { throw "$label updated $($counts.Updated) object(s); expected $(@($Updated).Count) ($(@($Updated) -join ', '))" }
    if ($counts.Deleted -ne @($Deleted).Count) { throw "$label deleted $($counts.Deleted) object(s); expected $(@($Deleted).Count) ($(@($Deleted) -join ', '))" }
    if (-not $AnyUnchanged -and $counts.Unchanged -ne $Unchanged) { throw "$label read $($counts.Unchanged) unchanged object(s); expected $Unchanged" }

    if (@($Added).Count -gt 0) {
        $addedNames = @(Get-ActivityItemDisplayName -ActivityId $Run.ActivityId -ObjectChangeType Added)
        $expected = @($Added | Sort-Object)
        if (($addedNames -join '|') -ne ($expected -join '|')) {
            throw "$label added [$($addedNames -join ', ')]; expected [$($expected -join ', ')]"
        }
    }
    if (@($Updated).Count -gt 0) {
        $updatedNames = @(Get-ActivityItemDisplayName -ActivityId $Run.ActivityId -ObjectChangeType Updated)
        $expected = @($Updated | Sort-Object)
        if (($updatedNames -join '|') -ne ($expected -join '|')) {
            throw "$label updated [$($updatedNames -join ', ')]; expected [$($expected -join ', ')]"
        }
    }
    if (@($Deleted).Count -gt 0) {
        $deletedNames = @(Get-ActivityItemDisplayName -ActivityId $Run.ActivityId -ObjectChangeType Deleted)
        $expected = @($Deleted | Sort-Object)
        if (($deletedNames -join '|') -ne ($expected -join '|')) {
            throw "$label deleted [$($deletedNames -join ', ')]; expected [$($expected -join ', ')]"
        }
    }

    $unchangedRead = if ($AnyUnchanged) { $counts.Unchanged } else { $Unchanged }
    Write-Host "  OK $label imported exactly what was expected (added $(@($Added).Count), updated $(@($Updated).Count), deleted $(@($Deleted).Count), unchanged $unchangedRead)" -ForegroundColor Green
}

function Invoke-PartitionStep {
    <#
    .SYNOPSIS
        Runs one of the steps after the Full Import ones (a Delta Import step, or UnscopedCreatedDeletion) when it was
        asked for and applies to this directory type, recording its result.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$true)]
        [scriptblock]$Body
    )

    if (($Step -ne "All" -and $Step -ne $Name) -or $Name -notin $partitionSteps) {
        return
    }

    $kind = if ($Name -in $deltaSteps) { "Delta Import step" } else { "Partition step" }
    Write-TestSection "${kind}: $Name"
    try {
        & $Body
        $testResults.Steps += @{ Name = $Name; Success = $true }
    }
    catch {
        Write-Host "  FAIL $Name`: $_" -ForegroundColor Red
        $testResults.Steps += @{ Name = $Name; Success = $false; Error = $_.ToString() }
    }
}

try {
    # Step 0: Setup
    Write-TestSection "Step 0: Setup and Verification"

    if (-not $ApiKey) {
        throw "API key required for authentication"
    }

    if ($isRfcDirectory) {
        # OpenLDAP: wait for container to be healthy (users already populated by runner)
        Write-Host "Waiting for OpenLDAP to be healthy..." -ForegroundColor Gray
        $maxWaitSeconds = 120
        $elapsed = 0
        $interval = 5
        $containerStatus = ""
        while ($elapsed -lt $maxWaitSeconds) {
            $containerStatus = docker inspect --format='{{.State.Health.Status}}' $DirectoryConfig.ContainerName 2>&1
            if ($containerStatus -eq "healthy") {
                break
            }
            Write-Host "  Status: $containerStatus (waiting... ${elapsed}s / ${maxWaitSeconds}s)" -ForegroundColor Gray
            Start-Sleep -Seconds $interval
            $elapsed += $interval
        }

        if ($containerStatus -ne "healthy") {
            throw "$($DirectoryConfig.ContainerName) container did not become healthy within ${maxWaitSeconds}s (status: $containerStatus)"
        }
        Write-Host "  OK OpenLDAP is healthy" -ForegroundColor Green

        # Verify users exist in both suffixes
        $yellowstoneCount = Get-LDAPUserCount -DirectoryConfig $DirectoryConfig
        Write-Host "  Yellowstone users found: $yellowstoneCount" -ForegroundColor Gray

        if ($yellowstoneCount -lt 1) {
            throw "No users found in Yellowstone suffix — Populate-OpenLDAP.ps1 may not have run"
        }
        Write-Host "  OK OpenLDAP has pre-populated test data" -ForegroundColor Green

        if ($runsDeltaSteps) {
            Write-Host "Creating the Delta Import steps' users in each partition..." -ForegroundColor Gray
            foreach ($partitionUsers in $deltaUserPartitions) {
                $dns = @(1..$rfcDeltaUsersPerPartition | ForEach-Object { "uid=s009-delta-$($partitionUsers.Label)-$_,ou=People,$($partitionUsers.Suffix)" })
                foreach ($dn in $dns) {
                    $removed = Remove-DirectoryEntry -DirectoryConfig $partitionUsers.Config -Dn $dn
                    if (-not $removed.Success) { throw "Could not remove '$dn' left by an earlier run: $($removed.Output)" }
                }

                $ldif = ($dns | ForEach-Object {
                    $uid = ($_ -split ',')[0] -replace '^uid=', ''
                    @("dn: $_", "objectClass: inetOrgPerson", "uid: $uid", "cn: $uid", "sn: Delta", "givenName: Scenario",
                      "displayName: $uid", "departmentNumber: Delta") -join "`n"
                }) -join "`n`n"
                $added = Invoke-DirectoryLdif -DirectoryConfig $partitionUsers.Config -Ldif $ldif -Operation add
                if ($added.Outcome -ne 'Created') { throw "Could not create the Delta Import steps' users in $($partitionUsers.Suffix): $($added.Output)" }

                $deltaUsers[$partitionUsers.Key] = $dns
                Write-Host "  OK Created $($dns.Count) users in $($partitionUsers.Suffix)" -ForegroundColor Green
            }

            # The Full Import steps import these too.
            $expectedYellowstoneUsers += $rfcDeltaUsersPerPartition
            $expectedGlitterbandUsers += $rfcDeltaUsersPerPartition
            $expectedTotalUsers += 2 * $rfcDeltaUsersPerPartition
        }
    }
    else {
        # Samba AD or Active Directory: wait for the directory and create test users
        Write-Host "Waiting for Samba AD primary to be healthy..." -ForegroundColor Gray
        $maxWaitSeconds = 120
        $elapsed = 0
        $interval = 5
        $primaryStatus = ""
        while ($elapsed -lt $maxWaitSeconds) {
            $primaryStatus = Get-DirectoryHealthStatus -DirectoryConfig $DirectoryConfig
            if ($primaryStatus -eq "healthy") {
                break
            }
            Write-Host "  Status: $primaryStatus (waiting... ${elapsed}s / ${maxWaitSeconds}s)" -ForegroundColor Gray
            Start-Sleep -Seconds $interval
            $elapsed += $interval
        }

        if ($primaryStatus -ne "healthy") {
            throw "$($DirectoryConfig.Host) did not become healthy within ${maxWaitSeconds}s (status: $primaryStatus)"
        }
        Write-Host "  OK Samba AD primary is healthy" -ForegroundColor Green

        # Create test users in Samba AD
        Write-Host "Creating test users in Samba AD..." -ForegroundColor Gray

        foreach ($user in $sambaTestUsers) {
            # Delete if exists from previous run
            Remove-DirectoryUser -DirectoryConfig $DirectoryConfig -SamAccountName $user.Sam | Out-Null

            $userDn = "CN=$($user.FirstName) $($user.LastName),$testUsersOU,$($DirectoryConfig.BaseDN)"
            $deltaUsers.Primary += $userDn
            $createResult = New-DirectoryUser -DirectoryConfig $DirectoryConfig `
                -Dn $userDn `
                -SamAccountName $user.Sam `
                -Password "Password123!" `
                -Attributes ([ordered]@{
                    givenName  = $user.FirstName
                    sn         = $user.LastName
                    department = $user.Department
                })

            if ($createResult.Outcome -eq 'Created') {
                Write-Host "  OK Created $($user.Sam)" -ForegroundColor Green
            }
            elseif ($createResult.Outcome -eq 'AlreadyExists') {
                Write-Host "  $($user.Sam) already exists" -ForegroundColor Yellow
            }
            else {
                throw "Failed to create user $($user.Sam): $($createResult.Output)"
            }
        }
    }

    # Run Setup-Scenario-009 to configure JIM
    Write-Host "Running Scenario 009 setup..." -ForegroundColor Gray
    & "$PSScriptRoot/../Setup-Scenario-009.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template -DirectoryConfig $DirectoryConfig

    Write-Host "OK JIM configured for Scenario 009" -ForegroundColor Green

    # Re-import module to ensure we have connection
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Get connected system and run profile IDs
    $connectedSystems = Get-JIMConnectedSystem
    $ldapSystem = $connectedSystems | Where-Object { $_.name -eq $systemName }

    if (-not $ldapSystem) {
        throw "Connected system '$systemName' not found. Ensure Setup-Scenario-009.ps1 completed successfully."
    }

    $profiles = Get-JIMRunProfile -ConnectedSystemId $ldapSystem.id
    $scopedImportProfile = $profiles | Where-Object { $_.name -eq "Full Import (Scoped)" }
    $unscopedImportProfile = $profiles | Where-Object { $_.name -eq "Full Import (Unscoped)" }
    $syncProfile = $profiles | Where-Object { $_.name -eq "Full Synchronisation" }
    $scopedImport2Profile = if ($isRfcDirectory) {
        $profiles | Where-Object { $_.name -eq "Full Import (Scoped - Second)" }
    } else { $null }

    if (-not $scopedImportProfile -or -not $unscopedImportProfile -or -not $syncProfile) {
        throw "Required run profiles not found. Ensure Setup-Scenario-009.ps1 completed successfully."
    }

    if ($isRfcDirectory -and -not $scopedImport2Profile) {
        throw "OpenLDAP second scoped import profile not found. Ensure Setup-Scenario-009.ps1 completed successfully."
    }

    # The partitions the Delta Import steps read, and their Run Profiles. On Samba AD and Active Directory the second
    # Run Profile is Delta Import (Unscoped), which reads the one domain partition the first does.
    $partitions = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id)
    $partitionsById = @{}
    foreach ($partition in $partitions) { $partitionsById[[int]$partition.id] = $partition }
    $selectedPartitions = @($partitions | Where-Object { $_.selected })
    $primaryPartition = $partitions | Where-Object { $_.name -eq $DirectoryConfig.BaseDN -or $_.externalId -eq $DirectoryConfig.BaseDN } | Select-Object -First 1
    if (-not $primaryPartition -and $selectedPartitions.Count -eq 1) { $primaryPartition = $selectedPartitions[0] }
    $secondPartition = if ($isRfcDirectory) {
        $partitions | Where-Object { $_.name -eq $DirectoryConfig.SecondSuffix -or $_.externalId -eq $DirectoryConfig.SecondSuffix } | Select-Object -First 1
    } else { $null }

    $secondDeltaProfileName = if ($isRfcDirectory) { "Delta Import (Scoped - Second)" } else { "Delta Import (Unscoped)" }
    if ($runsPartitionSteps -and (-not $primaryPartition -or ($isRfcDirectory -and -not $secondPartition))) {
        throw "The steps after the Full Imports could not find the partitions Setup-Scenario-009.ps1 selected."
    }
    if ($runsDeltaSteps) {
        foreach ($profileName in @("Delta Import (Scoped)", "Delta Import (Unscoped)", $secondDeltaProfileName)) {
            if (-not ($profiles | Where-Object { $_.name -eq $profileName })) {
                throw "Run Profile '$profileName' not found. Ensure Setup-Scenario-009.ps1 completed successfully."
            }
        }
    }
    $primaryPartitionId = if ($primaryPartition) { [int]$primaryPartition.id } else { 0 }
    $secondPartitionId = if ($secondPartition) { [int]$secondPartition.id } else { 0 }
    $secondRunPartitionId = if ($isRfcDirectory) { $secondPartitionId } else { $primaryPartitionId }

    # A step after the Full Import ones, run on its own, builds on what the Full Import steps leave behind: Connected
    # System Objects stamped with the partition they were imported from, and a watermark for each partition.
    if ($Step -in $partitionSteps) {
        Write-TestSection "Prerequisite: scoped Full Imports for $Step"
        $baselineProfiles = @("Full Import (Scoped)") + $(if ($isRfcDirectory) { @("Full Import (Scoped - Second)") } else { @() })
        foreach ($profileName in $baselineProfiles) {
            $baseline = Invoke-ImportRunProfile -ProfileName $profileName
            Assert-ActivitySuccess -ActivityId $baseline.ActivityId -Name $profileName
        }
    }

    # Track CSO counts from each import for comparison
    $scopedPrimaryCsoAdds = 0
    $scopedSecondCsoAdds = 0

    # Test 1: Scoped Import (primary partition)
    if ($Step -eq "ScopedImport" -or $Step -eq "All") {
        Write-TestSection "Test 1: Scoped Import (primary partition)"

        Write-Host "Running Full Import (Scoped) - primary partition only..." -ForegroundColor Gray

        $importResult = Start-JIMRunProfile -ConnectedSystemId $ldapSystem.id -RunProfileId $scopedImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Scoped)"
        Start-Sleep -Seconds $WaitSeconds

        # Get activity stats to verify objects were imported
        $stats = Get-JIMActivityStats -Id $importResult.activityId
        Write-Host "  CSO adds: $($stats.totalCsoAdds)" -ForegroundColor Gray
        Write-Host "  CSO updates: $($stats.totalCsoUpdates)" -ForegroundColor Gray
        $scopedPrimaryCsoAdds = $stats.totalCsoAdds

        if ($isRfcDirectory) {
            # OpenLDAP: scoped import should only get Yellowstone users
            if ($stats.totalCsoAdds -ge $expectedYellowstoneUsers) {
                Write-Host "  OK Scoped import created $($stats.totalCsoAdds) CSOs (expected >= $expectedYellowstoneUsers from Yellowstone)" -ForegroundColor Green
                $testResults.Steps += @{ Name = "ScopedImport"; Success = $true }
            }
            else {
                Write-Host "  FAIL Expected at least $expectedYellowstoneUsers CSO adds from Yellowstone, got $($stats.totalCsoAdds)" -ForegroundColor Red
                $testResults.Steps += @{ Name = "ScopedImport"; Success = $false; Error = "Expected at least $expectedYellowstoneUsers CSO adds, got $($stats.totalCsoAdds)" }
            }
        }
        else {
            # Samba AD: verify test users were imported
            $minExpected = $sambaTestUsers.Count
            if ($stats.totalCsoAdds -ge $minExpected) {
                Write-Host "  OK Scoped import created at least $minExpected CSOs" -ForegroundColor Green
                $testResults.Steps += @{ Name = "ScopedImport"; Success = $true }
            }
            else {
                Write-Host "  FAIL Expected at least $minExpected CSO adds, got $($stats.totalCsoAdds)" -ForegroundColor Red
                $testResults.Steps += @{ Name = "ScopedImport"; Success = $false; Error = "Expected at least $minExpected CSO adds, got $($stats.totalCsoAdds)" }
            }
        }

        if (-not $isRfcDirectory) {
            # Samba AD: run sync immediately after single scoped import (only one partition)
            Write-Host "Running Full Synchronisation after scoped import..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $ldapSystem.id -RunProfileId $syncProfile.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (after scoped import)"
            Start-Sleep -Seconds $WaitSeconds

            $syncStats = Get-JIMActivityStats -Id $syncResult.activityId
            Write-Host "  Projections: $($syncStats.totalProjections)" -ForegroundColor Gray

            if ($syncStats.totalProjections -ge 1) {
                Write-Host "  OK Metaverse objects projected from scoped import" -ForegroundColor Green
            }
            else {
                Write-Host "  WARNING: Expected projections, got $($syncStats.totalProjections)" -ForegroundColor Yellow
            }
        }
    }

    # Test 1b: Scoped Import - second partition (OpenLDAP only)
    if ($isRfcDirectory -and ($Step -eq "ScopedImport" -or $Step -eq "All")) {
        Write-TestSection "Test 1b: Scoped Import (second partition — Glitterband)"

        Write-Host "Running Full Import (Scoped - Second) - second partition only..." -ForegroundColor Gray

        $importResult = Start-JIMRunProfile -ConnectedSystemId $ldapSystem.id -RunProfileId $scopedImport2Profile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Scoped - Second)"
        Start-Sleep -Seconds $WaitSeconds

        $stats = Get-JIMActivityStats -Id $importResult.activityId
        Write-Host "  CSO adds: $($stats.totalCsoAdds)" -ForegroundColor Gray
        Write-Host "  CSO updates: $($stats.totalCsoUpdates)" -ForegroundColor Gray
        $scopedSecondCsoAdds = $stats.totalCsoAdds

        if ($stats.totalCsoAdds -ge $expectedGlitterbandUsers) {
            Write-Host "  OK Scoped import (second) created $($stats.totalCsoAdds) CSOs (expected >= $expectedGlitterbandUsers from Glitterband)" -ForegroundColor Green
            $testResults.Steps += @{ Name = "ScopedImport2"; Success = $true }
        }
        else {
            Write-Host "  FAIL Expected at least $expectedGlitterbandUsers CSO adds from Glitterband, got $($stats.totalCsoAdds)" -ForegroundColor Red
            $testResults.Steps += @{ Name = "ScopedImport2"; Success = $false; Error = "Expected at least $expectedGlitterbandUsers CSO adds, got $($stats.totalCsoAdds)" }
        }

        # OpenLDAP: run a single Full Sync AFTER both scoped imports complete.
        # This avoids a concurrency issue where running sync between imports can cause
        # stale CSO state on the second sync run.
        Write-Host "Running Full Synchronisation after both scoped imports..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $ldapSystem.id -RunProfileId $syncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (after scoped imports)"
        Start-Sleep -Seconds $WaitSeconds

        $syncStats = Get-JIMActivityStats -Id $syncResult.activityId
        Write-Host "  Projections: $($syncStats.totalProjections)" -ForegroundColor Gray

        if ($syncStats.totalProjections -ge 1) {
            Write-Host "  OK $($syncStats.totalProjections) CSO(s) projected to Metaverse" -ForegroundColor Green
        }
        else {
            Write-Host "  WARNING: Expected at least 1 projection, got $($syncStats.totalProjections)" -ForegroundColor Yellow
        }
    }

    # Test 2: Unscoped Import (all selected partitions)
    if ($Step -eq "UnscopedImport" -or $Step -eq "All") {
        Write-TestSection "Test 2: Unscoped Import (all selected partitions)"

        Write-Host "Running Full Import (Unscoped) - without PartitionId..." -ForegroundColor Gray

        $importResult = Start-JIMRunProfile -ConnectedSystemId $ldapSystem.id -RunProfileId $unscopedImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Unscoped)"
        Start-Sleep -Seconds $WaitSeconds

        $stats = Get-JIMActivityStats -Id $importResult.activityId
        Write-Host "  CSO adds: $($stats.totalCsoAdds)" -ForegroundColor Gray
        Write-Host "  CSO updates: $($stats.totalCsoUpdates)" -ForegroundColor Gray

        if ($isRfcDirectory) {
            # OpenLDAP: after running both scoped imports, CSOs already exist for all users.
            # The unscoped import should report 0 new adds (data unchanged).
            # This proves the unscoped path covers both partitions — if it didn't cover
            # a partition, there would still be un-imported objects from that partition.
            Write-Host "  OK Unscoped import completed (0 new adds expected — all users already imported by scoped runs)" -ForegroundColor Green
            $testResults.Steps += @{ Name = "UnscopedImport"; Success = $true }
        }
        else {
            # Samba AD: single partition, same data as scoped import
            Write-Host "  OK Unscoped import completed successfully (no new objects expected — data unchanged from scoped import)" -ForegroundColor Green
            $testResults.Steps += @{ Name = "UnscopedImport"; Success = $true }
        }
    }

    # Test 3: Comparison and verification
    if ($Step -eq "Comparison" -or $Step -eq "All") {
        Write-TestSection "Test 3: Verification (metaverse consistency and partition isolation)"

        if (-not $isRfcDirectory) {
            # Samba AD: run a final sync to verify consistency after unscoped import
            Write-Host "Running Full Synchronisation after unscoped import..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $ldapSystem.id -RunProfileId $syncProfile.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (after unscoped import)"
            Start-Sleep -Seconds $WaitSeconds

            $syncStats = Get-JIMActivityStats -Id $syncResult.activityId
            Write-Host "  Projections: $($syncStats.totalProjections)" -ForegroundColor Gray
            Write-Host "  OK Full Sync consistent after unscoped import" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Comparison"; Success = $true; Note = "Metaverse consistent after both import paths" }
        }
        else {
            # OpenLDAP: verify partition isolation from import counts
            $totalScopedAdds = $scopedPrimaryCsoAdds + $scopedSecondCsoAdds

            Write-Host "  Partition isolation verification:" -ForegroundColor Cyan
            Write-Host "    Scoped primary (Yellowstone) CSO adds: $scopedPrimaryCsoAdds" -ForegroundColor Gray
            Write-Host "    Scoped second (Glitterband) CSO adds:  $scopedSecondCsoAdds" -ForegroundColor Gray
            Write-Host "    Total from scoped imports:             $totalScopedAdds" -ForegroundColor Gray
            Write-Host "    Expected total users:                  $expectedTotalUsers" -ForegroundColor Gray

            $comparisonSuccess = $true

            # Verify the two scoped imports got different objects (not the same objects twice)
            if ($scopedPrimaryCsoAdds -gt 0 -and $scopedSecondCsoAdds -gt 0) {
                Write-Host "  OK Both partitions produced distinct CSOs" -ForegroundColor Green
            }
            else {
                Write-Host "  FAIL One or both scoped imports produced 0 CSOs — partition filtering may not be working" -ForegroundColor Red
                $comparisonSuccess = $false
            }

            # Verify combined scoped adds equal expected total
            if ($totalScopedAdds -ge $expectedTotalUsers) {
                Write-Host "  OK Combined scoped imports cover all $expectedTotalUsers expected users" -ForegroundColor Green
            }
            else {
                Write-Host "  FAIL Combined scoped imports ($totalScopedAdds) less than expected ($expectedTotalUsers)" -ForegroundColor Red
                $comparisonSuccess = $false
            }

            $testResults.Steps += @{
                Name = "Comparison"
                Success = $comparisonSuccess
                Note = "Yellowstone=$scopedPrimaryCsoAdds, Glitterband=$scopedSecondCsoAdds, Total=$totalScopedAdds (expected $expectedTotalUsers)"
            }
        }
    }

    # ---------------------------------------------------------------------------------------------
    # Delta Import steps (#2715): each partition keeps its own Delta Import watermark
    # ---------------------------------------------------------------------------------------------

    $primaryUsers = @($deltaUsers.Primary)
    $secondUsers = @($deltaUsers.Second)
    $firstChangeSet = @($primaryUsers | Select-Object -First $changedUsersPerPartition)
    $secondChangeSet = @($secondUsers | Select-Object -First $changedUsersPerPartition)

    Invoke-PartitionStep -Name "DeltaBaseline" -Body {
        # The Full Imports recorded a watermark for each partition they read, so both Delta Imports read changes.
        $held = Get-PartitionDeltaImportWatermark -ConnectedSystemId $ldapSystem.id
        foreach ($partitionId in @($primaryPartitionId, $secondRunPartitionId) | Select-Object -Unique) {
            if ([string]::IsNullOrEmpty($held[$partitionId])) {
                throw "The Full Imports recorded no Delta Import watermark for $(Format-PartitionName -PartitionId $partitionId)"
            }
        }
        Write-Host "  OK The Full Imports recorded a watermark for every partition they read" -ForegroundColor Green

        $first = Invoke-ImportRunProfile -ProfileName "Delta Import (Scoped)"
        Assert-ActivitySuccess -ActivityId $first.ActivityId -Name $first.Name
        Assert-ImportedExactly -Run $first
        Assert-RunKeptToItsPartitions -Run $first -TargetPartitionIds @($primaryPartitionId)

        $second = Invoke-ImportRunProfile -ProfileName $secondDeltaProfileName
        Assert-ActivitySuccess -ActivityId $second.ActivityId -Name $second.Name
        Assert-ImportedExactly -Run $second
        Assert-RunKeptToItsPartitions -Run $second -TargetPartitionIds @($secondRunPartitionId)
    }

    Invoke-PartitionStep -Name "CrossPartitionDelta" -Body {
        if ($isRfcDirectory) {
            # The #2715 timeline: the second partition changes first, then the first. A watermark shared by the whole
            # Connected System moved past the second partition's changes when the first partition's Delta Import
            # recorded where it had read to, so the second partition's Delta Import never imported them.
            $secondNames = Set-PartitionUserDisplayName -Config $secondPartitionConfig -Dn $secondChangeSet -Prefix "S009 CrossPartitionDelta Second"
            $primaryNames = Set-PartitionUserDisplayName -Config $DirectoryConfig -Dn $firstChangeSet -Prefix "S009 CrossPartitionDelta Primary"

            $first = Invoke-ImportRunProfile -ProfileName "Delta Import (Scoped)"
            Assert-ActivitySuccess -ActivityId $first.ActivityId -Name $first.Name
            Assert-ImportedExactly -Run $first -Updated $primaryNames

            # What an administrator sees first: the second partition's changes are imported. Checked before either
            # run's watermarks, so a regression reports the changes it loses, not only the watermark it moved.
            $second = Invoke-ImportRunProfile -ProfileName "Delta Import (Scoped - Second)"
            Assert-ActivitySuccess -ActivityId $second.ActivityId -Name $second.Name
            Assert-ImportedExactly -Run $second -Updated $secondNames

            Assert-RunKeptToItsPartitions -Run $first -TargetPartitionIds @($primaryPartitionId)
            Assert-RunKeptToItsPartitions -Run $second -TargetPartitionIds @($secondPartitionId)
        }
        else {
            # One partition, two Run Profiles, one watermark: the second Run Profile starts where the first ended,
            # so it imports the changes made since, and not the first run's again.
            $firstNames = Set-PartitionUserDisplayName -Config $DirectoryConfig -Dn $firstChangeSet -Prefix "S009 CrossPartitionDelta First"
            $first = Invoke-ImportRunProfile -ProfileName "Delta Import (Scoped)"
            Assert-ActivitySuccess -ActivityId $first.ActivityId -Name $first.Name
            Assert-ImportedExactly -Run $first -Updated $firstNames
            Assert-RunKeptToItsPartitions -Run $first -TargetPartitionIds @($primaryPartitionId)

            $laterUsers = @($primaryUsers | Select-Object -Skip $changedUsersPerPartition -First $changedUsersPerPartition)
            $laterNames = Set-PartitionUserDisplayName -Config $DirectoryConfig -Dn $laterUsers -Prefix "S009 CrossPartitionDelta Later"
            $second = Invoke-ImportRunProfile -ProfileName "Delta Import (Unscoped)"
            Assert-ActivitySuccess -ActivityId $second.ActivityId -Name $second.Name
            Assert-ImportedExactly -Run $second -Updated $laterNames
            Assert-RunKeptToItsPartitions -Run $second -TargetPartitionIds @($primaryPartitionId)

            $firstReached = (Get-ActivityPartitionWatermark -Activity $first.Activity -Direction To)[$primaryPartitionId]
            $secondStarted = (Get-ActivityPartitionWatermark -Activity $second.Activity -Direction From)[$primaryPartitionId]
            if ($secondStarted -ne $firstReached) {
                throw "Delta Import (Unscoped) started from '$secondStarted', not from where Delta Import (Scoped) ended ('$firstReached'), though both read the one partition"
            }
            Write-Host "  OK Both Run Profiles share the partition's one watermark" -ForegroundColor Green
        }
    }

    Invoke-PartitionStep -Name "ScopedFullThenDelta" -Body {
        # A Full Import of the first partition records the first partition's watermark only, so the second
        # partition's Delta Import still imports what changed there before it.
        $secondNames = Set-PartitionUserDisplayName -Config $secondPartitionConfig -Dn $secondChangeSet -Prefix "S009 ScopedFullThenDelta Second"

        $full = Invoke-ImportRunProfile -ProfileName "Full Import (Scoped)"
        Assert-ActivitySuccess -ActivityId $full.ActivityId -Name $full.Name
        if ($full.Counts.Added -ne 0 -or $full.Counts.Updated -ne 0 -or $full.Counts.Deleted -ne 0) {
            throw "Full Import (Scoped) changed objects in a partition nothing changed in (added $($full.Counts.Added), updated $($full.Counts.Updated), deleted $($full.Counts.Deleted))"
        }
        Assert-RunKeptToItsPartitions -Run $full -TargetPartitionIds @($primaryPartitionId)

        $delta = Invoke-ImportRunProfile -ProfileName "Delta Import (Scoped - Second)"
        Assert-ActivitySuccess -ActivityId $delta.ActivityId -Name $delta.Name
        Assert-ImportedExactly -Run $delta -Updated $secondNames
        Assert-RunKeptToItsPartitions -Run $delta -TargetPartitionIds @($secondPartitionId)
    }

    Invoke-PartitionStep -Name "UnscopedDelta" -Body {
        # Delta Import (Unscoped) reads every selected partition from its own watermark and records one for each, so
        # the scoped Delta Imports after it find nothing new.
        $expectedNames = @(Set-PartitionUserDisplayName -Config $DirectoryConfig -Dn $firstChangeSet -Prefix "S009 UnscopedDelta Primary")
        $targetIds = @($primaryPartitionId)
        if ($isRfcDirectory) {
            $expectedNames += Set-PartitionUserDisplayName -Config $secondPartitionConfig -Dn $secondChangeSet -Prefix "S009 UnscopedDelta Second"
            $targetIds += $secondPartitionId
        }

        $unscoped = Invoke-ImportRunProfile -ProfileName "Delta Import (Unscoped)"
        Assert-ActivitySuccess -ActivityId $unscoped.ActivityId -Name $unscoped.Name
        Assert-ImportedExactly -Run $unscoped -Updated $expectedNames
        Assert-RunKeptToItsPartitions -Run $unscoped -TargetPartitionIds $targetIds

        # The read range has one point per partition, scoped by the partition's name.
        $expectedScopes = (@($targetIds | ForEach-Object { "$($partitionsById[$_].name)" }) | Sort-Object) -join ' | '
        foreach ($direction in @("From", "To")) {
            $scopes = @(Get-ImportReadRangeScope -Activity $unscoped.Activity -Direction $direction) -join ' | '
            if ($scopes -ne $expectedScopes) {
                throw "Delta Import (Unscoped)'s importReadRange.$($direction.ToLowerInvariant()) has points for [$scopes]; expected one per partition, [$expectedScopes]"
            }
        }
        Write-Host "  OK importReadRange has one point per partition on both sides" -ForegroundColor Green

        $scopedProfiles = @("Delta Import (Scoped)") + $(if ($isRfcDirectory) { @("Delta Import (Scoped - Second)") } else { @() })
        $scopedTargets = @($primaryPartitionId) + $(if ($isRfcDirectory) { @($secondPartitionId) } else { @() })
        for ($index = 0; $index -lt $scopedProfiles.Count; $index++) {
            $scoped = Invoke-ImportRunProfile -ProfileName $scopedProfiles[$index]
            Assert-ActivitySuccess -ActivityId $scoped.ActivityId -Name $scoped.Name
            Assert-ImportedExactly -Run $scoped
            Assert-RunKeptToItsPartitions -Run $scoped -TargetPartitionIds @($scopedTargets[$index])
        }
    }

    Invoke-PartitionStep -Name "PartitionDeletion" -Body {
        # Deleted in the second partition: the first partition's Delta Import does not read the deletion (and so
        # cannot pass it), and the second partition's marks the object deleted (OpenLDAP's access log carries the
        # entry's reqEntryUUID; 389 Directory Server's changelog a copy of the deleted entry).
        $deletedDn = $secondUsers[$changedUsersPerPartition]
        $deletedEntry = Get-DirectoryEntry -DirectoryConfig $secondPartitionConfig -Dn $deletedDn -Attributes @('displayName')
        if (-not $deletedEntry) { throw "The user to delete, '$deletedDn', is not in the directory" }
        $deletedName = [string]@($deletedEntry['displayName'])[0]
        $removed = Remove-DirectoryEntry -DirectoryConfig $secondPartitionConfig -Dn $deletedDn
        if ($removed.Outcome -ne 'Deleted') { throw "Could not delete '$deletedDn': $($removed.Output)" }
        Write-Host "  Deleted $deletedDn ('$deletedName') from the directory" -ForegroundColor Gray

        $first = Invoke-ImportRunProfile -ProfileName "Delta Import (Scoped)"
        Assert-ActivitySuccess -ActivityId $first.ActivityId -Name $first.Name
        Assert-ImportedExactly -Run $first
        Assert-RunKeptToItsPartitions -Run $first -TargetPartitionIds @($primaryPartitionId)

        $second = Invoke-ImportRunProfile -ProfileName "Delta Import (Scoped - Second)"
        Assert-ActivitySuccess -ActivityId $second.ActivityId -Name $second.Name
        Assert-ImportedExactly -Run $second -Deleted @($deletedName)
        Assert-RunKeptToItsPartitions -Run $second -TargetPartitionIds @($secondPartitionId)
    }

    Invoke-PartitionStep -Name "ReselectedPartition" -Body {
        # Deselecting a partition removes its watermark: objects can be judged deleted while it is out of scope, so a
        # reselected partition must not resume from a watermark taken before that. With no run in between, the next
        # Delta Import that reads it falls back to reading every object in its partitions.
        Set-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id -PartitionId $secondPartitionId -Selected $false | Out-Null
        $held = Get-PartitionDeltaImportWatermark -ConnectedSystemId $ldapSystem.id
        if ($null -ne $held[$secondPartitionId]) { throw "Deselecting $(Format-PartitionName -PartitionId $secondPartitionId) left its watermark in place" }
        if ([string]::IsNullOrEmpty($held[$primaryPartitionId])) { throw "Deselecting the second partition removed $(Format-PartitionName -PartitionId $primaryPartitionId)'s watermark too" }
        Set-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id -PartitionId $secondPartitionId -Selected $true | Out-Null
        $held = Get-PartitionDeltaImportWatermark -ConnectedSystemId $ldapSystem.id
        if ($null -ne $held[$secondPartitionId]) { throw "Reselecting $(Format-PartitionName -PartitionId $secondPartitionId) brought a watermark back" }
        Write-Host "  OK Deselecting the second partition removed its watermark, and reselecting it brought none back" -ForegroundColor Green

        $everyUser = @(Get-PartitionUserDn -Config $DirectoryConfig -Suffix $DirectoryConfig.BaseDN) +
            @(Get-PartitionUserDn -Config $secondPartitionConfig -Suffix $DirectoryConfig.SecondSuffix)

        $secondName = "$($secondPartition.name)"
        $fallbackPattern = '^A Delta Import was requested, but JIM holds no Delta Import watermark for the partition ' +
            [regex]::Escape($secondName) + ' to read changes from\. .+'
        $unscoped = Invoke-ImportRunProfile -ProfileName "Delta Import (Unscoped)"
        if ($unscoped.Activity.status -ne 'CompleteWithWarning') {
            throw "Delta Import (Unscoped) ended '$($unscoped.Activity.status)'; expected CompleteWithWarning, for the fallback to reading every object"
        }
        Assert-ActivitySuccess -ActivityId $unscoped.ActivityId -Name $unscoped.Name -AllowedWarningMessagePattern $fallbackPattern
        if ("$($unscoped.Activity.warningMessage)" -match [regex]::Escape("$($primaryPartition.name)")) {
            throw "Delta Import (Unscoped)'s warning names $(Format-PartitionName -PartitionId $primaryPartitionId), which has a watermark: $($unscoped.Activity.warningMessage)"
        }
        # Every object in both partitions read, none of them changed or missing: the earlier Delta Imports missed nothing.
        Assert-ImportedExactly -Run $unscoped -Unchanged $everyUser.Count
        Assert-RunKeptToItsPartitions -Run $unscoped -TargetPartitionIds @($primaryPartitionId, $secondPartitionId)

        $next = Invoke-ImportRunProfile -ProfileName "Delta Import (Scoped - Second)"
        Assert-ActivitySuccess -ActivityId $next.ActivityId -Name $next.Name
        Assert-ImportedExactly -Run $next
        Assert-RunKeptToItsPartitions -Run $next -TargetPartitionIds @($secondPartitionId)
    }

    Invoke-PartitionStep -Name "UnscopedCreatedDeletion" -Body {
        # #2723: an object a Full Import (Unscoped) imports first is recorded in the partition it is in, so the Full
        # Import scoped to that partition counts it and, once it is deleted in the directory, marks it deleted. Such
        # an object used to be recorded in no partition, which every partition-scoped run leaves out, so its deletion
        # was never detected. One new user per partition, so each scoped run must mark its own and no other.
        $newUsers = @(if ($isRfcDirectory) {
            foreach ($partitionUsers in $deltaUserPartitions) {
                $uid = "s009-unscoped-$($partitionUsers.Label)"
                $isPrimary = $partitionUsers.Key -eq "Primary"
                @{
                    Name          = $uid
                    Dn            = "uid=$uid,ou=People,$($partitionUsers.Suffix)"
                    Config        = $partitionUsers.Config
                    PartitionId   = $(if ($isPrimary) { $primaryPartitionId } else { $secondPartitionId })
                    ScopedProfile = $(if ($isPrimary) { "Full Import (Scoped)" } else { "Full Import (Scoped - Second)" })
                }
            }
        }
        else {
            @{
                Name          = "Partition Unscoped"
                Dn            = "CN=Partition Unscoped,$testUsersOU,$($DirectoryConfig.BaseDN)"
                Sam           = "partition.unscoped"
                Config        = $DirectoryConfig
                PartitionId   = $primaryPartitionId
                ScopedProfile = "Full Import (Scoped)"
            }
        })

        foreach ($user in $newUsers) {
            $leftover = Remove-DirectoryEntry -DirectoryConfig $user.Config -Dn $user.Dn
            if (-not $leftover.Success) { throw "Could not remove '$($user.Dn)' left by an earlier run: $($leftover.Output)" }

            $created = if ($isRfcDirectory) {
                $ldif = @("dn: $($user.Dn)", "objectClass: inetOrgPerson", "uid: $($user.Name)", "cn: $($user.Name)", "sn: Unscoped",
                    "givenName: Scenario", "displayName: $($user.Name)", "departmentNumber: Unscoped") -join "`n"
                Invoke-DirectoryLdif -DirectoryConfig $user.Config -Ldif $ldif -Operation add
            }
            else {
                New-DirectoryUser -DirectoryConfig $user.Config -Dn $user.Dn -SamAccountName $user.Sam -Password "Password123!" `
                    -Attributes ([ordered]@{ givenName = "Partition"; sn = "Unscoped"; displayName = $user.Name; department = "Engineering" })
            }
            if ($created.Outcome -ne 'Created') { throw "Could not create '$($user.Dn)': $($created.Output)" }
            $unscopedCreatedUsers.Add($user)
            Write-Host "  Created $($user.Dn) in the directory" -ForegroundColor Gray
        }

        $before = Get-PartitionObjectCount -ConnectedSystemId $ldapSystem.id
        $unscoped = Invoke-ImportRunProfile -ProfileName "Full Import (Unscoped)"
        Assert-ActivitySuccess -ActivityId $unscoped.ActivityId -Name $unscoped.Name
        Assert-ImportedExactly -Run $unscoped -Added @($newUsers | ForEach-Object { $_.Name }) -AnyUnchanged

        # What an administrator sees: each partition's count includes the user the unscoped run created in it, and
        # no other partition's count moves.
        $after = Get-PartitionObjectCount -ConnectedSystemId $ldapSystem.id
        foreach ($partitionId in @($after.Keys)) {
            $placedHere = @($newUsers | Where-Object { $_.PartitionId -eq $partitionId })
            $expected = $before[$partitionId] + $placedHere.Count
            if ($after[$partitionId] -ne $expected) {
                $expectedUsers = if ($placedHere.Count -gt 0) { " (with $(($placedHere | ForEach-Object { "'$($_.Name)'" }) -join ', '))" } else { "" }
                throw "After $($unscoped.Name), Get-JIMConnectedSystemObject -Count -PartitionId $partitionId counts $($after[$partitionId]) object(s) in $(Format-PartitionName -PartitionId $partitionId), $($before[$partitionId]) before it; expected $expected$expectedUsers"
            }
        }
        Write-Host "  OK Each partition's object count includes the user $($unscoped.Name) created in it" -ForegroundColor Green

        foreach ($user in $newUsers) {
            $removed = Remove-DirectoryEntry -DirectoryConfig $user.Config -Dn $user.Dn
            if ($removed.Outcome -ne 'Deleted') { throw "Could not delete '$($user.Dn)': $($removed.Output)" }
            Write-Host "  Deleted $($user.Dn) from the directory" -ForegroundColor Gray
        }
        $unscopedCreatedUsers.Clear()

        # Each partition's Full Import marks its own partition's user deleted, and leaves the other's to its own run.
        foreach ($user in $newUsers) {
            $scoped = Invoke-ImportRunProfile -ProfileName $user.ScopedProfile
            Assert-ActivitySuccess -ActivityId $scoped.ActivityId -Name $scoped.Name
            Assert-ImportedExactly -Run $scoped -Deleted @($user.Name) -AnyUnchanged
        }
    }

    # Calculate overall success
    $failedSteps = @($testResults.Steps | Where-Object { $_.Success -eq $false })
    $testResults.Success = ($failedSteps.Count -eq 0)
}
catch {
    Write-Host ""
    Write-Host "FAIL Test failed with error:" -ForegroundColor Red
    Write-Host "  $_" -ForegroundColor Red
    Write-Host ""
    $testResults.Steps += @{ Name = "Setup"; Success = $false; Error = $_.ToString() }
}
finally {
    if (-not $isRfcDirectory) {
        # Clean up test users from Samba AD (OpenLDAP uses pre-populated data — no cleanup needed)
        Write-Host ""
        Write-Host "Cleaning up test users..." -ForegroundColor Gray
        foreach ($user in $sambaTestUsers) {
            Remove-DirectoryUser -DirectoryConfig $DirectoryConfig -SamAccountName $user.Sam | Out-Null
        }
        Write-Host "  OK Test users cleaned up" -ForegroundColor Green
    }
    elseif (@($deltaUsers.Primary).Count + @($deltaUsers.Second).Count -gt 0) {
        # The pre-populated data stays; the users the Delta Import steps added go (one is already deleted).
        Write-Host ""
        Write-Host "Cleaning up the Delta Import steps' users..." -ForegroundColor Gray
        foreach ($partitionUsers in $deltaUserPartitions) {
            foreach ($dn in @($deltaUsers[$partitionUsers.Key])) {
                Remove-DirectoryEntry -DirectoryConfig $partitionUsers.Config -Dn $dn | Out-Null
            }
        }
        Write-Host "  OK Delta Import steps' users cleaned up" -ForegroundColor Green
    }

    if ($unscopedCreatedUsers.Count -gt 0) {
        # UnscopedCreatedDeletion stopped before deleting the users it added.
        Write-Host ""
        Write-Host "Cleaning up UnscopedCreatedDeletion's users..." -ForegroundColor Gray
        foreach ($user in $unscopedCreatedUsers) {
            Remove-DirectoryEntry -DirectoryConfig $user.Config -Dn $user.Dn | Out-Null
        }
        Write-Host "  OK UnscopedCreatedDeletion's users cleaned up" -ForegroundColor Green
    }
}

# Summary
Write-TestSection "Test Results Summary"

$passedCount = @($testResults.Steps | Where-Object { $_.Success -eq $true }).Count
$failedCount = @($testResults.Steps | Where-Object { $_.Success -eq $false }).Count
$totalCount = @($testResults.Steps).Count

Write-Host "Scenario: $($testResults.Scenario)" -ForegroundColor Cyan
Write-Host "Directory: $($DirectoryConfig.DirectoryType) $(if ($isRfcDirectory) { '(multi-partition)' } else { '(single partition)' })" -ForegroundColor Cyan
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
    Write-Host "OK All Scenario 009 tests passed!" -ForegroundColor Green
    exit 0
}
else {
    Write-Host ""
    Write-Host "FAIL Some tests failed" -ForegroundColor Red
    exit 1
}
