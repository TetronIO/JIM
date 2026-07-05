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
    6. AssertedNullOverridesSurvivor - "Null is a value" is set on the Primary Job Title
       mapping, then Frank's (S14-5) Primary Job Title is withdrawn (entry remains). Because
       the flag is set, JIM asserts null rather than falling back to Secondary's distinct
       "Architect (Secondary)" value; the Primary Full Synchronisation Activity records an
       AssertedNull sync outcome. Dave (S14-3) is the control: his unaffected Primary Job Title
       proves the flag has no collateral effect on other joined, in-scope contributors.
    7. NotJoinedNoOpinion - a brand-new user, Grace (S14-6, "Grace Green"), is added to the
       Secondary suffix ONLY (no Primary counterpart exists at all). Even with NullIsValue set
       on Primary's Job Title mapping, Primary has no joined CSO for Grace, so its rule is
       RuleNotApplicable ("no opinion") and never engages; Secondary contributes her Job Title
       and Description in full. This is the HR-migration cell of the tri-state matrix.
    8. MidLifeJoinBlanksClear - Grace (S14-6) subsequently joins the Primary suffix too, via an
       entry that omits `title` entirely. Her Primary CSO joins her EXISTING Metaverse Object
       (proven via a second lookup, not a duplicate projection); because Primary's Job Title
       mapping now has NullIsValue set and supplies no value, her Job Title flips from
       Secondary's value to an asserted null with Primary provenance (a new join's blank clears
       a previously-contributed value). Description, which has no NullIsValue set, wins to
       Primary normally on the same join.
    9. MvaNullIsValueAssertsEmptySet - "Null is a value" is set on the Primary Other
       Telephones mapping (a multi-valued attribute), then Frank's (S14-5) Primary
       telephoneNumber values are withdrawn entirely. The asserted null collapses to the
       Metaverse's usual single NullValue marker row (not a per-value marker for each of the
       two numbers Frank used to have), with Secondary's numbers completely absent (no
       fallback). Dave (S14-3) is the control.

    10. DisabledRuleNoOpinion - the Primary import Synchronisation Rule is disabled outright. A Full
        Synchronisation (Secondary) alone re-elects Dave's (S14-3) Description AND Job Title to
        Secondary in the same run: a disabled rule's mapping is excluded from the Attribute
        Priority contributor cache entirely, so it is treated as no opinion, not a stuck
        last-written value, and "Null is a value" on the disabled rule's Job Title mapping has no
        bearing (never consulted for an excluded mapping). Primary is then re-enabled and a Full
        Synchronisation (Primary) retakes both attributes, restoring the inherited end-state.

    11. PriorityReorderPropagation - Description's priority is reordered to Secondary=1/Primary=2.
        Delta Synchronisation of both systems, with no staged import changes, leaves Dave's
        Description untouched (apply-only propagation: Delta Synchronisation with nothing modified
        since the last sync processes no Connected System Objects at all). A Full Synchronisation
        (Secondary) then re-resolves every joined object against the new order, handing Dave's
        Description to Secondary. The order is restored to Primary=1/Secondary=2 and a Full
        Synchronisation (Primary) retakes it, restoring the inherited end-state.

    A third planned cell, OutOfScopeNoOpinion (excluding a subject from the Primary rule's scope via
    a Scoping Criteria Group and expecting a hand-over to Secondary, mirroring RecallReElection's "no
    opinion" re-election), was investigated and DROPPED: the engine does not currently compose scope
    exit with Attribute Priority re-election. HandleCsoOutOfScopeAsync's Disconnect branch
    (src/JIM.Worker/Processors/SyncTaskProcessorBase.cs, "Break the join between CSO and MVO") marks
    the leaving system's contributed attribute values for removal but never calls
    ReElectSurvivingContributorsAsync, unlike the structurally equivalent CSO-obsoletion path
    (ProcessObsoleteConnectedSystemObjectAsync, which does call it immediately after the same kind of
    removal marking). A scope exit under the default InboundOutOfScopeAction=Disconnect therefore
    blanks the attribute instead of handing it to a surviving lower-priority contributor. Writing a
    step that asserted a hand-over would fail against the real engine; writing one that asserted the
    blank instead would misrepresent a "no opinion, hand over" test as passing coverage for what is
    actually an unresolved composition gap between two features (see
    engineering/plans/doing/ATTRIBUTE_PRIORITY.md Phase 4, "Object moves into/out of a scoped rule's
    coverage: authority transfers on next sync", itself still unchecked). Reported to the user rather
    than coded around.

    Step composition under -Step All: RecallReElection through NoContributorCleared were each
    given a distinct subject (Alice, Bob, Carol, Erin) precisely so they compose safely when run
    back-to-back after BaselineResolution; none of them touches a user another step depends on.
    Full Import/Full Synchronisation are always run per-system (Primary or Secondary, whichever
    that step mutated), so a later step's full-system re-sync of an earlier step's mutated user is
    idempotent (no further change, no repeat recall/withdrawal outcome).

    AssertedNullOverridesSurvivor through MvaNullIsValueAssertsEmptySet (Phase C) use Frank
    (S14-5, previously untouched by Phases A/B) and a brand-new user, Grace (S14-6), rather than
    reusing Alice/Bob/Carol/Erin: those four carry Phase B end-state (Alice/Bob deleted from
    Primary; Carol/Erin's Primary Description withdrawn) that Phase C's assertions do not need to
    reason about on top of the NullIsValue tri-state. Dave (S14-3) continues to serve as the
    untouched control subject started by BaselineResolution. Every Phase C step that changes
    Attribute Priority configuration documents, in its own comments, exactly which Phase B
    subjects it does and does not affect: a Full Synchronisation re-evaluates every joined object,
    so a configuration change's blast radius must be reasoned through explicitly, not assumed
    narrow because only one user's LDAP entry was touched.

    DisabledRuleNoOpinion and PriorityReorderPropagation (Phase D) both reuse Dave (S14-3): unlike
    Phases B and C, neither step touches LDAP data at all (both mutate configuration only: rule
    Enabled state, then Attribute Priority order), and both restore their own configuration mutation
    before returning, so Dave ends each step in exactly the state Phase C left him in, undisturbed
    for whichever step runs next. Each documents, in its own comments, the full blast radius of its
    configuration change across every OTHER joined subject (a disabled rule or reordered priority
    affects every joined object, not just Dave), without asserting each of them individually, to
    keep the step's own assertions scoped to its named subject while remaining honest about scope.

.PARAMETER Step
    Which test step to execute (BaselineResolution, RecallReElection, IdenticalValueHandOver,
    WithdrawalReElection, NoContributorCleared, AssertedNullOverridesSurvivor,
    NotJoinedNoOpinion, MidLifeJoinBlanksClear, MvaNullIsValueAssertsEmptySet,
    DisabledRuleNoOpinion, PriorityReorderPropagation, All).
    Run-IntegrationTests.ps1 resets and repopulates OpenLDAP for every scenario invocation, so a
    single named -Step run starts from a fresh environment with no synchronised state; the script
    therefore establishes the baseline (both systems fully imported and synchronised) before
    dispatching any non-baseline step, and the step then mutates from that baseline rather than
    from a state left by an earlier step. NotJoinedNoOpinion, MidLifeJoinBlanksClear and
    MvaNullIsValueAssertsEmptySet additionally need "Null is a value" set on the relevant
    Metaverse attribute's Primary mapping; a shared idempotent helper
    (Set-Scenario14AttributePrimaryNullIsValue) sets it if a standalone run has not already done
    so, and MidLifeJoinBlanksClear also re-creates Grace's Secondary-only presence
    (NotJoinedNoOpinion's mutation) if she does not already exist, so every step remains
    independently runnable.

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
    [ValidateSet("BaselineResolution", "RecallReElection", "IdenticalValueHandOver", "WithdrawalReElection", "NoContributorCleared", "AssertedNullOverridesSurvivor", "NotJoinedNoOpinion", "MidLifeJoinBlanksClear", "MvaNullIsValueAssertsEmptySet", "DisabledRuleNoOpinion", "PriorityReorderPropagation", "All")]
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

    # Shared by AssertedNullOverridesSurvivor, NotJoinedNoOpinion, MidLifeJoinBlanksClear (Job
    # Title) and MvaNullIsValueAssertsEmptySet (Other Telephones): idempotently sets "Null is a
    # value" on the named Metaverse attribute's Primary mapping, leaving the existing
    # Primary=1/Secondary=2 order unchanged. Under -Step All, AssertedNullOverridesSurvivor sets
    # Job Title's flag first and every later Job Title call below becomes a verified no-op; a
    # standalone single-step run (e.g. -Step MidLifeJoinBlanksClear alone) has nothing set yet, so
    # the same call performs the real work. Returns the Primary/Secondary mapping IDs either way.
    function Set-Scenario14AttributePrimaryNullIsValue {
        param(
            [Parameter(Mandatory=$true)]
            [string]$AttributeName
        )

        $mvAttr = @(Get-JIMMetaverseAttribute) | Where-Object { $_.name -eq $AttributeName }
        if (-not $mvAttr) {
            throw "Metaverse attribute '$AttributeName' not found."
        }
        $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
        if (-not $mvUserType) {
            throw "Metaverse 'User' object type not found."
        }

        $priorityBefore = Get-JIMMetaverseAttributePriority -AttributeId $mvAttr.id -ObjectTypeId $mvUserType.id
        $contributorsBefore = @($priorityBefore.contributors)
        $primaryContributor = $contributorsBefore | Where-Object { $_.connectedSystemName -eq $primarySystemName }
        $secondaryContributor = $contributorsBefore | Where-Object { $_.connectedSystemName -eq $secondarySystemName }
        if (-not $primaryContributor -or -not $secondaryContributor) {
            throw "Could not resolve both '$AttributeName' contributors from Attribute Priority read-back."
        }

        if ($primaryContributor.nullIsValue -and $primaryContributor.priority -eq 1) {
            Write-Host "  '$AttributeName' NullIsValue already set on Primary (idempotent no-op)" -ForegroundColor Gray
            return @{ Primary = $primaryContributor.mappingId; Secondary = $secondaryContributor.mappingId }
        }

        Set-JIMMetaverseAttributePriority -AttributeId $mvAttr.id -ObjectTypeId $mvUserType.id `
            -MappingId @($primaryContributor.mappingId, $secondaryContributor.mappingId) `
            -NullIsValueMappingId @($primaryContributor.mappingId) | Out-Null

        $priorityAfter = Get-JIMMetaverseAttributePriority -AttributeId $mvAttr.id -ObjectTypeId $mvUserType.id
        $contributorsAfter = @($priorityAfter.contributors)
        $primaryAfter = $contributorsAfter | Where-Object { $_.connectedSystemName -eq $primarySystemName }
        $secondaryAfter = $contributorsAfter | Where-Object { $_.connectedSystemName -eq $secondarySystemName }
        if (-not $primaryAfter -or $primaryAfter.priority -ne 1 -or -not $primaryAfter.nullIsValue -or
            -not $secondaryAfter -or $secondaryAfter.priority -ne 2 -or $secondaryAfter.nullIsValue) {
            throw "'$AttributeName' NullIsValue read-back mismatch: expected Primary priority=1/nullIsValue=true, " +
                "Secondary priority=2/nullIsValue=false. Got: $(@($contributorsAfter | ForEach-Object { "$($_.connectedSystemName)=priority:$($_.priority),nullIsValue:$($_.nullIsValue)" }) -join ', ')"
        }
        Write-Host "  OK '$AttributeName' NullIsValue set on Primary (priority 1) and verified via read-back" -ForegroundColor Green

        return @{ Primary = $primaryAfter.mappingId; Secondary = $secondaryAfter.mappingId }
    }

    # Shared by NotJoinedNoOpinion (whose actual mutation this is) and MidLifeJoinBlanksClear
    # (which needs Grace's Secondary-only presence already in place, but only gets the plain
    # baseline when run standalone). Tolerates ldapmodify's "already exists" so a repeat call
    # (-Step All running both steps back to back) is a harmless LDAP-side no-op; Full
    # Import/Full Synchronisation of Secondary always run so the caller can rely on Grace being
    # present and synchronised on return.
    function New-Scenario14GraceSecondaryOnly {
        # Grace (S14-6, "Grace Green") follows the populate script's alliterative naming (Alice
        # Anderson, Bob Baker, ... Frank Foster) and its per-suffix formulas at index 6:
        #   Job Title:   jobTitles[6 % 6] = jobTitles[0] = "Engineer" -> "Engineer (Secondary)"
        #   Description: "Secondary-sourced description for Grace Green (S14)"
        #   Phones:      phonePrefix "20", index 6 -> "+44 20 7946 2060" / "+44 20 7946 2061"
        #   Manager:     the formula's Secondary rotation offset is 3, so (6 + 3) % 6 = index 3 =
        #                Dave; Dave is deliberately used for BOTH suffixes below (see the Primary
        #                add in MidLifeJoinBlanksClear) because Alice/Bob no longer have Primary
        #                entries after Phase B, so the formula's Primary offset (1 -> index 0 =
        #                Alice) would create a dangling manager DN in that suffix. Using Dave
        #                everywhere keeps both entries valid without inventing a bespoke rule.
        Write-Host "Adding Grace (S14-6) to the Secondary suffix only..." -ForegroundColor Gray
        $graceSecondaryDn = "uid=grace14,$($secondaryLdapConfig.UserContainer)"
        $graceManagerDn = "uid=dave14,$($secondaryLdapConfig.UserContainer)"
        $graceSecondaryLdif = @"
dn: $graceSecondaryDn
changetype: add
objectClass: inetOrgPerson
uid: grace14
cn: Grace Green (S14)
sn: Green
givenName: Grace
displayName: Grace Green (S14)
mail: grace14@glitterband.local
employeeNumber: S14-6
description: Secondary-sourced description for Grace Green (S14)
title: Engineer (Secondary)
manager: $graceManagerDn
telephoneNumber: +44 20 7946 2060
telephoneNumber: +44 20 7946 2061
userPassword: Test@123!

"@
        try {
            Invoke-Scenario14LdapModify -ContainerName $secondaryLdapConfig.ContainerName -LdapUri $secondaryLdapUri `
                -BindDN $secondaryLdapConfig.BindDN -BindPassword $secondaryLdapConfig.BindPassword -Ldif $graceSecondaryLdif
        }
        catch {
            if ("$_" -notmatch "already exists") { throw }
            Write-Host "  Grace's Secondary entry already exists (idempotent no-op)" -ForegroundColor Gray
        }

        Write-Host "Running Full Import (Secondary)..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Secondary) with Grace present"

        if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

        Write-Host "Running Full Synchronisation (Secondary)..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Secondary) with Grace present"
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

    # ========================================================================
    # Test 6: AssertedNullOverridesSurvivor
    # ========================================================================
    if ($Step -eq "AssertedNullOverridesSurvivor" -or $Step -eq "All") {
        Write-TestSection "Test 6: Asserted Null Overrides Survivor (Frank, NullIsValue on Primary's Job Title blocks fallback)"

        $assertedNullSuccess = $true
        $assertedNullNotes = @()

        try {
            $primaryImportRuleName = "$primarySystemName Import Users"

            # Blast radius of setting NullIsValue on the Primary Job Title mapping: it only changes
            # behaviour for a user whose Primary CSO is JOINED and CONNECTED, NO VALUE for Job Title
            # (the ConnectedNoValue state). A Full Synchronisation re-evaluates every joined object,
            # so every other Phase B subject needs reasoning through, not assuming:
            #   - Alice (S14-0) and Bob (S14-1) have no Primary CSO at all (deleted in Phase B); a
            #     rule with no joined CSO is RuleNotApplicable ("no opinion"), so NullIsValue never
            #     engages regardless of the flag. Secondary continues to supply their Job Title.
            #   - Carol (S14-2) and Erin (S14-4) still have a Primary CSO joined, and it still
            #     supplies a real Job Title value (only their Description was withdrawn in Phase
            #     B); ConnectedWithValue, so NullIsValue is irrelevant to them too.
            #   - Dave (S14-3) is completely untouched by any prior step; used below as the control.
            # Only Frank (S14-5), whose title is withdrawn below, hits ConnectedNoValue with
            # NullIsValue set, so he is the only user this step's assertion needs to cover.
            $null = Set-Scenario14AttributePrimaryNullIsValue -AttributeName "Job Title"

            Write-Host "Withdrawing Frank's Primary Job Title (entry remains, title attribute removed)..." -ForegroundColor Gray
            $frankPrimaryDn = "uid=frank14,$($primaryLdapConfig.UserContainer)"
            $withdrawTitleLdif = "dn: $frankPrimaryDn`nchangetype: modify`ndelete: title`n"
            Invoke-Scenario14LdapModify -ContainerName $primaryLdapConfig.ContainerName -LdapUri $primaryLdapUri `
                -BindDN $primaryLdapConfig.BindDN -BindPassword $primaryLdapConfig.BindPassword -Ldif $withdrawTitleLdif

            Write-Host "Running Full Import (Primary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after Frank's Job Title withdrawal"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after Frank's Job Title withdrawal"

            $frankMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-5" -PageSize 5) | Select-Object -First 1
            $daveMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-3" -PageSize 5) | Select-Object -First 1
            if (-not $frankMvo -or -not $daveMvo) {
                throw "Could not resolve Frank (S14-5) and/or Dave (S14-3) Metaverse Objects."
            }

            Assert-MvoAttributeValue -MvoId $frankMvo.id -AttributeName "Job Title" `
                -ExpectAssertedNull -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Frank's Job Title (asserted null with Primary provenance; NOT Secondary's 'Architect (Secondary)' fallback)"

            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Coordinator (Primary)" -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Dave's Job Title (control: unaffected by Frank's NullIsValue assertion)"

            Assert-ActivityItemsHaveOutcomeSummary -ActivityId $syncResult.activityId `
                -Name "Full Synchronisation (Primary) after Frank's Job Title withdrawal" `
                -ExpectedOutcomeType "AssertedNull"

            $assertedNullNotes += "Frank's Job Title asserted null with Primary provenance (no fallback to Secondary's 'Architect (Secondary)'); Dave's Primary-sourced title unaffected"
        }
        catch {
            $assertedNullSuccess = $false
            $assertedNullNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "AssertedNullOverridesSurvivor"
                Success = $assertedNullSuccess
                Note = ($assertedNullNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 7: NotJoinedNoOpinion
    # ========================================================================
    if ($Step -eq "NotJoinedNoOpinion" -or $Step -eq "All") {
        Write-TestSection "Test 7: Not Joined, No Opinion (Grace, Secondary-only; Primary's NullIsValue has no bearing on an unjoined rule)"

        $notJoinedSuccess = $true
        $notJoinedNotes = @()

        try {
            $secondaryImportRuleName = "$secondarySystemName Import Users"

            # Keep NullIsValue set on Primary's Job Title even when this step runs standalone: a
            # single -Step NotJoinedNoOpinion invocation only gets the plain baseline (Step 0b),
            # which never touches this flag. Idempotent: under -Step All this is a verified no-op
            # because AssertedNullOverridesSurvivor above already set it.
            $null = Set-Scenario14AttributePrimaryNullIsValue -AttributeName "Job Title"

            # Grace (S14-6) is a brand-new Secondary-only user: no Primary counterpart exists at
            # all, so the Primary Job Title rule has no joined CSO to evaluate and is
            # RuleNotApplicable ("no opinion") for her, regardless of NullIsValue. Secondary is
            # therefore the sole contributor and supplies its value in full: the HR-migration cell
            # of the tri-state matrix (engineering/plans/doing/ATTRIBUTE_PRIORITY.md Phase 4).
            New-Scenario14GraceSecondaryOnly

            $graceMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-6" -PageSize 5) | Select-Object -First 1
            if (-not $graceMvo) {
                throw "Could not resolve Grace (S14-6) Metaverse Object after her Secondary-only projection."
            }

            Assert-MvoAttributeValue -MvoId $graceMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Engineer (Secondary)" -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Grace's Job Title (Secondary contributes fully; Primary's NullIsValue is irrelevant with no joined CSO)"

            Assert-MvoAttributeValue -MvoId $graceMvo.id -AttributeName "Description" `
                -ExpectedValue "Secondary-sourced description for Grace Green (S14)" -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Grace's Description (Secondary, sole contributor)"

            $notJoinedNotes += "Grace projected from Secondary alone; Primary's Job Title NullIsValue had no bearing because Primary has no joined CSO for her (RuleNotApplicable)"
        }
        catch {
            $notJoinedSuccess = $false
            $notJoinedNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "NotJoinedNoOpinion"
                Success = $notJoinedSuccess
                Note = ($notJoinedNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 8: MidLifeJoinBlanksClear
    # ========================================================================
    if ($Step -eq "MidLifeJoinBlanksClear" -or $Step -eq "All") {
        Write-TestSection "Test 8: Mid-Life Join Blanks Clear (Grace joins Primary; her blank Job Title clears the Secondary value)"

        $midLifeSuccess = $true
        $midLifeNotes = @()

        try {
            $primaryImportRuleName = "$primarySystemName Import Users"

            # Same idempotent precondition as NotJoinedNoOpinion: needed for real on a standalone
            # -Step MidLifeJoinBlanksClear run, a verified no-op under -Step All.
            $null = Set-Scenario14AttributePrimaryNullIsValue -AttributeName "Job Title"

            $graceMvoBefore = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-6" -PageSize 5) | Select-Object -First 1
            if (-not $graceMvoBefore) {
                # Standalone run: Grace does not exist yet (NotJoinedNoOpinion's mutation never
                # ran). Re-create her Secondary-only presence first so this step has something to
                # join against, exactly as it would under -Step All.
                Write-Host "Grace (S14-6) not present yet; establishing her Secondary-only presence first (standalone run precondition)..." -ForegroundColor Gray
                New-Scenario14GraceSecondaryOnly
                $graceMvoBefore = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-6" -PageSize 5) | Select-Object -First 1
                if (-not $graceMvoBefore) {
                    throw "Could not resolve Grace (S14-6) Metaverse Object after seeding her Secondary-only presence."
                }
            }

            # Grace joins Primary via her shared Employee ID (S14-6). Her Primary entry omits
            # `title` entirely (every other attribute follows the Primary formula), so on join the
            # Primary rule is ConnectedNoValue for Job Title; with NullIsValue set, that asserts
            # null and clears the value Secondary had been contributing, rather than leaving
            # Secondary's "Engineer (Secondary)" in place. Manager points at Dave in both suffixes
            # for the same dangling-DN reason documented in New-Scenario14GraceSecondaryOnly.
            Write-Host "Adding Grace (S14-6) to the Primary suffix (no title attribute)..." -ForegroundColor Gray
            $gracePrimaryDn = "uid=grace14,$($primaryLdapConfig.UserContainer)"
            $gracePrimaryManagerDn = "uid=dave14,$($primaryLdapConfig.UserContainer)"
            $gracePrimaryLdif = @"
dn: $gracePrimaryDn
changetype: add
objectClass: inetOrgPerson
uid: grace14
cn: Grace Green (S14)
sn: Green
givenName: Grace
displayName: Grace Green (S14)
mail: grace14@yellowstone.local
employeeNumber: S14-6
description: Primary-sourced description for Grace Green (S14)
manager: $gracePrimaryManagerDn
telephoneNumber: +44 20 7946 1060
telephoneNumber: +44 20 7946 1061
userPassword: Test@123!

"@
            Invoke-Scenario14LdapModify -ContainerName $primaryLdapConfig.ContainerName -LdapUri $primaryLdapUri `
                -BindDN $primaryLdapConfig.BindDN -BindPassword $primaryLdapConfig.BindPassword -Ldif $gracePrimaryLdif

            Write-Host "Running Full Import (Primary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after Grace's mid-life Primary join"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after Grace's mid-life Primary join"

            $graceMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-6" -PageSize 5) | Select-Object -First 1
            if (-not $graceMvo) {
                throw "Could not resolve Grace (S14-6) Metaverse Object after her mid-life Primary join."
            }
            if ($graceMvo.id -ne $graceMvoBefore.id) {
                throw "Grace's Primary CSO projected a NEW Metaverse Object (ID $($graceMvo.id)) instead of joining her existing one (ID $($graceMvoBefore.id)). Check the Employee ID matching rule."
            }

            # Second lookup, still returning exactly one MVO: proves the join, not a duplicate projection.
            $graceMvoRecheck = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-6" -PageSize 5)
            if ($graceMvoRecheck.Count -ne 1) {
                throw "Expected exactly one Metaverse Object for Grace (S14-6) after her Primary join, found $($graceMvoRecheck.Count). Duplicate projection suspected."
            }

            Assert-MvoAttributeValue -MvoId $graceMvo.id -AttributeName "Job Title" `
                -ExpectAssertedNull -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Grace's Job Title (Primary's blank asserts null on join, clearing Secondary's 'Engineer (Secondary)')"

            Assert-MvoAttributeValue -MvoId $graceMvo.id -AttributeName "Description" `
                -ExpectedValue "Primary-sourced description for Grace Green (S14)" -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Grace's Description (normal win: Primary outranks Secondary on join, no NullIsValue involved)"

            $midLifeNotes += "Grace's Primary CSO joined her existing Metaverse Object (no duplicate projection); her blank Job Title asserted null and cleared Secondary's value, while Description won normally to Primary"
        }
        catch {
            $midLifeSuccess = $false
            $midLifeNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "MidLifeJoinBlanksClear"
                Success = $midLifeSuccess
                Note = ($midLifeNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 9: MvaNullIsValueAssertsEmptySet
    # ========================================================================
    if ($Step -eq "MvaNullIsValueAssertsEmptySet" -or $Step -eq "All") {
        Write-TestSection "Test 9: MVA NullIsValue Asserts Empty Set (Frank, Other Telephones cleared to nothing, not a Secondary fallback)"

        $mvaNullSuccess = $true
        $mvaNullNotes = @()

        try {
            $primaryImportRuleName = "$primarySystemName Import Users"

            # Same blast-radius reasoning as AssertedNullOverridesSurvivor, for a different
            # attribute: only Frank hits ConnectedNoValue for Other Telephones here (Alice/Bob have
            # no Primary CSO at all; Carol/Dave/Erin/Grace's Primary entries all still supply
            # telephoneNumber values). Dave is the control.
            $null = Set-Scenario14AttributePrimaryNullIsValue -AttributeName "Other Telephones"

            Write-Host "Withdrawing Frank's Primary Other Telephones (entry remains, telephoneNumber attribute removed)..." -ForegroundColor Gray
            $frankPrimaryDn = "uid=frank14,$($primaryLdapConfig.UserContainer)"
            $withdrawPhonesLdif = "dn: $frankPrimaryDn`nchangetype: modify`ndelete: telephoneNumber`n"
            Invoke-Scenario14LdapModify -ContainerName $primaryLdapConfig.ContainerName -LdapUri $primaryLdapUri `
                -BindDN $primaryLdapConfig.BindDN -BindPassword $primaryLdapConfig.BindPassword -Ldif $withdrawPhonesLdif

            Write-Host "Running Full Import (Primary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after Frank's Other Telephones withdrawal"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after Frank's Other Telephones withdrawal"

            $frankMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-5" -PageSize 5) | Select-Object -First 1
            $daveMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-3" -PageSize 5) | Select-Object -First 1
            if (-not $frankMvo -or -not $daveMvo) {
                throw "Could not resolve Frank (S14-5) and/or Dave (S14-3) Metaverse Objects."
            }

            # ApplyNoValueOutcome (src/JIM.Application/Servers/SyncEngine.AttributeFlow.cs) strips
            # every existing real value row for the attribute and writes exactly ONE NullValue
            # marker row, regardless of the attribute's plurality: the "ConnectedNoValue, no values
            # at all" branch (csoAttributeValues.Count == 0) is reached identically whether the
            # target is single- or multi-valued, so a multi-valued asserted null persists as one
            # marker row, not one marker per formerly-held value. -ExpectAssertedNull's
            # row-count-of-1 check therefore applies unchanged to an MVA target; see the
            # engineering/plans/doing/ATTRIBUTE_PRIORITY.md Phase 4 "NullIsValue on an MVA asserts
            # the empty set" checklist cell.
            Assert-MvoAttributeValue -MvoId $frankMvo.id -AttributeName "Other Telephones" `
                -ExpectAssertedNull -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Frank's Other Telephones (asserted empty set with Primary provenance; Secondary's numbers absent, not a fallback)"

            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Other Telephones" `
                -ExpectedValues @("+44 20 7946 1030", "+44 20 7946 1031") `
                -Name "Dave's Other Telephones (control: unaffected by Frank's NullIsValue assertion)"

            Assert-ActivityItemsHaveOutcomeSummary -ActivityId $syncResult.activityId `
                -Name "Full Synchronisation (Primary) after Frank's Other Telephones withdrawal" `
                -ExpectedOutcomeType "AssertedNull"

            $mvaNullNotes += "Frank's Other Telephones asserted as an empty set with Primary provenance (Secondary's numbers absent, no fallback); Dave's Primary-sourced numbers unaffected"
        }
        catch {
            $mvaNullSuccess = $false
            $mvaNullNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "MvaNullIsValueAssertsEmptySet"
                Success = $mvaNullSuccess
                Note = ($mvaNullNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 10: DisabledRuleNoOpinion
    # ========================================================================
    if ($Step -eq "DisabledRuleNoOpinion" -or $Step -eq "All") {
        Write-TestSection "Test 10: Disabled Rule, No Opinion (Dave, disabling Primary hands Description and Job Title to Secondary)"

        $disabledRuleSuccess = $true
        $disabledRuleNotes = @()

        try {
            $primaryImportRuleName = "$primarySystemName Import Users"
            $secondaryImportRuleName = "$secondarySystemName Import Users"

            $primaryImportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $primaryImportRuleName } | Select-Object -First 1
            if (-not $primaryImportRule) {
                throw "Could not resolve '$primaryImportRuleName' Synchronisation Rule."
            }

            $daveMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-3" -PageSize 5) | Select-Object -First 1
            if (-not $daveMvo) {
                throw "Could not resolve Dave (S14-3) Metaverse Object."
            }

            # Disable Primary's import rule. AttributePriorityContext's constructor
            # (src/JIM.Application/Services/AttributePriorityContext.cs:44-65) builds its contributor cache
            # only from "allSyncRules.Where(r => r.Enabled && r.Direction == SyncRuleDirection.Import)": a
            # disabled rule's mapping is excluded from the cache entirely, not merely flagged. ShouldApply
            # (AttributePriorityContext.cs:107-123) then treats a stale incumbent whose rule no longer
            # appears in the cache ("GetContributor(...) returns null") exactly like "no comparable
            # incumbent" and returns true: a disabled rule is no opinion, just like RuleNotApplicable (no
            # joined CSO), not a stuck last-written value that blocks a lower-priority challenger.
            #
            # Disabling itself changes nothing on its own: GetSyncRulesAsync(connectedSystemId,
            # includeDisabledSyncRules: false, ...) (src/JIM.PostgresData/Repositories/ConnectedSystemRepository.cs:4006-4041),
            # called by both SyncFullSyncTaskProcessor.cs:69 and SyncDeltaSyncTaskProcessor.cs:86, filters
            # the disabled rule out of "activeSyncRules" at the query layer, before any per-object
            # processing happens. No recall or re-evaluation fires from the act of disabling; only the
            # NEXT sync run that touches the attribute picks up the change.
            Write-Host "Disabling '$primaryImportRuleName'..." -ForegroundColor Gray
            Set-JIMSyncRule -Id $primaryImportRule.id -Disable | Out-Null

            $primaryImportRuleAfterDisable = Get-JIMSyncRule -Id $primaryImportRule.id
            if ($primaryImportRuleAfterDisable.enabled) {
                throw "'$primaryImportRuleName' still reports enabled=true after Set-JIMSyncRule -Disable."
            }
            Write-Host "  OK '$primaryImportRuleName' disabled and verified via read-back" -ForegroundColor Green

            # A Full Synchronisation of SECONDARY alone is what re-elects Dave's attributes: it calls
            # ProcessInboundAttributeFlow for every joined CSO's own mapping unconditionally
            # (SyncTaskProcessorBase.cs:1104), regardless of whether that CSO's own staged data changed,
            # so Secondary's mapping is re-evaluated against the freshly-rebuilt AttributePriorityContext
            # (rebuilt once per run from the CURRENT Enabled state, per BuildDriftDetectionCache, called
            # at SyncFullSyncTaskProcessor.cs:83) with Primary now absent from it. A Full Synchronisation
            # of the disabled PRIMARY system is deliberately NOT run here: its own rule is filtered out of
            # "activeSyncRules" before any mapping is even read, so it would do nothing useful; this is
            # the opposite of RecallReElection/WithdrawalReElection, where it is the LEAVING system's own
            # Full Synchronisation that drives the recall (an explicit Disconnected/ConnectedNoValue
            # outcome on that system's own CSO). A disabled rule has no such explicit outcome at all: its
            # CSO is simply never visited.
            Write-Host "Running Full Synchronisation (Secondary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Secondary) after disabling '$primaryImportRuleName'"

            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Description" `
                -ExpectedValue "Secondary-sourced description for Dave Dixon (S14)" `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Dave's Description (Primary disabled: no opinion, Secondary takes over)"

            # Job Title carries "Null is a value" on Primary's mapping (set by Phase C's
            # Set-Scenario14AttributePrimaryNullIsValue), but the flag is irrelevant here: a disabled
            # rule's mapping is excluded from the contributor cache before NullIsValue is ever consulted
            # (SyncEngine.AttributeFlow.cs's ApplyNoValueOutcome only runs for a mapping that is actually
            # processed). Job Title therefore behaves identically to Description, handing over to
            # Secondary's real value in full rather than asserting null: this proves the "disabled = no
            # opinion, flag irrelevant" hypothesis rather than merely assuming it.
            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Coordinator (Secondary)" `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Dave's Job Title (Primary disabled: NullIsValue on the disabled rule's mapping has no bearing, Secondary takes over in full)"

            # Blast radius: disabling Primary's rule removes EVERY Primary mapping from the priority
            # contributor cache, for EVERY attribute, for EVERY object still joined to Primary; it is not
            # scoped to Dave or to Description/Job Title. The same Full Synchronisation (Secondary) run
            # above also hands Carol's and Erin's Job Title (still Primary-sourced going into this step),
            # Frank's Job Title and Other Telephones (previously asserted null; with Primary's NullIsValue
            # mapping excluded while disabled, Secondary's real values win instead of a null assertion)
            # and Grace's Job Title and Description over to Secondary. Only Alice and Bob are unaffected
            # (they have no Primary CSO at all, so Primary was already RuleNotApplicable for them before
            # and after the disable). None of this is asserted individually here, to keep the step's own
            # assertions scoped to its named subject, but an administrator disabling an authoritative
            # import rule with live joined objects must understand the effect is this broad.
            $disabledRuleNotes += "Disabling '$primaryImportRuleName' handed Dave's Description and Job Title to Secondary (NullIsValue on the disabled rule irrelevant); the same Full Synchronisation (Secondary) run also re-elected every other Primary-joined subject's Primary-sourced attributes (not asserted individually here; see step comments for the full blast radius)"

            # Re-enable and restore. Full Synchronisation (Primary) alone is sufficient: Primary's mapping
            # re-enters the freshly-rebuilt AttributePriorityContext at priority 1, beating the Secondary
            # incumbent (priority 2) for every attribute Primary still supplies a value for, per
            # ShouldApply's canonical (priority ascending, mapping id) comparison. This also restores
            # Carol/Erin/Frank/Grace's attributes from the blast radius above, since Full Synchronisation
            # reprocesses every Primary CSO, not just Dave's.
            Write-Host "Re-enabling '$primaryImportRuleName'..." -ForegroundColor Gray
            Set-JIMSyncRule -Id $primaryImportRule.id -Enable | Out-Null

            $primaryImportRuleAfterEnable = Get-JIMSyncRule -Id $primaryImportRule.id
            if (-not $primaryImportRuleAfterEnable.enabled) {
                throw "'$primaryImportRuleName' still reports enabled=false after Set-JIMSyncRule -Enable."
            }
            Write-Host "  OK '$primaryImportRuleName' re-enabled and verified via read-back" -ForegroundColor Green

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after re-enabling '$primaryImportRuleName'"

            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Description" `
                -ExpectedValue "Primary-sourced description for Dave Dixon (S14)" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Dave's Description (Primary re-enabled: the priority gate lets the higher-priority contributor retake)"

            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Coordinator (Primary)" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Dave's Job Title (Primary re-enabled: retaken, restoring the inherited end-state for later steps)"

            $disabledRuleNotes += "Re-enabling '$primaryImportRuleName' and running Full Synchronisation (Primary) restored Dave's Description and Job Title to Primary, and (per the same blast-radius reasoning) every other Primary-joined subject's Primary-sourced attributes too"
        }
        catch {
            $disabledRuleSuccess = $false
            $disabledRuleNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "DisabledRuleNoOpinion"
                Success = $disabledRuleSuccess
                Note = ($disabledRuleNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 11: PriorityReorderPropagation
    # ========================================================================
    if ($Step -eq "PriorityReorderPropagation" -or $Step -eq "All") {
        Write-TestSection "Test 11: Priority Reorder Propagation (Description: Secondary=1/Primary=2; apply-only, Delta Synchronisation no-ops, Full Synchronisation re-resolves)"

        $reorderSuccess = $true
        $reorderNotes = @()

        try {
            $primaryImportRuleName = "$primarySystemName Import Users"
            $secondaryImportRuleName = "$secondarySystemName Import Users"

            $daveMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-3" -PageSize 5) | Select-Object -First 1
            if (-not $daveMvo) {
                throw "Could not resolve Dave (S14-3) Metaverse Object."
            }

            $mvDescriptionAttr = @(Get-JIMMetaverseAttribute) | Where-Object { $_.name -eq "Description" }
            $mvUserTypeForReorder = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
            if (-not $mvDescriptionAttr -or -not $mvUserTypeForReorder) {
                throw "Could not resolve the 'Description' Metaverse attribute and/or 'User' Metaverse Object Type."
            }

            $priorityBefore = Get-JIMMetaverseAttributePriority -AttributeId $mvDescriptionAttr.id -ObjectTypeId $mvUserTypeForReorder.id
            $contributorsBefore = @($priorityBefore.contributors)
            $primaryMapping = $contributorsBefore | Where-Object { $_.connectedSystemName -eq $primarySystemName }
            $secondaryMapping = $contributorsBefore | Where-Object { $_.connectedSystemName -eq $secondarySystemName }
            if (-not $primaryMapping -or -not $secondaryMapping) {
                throw "Could not resolve both 'Description' contributors from Attribute Priority read-back."
            }
            if ($primaryMapping.priority -ne 1 -or $secondaryMapping.priority -ne 2) {
                throw "Expected the inherited Primary=1/Secondary=2 order for 'Description' at the start of this step; found Primary=$($primaryMapping.priority), Secondary=$($secondaryMapping.priority). A prior step may not have restored its own configuration mutation."
            }

            # Reorder: Secondary=1, Primary=2. Set-JIMMetaverseAttributePriority's -MappingId array order
            # IS the priority order (highest first), exactly as Setup-Scenario14.ps1 Step 10 and the
            # Set-Scenario14AttributePrimaryNullIsValue helper above already rely on.
            Write-Host "Reordering 'Description' priority: Secondary=1, Primary=2..." -ForegroundColor Gray
            Set-JIMMetaverseAttributePriority -AttributeId $mvDescriptionAttr.id -ObjectTypeId $mvUserTypeForReorder.id `
                -MappingId @($secondaryMapping.mappingId, $primaryMapping.mappingId) | Out-Null

            $priorityAfterReorder = Get-JIMMetaverseAttributePriority -AttributeId $mvDescriptionAttr.id -ObjectTypeId $mvUserTypeForReorder.id
            $contributorsAfterReorder = @($priorityAfterReorder.contributors)
            $secondaryAfterReorder = $contributorsAfterReorder | Where-Object { $_.connectedSystemName -eq $secondarySystemName }
            $primaryAfterReorder = $contributorsAfterReorder | Where-Object { $_.connectedSystemName -eq $primarySystemName }
            if (-not $secondaryAfterReorder -or $secondaryAfterReorder.priority -ne 1 -or -not $primaryAfterReorder -or $primaryAfterReorder.priority -ne 2) {
                throw "'Description' priority read-back mismatch after reorder: expected Secondary=1/Primary=2, got $(@($contributorsAfterReorder | ForEach-Object { "$($_.connectedSystemName)=$($_.priority)" }) -join ', ')"
            }
            Write-Host "  OK 'Description' reordered to Secondary=1, Primary=2 and verified via read-back" -ForegroundColor Green

            # (a) Apply-only propagation (engineering/plans/doing/ATTRIBUTE_PRIORITY.md, "Configuration
            # Change Propagation"): changing priority configuration does not itself initiate
            # synchronisation. SyncDeltaSyncTaskProcessor.cs:49-74 computes the delta watermark from
            # ConnectedSystem.LastSyncCompletedAt and, when GetConnectedSystemObjectModifiedSinceCountAsync
            # returns zero, completes immediately without processing a single Connected System Object ("No
            # CSOs modified since last sync. Completing immediately."). No LDAP data has changed since the
            # last Full Import/Full Synchronisation of either system in this step, so both Delta
            # Synchronisations below touch nothing, leaving Dave's Description exactly as it was (Primary's
            # value, Primary provenance) despite the reorder.
            $primaryDeltaSync = $primaryProfiles | Where-Object { $_.name -eq "Delta Synchronisation" }
            $secondaryDeltaSync = $secondaryProfiles | Where-Object { $_.name -eq "Delta Synchronisation" }
            if (-not $primaryDeltaSync -or -not $secondaryDeltaSync) {
                throw "Could not resolve 'Delta Synchronisation' Run Profiles. Ensure Setup-Scenario14.ps1 completed successfully."
            }

            Write-Host "Running Delta Synchronisation (Primary) with no staged import changes..." -ForegroundColor Gray
            $deltaPrimaryResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryDeltaSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $deltaPrimaryResult.activityId -Name "Delta Synchronisation (Primary) after reordering 'Description'"

            Write-Host "Running Delta Synchronisation (Secondary) with no staged import changes..." -ForegroundColor Gray
            $deltaSecondaryResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryDeltaSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $deltaSecondaryResult.activityId -Name "Delta Synchronisation (Secondary) after reordering 'Description'"

            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Description" `
                -ExpectedValue "Primary-sourced description for Dave Dixon (S14)" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Dave's Description (unchanged: Delta Synchronisation with no staged import changes processes no Connected System Objects, apply-only)"

            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Coordinator (Primary)" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Dave's Job Title (control: only Description's priority was reordered)"

            # (b) Full Synchronisation re-resolves every joined object against the new configuration.
            # Secondary now outranks Primary for Description (priority 1 vs 2), so Full Synchronisation
            # (Secondary) re-evaluating Secondary's own mapping via ProcessInboundAttributeFlow flips
            # Dave's Description over.
            Write-Host "Running Full Synchronisation (Secondary)..." -ForegroundColor Gray
            $fullSecondaryResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $fullSecondaryResult.activityId -Name "Full Synchronisation (Secondary) after reordering 'Description'"

            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Description" `
                -ExpectedValue "Secondary-sourced description for Dave Dixon (S14)" `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Dave's Description (Full Synchronisation re-resolves to the new priority order: Secondary now wins)"

            # Blast radius: reordering Description's priority affects every joined object's Description,
            # not just Dave's. Frank's and Grace's Primary-sourced Description also hand over to Secondary
            # during this window (Carol's and Erin's are already Secondary-sourced since Phase B/C and see
            # no change; Alice/Bob have no Primary CSO and are likewise unaffected). Job Title, Manager and
            # Other Telephones are untouched throughout, since only Description's priority order changed.
            $reorderNotes += "Reordering 'Description' to Secondary=1/Primary=2 had no effect until a Full Synchronisation ran (apply-only): Delta Synchronisation with no staged changes left Dave's Description on Primary, Full Synchronisation (Secondary) flipped it to Secondary (Frank and Grace's Description likewise handed to Secondary; not asserted individually here)"

            # Restore Primary=1/Secondary=2 and Full Synchronisation (Primary) so the inherited end-state
            # for any later phase is unchanged by this step.
            Write-Host "Restoring 'Description' priority: Primary=1, Secondary=2..." -ForegroundColor Gray
            Set-JIMMetaverseAttributePriority -AttributeId $mvDescriptionAttr.id -ObjectTypeId $mvUserTypeForReorder.id `
                -MappingId @($primaryMapping.mappingId, $secondaryMapping.mappingId) | Out-Null

            $priorityRestored = Get-JIMMetaverseAttributePriority -AttributeId $mvDescriptionAttr.id -ObjectTypeId $mvUserTypeForReorder.id
            $contributorsRestored = @($priorityRestored.contributors)
            $primaryRestored = $contributorsRestored | Where-Object { $_.connectedSystemName -eq $primarySystemName }
            $secondaryRestored = $contributorsRestored | Where-Object { $_.connectedSystemName -eq $secondarySystemName }
            if (-not $primaryRestored -or $primaryRestored.priority -ne 1 -or -not $secondaryRestored -or $secondaryRestored.priority -ne 2) {
                throw "'Description' priority read-back mismatch after restore: expected Primary=1/Secondary=2, got $(@($contributorsRestored | ForEach-Object { "$($_.connectedSystemName)=$($_.priority)" }) -join ', ')"
            }
            Write-Host "  OK 'Description' restored to Primary=1, Secondary=2 and verified via read-back" -ForegroundColor Green

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $fullPrimaryResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $fullPrimaryResult.activityId -Name "Full Synchronisation (Primary) after restoring 'Description' priority"

            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Description" `
                -ExpectedValue "Primary-sourced description for Dave Dixon (S14)" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Dave's Description (priority order restored, Full Synchronisation retakes Primary)"

            $reorderNotes += "Restored Primary=1/Secondary=2 for 'Description' and ran Full Synchronisation (Primary); Dave's Description (and Frank/Grace's, per the same blast radius) returned to Primary's value"
        }
        catch {
            $reorderSuccess = $false
            $reorderNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "PriorityReorderPropagation"
                Success = $reorderSuccess
                Note = ($reorderNotes -join "; ")
            }
        }
    }

    # Inherited end-state at this point (under -Step All, or after the last Phase D step run
    # standalone left its flags/data in place): Phase D's two steps (DisabledRuleNoOpinion,
    # PriorityReorderPropagation) each restore their own configuration mutation (rule Enabled state;
    # Attribute Priority order) before returning, so the state below is UNCHANGED from Phase C's
    # inherited end-state. Frank (S14-5) has both Job Title and Other
    # Telephones asserted null with Primary provenance; Grace (S14-6) is joined to BOTH suffixes,
    # with her Job Title asserted null (Primary provenance) and her Description Primary-sourced;
    # NullIsValue is set on Primary's Job Title and Other Telephones mappings. No later Phase C
    # step depends on Job Title/Other Telephones neutrality, and Phase C deliberately does NOT
    # unset these flags at the end of MvaNullIsValueAssertsEmptySet: no unsetting cmdlet call is
    # needed there because nothing downstream in this phase requires it. Phase D's own steps read
    # this same inherited state (both mutate and restore configuration only; neither touches LDAP
    # data), so any future phase inherits it unchanged and must manage its own preconditions against
    # it rather than assuming a clean slate.

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
