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

    12. OutOfScopeNoOpinion - Erin's (S14-4) Primary entry is excluded from the Primary import
        Synchronisation Rule's scope via a Scoping Criteria Group (employeeNumber NotEquals
        "S14-4"), leaving every other user, including Grace (S14-6, joined to Primary too since
        MidLifeJoinBlanksClear), unaffected. A Full Import (Primary) + Full Synchronisation
        (Primary) then push her Primary CSO out of scope: InboundOutOfScopeAction=Disconnect (the
        Synchronisation Rules' unset default) breaks the join, recalls every attribute her Primary
        CSO contributed and, in the SAME run, re-elects the surviving Secondary contribution,
        exactly as RecallReElection's CSO deletion and WithdrawalReElection's in-place withdrawal
        already prove for the other two ways a contributor can stop contributing. Job Title and
        Other Telephones hand over to Secondary's values; the Manager reference hands over to
        Secondary's rotation-offset-3 referent, Bob (S14-1), whose own Metaverse Object survives
        independently via his Secondary CSO since IdenticalValueHandOver deleted his Primary entry;
        Description, already cleared on both sides by NoContributorCleared, stays absent (no
        surviving contributor to hand over to). Dave (S14-3) is the control. The scoping criteria
        group is then removed and a Full Import (Primary) + Full Synchronisation (Primary)
        re-admits Erin: her Primary CSO rejoins the SAME Metaverse Object via the Employee ID
        matching rule (the same not-joined-CSO-rejoins-an-existing-object mechanics
        MidLifeJoinBlanksClear already proves), and Job Title retakes Primary's value. This is the
        third cell of the tri-state matrix NotJoinedNoOpinion's docstring calls out
        (RuleNotApplicable/no-joined-CSO is NotJoinedNoOpinion; ConnectedNoValue is
        AssertedNullOverridesSurvivor/MvaNullIsValueAssertsEmptySet): it was originally
        investigated and dropped as an engine gap (HandleCsoOutOfScopeAsync's Disconnect branch not
        calling ReElectSurvivingContributorsAsync), but that gap is now fixed (commits b9471c7,
        8c25ffc; proven at the workflow-test level by the ScopeExit_* tests in
        test/JIM.Worker.Tests/Workflows/AttributePriorityRecallWorkflowTests.cs), so this cell is
        now implemented rather than skipped.

    13. EnforceStateCorrectsLoser - the outbound half of Attribute Priority, and the first step in this
        file to assert on a Connected System's actual contents rather than on the Metaverse. The
        Enforce State export Synchronisation Rule on Secondary (Setup-Scenario14.ps1 Step 10b) is run
        once to drain the divergence every earlier step has been staging, then Frank's (S14-5) Secondary
        `description` is edited directly in the directory. A Full Import + Full Synchronisation
        (Secondary) carry the change into his Connected System Object but no further: it loses
        resolution to Primary at priority 1, so the Metaverse keeps Primary's value, and the directory
        is still diverged afterwards (inbound processing never writes to a Connected System). A
        subsequent Export (Secondary) then corrects the directory back to the winning value, read back
        over LDAP. Dave (S14-3) is the control.
    14. ScopedExceptionAuthority - fine-grained authority (worked example 2). A SECOND import
        Synchronisation Rule is created on Primary, scoped to Dave (S14-3) alone, and Description is
        ordered Exceptions=1, Secondary=2, Primary's plain rule=3: one Connected System holding two
        positions in one attribute's priority list, which a per-system priority model cannot express.
        Dave, in the exception rule's scope, resolves to Primary's value with the EXCEPTION rule's
        provenance; Frank (S14-5), outside it, resolves to Secondary's value, because the exception rule
        has no opinion for him and Secondary at 2 beats Primary's plain rule at 3. Same two systems,
        same attribute, opposite winners: authority is per object. One Export (Secondary) then proves
        both directions of the correction at once, rewriting Dave's Secondary entry (it lost) while
        leaving Frank's alone (it won). The exception rule is removed and the original
        Primary=1/Secondary=2 order restored before returning.
    15. GraceFreezesSoleSource - a deletion grace period is configured on the "User" Metaverse Object
        Type (WhenAuthoritativeSourceDisconnected, Primary authoritative) and Carol's (S14-2) Primary
        entry is deleted. Her Metaverse Object survives, pending deletion, and Common Name (the only
        Primary-only mapping, Setup-Scenario14.ps1 Step 9b) is FROZEN rather than recalled, because it
        has no surviving contributor; Job Title, which has one, hands over to Secondary in the same run.
    16. GraceExpiryDeletesAndExports - the grace period is then made to expire by shortening it to zero
        (eligibility is recomputed per housekeeping cycle against the type's current grace period, so
        this reaches the same condition as waiting the window out). Housekeeping deletes Carol's
        Metaverse Object and stages a delete Pending Export; an Export (Secondary) applies it, and her
        Secondary directory entry is gone when read back over LDAP.
    17. GraceFallbackFlowsAndExports - Frank's (S14-5) Secondary Display Name is edited to a distinct
        value while Primary still wins it (proving it lost), then his Primary entry is deleted. With a
        grace period configured, Display Name still hands over to Secondary's distinct value: a grace
        period suppresses recall only where there is no survivor. The Export carrying it completes
        without error.
    18. GraceAssertedNullBeatsSurvivor - with a grace period configured and "Null is a value" set on
        Primary's Job Title mapping, Erin's (S14-4) Primary title is withdrawn IN PLACE (her entry
        remains). The explicit null assertion outranks Secondary's surviving value and the grace-period
        freeze does not preserve the withdrawn value, because the freeze lives in the obsoletion and
        scope-exit paths, not the withdrawal path. This is #1307's third cell corrected: as written it
        asked for a disconnecting source's null assertion to win, which the engine deliberately cannot
        do (ReElectSurvivingContributorsAsync excludes the leaver's own rule), and which Test 7 already
        proves for the unjoined case.
    19. GraceExpressionInputFallback - the motivating failure. The Secondary export rule's expression
        mapping builds a Distinguished Name from Display Name (Setup-Scenario14.ps1 Step 10c). After
        Frank's Primary disconnect the expression is rebuilt from the re-elected Secondary value, and
        the produced string is read back over LDAP and asserted to have non-empty values in every RDN
        component: the "CN=,OU=..." output an unfallen-back-to null would have produced.

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

    OutOfScopeNoOpinion (Phase E) deliberately reuses Erin (S14-4) rather than a fresh subject: its
    Description assertion depends on NoContributorCleared's (Phase B) end-state, since the scope
    exit's "no surviving contributor" hand-over for Description only holds if both suffixes already
    carry no value for it. Under -Step All this dependency is satisfied for free (NoContributorCleared
    ran earlier in the same invocation); a standalone -Step OutOfScopeNoOpinion run only gets the
    plain baseline (Step 0b), where Erin's Description is still Primary's original value, so the step
    establishes its own precondition via a dedicated idempotent helper
    (Set-Scenario14ErinDescriptionWithdrawn, mirroring Set-Scenario14AttributePrimaryNullIsValue's
    check-then-mutate pattern) before touching scope, so its assertions are identical in both modes.
    Its own configuration mutation (the Primary import rule's Scoping Criteria Group) is removed
    before returning, restoring Job Title, Manager and Other Telephones to the inherited end-state
    exactly as Phase D's steps restore theirs; Description remains absent throughout, since no step
    ever gives it a new value to hand over. Bob's Metaverse Object (S14-1, joined via Secondary only
    since IdenticalValueHandOver) is read as Erin's re-elected Manager reference, not written, so this
    step leaves him untouched too.

    EnforceStateCorrectsLoser and ScopedExceptionAuthority (Phase F) are the first steps to write to a
    Connected System, and they run last for that reason. The Enforce State export rule has existed
    since setup, so every earlier step's Full Synchronisation (Secondary) has been staging corrective
    Pending Exports for the whole population; nothing runs the Export Run Profile until Phase F, so
    those stage harmlessly and no earlier assertion is affected (they all read the Metaverse, which an
    unexecuted Pending Export cannot change). Phase F's first act is to drain that backlog, which
    rewrites Secondary's Description and Job Title for EVERY joined object to the Metaverse's values,
    including clearing the ones Phases B and C left absent or asserted null. That is a one-way change to
    the directory's contents and is why this phase is last: a future phase must establish its own
    Secondary-side values rather than assume the populate script's. Both steps use Frank (S14-5) and
    Dave (S14-3), and both stage the Secondary values their assertions depend on, so each reads
    identically under -Step All and standalone. ScopedExceptionAuthority removes its own configuration
    mutation (the exception Synchronisation Rule and the three-way Description order) before returning,
    exactly as Phases D and E restore theirs.

.PARAMETER Step
    Which test step to execute (BaselineResolution, RecallReElection, IdenticalValueHandOver,
    WithdrawalReElection, NoContributorCleared, AssertedNullOverridesSurvivor,
    NotJoinedNoOpinion, MidLifeJoinBlanksClear, MvaNullIsValueAssertsEmptySet,
    DisabledRuleNoOpinion, PriorityReorderPropagation, OutOfScopeNoOpinion,
    EnforceStateCorrectsLoser, ScopedExceptionAuthority, GraceFreezesSoleSource,
    GraceExpiryDeletesAndExports, GraceFallbackFlowsAndExports, GraceAssertedNullBeatsSurvivor,
    GraceExpressionInputFallback, All).
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
    independently runnable. OutOfScopeNoOpinion similarly needs Erin's (S14-4) Description already
    withdrawn on both suffixes (NoContributorCleared's end-state); a dedicated idempotent helper
    (Set-Scenario14ErinDescriptionWithdrawn) establishes it if a standalone run has not already done
    so.

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
    [ValidateSet("BaselineResolution", "RecallReElection", "IdenticalValueHandOver", "WithdrawalReElection", "NoContributorCleared", "AssertedNullOverridesSurvivor", "NotJoinedNoOpinion", "MidLifeJoinBlanksClear", "MvaNullIsValueAssertsEmptySet", "DisabledRuleNoOpinion", "PriorityReorderPropagation", "OutOfScopeNoOpinion", "EnforceStateCorrectsLoser", "ScopedExceptionAuthority", "GraceFreezesSoleSource", "GraceExpiryDeletesAndExports", "GraceFallbackFlowsAndExports", "GraceAssertedNullBeatsSurvivor", "GraceExpressionInputFallback", "All")]
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

# Import helpers. LDAP-Helpers supplies the read side (Invoke-LDAPSearch / Expand-LDIFFoldedLine) that
# the Enforce State export assertions need: they have to observe the directory's ACTUAL state, which
# only an independent LDAP read can show.
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

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

function Get-Scenario14LdapAttribute {
    <#
    .SYNOPSIS
        Reads a single-valued attribute straight from a Scenario 14 OpenLDAP suffix, bypassing JIM.

    .DESCRIPTION
        The Enforce State export assertions (EnforceStateCorrectsLoser, ScopedExceptionAuthority) must
        observe the Connected System's ACTUAL state, not JIM's view of it. A Connected System Object
        mirrors whatever the last import saw, so asserting against it would only prove that JIM
        remembers staging a Pending Export, never that the directory itself was corrected; an
        independent LDAP read is the only assertion that distinguishes the two.

        Returns $null when the entry carries no value for the attribute, which is itself a meaningful
        outcome (an export that cleared the attribute rather than rewriting it).
    #>
    param(
        [Parameter(Mandatory=$true)] [hashtable]$LdapConfig,
        [Parameter(Mandatory=$true)] [string]$Uid,
        [Parameter(Mandatory=$true)] [string]$AttributeName
    )

    $raw = Invoke-LDAPSearch -ContainerName $LdapConfig.ContainerName -Server "localhost" -Port $LdapConfig.Port `
        -BaseDN $LdapConfig.UserContainer -BindDN $LdapConfig.BindDN -BindPassword $LdapConfig.BindPassword `
        -Filter "(uid=$Uid)" -Attributes @($AttributeName)
    if ($null -eq $raw) {
        throw "ldapsearch for uid=$Uid under $($LdapConfig.UserContainer) returned nothing; the entry is missing or the directory is unreachable."
    }

    # Unfold first: ldapsearch wraps values at 78 columns, and Scenario 14's description strings sit
    # close enough to that boundary that a naive line-by-line read would silently truncate them.
    foreach ($line in (Expand-LDIFFoldedLine -RawLdif ($raw -join "`n"))) {
        # "attr:: <base64>" is ldapsearch's encoding for values it cannot represent safely in plain
        # LDIF (leading/trailing whitespace, non-ASCII). Decode rather than treating it as a miss.
        # Case-insensitive: LDAP attribute descriptions are case-insensitive and slapd echoes back the
        # schema's canonical spelling, which need not match what was asked for.
        if ($line.StartsWith("${AttributeName}:: ", [System.StringComparison]::OrdinalIgnoreCase)) {
            return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($line.Substring($AttributeName.Length + 3)))
        }
        if ($line.StartsWith("${AttributeName}: ", [System.StringComparison]::OrdinalIgnoreCase)) {
            return $line.Substring($AttributeName.Length + 2)
        }
    }

    return $null
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
    # Export exists on Secondary only: it is the sole system with an export Synchronisation Rule
    # (Setup-Scenario14.ps1 Step 10b), because Secondary is the loser whose divergence Enforce State
    # corrects. Primary is the winner and is never written to.
    $secondaryExport = $secondaryProfiles | Where-Object { $_.name -eq "Export" }

    if (-not $primaryFullImport -or -not $secondaryFullImport -or -not $primaryFullSync -or -not $secondaryFullSync -or -not $secondaryExport) {
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
        # Fixed six-user dataset: importing more means the directory retained stale objects from an earlier
        # scenario (see Assert-ImportedObjectCount). Fail loudly here rather than hours into a full regression.
        Assert-ImportedObjectCount -ActivityId $importResult.activityId -Expected 6 -Name "Full Import (Primary)"

        Write-Host "Running Full Import (Secondary)..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Secondary)"
        Assert-ImportedObjectCount -ActivityId $importResult.activityId -Expected 6 -Name "Full Import (Secondary)"

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

    # Used solely by OutOfScopeNoOpinion: its Description assertion needs Erin's (S14-4)
    # Description to already carry no contributor on EITHER suffix (NoContributorCleared's, Test
    # 5, end-state), otherwise a scope exit would hand Description over to Secondary exactly like
    # Job Title, and the "-ExpectNoValue" assertion would fail. Under -Step All this holds for
    # free; a standalone -Step OutOfScopeNoOpinion run only gets the plain baseline (Step 0b),
    # where Erin's Description is still Primary's original value, so this idempotent helper
    # establishes the precondition itself. Mirrors Set-Scenario14AttributePrimaryNullIsValue's
    # check-then-mutate shape: probe the current state via Assert-MvoAttributeValue (catching its
    # thrown assertion failure as "not yet withdrawn" rather than duplicating the psql query), only
    # withdrawing if the probe shows a real value still present.
    function Set-Scenario14ErinDescriptionWithdrawn {
        param(
            [Parameter(Mandatory=$true)]
            [guid]$ErinMvoId
        )

        $alreadyWithdrawn = $true
        try {
            Assert-MvoAttributeValue -MvoId $ErinMvoId -AttributeName "Description" -ExpectNoValue `
                -Name "Erin's Description precondition probe (idempotent check)"
        }
        catch {
            $alreadyWithdrawn = $false
        }

        if ($alreadyWithdrawn) {
            Write-Host "  Erin's Description already has no contributor on either suffix (idempotent no-op; NoContributorCleared already ran)" -ForegroundColor Gray
            return
        }

        Write-Host "  Erin's Description not yet withdrawn (standalone run); withdrawing both suffixes to match NoContributorCleared's end-state..." -ForegroundColor Gray

        $erinSecondaryDn = "uid=erin14,$($secondaryLdapConfig.UserContainer)"
        $withdrawSecondaryLdif = "dn: $erinSecondaryDn`nchangetype: modify`ndelete: description`n"
        Invoke-Scenario14LdapModify -ContainerName $secondaryLdapConfig.ContainerName -LdapUri $secondaryLdapUri `
            -BindDN $secondaryLdapConfig.BindDN -BindPassword $secondaryLdapConfig.BindPassword -Ldif $withdrawSecondaryLdif

        $erinPrimaryDn = "uid=erin14,$($primaryLdapConfig.UserContainer)"
        $withdrawPrimaryLdif = "dn: $erinPrimaryDn`nchangetype: modify`ndelete: description`n"
        Invoke-Scenario14LdapModify -ContainerName $primaryLdapConfig.ContainerName -LdapUri $primaryLdapUri `
            -BindDN $primaryLdapConfig.BindDN -BindPassword $primaryLdapConfig.BindPassword -Ldif $withdrawPrimaryLdif

        # Both suffixes already carry no description before either import runs, so a normal
        # baseline re-sync (Import both, then Sync both) converges directly on "no contributor,
        # cleared" without needing NoContributorCleared's own step-by-step ordering (which proves
        # the loser-then-winner sequencing separately; that invariant is not re-proven here, only
        # the end-state is required).
        Invoke-Scenario14BaselineRuns

        Assert-MvoAttributeValue -MvoId $ErinMvoId -AttributeName "Description" -ExpectNoValue `
            -Name "Erin's Description (withdrawn both suffixes; precondition established for a standalone run)"
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

    # ========================================================================
    # Test 12: OutOfScopeNoOpinion
    # ========================================================================
    if ($Step -eq "OutOfScopeNoOpinion" -or $Step -eq "All") {
        Write-TestSection "Test 12: Out Of Scope, No Opinion (Erin, scope exit re-elects Secondary in the same run)"

        $outOfScopeSuccess = $true
        $outOfScopeNotes = @()

        try {
            $primaryImportRuleName = "$primarySystemName Import Users"
            $secondaryImportRuleName = "$secondarySystemName Import Users"

            $primaryImportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $primaryImportRuleName } | Select-Object -First 1
            if (-not $primaryImportRule) {
                throw "Could not resolve '$primaryImportRuleName' Synchronisation Rule."
            }

            $erinMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-4" -PageSize 5) | Select-Object -First 1
            $bobMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-1" -PageSize 5) | Select-Object -First 1
            $daveMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-3" -PageSize 5) | Select-Object -First 1
            if (-not $erinMvo -or -not $bobMvo -or -not $daveMvo) {
                throw "Could not resolve Erin (S14-4), Bob (S14-1) and/or Dave (S14-3) Metaverse Objects."
            }

            # Standalone-run precondition (see the OutOfScopeNoOpinion/Phase E docstring paragraph
            # above): this step's Description assertion below only holds if Erin's Description
            # already has no contributor on either suffix. Idempotent no-op under -Step All, since
            # NoContributorCleared (Test 5) has already established it by this point.
            Set-Scenario14ErinDescriptionWithdrawn -ErinMvoId $erinMvo.id

            # Exclude ONLY Erin from the Primary import Synchronisation Rule's scope. employeeNumber
            # NotEquals "S14-4" is the cleanest criterion the operators support (New-JIMScopingCriterion's
            # -ComparisonType set includes NotEquals; src/JIM.PowerShell/Public/ScopingCriteria/
            # New-JIMScopingCriterion.ps1): a single criterion on Erin's own join key keeps every
            # other user in scope, including Grace (S14-6), who by this point under -Step All is
            # joined to Primary too (MidLifeJoinBlanksClear). An Equals-based "match everyone except
            # Erin" formulation would need one clause per surviving user and would silently stop
            # covering any user added afterwards; NotEquals on the excluded subject's own key scales
            # without maintenance and is the only one of the two that does.
            Write-Host "Scoping Erin (S14-4) out of the Primary import rule (employeeNumber NotEquals 'S14-4')..." -ForegroundColor Gray

            # Resolve the employeeNumber attribute id from the Primary system's schema and pass
            # -ConnectedSystemAttributeId, per the Scenario 10 precedent. The cmdlet's
            # -ConnectedSystemAttributeName resolution path depends on the object-types API endpoint
            # returning attribute collections, which it does not, so name resolution fails with
            # "Could not find object type attributes.".
            $primaryObjectTypes = @(Get-JIMConnectedSystem -Id $primarySystem.id -ObjectTypes)
            $primaryUserType = $primaryObjectTypes | Where-Object { $_.name -eq "inetOrgPerson" }
            $employeeNumberAttr = $primaryUserType.attributes | Where-Object { $_.name -eq "employeeNumber" }
            if (-not $employeeNumberAttr) {
                throw "'employeeNumber' attribute not found on the Primary system's inetOrgPerson object type."
            }

            $scopeGroup = New-JIMScopingCriteriaGroup -SyncRuleId $primaryImportRule.id -Type All -PassThru
            New-JIMScopingCriterion -SyncRuleId $primaryImportRule.id -GroupId $scopeGroup.id `
                -ConnectedSystemAttributeId $employeeNumberAttr.id -ComparisonType NotEquals -StringValue "S14-4" | Out-Null

            $persistedCriteria = @(Get-JIMScopingCriteria -SyncRuleId $primaryImportRule.id)
            if ($persistedCriteria.Count -ne 1 -or @($persistedCriteria[0].criteria).Count -ne 1) {
                throw "Scoping criteria read-back mismatch: expected exactly one group with one criterion on '$primaryImportRuleName', found $($persistedCriteria.Count) group(s)."
            }
            Write-Host "  OK Scoping criteria group $($scopeGroup.id) persisted and verified via read-back" -ForegroundColor Green

            # InboundOutOfScopeAction defaults to Disconnect (SyncRule.cs) and Setup-Scenario14.ps1
            # never overrides it for either rule, so Erin's Primary CSO going out of scope breaks
            # the join outright via HandleCsoOutOfScopeAsync's Disconnect branch.
            Write-Host "Running Full Import (Primary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after scoping Erin out"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after scoping Erin out"

            # HandleCsoOutOfScopeAsync's Disconnect branch (src/JIM.Worker/Processors/
            # SyncTaskProcessorBase.cs) now calls ReElectSurvivingContributorsAsync, queues export
            # evaluation and emits NoContributor outcomes for attributes with no survivor (commits
            # b9471c7, 8c25ffc; proven at the workflow-test level by the ScopeExit_* tests in
            # test/JIM.Worker.Tests/Workflows/AttributePriorityRecallWorkflowTests.cs). Job Title and
            # Other Telephones both had Primary as their sole contributor going into this step, so
            # both hand over to Secondary in the same run.
            Assert-MvoAttributeValue -MvoId $erinMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Consultant (Secondary)" `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Erin's Job Title (scope exit re-elects Secondary, same run)"

            Assert-MvoAttributeValue -MvoId $erinMvo.id -AttributeName "Other Telephones" `
                -ExpectedValues @("+44 20 7946 2040", "+44 20 7946 2041") `
                -Name "Erin's Other Telephones (full MVA hand-over to Secondary's set)"

            # Manager: Secondary's rotation offset is 3, so (4 + 3) % 6 = index 1 = Bob (S14-1).
            # Bob's own Metaverse Object survives independently of this step: IdenticalValueHandOver
            # (Test 3) deleted his Primary entry outright, so he has been joined via his Secondary
            # CSO alone since Phase B; resolved above via the same Employee ID lookup as every other
            # subject in this file.
            Assert-MvoAttributeValue -MvoId $erinMvo.id -AttributeName "Manager" `
                -ExpectedReferenceMvoId $bobMvo.id `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Erin's Manager (re-elected reference: Bob, Secondary's rotation offset 3)"

            # Description has no surviving contributor to hand over to: Set-Scenario14ErinDescriptionWithdrawn
            # above guarantees both suffixes already carry no value for it, so the scope exit finds
            # nothing to recall and nothing to re-elect. The NullIsValue flag inherited on Primary's
            # Job Title mapping since Phase C (Set-Scenario14AttributePrimaryNullIsValue) has no
            # bearing on Job Title here either: NullIsValue only changes behaviour for a mapping that
            # IS evaluated (the ConnectedNoValue state), and Primary is not evaluated for Erin at all
            # post-scope-exit, since its CSO is no longer joined to her Metaverse Object at all
            # (RuleNotApplicable in all but name), exactly like Alice/Bob's no-Primary-CSO-at-all
            # state documented in AssertedNullOverridesSurvivor's blast-radius notes.
            Assert-MvoAttributeValue -MvoId $erinMvo.id -AttributeName "Description" `
                -ExpectNoValue `
                -Name "Erin's Description (still absent: no surviving contributor either side)"

            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Coordinator (Primary)" -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Dave's Job Title (control: unaffected by Erin's scope exit)"

            $outOfScopeNotes += "Erin's Primary CSO fell out of scope (Disconnect); Job Title, Manager and Other Telephones re-elected to Secondary in the same run, Description stayed absent (no survivor), Dave unaffected"

            # Restore: remove the scoping criteria group and re-admit Erin. Mirrors the removal
            # mechanism Scenario 10 uses to fully unscope a rule (Invoke-Scenario10-SyncRuleScoping.ps1,
            # Reset-JIMForCascadeTest): iterate every root-level group and delete it, leaving the rule
            # with zero scoping criteria groups, which is unconditionally in scope for every object.
            Write-Host "Removing Erin's scoping criteria group..." -ForegroundColor Gray
            foreach ($group in @(Get-JIMScopingCriteria -SyncRuleId $primaryImportRule.id)) {
                Remove-JIMScopingCriteriaGroup -SyncRuleId $primaryImportRule.id -GroupId $group.id -Confirm:$false | Out-Null
            }
            $criteriaAfterRemoval = @(Get-JIMScopingCriteria -SyncRuleId $primaryImportRule.id)
            if ($criteriaAfterRemoval.Count -ne 0) {
                throw "Expected zero scoping criteria groups on '$primaryImportRuleName' after removal, found $($criteriaAfterRemoval.Count)."
            }
            Write-Host "  OK Scoping criteria group removed and verified via read-back" -ForegroundColor Green

            Write-Host "Running Full Import (Primary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after re-admitting Erin"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after re-admitting Erin"

            # Re-entry to scope: Erin's Primary CSO was left NotJoined by HandleCsoOutOfScopeAsync
            # (JoinType=NotJoined, MetaverseObjectId=null), but the CSO itself was never deleted, so
            # a normal Full Synchronisation re-runs the Employee ID matching rule and rejoins her
            # EXISTING Metaverse Object rather than projecting a new one, exactly as
            # MidLifeJoinBlanksClear (Test 8) already proves for a not-joined CSO joining an
            # established object. No step beyond a normal Full Import + Full Synchronisation is
            # required for the rejoin.
            $erinMvoAfterRejoin = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-4" -PageSize 5)
            if ($erinMvoAfterRejoin.Count -ne 1 -or $erinMvoAfterRejoin[0].id -ne $erinMvo.id) {
                throw "Erin's Primary CSO did not rejoin her existing Metaverse Object (ID $($erinMvo.id)) on scope re-entry; found $($erinMvoAfterRejoin.Count) Metaverse Object(s)."
            }

            Assert-MvoAttributeValue -MvoId $erinMvo.id -AttributeName "Job Title" `
                -ExpectedValue "Consultant (Primary)" -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Erin's Job Title (re-entry to scope rejoins; Primary retakes)"

            Assert-MvoAttributeValue -MvoId $erinMvo.id -AttributeName "Other Telephones" `
                -ExpectedValues @("+44 20 7946 1040", "+44 20 7946 1041") `
                -Name "Erin's Other Telephones (Primary retakes on rejoin)"

            Assert-MvoAttributeValue -MvoId $erinMvo.id -AttributeName "Manager" `
                -ExpectedReferenceMvoId (@(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-5" -PageSize 5) | Select-Object -First 1).id `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Erin's Manager (Primary retakes on rejoin: Frank, Primary's rotation offset 1)"

            $outOfScopeNotes += "Removed the scoping criteria group; Erin's Primary CSO rejoined her existing Metaverse Object via Employee ID matching, and Job Title, Manager and Other Telephones returned to Primary"
        }
        catch {
            $outOfScopeSuccess = $false
            $outOfScopeNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "OutOfScopeNoOpinion"
                Success = $outOfScopeSuccess
                Note = ($outOfScopeNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 13: EnforceStateCorrectsLoser
    # ========================================================================
    if ($Step -eq "EnforceStateCorrectsLoser" -or $Step -eq "All") {
        Write-TestSection "Test 13: Enforce State Corrects The Loser (Frank, a direct Secondary edit corrected by export)"

        $enforceSuccess = $true
        $enforceNotes = @()

        try {
            $primaryImportRuleName = "$primarySystemName Import Users"

            $frankMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-5" -PageSize 5) | Select-Object -First 1
            $daveMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-3" -PageSize 5) | Select-Object -First 1
            if (-not $frankMvo -or -not $daveMvo) {
                throw "Could not resolve Frank (S14-5) and/or Dave (S14-3) Metaverse Objects."
            }

            $frankUid = "frank14"
            $frankWinningDescription = "Primary-sourced description for Frank Foster (S14)"

            # Converge the loser first. The Enforce State export rule has existed since setup, so every
            # Full Synchronisation (Secondary) run by an earlier step has been staging corrective
            # Pending Exports for the whole population (Secondary's seeded description/title differ from
            # the Metaverse values Primary won). Draining that backlog BEFORE the direct edit below is
            # what makes this test honest: without it, the correction observed at the end could be the
            # baseline divergence being cleaned up rather than the direct edit being reverted, and the
            # step would pass even if a losing contributor's change were never corrected at all.
            #
            # Blast radius: this converges Description and Job Title for EVERY joined Secondary object,
            # not just Frank's, and it is a one-way change to the directory's contents (Erin's
            # description and Frank's/Grace's title are cleared outright, since their Metaverse values
            # are absent or asserted null by Phases B and C). Nothing later in this file reads
            # Secondary's seeded description or title values, and Phase F is the last phase, so this is
            # contained; a future phase must not assume Secondary still carries its populate-time values.
            Write-Host "Running Export (Secondary) to converge the losing system onto the Metaverse's values..." -ForegroundColor Gray
            $exportResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryExport.id -Wait -PassThru
            Assert-ExportSuccess -ActivityId $exportResult.activityId -Name "Export (Secondary) baseline convergence"

            Write-Host "Running Full Import (Secondary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Secondary) after baseline convergence"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Secondary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Secondary) after baseline convergence"

            $converged = Get-Scenario14LdapAttribute -LdapConfig $secondaryLdapConfig -Uid $frankUid -AttributeName "description"
            if ($converged -ne $frankWinningDescription) {
                throw "Convergence precondition failed: expected the Enforce State export to have written the Metaverse value '$frankWinningDescription' into Frank's Secondary entry, found '$converged'."
            }
            Write-Host "  OK Frank's Secondary 'description' converged on the winning value; the directory and the Metaverse now agree" -ForegroundColor Green
            $enforceNotes += "Drained the Enforce State export backlog so the correction below is attributable to the direct edit alone"

            # The direct change in the LOSING system: an administrator editing the lower-priority
            # directory by hand, which is exactly the "direct AD 1 changes to non-exception groups" leg
            # of worked example 2 (engineering/plans/doing/ATTRIBUTE_PRIORITY.md).
            $frankDirectEdit = "Directly edited in the Secondary directory for Frank Foster (S14)"
            Write-Host "Editing Frank's Secondary 'description' directly in the directory..." -ForegroundColor Gray
            $editLdif = "dn: uid=$frankUid,$($secondaryLdapConfig.UserContainer)`nchangetype: modify`nreplace: description`ndescription: $frankDirectEdit`n"
            Invoke-Scenario14LdapModify -ContainerName $secondaryLdapConfig.ContainerName -LdapUri $secondaryLdapUri `
                -BindDN $secondaryLdapConfig.BindDN -BindPassword $secondaryLdapConfig.BindPassword -Ldif $editLdif

            Write-Host "Running Full Import (Secondary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Secondary) after Frank's direct Secondary edit"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            Write-Host "Running Full Synchronisation (Secondary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Secondary) after Frank's direct Secondary edit"

            # Inbound leg: the losing contribution never reaches the Metaverse. Sync flow stays linear;
            # the import still updated the Connected System Object (it mirrors the source system's real
            # state), the contribution simply lost resolution to Primary at priority 1.
            Assert-MvoAttributeValue -MvoId $frankMvo.id -AttributeName "Description" `
                -ExpectedValue $frankWinningDescription `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Frank's Description (the losing system's direct edit never reaches the Metaverse)"

            # The divergence must still be present in the directory at this point: inbound processing
            # never writes to a Connected System, so nothing has corrected it yet. Asserting this
            # explicitly separates "the export corrected it" from "it was never diverged".
            $beforeCorrection = Get-Scenario14LdapAttribute -LdapConfig $secondaryLdapConfig -Uid $frankUid -AttributeName "description"
            if ($beforeCorrection -ne $frankDirectEdit) {
                throw "Expected Frank's Secondary entry to still carry the direct edit '$frankDirectEdit' before the corrective export ran, found '$beforeCorrection'."
            }
            Write-Host "  OK Secondary still diverged after synchronisation (inbound processing never writes to a Connected System)" -ForegroundColor Green

            # Outbound leg: the correction is the ordinary export path. Secondary is also an export
            # target, so export evaluation finds its actual state differs from the state derived from
            # the Metaverse Object and stages a corrective Pending Export like any other.
            Write-Host "Running Export (Secondary)..." -ForegroundColor Gray
            $exportResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryExport.id -Wait -PassThru
            Assert-ExportSuccess -ActivityId $exportResult.activityId -Name "Export (Secondary) correcting Frank's direct edit"

            $afterCorrection = Get-Scenario14LdapAttribute -LdapConfig $secondaryLdapConfig -Uid $frankUid -AttributeName "description"
            if ($afterCorrection -ne $frankWinningDescription) {
                throw "Enforce State did not correct the losing system: expected Frank's Secondary 'description' to be restored to '$frankWinningDescription', found '$afterCorrection'."
            }
            Write-Host "  OK Frank's Secondary 'description' corrected back to the winning contributor's value" -ForegroundColor Green

            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Description" `
                -ExpectedValue "Primary-sourced description for Dave Dixon (S14)" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Dave's Description (control: unaffected by Frank's direct edit and its correction)"

            $enforceNotes += "Frank's direct Secondary edit lost resolution to Primary (priority 1), stayed out of the Metaverse, and was corrected back in the directory by the Enforce State export; Dave unaffected"
        }
        catch {
            $enforceSuccess = $false
            $enforceNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "EnforceStateCorrectsLoser"
                Success = $enforceSuccess
                Note = ($enforceNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 14: ScopedExceptionAuthority
    # ========================================================================
    if ($Step -eq "ScopedExceptionAuthority" -or $Step -eq "All") {
        Write-TestSection "Test 14: Scoped Exception Authority (per-object authority: Dave in scope, Frank out)"

        $exceptionSuccess = $true
        $exceptionNotes = @()
        $exceptionRuleName = "$primarySystemName Import Users (Exceptions)"
        $exceptionRule = $null

        try {
            $primaryImportRuleName = "$primarySystemName Import Users"
            $secondaryImportRuleName = "$secondarySystemName Import Users"

            $daveMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-3" -PageSize 5) | Select-Object -First 1
            $frankMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-5" -PageSize 5) | Select-Object -First 1
            if (-not $daveMvo -or -not $frankMvo) {
                throw "Could not resolve Dave (S14-3) and/or Frank (S14-5) Metaverse Objects."
            }

            $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
            $mvDescriptionAttr = @(Get-JIMMetaverseAttribute) | Where-Object { $_.name -eq "Description" }
            if (-not $mvUserType -or -not $mvDescriptionAttr) {
                throw "Could not resolve the Metaverse 'User' object type and/or the 'Description' attribute."
            }

            $primaryObjectTypes = @(Get-JIMConnectedSystem -Id $primarySystem.id -ObjectTypes)
            $primaryUserType = $primaryObjectTypes | Where-Object { $_.name -eq "inetOrgPerson" }
            $employeeNumberAttr = $primaryUserType.attributes | Where-Object { $_.name -eq "employeeNumber" }
            $descriptionCsAttr = $primaryUserType.attributes | Where-Object { $_.name -eq "description" }
            if (-not $employeeNumberAttr -or -not $descriptionCsAttr) {
                throw "'employeeNumber' and/or 'description' not found on the Primary system's inetOrgPerson object type."
            }

            # A SECOND import Synchronisation Rule on the SAME Connected System, narrowly scoped. This is
            # the whole mechanism behind fine-grained authority: priority list entries are Synchronisation
            # Rules, not Connected Systems, so a system can hold two positions in one attribute's list and
            # authority becomes per object rather than per system. Deliberately created WITHOUT
            # -ProjectToMetaverse: the plain Primary rule already projects, and a second projecting rule on
            # the same system would add nothing here beyond a second chance to project the same objects.
            Write-Host "Creating the scoped exception Synchronisation Rule on Primary..." -ForegroundColor Gray
            $exceptionRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $exceptionRuleName } | Select-Object -First 1
            if (-not $exceptionRule) {
                $exceptionRule = New-JIMSyncRule -Name $exceptionRuleName `
                    -ConnectedSystemId $primarySystem.id `
                    -ConnectedSystemObjectTypeId $primaryUserType.id `
                    -MetaverseObjectTypeId $mvUserType.id `
                    -Direction Import `
                    -PassThru
            }

            # Scope it to Dave (S14-3) alone. Equals on the excluded-from-nothing subject's own join key
            # is the right operator here, the mirror image of OutOfScopeNoOpinion's NotEquals: there the
            # criterion had to keep everyone EXCEPT one subject in scope, here it must admit ONLY one.
            if (@(Get-JIMScopingCriteria -SyncRuleId $exceptionRule.id).Count -eq 0) {
                $scopeGroup = New-JIMScopingCriteriaGroup -SyncRuleId $exceptionRule.id -Type All -PassThru
                New-JIMScopingCriterion -SyncRuleId $exceptionRule.id -GroupId $scopeGroup.id `
                    -ConnectedSystemAttributeId $employeeNumberAttr.id -ComparisonType Equals -StringValue "S14-3" | Out-Null
            }

            $exceptionMappings = @(Get-JIMSyncRuleMapping -SyncRuleId $exceptionRule.id)
            $exceptionMapping = $exceptionMappings | Where-Object { $_.targetMetaverseAttributeId -eq $mvDescriptionAttr.id } | Select-Object -First 1
            if (-not $exceptionMapping) {
                $exceptionMapping = New-JIMSyncRuleMapping -SyncRuleId $exceptionRule.id `
                    -TargetMetaverseAttributeId $mvDescriptionAttr.id `
                    -SourceConnectedSystemAttributeId $descriptionCsAttr.id
            }
            Write-Host "  OK '$exceptionRuleName' created, scoped to employeeNumber = 'S14-3', contributing 'Description'" -ForegroundColor Green

            # Order Description as exception=1, Secondary=2, Primary's plain rule=3. Contributors are
            # matched on syncRuleName, not connectedSystemName: two of the three now belong to the SAME
            # Connected System, which is precisely the configuration a per-system priority model cannot
            # express (see the plan's "Traditional ILM systems cannot express this" note).
            $priorityBefore = Get-JIMMetaverseAttributePriority -AttributeId $mvDescriptionAttr.id -ObjectTypeId $mvUserType.id
            $contributorsBefore = @($priorityBefore.contributors)
            $plainPrimaryContributor = $contributorsBefore | Where-Object { $_.syncRuleName -eq $primaryImportRuleName } | Select-Object -First 1
            $secondaryContributor = $contributorsBefore | Where-Object { $_.syncRuleName -eq $secondaryImportRuleName } | Select-Object -First 1
            if (-not $plainPrimaryContributor -or -not $secondaryContributor) {
                throw "Could not resolve the plain Primary and Secondary 'Description' contributors: found $(@($contributorsBefore | ForEach-Object { $_.syncRuleName }) -join ', ')."
            }

            Write-Host "Ordering 'Description' as Exceptions=1, Secondary=2, Primary=3..." -ForegroundColor Gray
            Set-JIMMetaverseAttributePriority -AttributeId $mvDescriptionAttr.id -ObjectTypeId $mvUserType.id `
                -MappingId @($exceptionMapping.id, $secondaryContributor.mappingId, $plainPrimaryContributor.mappingId) | Out-Null

            $priorityAfter = @((Get-JIMMetaverseAttributePriority -AttributeId $mvDescriptionAttr.id -ObjectTypeId $mvUserType.id).contributors)
            if ($priorityAfter.Count -ne 3 -or
                $priorityAfter[0].syncRuleName -ne $exceptionRuleName -or $priorityAfter[0].priority -ne 1 -or
                $priorityAfter[1].syncRuleName -ne $secondaryImportRuleName -or $priorityAfter[1].priority -ne 2 -or
                $priorityAfter[2].syncRuleName -ne $primaryImportRuleName -or $priorityAfter[2].priority -ne 3) {
                throw "'Description' priority read-back mismatch: expected Exceptions(1)/Secondary(2)/Primary(3), got $(@($priorityAfter | ForEach-Object { "$($_.syncRuleName)=$($_.priority)" }) -join ', ')."
            }
            Write-Host "  OK 'Description' ordered Exceptions=1, Secondary=2, Primary=3 and verified via read-back" -ForegroundColor Green

            # Give both subjects distinctive Secondary values, so this step's assertions read identically
            # under -Step All (where EnforceStateCorrectsLoser has already rewritten Secondary's
            # descriptions to the Metaverse values) and standalone (where they are still the populate
            # script's seeded values). Establishing its own preconditions rather than inheriting them is
            # the same discipline Set-Scenario14ErinDescriptionWithdrawn applies for Phase E.
            $daveDirectEdit = "Directly edited in the Secondary directory for Dave Dixon (S14)"
            $frankSecondaryDescription = "Exception-era Secondary description for Frank Foster (S14)"

            # Confirm any outstanding export BEFORE overwriting the directory. Under -Step All,
            # EnforceStateCorrectsLoser's last act is an Export (Secondary) that wrote 'description'
            # values JIM has not yet seen back; overwriting them here without importing first would
            # have the confirming import find a different value and correctly report the export as
            # unconfirmed (an ExportNotConfirmed RPEI, and a CompleteWithWarning Activity). That is JIM
            # behaving properly, not a defect, so the test performs the confirming import rather than
            # tolerating the warning. A standalone run has nothing outstanding and this is a no-op.
            Write-Host "Running Full Import (Secondary) to confirm any outstanding export..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Secondary) confirming outstanding exports"

            Write-Host "Staging Dave's and Frank's Secondary 'description' values..." -ForegroundColor Gray
            foreach ($staged in @(
                @{ Uid = "dave14"; Value = $daveDirectEdit }
                @{ Uid = "frank14"; Value = $frankSecondaryDescription }
            )) {
                $stageLdif = "dn: uid=$($staged.Uid),$($secondaryLdapConfig.UserContainer)`nchangetype: modify`nreplace: description`ndescription: $($staged.Value)`n"
                Invoke-Scenario14LdapModify -ContainerName $secondaryLdapConfig.ContainerName -LdapUri $secondaryLdapUri `
                    -BindDN $secondaryLdapConfig.BindDN -BindPassword $secondaryLdapConfig.BindPassword -Ldif $stageLdif
            }

            Write-Host "Running Full Import (Secondary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Secondary) with the exception rule in place"

            if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

            # Both systems get a Full Synchronisation: a priority change only propagates to objects a
            # synchronisation actually processes (PriorityReorderPropagation, Test 11), and the newly
            # created exception rule has never been evaluated against Primary's Connected System Objects
            # at all, which only a Full Synchronisation (Primary) does.
            Write-Host "Running Full Synchronisation (Secondary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Secondary) with the exception rule in place"

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) with the exception rule in place"

            # Dave is in the exception rule's scope, so Primary's value wins at priority 1 with the
            # EXCEPTION rule's provenance, not the plain rule's. Asserting the contributing rule name is
            # what makes this a test of the scoped rule rather than of Primary generally: the two rules
            # carry the same value from the same directory entry, and only the provenance distinguishes
            # which one won.
            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Description" `
                -ExpectedValue "Primary-sourced description for Dave Dixon (S14)" `
                -ExpectedContributingSyncRuleName $exceptionRuleName `
                -Name "Dave's Description (in the exception rule's scope: priority 1 wins)"

            # Frank is outside the exception rule's scope, so that rule has no opinion for him and
            # Secondary at priority 2 beats Primary's plain rule at priority 3. Same two Connected
            # Systems, same attribute, opposite winner: authority is per object.
            Assert-MvoAttributeValue -MvoId $frankMvo.id -AttributeName "Description" `
                -ExpectedValue $frankSecondaryDescription `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Frank's Description (outside the exception rule's scope: Secondary at 2 beats Primary's plain rule at 3)"

            $exceptionNotes += "With Description ordered Exceptions=1/Secondary=2/Primary=3, Dave (in scope) resolved to Primary's value via the exception rule while Frank (out of scope) resolved to Secondary's: per-object authority across the same two systems"

            # The export leg, for both outcomes at once. Dave's Secondary entry diverges from the
            # Metaverse (it lost), so Enforce State corrects it; Frank's Secondary entry IS the Metaverse
            # value (it won), so there is nothing to correct. Asserting the second is what proves the
            # correction is priority-driven rather than a blanket overwrite of the export target.
            Write-Host "Running Export (Secondary)..." -ForegroundColor Gray
            $exportResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryExport.id -Wait -PassThru
            Assert-ExportSuccess -ActivityId $exportResult.activityId -Name "Export (Secondary) correcting Dave's direct edit"

            $daveAfterCorrection = Get-Scenario14LdapAttribute -LdapConfig $secondaryLdapConfig -Uid "dave14" -AttributeName "description"
            if ($daveAfterCorrection -ne "Primary-sourced description for Dave Dixon (S14)") {
                throw "Enforce State did not correct the losing system for an in-scope exception object: expected Dave's Secondary 'description' to be 'Primary-sourced description for Dave Dixon (S14)', found '$daveAfterCorrection'."
            }
            Write-Host "  OK Dave's Secondary 'description' corrected to the exception rule's winning value" -ForegroundColor Green

            $frankAfterExport = Get-Scenario14LdapAttribute -LdapConfig $secondaryLdapConfig -Uid "frank14" -AttributeName "description"
            if ($frankAfterExport -ne $frankSecondaryDescription) {
                throw "Frank's Secondary 'description' was rewritten to '$frankAfterExport'; the winning contributor's own system must not be corrected (expected '$frankSecondaryDescription')."
            }
            Write-Host "  OK Frank's Secondary 'description' left alone; the winner's own system is never corrected" -ForegroundColor Green

            $exceptionNotes += "Enforce State corrected Dave's Secondary entry (it lost) and left Frank's alone (it won), in the same export"

            # Restore. Deleting the exception rule takes its mapping out of the priority list, leaving
            # Secondary and Primary to densify to 1 and 2 in their current relative order (Secondary
            # first), so the original Primary=1/Secondary=2 must be set back explicitly, exactly as
            # PriorityReorderPropagation restores its own reorder.
            Write-Host "Removing the exception Synchronisation Rule..." -ForegroundColor Gray
            Remove-JIMSyncRule -Id $exceptionRule.id -Force | Out-Null
            if (@(Get-JIMSyncRule) | Where-Object { $_.name -eq $exceptionRuleName }) {
                throw "'$exceptionRuleName' still present after removal."
            }

            Set-JIMMetaverseAttributePriority -AttributeId $mvDescriptionAttr.id -ObjectTypeId $mvUserType.id `
                -MappingId @($plainPrimaryContributor.mappingId, $secondaryContributor.mappingId) | Out-Null

            $priorityRestored = @((Get-JIMMetaverseAttributePriority -AttributeId $mvDescriptionAttr.id -ObjectTypeId $mvUserType.id).contributors)
            if ($priorityRestored.Count -ne 2 -or
                $priorityRestored[0].syncRuleName -ne $primaryImportRuleName -or $priorityRestored[0].priority -ne 1 -or
                $priorityRestored[1].syncRuleName -ne $secondaryImportRuleName -or $priorityRestored[1].priority -ne 2) {
                throw "'Description' priority read-back mismatch after restore: expected Primary(1)/Secondary(2), got $(@($priorityRestored | ForEach-Object { "$($_.syncRuleName)=$($_.priority)" }) -join ', ')."
            }
            Write-Host "  OK Exception rule removed and 'Description' restored to Primary=1, Secondary=2" -ForegroundColor Green

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after removing the exception rule"

            # Dave is the provenance-repair case (#1292). Deleting the Synchronisation Rule that contributed his
            # value nulls the value row's ContributedBySyncRuleId (the FK is ON DELETE SET NULL), and the plain rule
            # then contributes the IDENTICAL string, so the attribute-flow writers diff to nothing: no removal, no
            # addition, and nothing that would carry new provenance. The surviving contributor therefore has to take
            # the row over explicitly, which is what this asserts. Frank's value did change in this step, so his
            # provenance travels with the new value in the ordinary way.
            Assert-MvoAttributeValue -MvoId $daveMvo.id -AttributeName "Description" `
                -ExpectedValue "Primary-sourced description for Dave Dixon (S14)" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Dave's Description (exception rule removed: Primary's plain rule takes over the orphaned value)"

            Assert-MvoAttributeValue -MvoId $frankMvo.id -AttributeName "Description" `
                -ExpectedValue "Primary-sourced description for Frank Foster (S14)" `
                -ExpectedContributingSyncRuleName $primaryImportRuleName `
                -Name "Frank's Description (exception rule removed: authority returns to Primary)"

            $exceptionNotes += "Removed the exception rule and restored Primary=1/Secondary=2; both subjects returned to Primary's plain rule"
        }
        catch {
            $exceptionSuccess = $false
            $exceptionNotes += "Error: $_"
            throw
        }
        finally {
            $testResults.Steps += @{
                Name = "ScopedExceptionAuthority"
                Success = $exceptionSuccess
                Note = ($exceptionNotes -join "; ")
            }
        }
    }

    # Inherited end-state at this point (under -Step All, or after the last Phase D/E step run
    # standalone left its flags/data in place): Phase D's two steps (DisabledRuleNoOpinion,
    # PriorityReorderPropagation) each restore their own configuration mutation (rule Enabled state;
    # Attribute Priority order) before returning, so the state below is UNCHANGED from Phase C's
    # inherited end-state for everyone except Erin. Frank (S14-5) has both Job Title and Other
    # Telephones asserted null with Primary provenance; Grace (S14-6) is joined to BOTH suffixes,
    # with her Job Title asserted null (Primary provenance) and her Description Primary-sourced;
    # NullIsValue is set on Primary's Job Title and Other Telephones mappings. No later Phase C
    # step depends on Job Title/Other Telephones neutrality, and Phase C deliberately does NOT
    # unset these flags at the end of MvaNullIsValueAssertsEmptySet: no unsetting cmdlet call is
    # needed there because nothing downstream in this phase requires it. Phase D's own steps read
    # this same inherited state (both mutate and restore configuration only; neither touches LDAP
    # data), so any future phase inherits it unchanged and must manage its own preconditions against
    # it rather than assuming a clean slate. Phase E (OutOfScopeNoOpinion) restores its own scoping
    # mutation too (the Primary import rule ends with zero Scoping Criteria Groups, exactly as it
    # began), and leaves Erin (S14-4) exactly where Phase B left her: Description absent both sides,
    # Job Title/Manager/Other Telephones back on Primary (Job Title "Consultant (Primary)", Manager
    # Frank via rotation offset 1, Other Telephones the 1040/1041 pair).
    #
    # Phase F then adds the only Connected-System-side change in the file: Secondary's Description and
    # Job Title now hold the Metaverse's values for every joined object rather than the populate
    # script's, and Frank's Secondary Description holds ScopedExceptionAuthority's staged
    # "Exception-era ..." string (his Metaverse value is back on Primary's, so a further Export
    # (Secondary) would correct it; none is run). Metaverse state is unchanged by Phase F: both steps
    # restore their configuration mutations and both subjects end on Primary's Description, exactly as
    # Phase E left them.

    # ========================================================================
    # Phase G: deletion grace period and next-contributor fallback (#1307)
    #
    # Everything above proves re-election with NO grace period configured. These steps configure one
    # on the "User" Metaverse Object Type and re-run the same mechanics, because the grace period
    # changes what happens to an attribute with no surviving contributor: it is frozen (preserved)
    # rather than cleared, so identity-critical single-source values that feed expression-based
    # exports survive the window (SyncTaskProcessorBase.ProcessObsoleteConnectedSystemObjectAsync).
    #
    # The deletion rule is WhenAuthoritativeSourceDisconnected with Primary as the authoritative
    # source, not WhenLastConnectorDisconnected. Under the latter, opening a grace window means
    # disconnecting the subject's LAST connector, which leaves no Connected System Object anywhere to
    # receive the delete Pending Export on expiry, so the expiry step could observe the Metaverse
    # Object vanish but never that anything was deprovisioned. The authoritative-source rule opens the
    # window on Primary's departure while Secondary survives as both the fallback contributor and the
    # export target, which is the configuration all five cells actually need.
    #
    # Grace expiry is made to happen by SHORTENING the grace period to zero rather than by waiting it
    # out. Eligibility is recomputed per housekeeping cycle as
    # LastConnectorDisconnectedDate + Type.DeletionGracePeriod <= now
    # (MetaverseRepository.GetMetaverseObjectsEligibleForDeletionAsync), against the type's CURRENT
    # configuration, so shortening it makes an already-pending object eligible on the next cycle. That
    # is the same condition a real expiry satisfies, reached deterministically instead of by sleeping
    # for the length of the window.
    #
    # Blast radius: the deletion policy is set on the Metaverse Object Type, so it governs every
    # object of that type, not just the step's subject. No step before Phase G disconnects anything
    # after Phase G begins, and each step restores the policy to its inherited state (Manual, no grace
    # period) before returning, so a policy left set cannot leak into a later step or a later run.
    # ========================================================================

    # Idempotent deletion-policy control shared by every Phase G step. Every step sets the policy it
    # needs on entry and restores it in its own finally block, so a standalone single-step run and a
    # -Step All run configure identically.
    function Set-Scenario14UserTypeDeletionPolicy {
        param(
            [Parameter(Mandatory=$true)] [ValidateSet("Manual", "WhenLastConnectorDisconnected", "WhenAuthoritativeSourceDisconnected")]
            [string]$DeletionRule,
            [Parameter(Mandatory=$true)] [TimeSpan]$GracePeriod,
            [Parameter(Mandatory=$false)] [int[]]$TriggerConnectedSystemIds
        )

        $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
        if (-not $mvUserType) {
            throw "Metaverse 'User' object type not found."
        }

        if ($DeletionRule -eq "WhenAuthoritativeSourceDisconnected") {
            Set-JIMMetaverseObjectType -Id $mvUserType.id -DeletionRule $DeletionRule `
                -DeletionGracePeriod $GracePeriod -DeletionTriggerConnectedSystemIds $TriggerConnectedSystemIds | Out-Null
        }
        else {
            Set-JIMMetaverseObjectType -Id $mvUserType.id -DeletionRule $DeletionRule `
                -DeletionGracePeriod $GracePeriod | Out-Null
        }

        # Read back: a deletion policy that silently failed to apply would make every freeze assertion
        # below pass as an ordinary no-grace-period clear, which is the wrong reason to be green.
        $readBack = Get-JIMMetaverseObjectType -Id $mvUserType.id
        if ($readBack.deletionRule -ne $DeletionRule) {
            throw "'User' deletion rule read back as '$($readBack.deletionRule)'; expected '$DeletionRule'."
        }
        $expectedGrace = if ($GracePeriod -eq [TimeSpan]::Zero) { $null } else { $GracePeriod }
        $actualGrace = if ($readBack.deletionGracePeriod) { [TimeSpan]::Parse($readBack.deletionGracePeriod) } else { $null }
        if ($actualGrace -ne $expectedGrace) {
            throw "'User' deletion grace period read back as '$actualGrace'; expected '$expectedGrace'."
        }

        Write-Host "  OK 'User' deletion policy set: $DeletionRule, grace period $(if ($expectedGrace) { $expectedGrace } else { 'none' })" -ForegroundColor Green
        return $mvUserType.id
    }

    # Restores the inherited state: Manual with no grace period, which is what the seed data ships and
    # what every step before Phase G ran against.
    function Restore-Scenario14UserTypeDeletionPolicy {
        Set-Scenario14UserTypeDeletionPolicy -DeletionRule "Manual" -GracePeriod ([TimeSpan]::Zero) | Out-Null
    }

    function Remove-Scenario14LdapEntry {
        param(
            [Parameter(Mandatory=$true)] [hashtable]$LdapConfig,
            [Parameter(Mandatory=$true)] [string]$LdapUri,
            [Parameter(Mandatory=$true)] [string]$Dn,
            [Parameter(Mandatory=$false)] [switch]$IgnoreMissing
        )

        $deleteOutput = docker exec $LdapConfig.ContainerName ldapdelete -x -H $LdapUri -D $LdapConfig.BindDN -w $LdapConfig.BindPassword "$Dn" 2>&1
        if ($LASTEXITCODE -ne 0) {
            if ($IgnoreMissing -and ($deleteOutput -join " ") -match "No such object") {
                Write-Host "  '$Dn' already absent (idempotent no-op)" -ForegroundColor Gray
                return
            }
            throw "ldapdelete failed for '$Dn' (exit $LASTEXITCODE): $deleteOutput"
        }
        Write-Host "  OK Deleted $Dn" -ForegroundColor Green
    }

    # Housekeeping runs in the worker's idle loop on a 60 second cadence (Worker.PerformHousekeepingAsync),
    # so a deleted-yet check has to poll rather than assert once. Returns the Activity-free observation
    # that the object is gone; the delete Pending Export it staged is asserted separately by the caller
    # against the directory itself, which is the only evidence that the deprovision actually happened.
    function Wait-Scenario14MvoDeleted {
        param(
            [Parameter(Mandatory=$true)] [string]$MvoId,
            [Parameter(Mandatory=$true)] [string]$Name,
            [Parameter(Mandatory=$false)] [int]$TimeoutSeconds = 180
        )

        $elapsed = 0
        $interval = 10
        while ($elapsed -lt $TimeoutSeconds) {
            $stillThere = $null
            try { $stillThere = Get-JIMMetaverseObject -Id $MvoId } catch { $stillThere = $null }
            if (-not $stillThere) {
                Write-Host "  OK $Name deleted by housekeeping after ~${elapsed}s" -ForegroundColor Green
                return
            }
            Start-Sleep -Seconds $interval
            $elapsed += $interval
        }

        throw "$Name was still present ${TimeoutSeconds}s after its grace period was made to expire. Housekeeping deletes on a 60s idle-loop cadence; a timeout here means the object was never eligible (check the deletion rule, the trigger system list, and LastConnectorDisconnectedDate) rather than merely slow."
    }

    # ========================================================================
    # Test 15: GraceFreezesSoleSource
    # ========================================================================
    if ($Step -eq "GraceFreezesSoleSource" -or $Step -eq "All") {
        Write-TestSection "Test 15: Grace Period Freezes a Sole-Source Attribute (Carol)"

        $graceFreezeSuccess = $true
        $graceFreezeNotes = @()

        try {
            $secondaryImportRuleName = "$secondarySystemName Import Users"

            # Common Name is the only attribute in this scenario that Primary alone contributes
            # (Setup-Scenario14.ps1 Step 9b). Every other mapped attribute has a Secondary contributor
            # and therefore a survivor to be re-elected to, which is the opposite of what a freeze is.
            Set-Scenario14UserTypeDeletionPolicy -DeletionRule "WhenAuthoritativeSourceDisconnected" `
                -GracePeriod ([TimeSpan]::FromHours(1)) -TriggerConnectedSystemIds @($primarySystem.id) | Out-Null

            $carolMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-2" -PageSize 5) | Select-Object -First 1
            if (-not $carolMvo) {
                throw "Could not resolve Carol (S14-2) Metaverse Object."
            }

            Assert-MvoAttributeValue -MvoId $carolMvo.id -AttributeName "Common Name" `
                -ExpectedValue "Carol Clarke (S14)" `
                -Name "Carol's Common Name before the disconnect (sole-source, contributed by Primary)"

            Write-Host "Deleting Carol from the Primary suffix..." -ForegroundColor Gray
            Remove-Scenario14LdapEntry -LdapConfig $primaryLdapConfig -LdapUri $primaryLdapUri `
                -Dn "uid=carol14,$($primaryLdapConfig.UserContainer)" -IgnoreMissing

            Write-Host "Running Full Import (Primary)..." -ForegroundColor Gray
            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after Carol's Primary deletion"

            Write-Host "Running Full Synchronisation (Primary)..." -ForegroundColor Gray
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after Carol's Primary deletion"

            # The grace window is open: the object is pending deletion but not yet deleted.
            $carolAfter = Get-JIMMetaverseObject -Id $carolMvo.id
            if (-not $carolAfter) {
                throw "Carol's Metaverse Object was deleted outright. With a one hour grace period configured it must survive the disconnect; a deletion here means the grace period was not applied."
            }
            if (-not $carolAfter.isPendingDeletion) {
                throw "Carol's Metaverse Object is not pending deletion after her authoritative Primary source disconnected. Check the deletion rule and its trigger Connected System list."
            }
            Write-Host "  OK Carol's Metaverse Object is pending deletion, eligible after $($carolAfter.deletionEligibleDate)" -ForegroundColor Green

            # The freeze. Common Name has no surviving contributor, so with NO grace period it would be
            # recalled and cleared; with one configured it is preserved instead. This is the assertion
            # the whole step exists for: turn the grace period off and it fails.
            Assert-MvoAttributeValue -MvoId $carolMvo.id -AttributeName "Common Name" `
                -ExpectedValue "Carol Clarke (S14)" `
                -Name "Carol's Common Name (frozen under the grace period: sole-source, no survivor to re-elect)"

            # ... while the contested attributes hand over normally in the same run. A grace period
            # freezes only what has no survivor; anything with one is still re-elected, which is what
            # makes the freeze safe rather than a blanket recall suppression.
            #
            # Display Name is the attribute asserted on, NOT Job Title, because Job Title cannot show a
            # hand-over by value under -Step All: Phase F's Enforce State export rewrites Secondary's Job
            # Title for EVERY joined object to the Metaverse's value, so by the time Phase G runs, Secondary
            # holds "Analyst (Primary)" too and re-electing it produces the same string Primary contributed.
            # That is the "a future phase must establish its own Secondary-side values rather than assume the
            # populate script's" warning in this file's Phase F notes, and the assertion has to respect it.
            #
            # Both suffixes carry the same Display Name string in either mode, so the hand-over is visible in
            # provenance rather than in the value, exactly as IdenticalValueHandOver (Test 3) proves for
            # Description, and the step reads identically standalone and under -Step All.
            Assert-MvoAttributeValue -MvoId $carolMvo.id -AttributeName "Display Name" `
                -ExpectedValue "Carol Clarke (S14)" `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Carol's Display Name (re-elected to Secondary during the grace window)"

            $graceFreezeNotes += "Carol's sole-source Common Name was frozen under an open grace window while her contested Job Title handed over to Secondary in the same run"
        }
        catch {
            $graceFreezeSuccess = $false
            $graceFreezeNotes += "Error: $_"
            throw
        }
        finally {
            # Restored even though GraceExpiryDeletesAndExports sets it again immediately: a Manual
            # rule is not in housekeeping's eligibility query, so Carol cannot be deleted in the gap
            # between the two steps, and a run that stops here leaves the type as it found it.
            Restore-Scenario14UserTypeDeletionPolicy
            $testResults.Steps += @{
                Name = "GraceFreezesSoleSource"
                Success = $graceFreezeSuccess
                Note = ($graceFreezeNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 16: GraceExpiryDeletesAndExports
    # ========================================================================
    if ($Step -eq "GraceExpiryDeletesAndExports" -or $Step -eq "All") {
        Write-TestSection "Test 16: Grace Period Expiry Deletes and Deprovisions (Carol)"

        $graceExpirySuccess = $true
        $graceExpiryNotes = @()

        try {
            # Continues from GraceFreezesSoleSource under -Step All. Standalone, Carol is still joined
            # to Primary, so the same disconnect is performed here first: the step must establish its
            # own precondition rather than depend on run order (the pattern OutOfScopeNoOpinion
            # established with Set-Scenario14ErinDescriptionWithdrawn).
            Set-Scenario14UserTypeDeletionPolicy -DeletionRule "WhenAuthoritativeSourceDisconnected" `
                -GracePeriod ([TimeSpan]::FromHours(1)) -TriggerConnectedSystemIds @($primarySystem.id) | Out-Null

            $carolSearchResult = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-2" -PageSize 5) | Select-Object -First 1
            if (-not $carolSearchResult) {
                throw "Could not resolve Carol (S14-2) Metaverse Object."
            }
            # The attribute search returns list-shaped results, which carry no deletion state; only the
            # by-id retrieval does. Under Set-StrictMode reading isPendingDeletion off the search result
            # throws rather than returning null, so re-fetch before asking.
            $carolMvo = Get-JIMMetaverseObject -Id $carolSearchResult.id

            if (-not $carolMvo.isPendingDeletion) {
                Write-Host "Carol is not yet pending deletion; disconnecting her Primary entry first..." -ForegroundColor Gray
                Remove-Scenario14LdapEntry -LdapConfig $primaryLdapConfig -LdapUri $primaryLdapUri `
                    -Dn "uid=carol14,$($primaryLdapConfig.UserContainer)" -IgnoreMissing

                $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
                Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) establishing Carol's pending deletion"

                $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
                Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) establishing Carol's pending deletion"

                $carolMvo = Get-JIMMetaverseObject -Id $carolMvo.id
                if (-not $carolMvo.isPendingDeletion) {
                    throw "Carol's Metaverse Object is still not pending deletion after her Primary entry was removed."
                }
            }

            # Carol's Secondary entry must exist for the deprovision to be observable: the assertion
            # that matters is that the DIRECTORY entry goes, not that JIM forgot about it.
            $carolSecondaryCnBefore = Get-Scenario14LdapAttribute -LdapConfig $secondaryLdapConfig -Uid "carol14" -AttributeName "cn"
            if (-not $carolSecondaryCnBefore) {
                throw "Carol's Secondary entry is missing before the expiry step; there is nothing for the delete export to deprovision."
            }

            # Expire the window. Eligibility is recomputed against the type's current grace period on
            # every housekeeping cycle, so zeroing it is exactly the condition a real expiry reaches.
            Write-Host "Expiring the grace period (setting it to zero)..." -ForegroundColor Gray
            Set-Scenario14UserTypeDeletionPolicy -DeletionRule "WhenAuthoritativeSourceDisconnected" `
                -GracePeriod ([TimeSpan]::Zero) -TriggerConnectedSystemIds @($primarySystem.id) | Out-Null

            Wait-Scenario14MvoDeleted -MvoId $carolMvo.id -Name "Carol's Metaverse Object"

            # Housekeeping staged the delete Pending Export; running the Export Run Profile applies it.
            Write-Host "Running Export (Secondary)..." -ForegroundColor Gray
            $exportResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryExport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "Export (Secondary) applying Carol's delete Pending Export"

            $carolSecondaryCnAfter = $null
            try {
                $carolSecondaryCnAfter = Get-Scenario14LdapAttribute -LdapConfig $secondaryLdapConfig -Uid "carol14" -AttributeName "cn"
            }
            catch {
                # Get-Scenario14LdapAttribute throws when the entry itself is gone, which is the
                # outcome under test. An empty result (entry present, attribute absent) is not.
                $carolSecondaryCnAfter = $null
            }

            if ($carolSecondaryCnAfter) {
                throw "Carol's Secondary entry still exists after the delete export ran (cn='$carolSecondaryCnAfter'). The Metaverse Object was deleted but the account was not deprovisioned, which is the failure this cell exists to catch."
            }
            Write-Host "  OK Carol's Secondary directory entry was deleted by the export" -ForegroundColor Green

            $graceExpiryNotes += "Carol's Metaverse Object was deleted once the grace period expired, and the staged delete Pending Export removed her Secondary directory entry"
        }
        catch {
            $graceExpirySuccess = $false
            $graceExpiryNotes += "Error: $_"
            throw
        }
        finally {
            Restore-Scenario14UserTypeDeletionPolicy
            $testResults.Steps += @{
                Name = "GraceExpiryDeletesAndExports"
                Success = $graceExpirySuccess
                Note = ($graceExpiryNotes -join "; ")
            }
        }
    }

    # Shared by GraceFallbackFlowsAndExports and GraceExpressionInputFallback: both need Frank (S14-5)
    # holding a DISTINCT Secondary Display Name and no Primary connector, so the fallback the Metaverse
    # elects is visibly Secondary's rather than a string both suffixes happen to share. Idempotent, so
    # the second caller is a verified no-op under -Step All and does the real work standalone.
    #
    # Frank is the subject rather than Dave (S14-3), who stays the untouched control this file has used
    # throughout, and rather than Grace (S14-6), who does not exist at all in a standalone run's plain
    # baseline. Frank exists at baseline and Phase C only touched his Job Title and Other Telephones,
    # never his Display Name.
    $frankSecondaryDisplayName = "Frank Foster (Secondary S14)"

    function Set-Scenario14FrankPrimaryDisconnectedWithSecondaryDisplayName {
        $frankMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-5" -PageSize 5) | Select-Object -First 1
        if (-not $frankMvo) {
            throw "Could not resolve Frank (S14-5) Metaverse Object."
        }

        # Stage the distinct Secondary value while Primary still wins, then prove it LOST. Without this
        # check the later hand-over assertion could pass against a value Secondary never supplied.
        $frankSecondaryDn = "uid=frank14,$($secondaryLdapConfig.UserContainer)"
        $stageLdif = "dn: $frankSecondaryDn`nchangetype: modify`nreplace: displayName`ndisplayName: $frankSecondaryDisplayName`n"
        Invoke-Scenario14LdapModify -ContainerName $secondaryLdapConfig.ContainerName -LdapUri $secondaryLdapUri `
            -BindDN $secondaryLdapConfig.BindDN -BindPassword $secondaryLdapConfig.BindPassword -Ldif $stageLdif

        $importResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Secondary) staging Frank's distinct Display Name"
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Secondary) staging Frank's distinct Display Name"

        # Only meaningful while Primary is still joined; once disconnected there is nothing to lose to.
        $frankPrimaryDn = "uid=frank14,$($primaryLdapConfig.UserContainer)"
        $frankPrimaryStillPresent = $null
        try { $frankPrimaryStillPresent = Get-Scenario14LdapAttribute -LdapConfig $primaryLdapConfig -Uid "frank14" -AttributeName "cn" } catch { $frankPrimaryStillPresent = $null }
        if ($frankPrimaryStillPresent) {
            Assert-MvoAttributeValue -MvoId $frankMvo.id -AttributeName "Display Name" `
                -ExpectedValue "Frank Foster (S14)" `
                -Name "Frank's Display Name while Primary still contributes (the staged Secondary value must LOSE)"
        }

        Write-Host "Disconnecting Frank from the Primary suffix..." -ForegroundColor Gray
        Remove-Scenario14LdapEntry -LdapConfig $primaryLdapConfig -LdapUri $primaryLdapUri -Dn $frankPrimaryDn -IgnoreMissing

        $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after Frank's Primary disconnect"
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after Frank's Primary disconnect"

        return $frankMvo
    }

    # ========================================================================
    # Test 17: GraceFallbackFlowsAndExports
    # ========================================================================
    if ($Step -eq "GraceFallbackFlowsAndExports" -or $Step -eq "All") {
        Write-TestSection "Test 17: Fallback Flows and Exports Under a Grace Period (Frank)"

        $graceFallbackSuccess = $true
        $graceFallbackNotes = @()

        try {
            $secondaryImportRuleName = "$secondarySystemName Import Users"

            Set-Scenario14UserTypeDeletionPolicy -DeletionRule "WhenAuthoritativeSourceDisconnected" `
                -GracePeriod ([TimeSpan]::FromHours(1)) -TriggerConnectedSystemIds @($primarySystem.id) | Out-Null

            $frankMvo = Set-Scenario14FrankPrimaryDisconnectedWithSecondaryDisplayName

            # The fallback: a grace period does not suppress re-election. An attribute WITH a surviving
            # contributor hands over during the window exactly as it would without one; only attributes
            # with no survivor are frozen (which GraceFreezesSoleSource covers separately).
            Assert-MvoAttributeValue -MvoId $frankMvo.id -AttributeName "Display Name" `
                -ExpectedValue $frankSecondaryDisplayName `
                -ExpectedContributingSyncRuleName $secondaryImportRuleName `
                -Name "Frank's Display Name (re-elected to Secondary's distinct value during the grace window)"

            # Exports succeed carrying the fallback value. The export rule's expression mapping consumes
            # Display Name, so this run is the one that would have staged CN=, had the fallback not
            # fired; an errored Activity here is the failure the cell exists to catch.
            Write-Host "Running Export (Secondary)..." -ForegroundColor Gray
            $exportResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryExport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "Export (Secondary) carrying Frank's fallback Display Name"

            $graceFallbackNotes += "Frank's Display Name handed over to Secondary's distinct value during an open grace window, and the Export carrying it completed without error"
        }
        catch {
            $graceFallbackSuccess = $false
            $graceFallbackNotes += "Error: $_"
            throw
        }
        finally {
            Restore-Scenario14UserTypeDeletionPolicy
            $testResults.Steps += @{
                Name = "GraceFallbackFlowsAndExports"
                Success = $graceFallbackSuccess
                Note = ($graceFallbackNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 18: GraceAssertedNullBeatsSurvivor
    # ========================================================================
    if ($Step -eq "GraceAssertedNullBeatsSurvivor" -or $Step -eq "All") {
        Write-TestSection "Test 18: An Asserted Null Beats the Survivor Under a Grace Period (Erin)"

        $graceNullSuccess = $true
        $graceNullNotes = @()

        try {
            # #1307's third cell asks for the primary source to DISCONNECT with "Null is a value" set and
            # expects the attribute to go null rather than fall back. The engine cannot do that and should
            # not: ReElectSurvivingContributorsAsync excludes the leaver's own rule outright
            # (r.ConnectedSystemId != leaver.ConnectedSystemId), so a disconnected contributor has no
            # opinion to assert, which NotJoinedNoOpinion (Test 7) already proves for the unjoined case.
            #
            # The interaction that IS real, and that this step covers, is the in-place withdrawal: the
            # source stays joined and stops supplying the value, so its rule is still applicable and its
            # explicit null assertion outranks the survivor. The open question a grace period raises is
            # whether the freeze preserves the withdrawn value anyway; it must not, because the freeze
            # lives in the obsoletion and scope-exit paths, not the withdrawal path.
            Set-Scenario14UserTypeDeletionPolicy -DeletionRule "WhenAuthoritativeSourceDisconnected" `
                -GracePeriod ([TimeSpan]::FromHours(1)) -TriggerConnectedSystemIds @($primarySystem.id) | Out-Null

            # Job Title already carries NullIsValue on its Primary mapping under -Step All (Test 6); the
            # shared helper sets it for a standalone run and verifies it either way.
            Set-Scenario14AttributePrimaryNullIsValue -AttributeName "Job Title" | Out-Null

            $erinMvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S14-4" -PageSize 5) | Select-Object -First 1
            if (-not $erinMvo) {
                throw "Could not resolve Erin (S14-4) Metaverse Object."
            }

            # Secondary must hold a value for the assertion to mean anything: the whole point is that an
            # explicit null beats an available survivor, not that there was nothing to fall back to.
            $erinSecondaryTitle = Get-Scenario14LdapAttribute -LdapConfig $secondaryLdapConfig -Uid "erin14" -AttributeName "title"
            if (-not $erinSecondaryTitle) {
                throw "Erin's Secondary title is absent; with no survivor available this step would pass whether or not the null assertion outranks fallback."
            }

            Write-Host "Withdrawing Erin's Primary title in place (entry remains)..." -ForegroundColor Gray
            $withdrawLdif = "dn: uid=erin14,$($primaryLdapConfig.UserContainer)`nchangetype: modify`ndelete: title`n"
            Invoke-Scenario14LdapModify -ContainerName $primaryLdapConfig.ContainerName -LdapUri $primaryLdapUri `
                -BindDN $primaryLdapConfig.BindDN -BindPassword $primaryLdapConfig.BindPassword -Ldif $withdrawLdif

            $importResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullImport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Primary) after Erin's Job Title withdrawal"
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $primarySystem.id -RunProfileId $primaryFullSync.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Primary) after Erin's Job Title withdrawal"

            Assert-MvoAttributeValue -MvoId $erinMvo.id -AttributeName "Job Title" `
                -ExpectNoValue `
                -Name "Erin's Job Title (asserted null outranks Secondary's surviving value, and the grace period does not preserve it)"

            Assert-ActivityItemsHaveOutcomeSummary -ActivityId $syncResult.activityId `
                -Name "Full Synchronisation (Primary) after Erin's Job Title withdrawal" `
                -ExpectedOutcomeType "AssertedNull"

            $graceNullNotes += "Erin's Primary Job Title withdrawal asserted null over Secondary's surviving value with a grace period configured; the freeze did not preserve the withdrawn value"
        }
        catch {
            $graceNullSuccess = $false
            $graceNullNotes += "Error: $_"
            throw
        }
        finally {
            Restore-Scenario14UserTypeDeletionPolicy
            $testResults.Steps += @{
                Name = "GraceAssertedNullBeatsSurvivor"
                Success = $graceNullSuccess
                Note = ($graceNullNotes -join "; ")
            }
        }
    }

    # ========================================================================
    # Test 19: GraceExpressionInputFallback
    # ========================================================================
    if ($Step -eq "GraceExpressionInputFallback" -or $Step -eq "All") {
        Write-TestSection "Test 19: Expression Output Built From the Fallback Value (Frank)"

        $graceExpressionSuccess = $true
        $graceExpressionNotes = @()

        try {
            # The motivating failure for the whole of #1307: a disconnecting source nulls an expression's
            # input, the expression evaluates cleanly to a syntactically invalid Distinguished Name
            # (CN=,OU=...), and the export fails or writes rubbish. The fallback is what makes it
            # impossible, and this is the only cell that reads the PRODUCED value rather than JIM's view
            # of the Metaverse.
            #
            # Red proof: remove Secondary's displayName before running this, leaving Display Name with no
            # surviving contributor, and the expression's input is whatever the freeze left behind rather
            # than a re-elected value; turn the grace period off as well and it is nothing at all, which
            # is when CN=, appears.
            Set-Scenario14UserTypeDeletionPolicy -DeletionRule "WhenAuthoritativeSourceDisconnected" `
                -GracePeriod ([TimeSpan]::FromHours(1)) -TriggerConnectedSystemIds @($primarySystem.id) | Out-Null

            $frankMvo = Set-Scenario14FrankPrimaryDisconnectedWithSecondaryDisplayName

            Assert-MvoAttributeValue -MvoId $frankMvo.id -AttributeName "Display Name" `
                -ExpectedValue $frankSecondaryDisplayName `
                -Name "Frank's Display Name (the expression's input, re-elected from Secondary)"

            Write-Host "Running Export (Secondary)..." -ForegroundColor Gray
            $exportResult = Start-JIMRunProfile -ConnectedSystemId $secondarySystem.id -RunProfileId $secondaryExport.id -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "Export (Secondary) writing the expression-built Distinguished Name"

            # Read the produced value from the directory, not from JIM: a Connected System Object mirrors
            # what the last import saw, so asserting against it would only prove JIM remembers staging
            # the export.
            $producedDn = Get-Scenario14LdapAttribute -LdapConfig $secondaryLdapConfig -Uid "frank14" -AttributeName "physicalDeliveryOfficeName"
            if (-not $producedDn) {
                throw "No expression output was written to Frank's Secondary physicalDeliveryOfficeName; the export staged nothing, so the cell proves nothing."
            }

            $expectedDn = "CN=$frankSecondaryDisplayName,$($secondaryLdapConfig.UserContainer)"
            if ($producedDn -ne $expectedDn) {
                throw "Expression output mismatch. Expected '$expectedDn', got '$producedDn'."
            }

            # The structural assertion, stated independently of the value comparison above: no RDN
            # component may be empty. This is what "CN=,OU=Users,..." fails and what the connector's own
            # HasValidRdnValues rejects at export time for a real Distinguished Name.
            foreach ($rdn in ($producedDn -split '(?<!\\),')) {
                $parts = $rdn -split '=', 2
                if ($parts.Count -ne 2 -or [string]::IsNullOrWhiteSpace($parts[1])) {
                    throw "Expression output '$producedDn' contains an empty RDN component ('$rdn'). This is the invalid-Distinguished-Name failure the next-contributor fallback exists to prevent."
                }
            }
            Write-Host "  OK Expression output '$producedDn' has non-empty values in every RDN component" -ForegroundColor Green

            $graceExpressionNotes += "The expression-built Distinguished Name was rebuilt from Frank's re-elected Secondary Display Name and exported with every RDN component non-empty"
        }
        catch {
            $graceExpressionSuccess = $false
            $graceExpressionNotes += "Error: $_"
            throw
        }
        finally {
            Restore-Scenario14UserTypeDeletionPolicy
            $testResults.Steps += @{
                Name = "GraceExpressionInputFallback"
                Success = $graceExpressionSuccess
                Note = ($graceExpressionNotes -join "; ")
            }
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
