// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Application.UniqueValues;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// End-to-end workflow tests for Unique Value Generation (#242, Phase 2 work package G), driving the real Full
/// and Delta Synchronisation pipelines (engine, worker, repositories) against an in-memory database rather than
/// the unique value service or the engine's <c>ApplyGeneratedValue</c> in isolation. Topology throughout: an
/// "HR" Connected System whose import Synchronisation Rule carries a generated Account Name Attribute Flow
/// (base <c>Lower(cs["first"]) + "." + Lower(cs["last"])</c>, only-if-taken), projecting a "Person" Metaverse
/// Object Type. Adoption tests add a "Directory" Connected System joined by Employee Number, with an export
/// Synchronisation Rule making it a participating target.
/// </summary>
[TestFixture]
public class UniqueValueGenerationWorkflowTests : WorkflowTestBase
{
    #region Basic generation, collisions, stability

    [Test]
    public async Task FullSync_GeneratedAccountName_AssignsValuesAndRecordsOutcomesAsync()
    {
        var ctx = await SetUpBasicGenerationAsync();
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");
        await SeedHrCsoAsync(ctx, "Ada", "Lovelace", "E2");

        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx), Is.EquivalentTo(new[] { "joe.bloggs", "ada.lovelace" }));
            Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(2), "one assignment per object");
            // A generated (not adopted) import-mode value starts Proposed: nothing in this release's scope
            // (export-mode generation and Collision Remediation are out of scope, per the work package brief)
            // transitions it to Committed. Adopted values are the one exception (BuildAssignment starts them
            // Committed directly), covered by the adoption tests below.
            Assert.That(SyncRepo.GeneratedValueAssignments.Values.All(a => a.State == GeneratedValueAssignmentState.Proposed), Is.True);

            var assignedOutcomes = activity.RunProfileExecutionItems
                .SelectMany(r => r.SyncOutcomes)
                .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned)
                .ToList();
            Assert.That(assignedOutcomes, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task FullSync_TwoObjectsSamePageSameBase_SecondGetsNumericSuffixAsync()
    {
        var ctx = await SetUpBasicGenerationAsync();
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E2");
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E3");

        await RunFullSyncReturningActivityAsync(ctx.Hr);

        Assert.That(ResolvedAccountNames(ctx), Is.EquivalentTo(new[] { "joe.bloggs", "joe.bloggs1", "joe.bloggs2" }));
    }

    [Test]
    public async Task FullSync_TwoObjectsSameBaseAcrossTwoPages_SecondGetsNumericSuffixAsync()
    {
        var ctx = await SetUpBasicGenerationAsync();
        await SetSyncPageSizeAsync(1);
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E2");

        await RunFullSyncReturningActivityAsync(ctx.Hr);

        Assert.That(ResolvedAccountNames(ctx), Is.EquivalentTo(new[] { "joe.bloggs", "joe.bloggs1" }));
    }

    [Test]
    public async Task FullSync_ValueHeldInMixedCaseByAnotherObject_ForcesTheSuffixAsync()
    {
        var ctx = await SetUpBasicGenerationAsync();
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1"); // becomes "joe.bloggs"

        await RunFullSyncReturningActivityAsync(ctx.Hr);
        Assert.That(ResolvedAccountNames(ctx), Is.EquivalentTo(new[] { "joe.bloggs" }));

        // Simulate a Metaverse Object holding the value in mixed case (a different source's casing convention).
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var accountNameValue = mvo.AttributeValues.Single(av => av.AttributeId == ctx.MvAccountNameAttributeId);
        accountNameValue.StringValue = "Joe.Bloggs";

        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E2");
        await RunFullSyncReturningActivityAsync(ctx.Hr);

        var secondMvo = SyncRepo.MetaverseObjects.Values.Single(m => m.Id != mvo.Id);
        var secondValue = secondMvo.AttributeValues.Single(av => av.AttributeId == ctx.MvAccountNameAttributeId);
        Assert.That(secondValue.StringValue, Is.EqualTo("joe.bloggs1"), "case-insensitive uniqueness must force the suffix");
    }

    [Test]
    public async Task FullSync_ReRun_IsStableAsync()
    {
        var ctx = await SetUpBasicGenerationAsync();
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncReturningActivityAsync(ctx.Hr);

        var assignmentIdBefore = SyncRepo.GeneratedValueAssignments.Keys.Single();
        var valueBefore = ResolvedAccountNames(ctx).Single();

        var secondActivity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo(valueBefore), "nothing is recomputed or renumbered");
            Assert.That(SyncRepo.GeneratedValueAssignments.Keys.Single(), Is.EqualTo(assignmentIdBefore), "no new assignment");
            Assert.That(secondActivity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes)
                .Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned), Is.False,
                "a re-run must record no new GeneratedValueAssigned outcome");
        }
    }

    [Test]
    public async Task DeltaSync_ReRun_IsStableAsync()
    {
        var ctx = await SetUpBasicGenerationAsync();
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncReturningActivityAsync(ctx.Hr);

        var valueBefore = ResolvedAccountNames(ctx).Single();
        var assignmentIdBefore = SyncRepo.GeneratedValueAssignments.Keys.Single();

        // Touch the CSO (an unrelated field) so delta sync picks it up without changing first/last.
        var hrCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Hr.Id);
        await ModifyCsoAsync(hrCso);

        var reloadedHr = await ReloadEntityAsync(ctx.Hr);
        var deltaProfile = await CreateRunProfileAsync(reloadedHr.Id, "HR Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var deltaActivity = await CreateActivityAsync(reloadedHr.Id, deltaProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloadedHr, deltaProfile, deltaActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo(valueBefore));
            Assert.That(SyncRepo.GeneratedValueAssignments.Keys.Single(), Is.EqualTo(assignmentIdBefore));
        }
    }

    #endregion

    #region Missing input

    [Test]
    public async Task FullSync_MissingInput_ContributesNoValueAndKeepsExistingOnceGeneratedAsync()
    {
        var ctx = await SetUpBasicGenerationAsync();
        var hrCso = await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");

        var firstActivity = await RunFullSyncReturningActivityAsync(ctx.Hr);
        Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("joe.bloggs"));
        Assert.That(firstActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.GeneratedValueExhausted), Is.False);

        // The input the base expression reads (family name) later disappears.
        var lastValue = hrCso.AttributeValues.Single(av => av.Attribute?.Name == "last");
        hrCso.AttributeValues.Remove(lastValue);
        await ModifyCsoAsync(hrCso);

        var secondActivity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("joe.bloggs"), "a committed generated value must never be recomputed by its inputs");
            Assert.That(secondActivity.RunProfileExecutionItems.Any(r => r.ErrorType != null), Is.False, "a missing input is never an error for a generated mapping");
        }
    }

    #endregion

    #region Priority supersession

    [Test]
    public async Task FullSync_HigherPriorityOrdinarySourceLaterSuppliesAValue_SupersedesTheGeneratedValueAndDeletesTheAssignmentAsync()
    {
        // Same Connected System, a SECOND import Synchronisation Rule over the same Connected System Object
        // Type (fine-grained authority): an ordinary Attribute Flow at priority 1 targets the same Metaverse
        // attribute as the generated mapping (priority 2), initially silent (its source value absent, so the
        // generated mapping wins by FR 6). Both rules evaluate in the same inbound pass for this CSO, which is
        // what lets the lifecycle reconciliation's "touched this pass" gate see the supersession the moment the
        // ordinary source starts contributing. Two mappings on the SAME rule targeting the same attribute is
        // not a supported shape (a rule carries at most one mapping per target attribute), hence the second rule.
        var ctx = await SetUpBasicGenerationAsync(generatedMappingPriority: 2);
        var hrType = SyncRepo.ObjectTypes[ctx.HrCsoTypeId];
        var overrideAttr = new ConnectedSystemObjectTypeAttribute { Name = "accountNameOverride", Type = AttributeDataType.Text, Selected = true };
        DbContext.ConnectedSystemAttributes.Add(overrideAttr);
        await DbContext.SaveChangesAsync();
        hrType.Attributes.Add(overrideAttr);

        var overrideRule = await CreateImportSyncRuleAsync(ctx.Hr.Id, hrType, ctx.MvType, "HR Override Import", enableProjection: false);
        overrideRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = overrideRule,
            SyncRuleId = overrideRule.Id,
            Priority = 1,
            TargetMetaverseAttribute = ctx.MvAccountNameAttribute,
            TargetMetaverseAttributeId = ctx.MvAccountNameAttributeId,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = overrideAttr, ConnectedSystemAttributeId = overrideAttr.Id } }
        });
        await DbContext.SaveChangesAsync();

        var hrCso = await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1"); // override absent: generated mapping wins first
        await RunFullSyncReturningActivityAsync(ctx.Hr);
        Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("joe.bloggs"));
        Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1));

        // The authoritative override now supplies a value: the priority-1 mapping wins the gate this pass.
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = overrideAttr.Id,
            Attribute = overrideAttr,
            StringValue = "j.bloggs.authoritative"
        });
        await ModifyCsoAsync(hrCso);

        await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("j.bloggs.authoritative"), "the higher-priority source must win visibly");
            Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty, "the superseded assignment must be deleted");
        }
    }

    [Test]
    public async Task FullSync_AnotherSystemSupersedesTheGeneratedValueInItsOwnRun_GeneratingSystemsNextRunDeletesTheAssignmentAsync()
    {
        // Unlike the fine-grained-authority test above (two rules on the SAME Connected System, so the
        // supersession and the reconciliation happen in one pass), this proves the CROSS-system case: the
        // superseding value arrives via a DIFFERENT Connected System's own run, which cannot reconcile HR's
        // assignment itself (work package G review fix 1: reconciliation is keyed to a run's own generated
        // mappings). Only the generating system's (HR's) own later run, examining every known assignment
        // rather than only ones touched that pass, sees the value now belongs to another rule and deletes it.
        var ctx = await SetUpCrossSystemSupersessionScenarioAsync();

        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncReturningActivityAsync(ctx.Hr);
        Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("joe.bloggs"));
        Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1));

        // Directory supplies its own, higher-priority value in ITS OWN run (not HR's).
        await SeedDirectoryOverrideCsoAsync(ctx, "E1", "j.bloggs.authoritative");
        await RunFullSyncReturningActivityAsync(ctx.Directory!);

        Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("j.bloggs.authoritative"), "the higher-priority source must win visibly");
        Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1),
            "Directory's own run cannot reconcile an assignment it did not generate; the stale row survives until HR's next run");

        var secondHrActivity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("j.bloggs.authoritative"), "HR's own re-run must not overwrite Directory's value");
            Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty, "the generating system's next run must delete the stale assignment");
            Assert.That(secondHrActivity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes)
                .Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned), Is.False,
                "no new value is generated; only the stale assignment is cleaned up");
        }
    }

    #endregion

    #region Exhaustion and width

    [Test]
    public async Task FullSync_AttemptLimitExhausted_RecordsErrorAndObjectsOtherAttributesStillFlowAsync()
    {
        var ctx = await SetUpBasicGenerationAsync(attemptLimit: 2);
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1"); // "joe.bloggs"
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E2"); // "joe.bloggs1" (attempt 2, the limit)
        await RunFullSyncReturningActivityAsync(ctx.Hr);
        Assert.That(ResolvedAccountNames(ctx), Is.EquivalentTo(new[] { "joe.bloggs", "joe.bloggs1" }));

        // A third object with the same base has only two candidates available within the attempt limit, both taken.
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E3");
        var thirdActivity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        var exhaustedErrors = thirdActivity.RunProfileExecutionItems
            .Where(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.GeneratedValueExhausted)
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exhaustedErrors, Has.Count.EqualTo(1));
            Assert.That(exhaustedErrors[0].ErrorMessage, Does.Contain("Account Name"));
            // The object's Employee Number (an ordinary, non-generated attribute) still flows despite the failure.
            var thirdMvo = SyncRepo.MetaverseObjects.Values.Single(m => m.AttributeValues.Any(av => av.AttributeId == ctx.MvEmployeeIdAttributeId && av.StringValue == "E3"));
            Assert.That(thirdMvo.AttributeValues.Any(av => av.AttributeId == ctx.MvAccountNameAttributeId), Is.False, "no value is written on exhaustion");
        }
    }

    [Test]
    public async Task FullSync_SequenceWidthExceeded_RecordsGeneratedValueWidthExceededErrorAsync()
    {
        var ctx = await SetUpSequenceGenerationAsync(sequenceStart: 98, fixedWidth: 2); // width 2 digits: 98, 99, then 100 overflows
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E2");
        await RunFullSyncReturningActivityAsync(ctx.Hr);

        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E3");
        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        var widthErrors = activity.RunProfileExecutionItems
            .Where(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.GeneratedValueWidthExceeded)
            .ToList();
        Assert.That(widthErrors, Has.Count.EqualTo(1));
    }

    #endregion

    #region Sequence on a Number target

    [Test]
    public async Task FullSync_SequenceOnNumberTarget_YieldsConsecutiveNumbersAcrossPagesWithOneCounterRowAsync()
    {
        var ctx = await SetUpSequenceGenerationAsync(sequenceStart: 100);
        await SetSyncPageSizeAsync(1);
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");
        await SeedHrCsoAsync(ctx, "Ada", "Lovelace", "E2");
        await SeedHrCsoAsync(ctx, "Grace", "Hopper", "E3");

        await RunFullSyncReturningActivityAsync(ctx.Hr);

        var numbers = SyncRepo.MetaverseObjects.Values
            .Select(m => m.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvAccountNameAttributeId)?.IntValue)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .OrderBy(v => v)
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(numbers, Is.EqualTo(new[] { 100, 101, 102 }));
            Assert.That(SyncRepo.GeneratedValueSequences, Has.Count.EqualTo(1), "a single counter row for the attribute");
        }
    }

    #endregion

    #region Adopt before generate (connector-space adoption removed: product-owner decision)

    /// <summary>
    /// Formerly <c>FullSync_ParticipatingSystemAlreadyHoldsAValue_AdoptsItInsteadOfGeneratingAsync</c>: before
    /// the product-owner decision, a participating export target already holding a value (even with no import
    /// Attribute Flow reading it back) was adopted. That precedence rule sat outside the Attribute Flow priority
    /// model and has been removed (#242): with no import flow from Directory, its "jsmith" is invisible to
    /// generation entirely, so HR generates its own value and, on export, overwrites it.
    /// </summary>
    [Test]
    public async Task FullSync_ParticipatingSystemAlreadyHoldsAValueButNoImportFlow_GeneratesAndExportsOverwritingItAsync()
    {
        var ctx = await SetUpAdoptionScenarioAsync();

        // Directory is a brownfield join: it already holds "jsmith" for the same Employee Number before HR ever
        // syncs. Directory's Synchronisation Rule only EXPORTS Account Name (no import flow reads sAMAccountName
        // back in), so this value sits outside the Attribute Flow priority model entirely.
        await SeedDirectoryCsoAsync(ctx, "E1", "jsmith");
        await RunFullSyncReturningActivityAsync(ctx.Directory!);

        await SeedHrCsoAsync(ctx, "John", "Smith", "E1");
        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("john.smith"), "generation must never read a joined target's own value");

            var assignment = SyncRepo.GeneratedValueAssignments.Values.Single();
            Assert.That(assignment.Adopted, Is.False, "connector-space adoption has been removed");
            Assert.That(assignment.State, Is.EqualTo(GeneratedValueAssignmentState.Proposed));

            Assert.That(activity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes)
                .Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned), Is.True);
            Assert.That(activity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes)
                .Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAdopted), Is.False);

            // The generated value flows to the participating target on export, overwriting the brownfield value.
            var pendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemId == ctx.Directory!.Id);
            Assert.That(pendingExport.AttributeValueChanges.Single().StringValue, Is.EqualTo("john.smith"),
                "the export carries the generated value, never the adopted brownfield one");
        }
    }

    /// <summary>
    /// Formerly <c>FullSync_ExcludedSystemsValue_IsNotAdoptedAsync</c>: exclusions used to gate the
    /// connector-space adoption read. That read is gone entirely now, so this is rewritten to cover the case
    /// exclusions still matter for (#242, FR 30's replacement): a higher-priority IMPORT Attribute Flow from
    /// the target, entirely inside the priority model, means the generated mapping never even runs - it never
    /// gets a chance to generate, let alone adopt.
    /// </summary>
    [Test]
    public async Task FullSync_HigherPriorityImportFlowFromTheTargetSuppliesAValue_NoGenerationNoAssignmentAsync()
    {
        var ctx = await SetUpAdoptionScenarioAsync();

        // An administrator's deliberate "keep the existing target account's value" configuration: an ordinary,
        // higher-priority (1) import Attribute Flow on Directory's own rule, reading the same sAMAccountName
        // attribute its export rule writes to.
        var directoryImportRule = SyncRepo.SyncRules.Values.Single(r => r.ConnectedSystemId == ctx.Directory!.Id && r.Direction == SyncRuleDirection.Import);
        var directoryType = SyncRepo.ObjectTypes[ctx.DirectoryCsoTypeId!.Value];
        var sAMAccountNameAttr = directoryType.Attributes.Single(a => a.Name == "sAMAccountName");
        directoryImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryImportRule,
            SyncRuleId = directoryImportRule.Id,
            Priority = 1,
            TargetMetaverseAttribute = ctx.MvAccountNameAttribute,
            TargetMetaverseAttributeId = ctx.MvAccountNameAttributeId,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = sAMAccountNameAttr, ConnectedSystemAttributeId = sAMAccountNameAttr.Id } }
        });
        await DbContext.SaveChangesAsync();

        await SeedDirectoryCsoAsync(ctx, "E1", "jsmith");
        await RunFullSyncReturningActivityAsync(ctx.Directory!);

        await SeedHrCsoAsync(ctx, "John", "Smith", "E1");
        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("jsmith"), "the higher-priority import flow must win; the generated mapping never runs");
            Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty, "a mapping that never wins priority must never record an assignment");
            Assert.That(activity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes)
                .Any(o => o.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned
                    or ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAdopted), Is.False);
        }
    }

    /// <summary>
    /// The new adoption source (#242, product-owner decision): the Metaverse Object's OWN current value, left
    /// behind when a higher-priority contributor is disabled (#1537: a dormant mapping's contributed value is
    /// retained, not cleared). The generated mapping, now the attribute's sole active contributor, is handed
    /// the attribute with the value already sitting on the object and adopts it rather than generating afresh.
    /// Formerly <c>FullSync_SequenceOnNumberTarget_ParticipatingSystemAlreadyHoldsANumber_AdoptsItAsync</c>,
    /// which proved the same numeric rendering path (IntValue, not StringValue) against the now-removed
    /// connector-space read; this proves it against the new Metaverse-own-value read instead.
    /// </summary>
    [Test]
    public async Task FullSync_SequenceOnNumberTarget_HigherPriorityContributorDisabledLeavingItsNumberBehind_AdoptsItAsync()
    {
        var ctx = await SetUpNumericAdoptionScenarioAsync();

        var directoryImportRule = SyncRepo.SyncRules.Values.Single(r => r.ConnectedSystemId == ctx.Directory!.Id && r.Direction == SyncRuleDirection.Import);
        var directoryType = SyncRepo.ObjectTypes[ctx.DirectoryCsoTypeId!.Value];
        var sAMAccountNameAttr = directoryType.Attributes.Single(a => a.Name == "sAMAccountName");
        var directoryOverrideMapping = new SyncRuleMapping
        {
            SyncRule = directoryImportRule,
            SyncRuleId = directoryImportRule.Id,
            Priority = 1,
            TargetMetaverseAttribute = ctx.MvAccountNameAttribute,
            TargetMetaverseAttributeId = ctx.MvAccountNameAttributeId,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = sAMAccountNameAttr, ConnectedSystemAttributeId = sAMAccountNameAttr.Id } }
        };
        directoryImportRule.AttributeFlowRules.Add(directoryOverrideMapping);
        await DbContext.SaveChangesAsync();

        // Directory's higher-priority mapping owns Account Name outright at first.
        await SeedDirectoryCsoWithNumericAccountNameAsync(ctx, "E1", 4242);
        await RunFullSyncReturningActivityAsync(ctx.Directory!);

        await SeedHrCsoAsync(ctx, "John", "Smith", "E1");
        await RunFullSyncReturningActivityAsync(ctx.Hr);

        Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty, "sanity: the higher-priority contributor still owns the attribute");

        // Directory's mapping is disabled: the value it contributed is RETAINED (#1537, dormant mappings keep
        // their contributed values), handing the attribute to the generated mapping as the sole survivor.
        // No further SaveChangesAsync here: RunFullSyncReturningActivityAsync's own ReloadEntityAsync already
        // picks up the mutation on this tracked instance (the established pattern; see
        // RemovedMappingRecallWorkflowTests.FullSync_MappingDisabled_SoleContributor_LeavesValueInPlaceAsync),
        // and re-saving after the prior RunFullSyncReturningActivityAsync call has moved the context on throws
        // DbUpdateConcurrencyException.
        directoryOverrideMapping.Enabled = false;

        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            var mvo = SyncRepo.MetaverseObjects.Values.Single();
            var value = mvo.AttributeValues.Single(av => av.AttributeId == ctx.MvAccountNameAttributeId);
            Assert.That(value.IntValue, Is.EqualTo(4242), "the value already on the object is adopted, not regenerated");

            var assignment = SyncRepo.GeneratedValueAssignments.Values.Single();
            Assert.That(assignment.Adopted, Is.True);
            Assert.That(assignment.State, Is.EqualTo(GeneratedValueAssignmentState.Committed));
            Assert.That(assignment.Value, Is.EqualTo("4242"));

            // No GeneratedValueAdopted outcome node here, unlike the other adoption tests: the adopted value
            // is identical to what the object already held (Directory's own mapping wrote 4242, and adoption
            // reads that same value back), so ApplyGeneratedValue's own value-changed check finds nothing to
            // add or remove. The RPEI/outcome tree is built only when an attribute actually changes this pass
            // (SyncTaskProcessorBase.ProcessMetaverseObjectChangesAsync's attributesAdded + attributesRemoved
            // > 0 gate); pre-existing behaviour, unrelated to which value adoption reads from.
            var generationErrors = activity.RunProfileExecutionItems
                .Where(r => r.ErrorType is ActivityRunProfileExecutionItemErrorType.GeneratedValueExhausted
                    or ActivityRunProfileExecutionItemErrorType.GeneratedValueWidthExceeded
                    or ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved)
                .ToList();
            Assert.That(generationErrors, Is.Empty, "adoption must succeed cleanly, not merely avoid changing the value");
        }
    }

    /// <summary>
    /// The AdoptionConflict counterpart of the test above: the value a disabled higher-priority contributor
    /// left behind on THIS object is also held by a DIFFERENT object's live assignment, so
    /// <c>UniqueValueGenerationServer.TryAdoptAsync</c> must refuse to adopt it (never silently regenerate a
    /// different value instead) and record a collision error.
    /// </summary>
    [Test]
    public async Task FullSync_MetaverseOwnValueAlreadyHeldByAnotherObject_RecordsAdoptionConflictAsync()
    {
        var ctx = await SetUpAdoptionScenarioAsync();

        var directoryImportRule = SyncRepo.SyncRules.Values.Single(r => r.ConnectedSystemId == ctx.Directory!.Id && r.Direction == SyncRuleDirection.Import);
        var directoryType = SyncRepo.ObjectTypes[ctx.DirectoryCsoTypeId!.Value];
        var sAMAccountNameAttr = directoryType.Attributes.Single(a => a.Name == "sAMAccountName");
        var directoryOverrideMapping = new SyncRuleMapping
        {
            SyncRule = directoryImportRule,
            SyncRuleId = directoryImportRule.Id,
            Priority = 1,
            TargetMetaverseAttribute = ctx.MvAccountNameAttribute,
            TargetMetaverseAttributeId = ctx.MvAccountNameAttributeId,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = sAMAccountNameAttr, ConnectedSystemAttributeId = sAMAccountNameAttr.Id } }
        };
        directoryImportRule.AttributeFlowRules.Add(directoryOverrideMapping);
        await DbContext.SaveChangesAsync();

        // Another, unrelated object already holds "jsmith" as a live generated-value assignment.
        SyncRepo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = Guid.NewGuid(),
            MetaverseAttributeId = ctx.MvAccountNameAttributeId,
            Value = "jsmith",
            NormalisedValue = "jsmith",
            State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = SyncRepo.SyncRules[ctx.HrImportRuleId].AttributeFlowRules.Single(m => m.Generation != null).Generation!.Id,
            Created = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow
        });

        await SeedDirectoryCsoAsync(ctx, "E1", "jsmith");
        await RunFullSyncReturningActivityAsync(ctx.Directory!);

        await SeedHrCsoAsync(ctx, "John", "Smith", "E1");
        await RunFullSyncReturningActivityAsync(ctx.Hr);

        directoryOverrideMapping.Enabled = false;

        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1), "no new assignment for HR's object; only the pre-seeded one exists");
            var collisionErrors = activity.RunProfileExecutionItems
                .Where(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved)
                .ToList();
            Assert.That(collisionErrors, Has.Count.EqualTo(1));
        }
    }

    #endregion

    #region Reference pass integrity

    [Test]
    public async Task FullSync_GeneratedMappingWithReferenceAttributeOnTheSameRule_ResolvesCleanlyThroughBothPassesAsync()
    {
        // Proves the onlyReferenceAttributes guard (work package G, task 1a): without it, the deferred reference
        // pass would record a second pending generation after the worker resolved and cleared the first pass's,
        // and PersistPendingMetaverseObjectsAsync's integrity guard would throw before the next page persists.
        var ctx = await SetUpBasicGenerationAsync();

        var mvManagerAttr = new MetaverseAttribute
        {
            Name = "Manager",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { ctx.MvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvManagerAttr);
        await DbContext.SaveChangesAsync();
        ctx.MvType.Attributes.Add(mvManagerAttr);

        var hrCsoType = SyncRepo.ObjectTypes[ctx.HrCsoTypeId];
        var hrManagerAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "manager",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true
        };
        DbContext.ConnectedSystemAttributes.Add(hrManagerAttr);
        await DbContext.SaveChangesAsync();
        hrCsoType.Attributes.Add(hrManagerAttr);

        var importRule = SyncRepo.SyncRules[ctx.HrImportRuleId];
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = mvManagerAttr,
            TargetMetaverseAttributeId = mvManagerAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrManagerAttr, ConnectedSystemAttributeId = hrManagerAttr.Id } }
        });
        await DbContext.SaveChangesAsync();

        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1"); // no manager reference value: unresolved, harmlessly absent

        Assert.That(async () => await RunFullSyncReturningActivityAsync(ctx.Hr), Throws.Nothing,
            "the integrity guard must never fire: the reference pass must record no pending generation");
        Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("joe.bloggs"));

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(mvo.PendingGeneratedValues, Is.Empty);
    }

    #endregion

    #region Re-election into a run with no generated mappings of its own

    [Test]
    public async Task FullSync_HigherPriorityValueOnASystemWithNoGeneratedMappingsWithdraws_ReElectsTheGeneratedMappingWithNoErrorAsync()
    {
        // Proves work package G review fix 3: withdrawal re-election can flow a generated mapping into a run
        // whose OWN active rules have no generated mapping (Payroll here), so PrepareUniqueValueGenerationAsync
        // built no service for it. Before the fix, ResolvePendingGeneratedValuesAsync logged an error and
        // cleared the pending request, silently leaving Account Name blank. The generating system's (HR's) own
        // source contributes nothing throughout this test; only Payroll's withdrawal moves anything.
        var ctx = await SetUpReElectedGenerationScenarioAsync();

        await SeedPayrollCsoAsync(ctx, "E1", "payroll.value");
        await RunFullSyncReturningActivityAsync(ctx.Directory!); // Payroll projects and wins Account Name outright

        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncReturningActivityAsync(ctx.Hr); // HR joins; its generated mapping loses the gate, records nothing

        Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("payroll.value"));
        Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty, "the generated mapping has not contributed yet");

        // Payroll withdraws its override: its own run (no generated mappings of its own) re-elects HR's
        // generated mapping as the surviving contributor, and must resolve the resulting pending generation.
        var payrollCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Directory!.Id);
        var overrideValue = payrollCso.AttributeValues.Single(av => av.Attribute?.Name == "accountNameOverride");
        payrollCso.AttributeValues.Remove(overrideValue);
        await ModifyCsoAsync(payrollCso);

        var payrollActivity = await RunFullSyncReturningActivityAsync(ctx.Directory!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("joe.bloggs"), "the generated mapping must take over via re-election");
            Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1), "the generated value must be committed to an assignment, not just written to the object");
            var assignment = SyncRepo.GeneratedValueAssignments.Values.Single();
            Assert.That(assignment.Adopted, Is.False);
            // ErrorType defaults to NotSet (not null) on every RunProfileExecutionItem, including ones with no
            // error at all, so "no error" must check the specific generation error types rather than != null
            // (the same pattern the exhaustion and width tests above use).
            var generationErrors = payrollActivity.RunProfileExecutionItems
                .Where(r => r.ErrorType is ActivityRunProfileExecutionItemErrorType.GeneratedValueExhausted
                    or ActivityRunProfileExecutionItemErrorType.GeneratedValueWidthExceeded
                    or ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved)
                .ToList();
            Assert.That(generationErrors, Is.Empty,
                "a pending generation resolved lazily by a system with no generated mappings of its own must not log an error");
        }
    }

    #endregion

    #region Reservation set lifetime

    [Test]
    public async Task FullSync_ReservationSetIsReleasedAtRunEndAsync()
    {
        var ctx = await SetUpBasicGenerationAsync();
        var reservations = new UniqueValueReservationSet();
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");

        var reloaded = await ReloadEntityAsync(ctx.Hr);
        var profile = await CreateRunProfileAsync(reloaded.Id, "HR Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource(), uniqueValueReservations: reservations)
            .PerformFullSyncAsync();

        // "joe.bloggs" must no longer be reserved against THIS run's (now-finished) activity id: a later run
        // using the same reservation set can freely claim it again (the database gates are what protect it now).
        Assert.That(reservations.IsReservedByAnotherOwner(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, ctx.MvAccountNameAttributeId, "joe.bloggs"), Is.False);
    }

    [Test]
    public async Task FullSync_ReservationSetIsReleasedEvenWhenTheRunThrowsAsync()
    {
        // Replace the harness's repository with the throwing twin BEFORE seeding, so the run that throws sees
        // the same topology every other test builds against.
        var throwingRepo = new ThrowingAfterGenerationSyncRepository();
        throwingRepo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = throwingRepo;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);

        var ctx = await SetUpBasicGenerationAsync();
        var reservations = new UniqueValueReservationSet();
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");

        var reloaded = await ReloadEntityAsync(ctx.Hr);
        var profile = await CreateRunProfileAsync(reloaded.Id, "HR Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);

        Assert.That(
            async () => await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource(), uniqueValueReservations: reservations)
                .PerformFullSyncAsync(),
            Throws.InstanceOf<Exception>());

        Assert.That(reservations.IsReservedByAnotherOwner(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, ctx.MvAccountNameAttributeId, "joe.bloggs"), Is.False,
            "the reservation must be released even when the run fails");
    }

    #endregion

    #region No generated mappings

    [Test]
    public async Task FullSync_NoGeneratedMappings_MakesNoGeneratedValueRepositoryCallsAsync()
    {
        var counting = new GeneratedValueCallCountingSyncRepository();
        counting.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = counting;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "HrUser");
        var hrDisplayNameAttr = hrType.Attributes.Single(a => a.Name == "DisplayName");
        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrDisplayNameAttr, ConnectedSystemAttributeId = hrDisplayNameAttr.Id } }
        });
        await DbContext.SaveChangesAsync();
        await CreateCsoAsync(hr.Id, hrType, "Joe Bloggs", "E1");

        var reloaded = await ReloadEntityAsync(hr);
        var profile = await CreateRunProfileAsync(reloaded.Id, "HR Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        Assert.That(counting.GeneratedValueCallCount, Is.EqualTo(0), "a run with no generated mappings must make no generated-value repository calls");
    }

    #endregion

    #region Commit loser (cross-run collision)

    [Test]
    public async Task FullSync_CommitLoser_RecordsErrorAndTheObjectGetsTheNextCandidateNextRunAsync()
    {
        var ctx = await SetUpBasicGenerationAsync();
        await SeedHrCsoAsync(ctx, "Joe", "Bloggs", "E1");

        // From this point on, run through the conflict-injecting repository (same underlying seeded state):
        // it seeds a conflicting live assignment for "joe.bloggs" the instant CommitAssignmentsAsync tries to
        // persist this run's own proposal, simulating another run winning the race between resolve and commit.
        var conflictingRepo = new InjectConflictOnCommitSyncRepository(ctx.MvAccountNameAttributeId, "joe.bloggs");
        MigrateSeededStateTo(conflictingRepo);
        conflictingRepo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = conflictingRepo;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);

        var reloaded = await ReloadEntityAsync(ctx.Hr);
        var profile = await CreateRunProfileAsync(reloaded.Id, "HR Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        var lossErrors = activity.RunProfileExecutionItems
            .Where(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved)
            .ToList();
        Assert.That(lossErrors, Has.Count.EqualTo(1));

        // The object's own Metaverse value still shows "joe.bloggs" (never rolled back), but it has no
        // assignment of its own: the next synchronisation's gates see the other run's copy already held and
        // move the object on to the next candidate.
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var value = mvo.AttributeValues.Single(av => av.AttributeId == ctx.MvAccountNameAttributeId);
        Assert.That(value.StringValue, Is.EqualTo("joe.bloggs"));

        var secondActivity = await RunFullSyncReturningActivityAsync(ctx.Hr);
        var secondValue = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvAccountNameAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondValue?.StringValue, Is.EqualTo("joe.bloggs1"), "self-healing: the object moves on to the next candidate");
            Assert.That(secondActivity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes)
                .Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned), Is.True);
        }
    }

    #endregion

    #region Context and helpers

    private sealed record GenerationContext(
        ConnectedSystem Hr,
        MetaverseObjectType MvType,
        MetaverseAttribute MvAccountNameAttribute,
        MetaverseAttribute MvEmployeeIdAttribute,
        int HrCsoTypeId,
        ConnectedSystemObjectTypeAttribute HrEmployeeIdAttribute,
        int HrImportRuleId,
        ConnectedSystem? Directory = null,
        int? DirectoryCsoTypeId = null)
    {
        public int MvAccountNameAttributeId => MvAccountNameAttribute.Id;
        public int MvEmployeeIdAttributeId => MvEmployeeIdAttribute.Id;
    }

    /// <summary>
    /// Builds the basic topology: a Person Metaverse Object Type (Account Name, Employee Number) and an HR
    /// Connected System whose import Synchronisation Rule projects it, with a generated only-if-taken Account
    /// Name Attribute Flow (base <c>Lower(cs["first"]) + "." + Lower(cs["last"])</c>).
    /// </summary>
    private async Task<GenerationContext> SetUpBasicGenerationAsync(int generatedMappingPriority = int.MaxValue, int attemptLimit = 1000)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvAccountNameAttr = new MetaverseAttribute
        {
            Name = "Account Name",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvAccountNameAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvAccountNameAttr);

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "HrUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "first", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "last", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "employeeId", Type = AttributeDataType.Text, Selected = true }
        });
        var hrEmployeeIdAttr = hrType.Attributes.Single(a => a.Name == "employeeId");

        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeIdAttr, ConnectedSystemAttributeId = hrEmployeeIdAttr.Id } }
        });
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            Priority = generatedMappingPriority,
            TargetMetaverseAttribute = mvAccountNameAttr,
            TargetMetaverseAttributeId = mvAccountNameAttr.Id,
            Generation = new SyncRuleMappingGeneration
            {
                TokenKind = GeneratedValueTokenKind.OnlyIfTaken,
                SuffixStyle = GeneratedValueSuffixStyle.Number,
                SuffixStart = 1,
                AttemptLimit = attemptLimit,
                NeverReuse = true
            },
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "Lower(cs[\"first\"]) + \".\" + Lower(cs[\"last\"])" } }
        });
        await DbContext.SaveChangesAsync();

        return new GenerationContext(hr, mvType, mvAccountNameAttr, mvEmployeeIdAttr, hrType.Id, hrEmployeeIdAttr, importRule.Id);
    }

    /// <summary>
    /// Builds the same topology as <see cref="SetUpBasicGenerationAsync"/>, except the generated Account Name
    /// mapping targets a Number attribute with a Sequence token instead of Only-if-taken.
    /// </summary>
    private async Task<GenerationContext> SetUpSequenceGenerationAsync(long sequenceStart, int? fixedWidth = null)
    {
        var ctx = await SetUpBasicGenerationAsync();

        // Swap the Account Name Metaverse attribute's type to Number for this scenario's counter target, and
        // rebuild the generated mapping accordingly.
        ctx.MvAccountNameAttribute.Type = AttributeDataType.Number;
        var importRule = SyncRepo.SyncRules[ctx.HrImportRuleId];
        var generatedMapping = importRule.AttributeFlowRules.Single(m => m.Generation != null);
        generatedMapping.Sources.Clear();
        generatedMapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Sequence,
            SequenceStart = sequenceStart,
            SequenceIncrement = 1,
            FixedWidth = fixedWidth,
            OnWidthExceeded = GeneratedValueWidthOverflowBehaviour.StopAndReport,
            AttemptLimit = 1000,
            NeverReuse = true
        };
        await DbContext.SaveChangesAsync();

        return ctx;
    }

    /// <summary>
    /// Builds the adoption topology: <see cref="SetUpBasicGenerationAsync"/>'s HR system, plus a Directory
    /// Connected System that both imports (joining by Employee Number, projecting nothing new) and exports
    /// (an Account Name -> sAMAccountName Attribute Flow, making it a participating target for HR's generated
    /// mapping).
    /// </summary>
    private async Task<GenerationContext> SetUpAdoptionScenarioAsync(bool excludeDirectory = false)
    {
        var ctx = await SetUpBasicGenerationAsync();

        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "DirectoryUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "employeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "sAMAccountName", Type = AttributeDataType.Text, Selected = true }
        });
        var directoryEmployeeIdAttr = directoryType.Attributes.Single(a => a.Name == "employeeId");
        var directorySAMAccountNameAttr = directoryType.Attributes.Single(a => a.Name == "sAMAccountName");

        // Joining-only import rule: projects nothing new (HR already projects), just establishes the join.
        var directoryImportRule = await CreateImportSyncRuleAsync(directory.Id, directoryType, ctx.MvType, "Directory Import", enableProjection: true);
        directoryImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryImportRule,
            SyncRuleId = directoryImportRule.Id,
            TargetMetaverseAttribute = ctx.MvEmployeeIdAttribute,
            TargetMetaverseAttributeId = ctx.MvEmployeeIdAttributeId,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = directoryEmployeeIdAttr, ConnectedSystemAttributeId = directoryEmployeeIdAttr.Id } }
        });

        // HR must join, not project a second time, so it matches onto Directory's projected Metaverse Object.
        var hrImportRule = SyncRepo.SyncRules[ctx.HrImportRuleId];
        hrImportRule.ProjectToMetaverse = false;
        hrImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = hrImportRule,
            SyncRuleId = hrImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = ctx.MvEmployeeIdAttribute,
            TargetMetaverseAttributeId = ctx.MvEmployeeIdAttributeId,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = ctx.HrEmployeeIdAttribute, ConnectedSystemAttributeId = ctx.HrEmployeeIdAttribute.Id } }
        });

        var directoryExportRule = await CreateExportSyncRuleAsync(directory.Id, directoryType, ctx.MvType, "Directory Export", enableProvisioning: false);
        directoryExportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryExportRule,
            SyncRuleId = directoryExportRule.Id,
            TargetConnectedSystemAttribute = directorySAMAccountNameAttr,
            TargetConnectedSystemAttributeId = directorySAMAccountNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = ctx.MvAccountNameAttribute, MetaverseAttributeId = ctx.MvAccountNameAttributeId } }
        });

        if (excludeDirectory)
        {
            var generatedMapping = hrImportRule.AttributeFlowRules.Single(m => m.Generation != null);
            generatedMapping.Generation!.Exclusions.Add(new SyncRuleMappingGenerationExclusion
            {
                Generation = generatedMapping.Generation,
                ConnectedSystemId = directory.Id
            });
        }

        await DbContext.SaveChangesAsync();

        return ctx with { Directory = directory, DirectoryCsoTypeId = directoryType.Id };
    }

    /// <summary>
    /// Builds a cross-system supersession topology (work package G review fix 1): HR's basic generation
    /// topology (its generated Account Name mapping at priority 2), plus a Directory Connected System that
    /// JOINS the same Metaverse Object (by Employee Number, HR already projects) and carries an ORDINARY,
    /// higher-priority (1) Attribute Flow into the same Account Name attribute.
    /// </summary>
    private async Task<GenerationContext> SetUpCrossSystemSupersessionScenarioAsync()
    {
        var ctx = await SetUpBasicGenerationAsync(generatedMappingPriority: 2);

        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "DirectoryUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "employeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "accountNameOverride", Type = AttributeDataType.Text, Selected = true }
        });
        var directoryEmployeeIdAttr = directoryType.Attributes.Single(a => a.Name == "employeeId");
        var directoryOverrideAttr = directoryType.Attributes.Single(a => a.Name == "accountNameOverride");

        var directoryImportRule = await CreateImportSyncRuleAsync(directory.Id, directoryType, ctx.MvType, "Directory Import", enableProjection: false);
        directoryImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = directoryImportRule,
            SyncRuleId = directoryImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = ctx.MvEmployeeIdAttribute,
            TargetMetaverseAttributeId = ctx.MvEmployeeIdAttributeId,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = directoryEmployeeIdAttr, ConnectedSystemAttributeId = directoryEmployeeIdAttr.Id } }
        });
        directoryImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryImportRule,
            SyncRuleId = directoryImportRule.Id,
            Priority = 1,
            TargetMetaverseAttribute = ctx.MvAccountNameAttribute,
            TargetMetaverseAttributeId = ctx.MvAccountNameAttributeId,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = directoryOverrideAttr, ConnectedSystemAttributeId = directoryOverrideAttr.Id } }
        });

        await DbContext.SaveChangesAsync();

        return ctx with { Directory = directory, DirectoryCsoTypeId = directoryType.Id };
    }

    /// <summary>
    /// Builds <see cref="SetUpAdoptionScenarioAsync"/>'s topology, then swaps the generated Account Name
    /// attribute (and Directory's export target) to Number, and the generated mapping to a Sequence token
    /// (work package G review fix 2): proves adopt-before-generate reads a Number/LongNumber target's
    /// IntValue/LongValue, not just StringValue.
    /// </summary>
    private async Task<GenerationContext> SetUpNumericAdoptionScenarioAsync()
    {
        var ctx = await SetUpAdoptionScenarioAsync();

        ctx.MvAccountNameAttribute.Type = AttributeDataType.Number;

        var importRule = SyncRepo.SyncRules[ctx.HrImportRuleId];
        var generatedMapping = importRule.AttributeFlowRules.Single(m => m.Generation != null);
        generatedMapping.Sources.Clear();
        generatedMapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Sequence,
            SequenceStart = 1000,
            SequenceIncrement = 1,
            AttemptLimit = 1000,
            NeverReuse = true
        };

        var directoryExportRule = SyncRepo.SyncRules.Values.Single(r => r.ConnectedSystemId == ctx.Directory!.Id && r.Direction == SyncRuleDirection.Export);
        var exportMapping = directoryExportRule.AttributeFlowRules.Single();
        exportMapping.TargetConnectedSystemAttribute!.Type = AttributeDataType.Number;

        await DbContext.SaveChangesAsync();

        return ctx;
    }

    /// <summary>
    /// Builds a re-election topology (work package G review fix 3): HR's generated Account Name mapping
    /// (priority 2) plus a Payroll Connected System that PROJECTS the object and carries an ordinary,
    /// higher-priority (1) Account Name Attribute Flow of its own. Payroll has no generated mapping, so its
    /// own runs build no Unique Value Generation service until a pending generation actually reaches one via
    /// withdrawal re-election, which is exactly the gap this fix closes. HR only joins (Payroll projects).
    /// </summary>
    private async Task<GenerationContext> SetUpReElectedGenerationScenarioAsync()
    {
        var ctx = await SetUpBasicGenerationAsync(generatedMappingPriority: 2);

        var payroll = await CreateConnectedSystemAsync("Payroll");
        var payrollType = await CreateCsoTypeAsync(payroll.Id, "PayrollUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "employeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "accountNameOverride", Type = AttributeDataType.Text, Selected = true }
        });
        var payrollEmployeeIdAttr = payrollType.Attributes.Single(a => a.Name == "employeeId");
        var payrollOverrideAttr = payrollType.Attributes.Single(a => a.Name == "accountNameOverride");

        var payrollImportRule = await CreateImportSyncRuleAsync(payroll.Id, payrollType, ctx.MvType, "Payroll Import", enableProjection: true);
        payrollImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = payrollImportRule,
            SyncRuleId = payrollImportRule.Id,
            TargetMetaverseAttribute = ctx.MvEmployeeIdAttribute,
            TargetMetaverseAttributeId = ctx.MvEmployeeIdAttributeId,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = payrollEmployeeIdAttr, ConnectedSystemAttributeId = payrollEmployeeIdAttr.Id } }
        });
        payrollImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = payrollImportRule,
            SyncRuleId = payrollImportRule.Id,
            Priority = 1,
            TargetMetaverseAttribute = ctx.MvAccountNameAttribute,
            TargetMetaverseAttributeId = ctx.MvAccountNameAttributeId,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = payrollOverrideAttr, ConnectedSystemAttributeId = payrollOverrideAttr.Id } }
        });

        var hrImportRule = SyncRepo.SyncRules[ctx.HrImportRuleId];
        hrImportRule.ProjectToMetaverse = false;
        hrImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = hrImportRule,
            SyncRuleId = hrImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = ctx.MvEmployeeIdAttribute,
            TargetMetaverseAttributeId = ctx.MvEmployeeIdAttributeId,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = ctx.HrEmployeeIdAttribute, ConnectedSystemAttributeId = ctx.HrEmployeeIdAttribute.Id } }
        });

        await DbContext.SaveChangesAsync();

        return ctx with { Directory = payroll, DirectoryCsoTypeId = payrollType.Id };
    }

    private async Task<ConnectedSystemObject> SeedHrCsoAsync(GenerationContext ctx, string first, string last, string employeeId)
    {
        var hrType = SyncRepo.ObjectTypes[ctx.HrCsoTypeId];
        var externalIdAttr = hrType.Attributes.Single(a => a.IsExternalId);
        var firstAttr = hrType.Attributes.Single(a => a.Name == "first");
        var lastAttr = hrType.Attributes.Single(a => a.Name == "last");
        var employeeIdAttr = hrType.Attributes.Single(a => a.Name == "employeeId");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ctx.Hr.Id,
            TypeId = hrType.Id,
            Type = hrType,
            ConnectedSystem = SyncRepo.ConnectedSystems[ctx.Hr.Id],
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalIdAttr.Id, Attribute = externalIdAttr, GuidValue = Guid.NewGuid() });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = firstAttr.Id, Attribute = firstAttr, StringValue = first });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = lastAttr.Id, Attribute = lastAttr, StringValue = last });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = employeeIdAttr.Id, Attribute = employeeIdAttr, StringValue = employeeId });

        SyncRepo.SeedConnectedSystemObject(cso);
        await Task.CompletedTask;
        return cso;
    }

    private async Task<ConnectedSystemObject> SeedDirectoryCsoAsync(GenerationContext ctx, string employeeId, string sAMAccountName)
    {
        var directory = ctx.Directory ?? throw new InvalidOperationException("Context has no Directory system.");
        var directoryType = SyncRepo.ObjectTypes[ctx.DirectoryCsoTypeId!.Value];
        var externalIdAttr = directoryType.Attributes.Single(a => a.IsExternalId);
        var employeeIdAttr = directoryType.Attributes.Single(a => a.Name == "employeeId");
        var sAMAccountNameAttr = directoryType.Attributes.Single(a => a.Name == "sAMAccountName");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = directory.Id,
            TypeId = directoryType.Id,
            Type = directoryType,
            ConnectedSystem = SyncRepo.ConnectedSystems[directory.Id],
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalIdAttr.Id, Attribute = externalIdAttr, GuidValue = Guid.NewGuid() });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = employeeIdAttr.Id, Attribute = employeeIdAttr, StringValue = employeeId });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = sAMAccountNameAttr.Id, Attribute = sAMAccountNameAttr, StringValue = sAMAccountName });

        SyncRepo.SeedConnectedSystemObject(cso);
        await Task.CompletedTask;
        return cso;
    }

    private async Task<ConnectedSystemObject> SeedDirectoryOverrideCsoAsync(GenerationContext ctx, string employeeId, string accountNameOverride)
    {
        var directory = ctx.Directory ?? throw new InvalidOperationException("Context has no Directory system.");
        var directoryType = SyncRepo.ObjectTypes[ctx.DirectoryCsoTypeId!.Value];
        var externalIdAttr = directoryType.Attributes.Single(a => a.IsExternalId);
        var employeeIdAttr = directoryType.Attributes.Single(a => a.Name == "employeeId");
        var overrideAttr = directoryType.Attributes.Single(a => a.Name == "accountNameOverride");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = directory.Id,
            TypeId = directoryType.Id,
            Type = directoryType,
            ConnectedSystem = SyncRepo.ConnectedSystems[directory.Id],
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalIdAttr.Id, Attribute = externalIdAttr, GuidValue = Guid.NewGuid() });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = employeeIdAttr.Id, Attribute = employeeIdAttr, StringValue = employeeId });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = overrideAttr.Id, Attribute = overrideAttr, StringValue = accountNameOverride });

        SyncRepo.SeedConnectedSystemObject(cso);
        await Task.CompletedTask;
        return cso;
    }

    private async Task<ConnectedSystemObject> SeedDirectoryCsoWithNumericAccountNameAsync(GenerationContext ctx, string employeeId, int accountNumber)
    {
        var directory = ctx.Directory ?? throw new InvalidOperationException("Context has no Directory system.");
        var directoryType = SyncRepo.ObjectTypes[ctx.DirectoryCsoTypeId!.Value];
        var externalIdAttr = directoryType.Attributes.Single(a => a.IsExternalId);
        var employeeIdAttr = directoryType.Attributes.Single(a => a.Name == "employeeId");
        var sAMAccountNameAttr = directoryType.Attributes.Single(a => a.Name == "sAMAccountName");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = directory.Id,
            TypeId = directoryType.Id,
            Type = directoryType,
            ConnectedSystem = SyncRepo.ConnectedSystems[directory.Id],
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalIdAttr.Id, Attribute = externalIdAttr, GuidValue = Guid.NewGuid() });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = employeeIdAttr.Id, Attribute = employeeIdAttr, StringValue = employeeId });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = sAMAccountNameAttr.Id, Attribute = sAMAccountNameAttr, IntValue = accountNumber });

        SyncRepo.SeedConnectedSystemObject(cso);
        await Task.CompletedTask;
        return cso;
    }

    private async Task<ConnectedSystemObject> SeedPayrollCsoAsync(GenerationContext ctx, string employeeId, string? accountNameOverride)
    {
        var payroll = ctx.Directory ?? throw new InvalidOperationException("Context has no Payroll system.");
        var payrollType = SyncRepo.ObjectTypes[ctx.DirectoryCsoTypeId!.Value];
        var externalIdAttr = payrollType.Attributes.Single(a => a.IsExternalId);
        var employeeIdAttr = payrollType.Attributes.Single(a => a.Name == "employeeId");
        var overrideAttr = payrollType.Attributes.Single(a => a.Name == "accountNameOverride");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = payroll.Id,
            TypeId = payrollType.Id,
            Type = payrollType,
            ConnectedSystem = SyncRepo.ConnectedSystems[payroll.Id],
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalIdAttr.Id, Attribute = externalIdAttr, GuidValue = Guid.NewGuid() });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = employeeIdAttr.Id, Attribute = employeeIdAttr, StringValue = employeeId });
        if (accountNameOverride != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = overrideAttr.Id, Attribute = overrideAttr, StringValue = accountNameOverride });

        SyncRepo.SeedConnectedSystemObject(cso);
        await Task.CompletedTask;
        return cso;
    }

    private IReadOnlyList<string> ResolvedAccountNames(GenerationContext ctx) =>
        SyncRepo.MetaverseObjects.Values
            .Select(m => m.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvAccountNameAttributeId))
            .Where(v => v != null)
            .Select(v => v!.StringValue!)
            .ToList();

    private async Task SetSyncPageSizeAsync(int pageSize)
    {
        var setting = DbContext.ServiceSettingItems.FirstOrDefault(s => s.Key == "Sync.PageSize");
        if (setting != null)
        {
            setting.Value = pageSize.ToString();
            await DbContext.SaveChangesAsync();
        }
    }

    private async Task<Activity> RunFullSyncReturningActivityAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    /// <summary>
    /// Copies every entity the harness's current <see cref="WorkflowTestBase.SyncRepo"/> holds into
    /// <paramref name="target"/>, for a test that needs to swap in a repository double partway through (after
    /// building topology through the ordinary <see cref="SyncRepo"/>) rather than from the very start. Only the
    /// in-memory seed dictionaries a workflow test's helpers populate are copied; nothing here is a general
    /// repository clone.
    /// </summary>
    private void MigrateSeededStateTo(JIM.InMemoryData.SyncRepository target)
    {
        foreach (var cs in SyncRepo.ConnectedSystems.Values)
            target.SeedConnectedSystem(cs);
        foreach (var type in SyncRepo.ObjectTypes.Values)
            target.SeedObjectType(type);
        foreach (var rule in SyncRepo.SyncRules.Values)
            target.SeedSyncRule(rule);
        foreach (var cso in SyncRepo.ConnectedSystemObjects.Values)
            target.SeedConnectedSystemObject(cso);
        foreach (var mvo in SyncRepo.MetaverseObjects.Values)
            target.SeedMetaverseObject(mvo);
        foreach (var activity in SyncRepo.Activities.Values)
            target.SeedActivity(activity);
    }

    #endregion

    #region Test-only repository doubles

    private sealed class GeneratedValueCallCountingSyncRepository : JIM.InMemoryData.SyncRepository
    {
        public int GeneratedValueCallCount { get; private set; }

        public override Task CreateGeneratedValueAssignmentsAsync(IReadOnlyCollection<GeneratedValueAssignment> assignments)
        {
            GeneratedValueCallCount++;
            return base.CreateGeneratedValueAssignmentsAsync(assignments);
        }
    }

    /// <summary>
    /// Throws the moment the run tries to persist a generated assignment, simulating a hard mid-run failure so
    /// the reservation-release test can prove the reservation set is released even when the run does not
    /// complete normally.
    /// </summary>
    private sealed class ThrowingAfterGenerationSyncRepository : JIM.InMemoryData.SyncRepository
    {
        public override Task CreateGeneratedValueAssignmentsAsync(IReadOnlyCollection<GeneratedValueAssignment> assignments)
        {
            throw new InvalidOperationException("Simulated mid-run failure after generation, for the reservation-release test.");
        }
    }

    private sealed class InjectConflictOnCommitSyncRepository : JIM.InMemoryData.SyncRepository
    {
        private readonly int _metaverseAttributeId;
        private readonly string _normalisedValue;
        private bool _injected;

        public InjectConflictOnCommitSyncRepository(int metaverseAttributeId, string normalisedValue)
        {
            _metaverseAttributeId = metaverseAttributeId;
            _normalisedValue = normalisedValue;
        }

        public override Task CreateGeneratedValueAssignmentsAsync(IReadOnlyCollection<GeneratedValueAssignment> assignments)
        {
            if (!_injected && assignments.Any(a => a.MetaverseAttributeId == _metaverseAttributeId && a.NormalisedValue == _normalisedValue))
            {
                _injected = true;
                SeedGeneratedValueAssignment(new GeneratedValueAssignment
                {
                    Id = Guid.NewGuid(),
                    MetaverseObjectId = Guid.NewGuid(),
                    MetaverseAttributeId = _metaverseAttributeId,
                    Value = "joe.bloggs",
                    NormalisedValue = _normalisedValue,
                    State = GeneratedValueAssignmentState.Committed,
                    SyncRuleMappingGenerationId = 0,
                    Created = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow
                });
            }

            return base.CreateGeneratedValueAssignmentsAsync(assignments);
        }
    }

    #endregion
}
