// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Exceptions;
using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.TestSupport;
using JIM.Worker.Processors;
using JIM.Worker.Tests.UniqueValues;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Metaverse-Derived Attribute Flows in the worker (#1750, plan Phase 3), driven through the real Full and Delta
/// Synchronisation processors: the per-object level loop (derived levels interleaved with generation resolution), the
/// derived-input mark set on other hosting systems' objects at page flush ("Position 2"), and delta and full
/// selection honouring the mark. Runs with the feature flag on, as though it had shipped, except where a test says
/// otherwise.
/// </summary>
/// <remarks>
/// Topology: a Person Metaverse Object Type with Employee Id, Account Name, Region, Email and User Principal Name. HR
/// projects the Person and flows Employee Id and Account Name; its rule hosts the derived flows under test. AD joins
/// on Employee Id and flows Region, which a derived flow on HR's rule may read (Scenario 2). An optional Directory
/// export rule provisions mail and userPrincipalName from Email and User Principal Name (FR 8).
/// </remarks>
[TestFixture]
public class DerivedAttributeFlowWorkflowTests : WorkflowTestBase
{
    private const string EmailFromAccountName = "mv[\"Account Name\"] + \"@corp.local\"";
    private const string EmailFromAccountNameAndRegion = "mv[\"Account Name\"] + \"@\" + Lower(mv[\"Region\"]) + \".corp.local\"";
    private const string UpnFromEmail = "mv[\"Email\"]";

    // ---- Scenario 1 and FR 8 ----

    [Test]
    public async Task FullSync_TwoDerivedLevels_BothLandInOneChangeRecordAndOneExportEvaluationAsync()
    {
        var ctx = await SetUpAsync(emailExpression: EmailFromAccountName, withUpn: true, withDirectoryExport: true);
        SeedHr(ctx, "E1", "jbloggs");

        var activity = await RunFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var changes = SyncRepo.MetaverseObjectChanges.Values.Where(c => c.MetaverseObject == mvo).ToList();
        var directoryExports = SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == ctx.Directory!.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Where(r => r.ErrorType.HasValue && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet), Is.Empty);
            Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@corp.local"), "level 1 reads the Account Name flowed earlier in the same pass");
            Assert.That(Text(mvo, ctx.Upn), Is.EqualTo("jbloggs@corp.local"), "level 2 reads the Email derived at level 1 in the same pass");

            Assert.That(changes, Has.Count.EqualTo(1), "one Metaverse change record for the object's whole pass (FR 8)");
            Assert.That(changes.Single().AttributeChanges.Select(a => a.AttributeName),
                Is.SupersetOf(new[] { "Account Name", "Email", "User Principal Name" }),
                "derived values are ordinary Attribute Flow changes in the same record");

            Assert.That(directoryExports, Has.Count.EqualTo(1), "one export evaluation provisions the object with both derived values");
            var attributeIds = directoryExports.Single().AttributeValueChanges.Select(c => c.AttributeId).ToList();
            Assert.That(attributeIds, Does.Contain(ctx.DirectoryMail!.Id));
            Assert.That(attributeIds, Does.Contain(ctx.DirectoryUpn!.Id));
        }
    }

    [Test]
    public async Task FullSync_FlagOff_LegacyMetaverseReadSeesNothingAndNoMarksAreWrittenAsync()
    {
        // Flag off, the engine is exactly as it was before the feature: an import expression reading mv["..."] is an
        // ordinary flow that reads nothing, and marking and selection are inert.
        var ctx = await SetUpAsync(emailExpression: EmailFromAccountNameAndRegion, withUpn: false, withAd: true, flagOn: false);
        SeedHr(ctx, "E1", "jbloggs");
        SeedAd(ctx, "E1", "EMEA");

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Ad!);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(mvo, ctx.Email), Is.EqualTo("@.corp.local"), "legacy: mv[\"...\"] in an import expression reads null");
            Assert.That(Text(mvo, ctx.Region), Is.EqualTo("EMEA"));
            Assert.That(SyncRepo.DerivedInputMarkCalls, Is.Empty, "no graph, so nothing is ever marked");
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Any(c => c.DerivedInputChangePending), Is.False);
        }
    }

    // ---- Scenario 2: marking and delta selection ----

    [Test]
    public async Task DeltaSync_InputChangedByAnotherSystem_MarksHostingObjectAndItsNextDeltaReDerivesAsync()
    {
        var ctx = await SetUpScenario2Async();
        var hrCso = SeedHr(ctx, "E1", "jbloggs");
        var adCso = SeedAd(ctx, "E1", "EMEA");

        await RunFullSyncAsync(ctx.Hr);   // projects; Email not derivable yet (no Region)
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(Text(mvo, ctx.Email), Is.Null, "precondition: Region is absent, so Email contributes no value");

        await RunFullSyncAsync(ctx.Ad!);  // joins and flows Region, which HR's derived Email reads
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hrCso.DerivedInputChangePending, Is.True, "HR hosts a derived flow reading Region, so its object is marked");
            Assert.That(adCso.DerivedInputChangePending, Is.False, "the system being synchronised is never marked: its own derived pass already ran");
            Assert.That(Text(mvo, ctx.Email), Is.Null, "a derived flow runs only in its hosting system's synchronisation");
        }

        await RunDeltaSyncAsync(ctx.Hr);  // HR's own object is unchanged; the mark alone selects it
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@emea.corp.local"), "the HR delta re-derives Email from AD's Region");
            Assert.That(hrCso.DerivedInputChangePending, Is.False, "the mark is cleared once processed without error");
        }

        // An AD delta changes Region: HR is marked again and its next delta follows.
        SetText(adCso, ctx.AdRegion!, "APAC");
        await ModifyCsoAsync(adCso);
        await RunDeltaSyncAsync(ctx.Ad!);
        Assert.That(hrCso.DerivedInputChangePending, Is.True, "an AD delta changing Region marks HR's object");

        await RunDeltaSyncAsync(ctx.Hr);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@apac.corp.local"));
            Assert.That(hrCso.DerivedInputChangePending, Is.False);
        }
    }

    [Test]
    public async Task DeltaSync_HostingSystemSynchronisedBeforeTheSource_ConvergesOnItsNextDeltaAsync()
    {
        var ctx = await SetUpScenario2Async();
        SeedHr(ctx, "E1", "jbloggs");
        var adCso = SeedAd(ctx, "E1", "EMEA");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Ad!);
        await RunDeltaSyncAsync(ctx.Hr);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@emea.corp.local"), "precondition: converged");

        // Wrong order: AD's source data changes, but HR synchronises before AD does.
        SetText(adCso, ctx.AdRegion!, "APAC");
        await ModifyCsoAsync(adCso);
        await RunDeltaSyncAsync(ctx.Hr);
        Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@emea.corp.local"), "HR ran before the new Region reached the Metaverse");

        await RunDeltaSyncAsync(ctx.Ad!);
        Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@emea.corp.local"), "stale for one cycle: AD does not run HR's derived flow");

        await RunDeltaSyncAsync(ctx.Hr);
        Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@apac.corp.local"), "the mark makes HR's next delta converge, rather than never");
    }

    [Test]
    public async Task FullSync_SourceResynchronisedWithNoChange_MarksNothingAsync()
    {
        var ctx = await SetUpScenario2Async();
        var hrCso = SeedHr(ctx, "E1", "jbloggs");
        SeedAd(ctx, "E1", "EMEA");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Ad!);
        await RunDeltaSyncAsync(ctx.Hr);
        var callsBefore = SyncRepo.DerivedInputMarkCalls.Count;

        await RunFullSyncAsync(ctx.Ad!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.DerivedInputMarkCalls, Has.Count.EqualTo(callsBefore), "no Metaverse attribute changed, so there is nothing to mark");
            Assert.That(hrCso.DerivedInputChangePending, Is.False);
        }
    }

    // ---- Full synchronisation selection ----

    [Test]
    public async Task FullSync_UnchangedUnmarkedObject_StagesNothingAsync()
    {
        var ctx = await SetUpAsync(emailExpression: EmailFromAccountName, withUpn: true, withDirectoryExport: true);
        SeedHr(ctx, "E1", "jbloggs");
        await RunFullSyncAsync(ctx.Hr);
        var changesBefore = SyncRepo.MetaverseObjectChanges.Count;
        var exportsBefore = SyncRepo.PendingExports.Count;

        var activity = await RunFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.MetaverseObjectChanges, Has.Count.EqualTo(changesBefore), "re-deriving identical values changes nothing");
            Assert.That(SyncRepo.PendingExports, Has.Count.EqualTo(exportsBefore), "and so stages no export");
            Assert.That(activity.RunProfileExecutionItems.Where(r => r.ObjectChangeType == ObjectChangeType.AttributeFlow), Is.Empty);
            Assert.That(SyncRepo.DerivedInputMarkCalls, Is.Empty);
        }
    }

    [Test]
    public async Task FullSync_UnchangedButMarkedObject_IsNotSkippedAsync()
    {
        var ctx = await SetUpScenario2Async();
        var hrCso = SeedHr(ctx, "E1", "jbloggs");
        SeedAd(ctx, "E1", "EMEA");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Ad!);
        Assert.That(hrCso.DerivedInputChangePending, Is.True, "precondition: marked");

        // The loader flags an object unchanged since the last synchronisation; the mark must override that skip, as
        // ScopeReviewPending does (the PostgreSQL loader never flags a marked object; this is the processor's guard).
        hrCso.IsUnchangedSinceLastSync = true;
        await RunFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@emea.corp.local"));
            Assert.That(hrCso.DerivedInputChangePending, Is.False);
        }
    }

    // ---- Clearing only on success ----

    [Test]
    public async Task DeltaSync_MarkedObjectWhoseProcessingFails_StaysMarkedUntilItSucceedsAsync()
    {
        // Missing Input Behaviour "Fail the object" on Region: when AD withdraws Region the HR object errors, and an
        // errored object must keep its mark so a later run re-evaluates it.
        var ctx = await SetUpScenario2Async();
        var hrCso = SeedHr(ctx, "E1", "jbloggs");
        var adCso = SeedAd(ctx, "E1", "EMEA");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Ad!);
        await RunDeltaSyncAsync(ctx.Hr);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@emea.corp.local"), "precondition: converged");
        ctx.EmailMapping.Sources[0].MissingInputBehaviour = MissingInputBehaviour.FailObject;

        RemoveText(adCso, ctx.AdRegion!);
        await ModifyCsoAsync(adCso);
        await RunDeltaSyncAsync(ctx.Ad!);
        Assert.That(hrCso.DerivedInputChangePending, Is.True, "withdrawing Region marks HR");

        var failed = await RunDeltaSyncAsync(ctx.Hr);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.ExpressionMissingInput), Is.True,
                "precondition: the derived flow failed the object");
            Assert.That(hrCso.DerivedInputChangePending, Is.True, "an object whose processing errored keeps its mark");
        }

        SetText(adCso, ctx.AdRegion!, "APAC");
        await ModifyCsoAsync(adCso);
        await RunDeltaSyncAsync(ctx.Ad!);
        await RunDeltaSyncAsync(ctx.Hr);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@apac.corp.local"));
            Assert.That(hrCso.DerivedInputChangePending, Is.False, "cleared once processing succeeds");
        }
    }

    // ---- One bulk mark per page, none per object ----

    [Test]
    public async Task FullSync_ManyObjectsMarkedAcrossPages_OneBulkMarkPerPageAndNonePerObjectAsync()
    {
        var ctx = await SetUpScenario2Async();
        for (var i = 1; i <= 5; i++)
        {
            SeedHr(ctx, $"E{i}", $"user{i}");
            SeedAd(ctx, $"E{i}", "EMEA");
        }

        await SetSyncPageSizeAsync(2);
        await RunFullSyncAsync(ctx.Hr);
        var (countingRepository, counts) = CountingSyncRepositoryProxy.Create(SyncRepo);

        await RunFullSyncAsync(ctx.Ad!, countingRepository);   // 5 objects in pages of 2: three pages, each marking

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts.GetValueOrDefault(nameof(ISyncRepository.MarkConnectedSystemObjectsDerivedInputChangePendingAsync)), Is.EqualTo(3),
                "one bulk mark per page flush, never one per object");
            Assert.That(SyncRepo.DerivedInputMarkCalls.Sum(call => call.Count), Is.EqualTo(5));
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == ctx.Hr.Id).All(c => c.DerivedInputChangePending), Is.True);
            Assert.That(counts.GetValueOrDefault(nameof(ISyncRepository.ClearConnectedSystemObjectDerivedInputChangePendingAsync)), Is.Zero,
                "AD's own objects were never marked, so there is nothing to clear");
        }
    }

    // ---- FR 10: generation interleaved with derived levels ----

    [Test]
    public async Task FullSync_GeneratedAttributeAtLevelOne_IsResolvedBeforeLevelTwoReadsItAsync()
    {
        // Account Name is generated from a base reading mv["EmployeeId"], so it sits at level 1; Email reads it at
        // level 2. The level-1 generation must be resolved (one ResolveAsync batch) before level 2 evaluates.
        var ctx = await SetUpAsync(emailExpression: EmailFromAccountName, withUpn: true, generatedAccountNameBase: "Lower(mv[\"EmployeeId\"])");
        SeedHr(ctx, "E1", accountName: null);

        await RunFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(mvo, ctx.AccountName), Is.EqualTo("e1"), "the generated value, resolved at level 1");
            Assert.That(Text(mvo, ctx.Email), Is.EqualTo("e1@corp.local"), "level 2 read the resolved generated value");
            Assert.That(Text(mvo, ctx.Upn), Is.EqualTo("e1@corp.local"), "and level 3 the derived Email");
            Assert.That(mvo.PendingGeneratedValues, Is.Empty);
        }
    }

    // ---- Run-start cycle ----

    [Test]
    public async Task FullSync_EnabledDerivedFlowsFormACycle_FailsTheRunHardBeforeProcessingAnyObjectAsync()
    {
        var ctx = await SetUpAsync(emailExpression: "mv[\"User Principal Name\"]", withUpn: true);
        SeedHr(ctx, "E1", "jbloggs");

        var (run, activity) = await PrepareFullSyncAsync(ctx.Hr);

        var thrown = Assert.ThrowsAsync<DerivedFlowCycleException>(async () => await run());
        await FailRunLikeTheWorkerAsync(activity, thrown!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError), "the run fails hard");
            Assert.That(activity.ErrorMessage, Does.Contain("cycle"), "the cycle is named on the Activity, never silent");
            Assert.That(activity.ErrorMessage, Does.Contain("Email").And.Contain("User Principal Name").And.Contain("HR Import"),
                "every attribute and Synchronisation Rule on the cycle is named");
            Assert.That(activity.ErrorStackTrace, Is.Null,
                "a cycle is a configuration fault the administrator fixes, recorded as a clean message without a stack trace");
            Assert.That(activity.RunProfileExecutionItems, Is.Empty, "no object is processed on a guessed order");
            Assert.That(SyncRepo.MetaverseObjects, Is.Empty);
        }
    }

    [Test]
    public async Task DeltaSync_EnabledDerivedFlowsFormACycle_FailsTheRunHardAsync()
    {
        var ctx = await SetUpAsync(emailExpression: EmailFromAccountName, withUpn: true);
        var hrCso = SeedHr(ctx, "E1", "jbloggs");
        await RunFullSyncAsync(ctx.Hr);

        ctx.EmailMapping.Sources[0].Expression = "mv[\"User Principal Name\"]";
        await ModifyCsoAsync(hrCso);

        var (run, activity) = await PrepareDeltaSyncAsync(ctx.Hr);

        var thrown = Assert.ThrowsAsync<DerivedFlowCycleException>(async () => await run());
        await FailRunLikeTheWorkerAsync(activity, thrown!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(activity.ErrorMessage, Does.Contain("cycle").And.Contain("HR Import"));
            Assert.That(activity.ErrorStackTrace, Is.Null);
            Assert.That(activity.RunProfileExecutionItems, Is.Empty, "the changed object is not processed");
        }
    }

    // ---- Attribute Flow error parity ----

    [Test]
    public async Task DeltaSync_DerivedExpressionThrows_FailsTheObjectAndNamesTheHostingRuleAsync()
    {
        var ctx = await SetUpAsync(emailExpression: EmailFromAccountName, withUpn: false);
        var hrCso = SeedHr(ctx, "E1", "jbloggs");
        await RunFullSyncAsync(ctx.Hr);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();

        // Still reads mv, so still derived; no longer parses.
        ctx.EmailMapping.Sources[0].Expression = "mv[\"Account Name\"] + @@@";
        SetText(hrCso, ctx.HrAccountName, "jsmith");
        await ModifyCsoAsync(hrCso);

        var activity = await RunDeltaSyncAsync(ctx.Hr);

        var errors = activity.RunProfileExecutionItems.Where(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.ExpressionEvaluationError).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Has.Count.EqualTo(1), "a derived expression error is an Attribute Flow error on the object");
            Assert.That(errors.Single().ErrorMessage, Does.Contain("'Email'").And.Contain("Synchronisation Rule 'HR Import'"),
                "the error names the hosting Synchronisation Rule");
            Assert.That(Text(mvo, ctx.AccountName), Is.EqualTo("jbloggs"), "the object failed: none of its pass is applied (decision 10)");
            Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@corp.local"));
        }
    }

    [Test]
    public async Task FullSync_DerivedMappingMissingInputFailsTheMapping_RecordsAnAttributeFlowErrorNamingTheRuleAsync()
    {
        var ctx = await SetUpScenario2Async(emailMissingInputBehaviour: MissingInputBehaviour.FailMapping);
        SeedHr(ctx, "E1", "jbloggs");

        var activity = await RunFullSyncAsync(ctx.Hr);   // Region absent: the derived Email mapping fails, the rest flows

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var errors = activity.RunProfileExecutionItems.Where(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.ExpressionMissingInput).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Has.Count.EqualTo(1), "surfaced exactly as the ordinary pass surfaces a failed mapping");
            Assert.That(errors.Single().ErrorMessage, Does.Contain("mv[\"Region\"]").And.Contain("Synchronisation Rule 'HR Import'"));
            Assert.That(Text(mvo, ctx.AccountName), Is.EqualTo("jbloggs"), "the object's other attributes still flow");
            Assert.That(Text(mvo, ctx.Email), Is.Null);
        }
    }

    // ---- Keeping the mark: derived-flow errors and the row-version guard ----

    [Test]
    public async Task DeltaSync_MarkedObjectWhoseDerivedMappingFails_KeepsItsMarkAndReportsAgainUntilItSucceedsAsync()
    {
        // Missing Input Behaviour "Fail the mapping" on the derived Email: the object's other attributes flow, but the
        // derived flow failed (decision 10), so the mark is kept and every run re-evaluates and re-reports it.
        var ctx = await SetUpScenario2Async(emailMissingInputBehaviour: MissingInputBehaviour.FailMapping);
        var hrCso = SeedHr(ctx, "E1", "jbloggs");
        var adCso = SeedAd(ctx, "E1", "EMEA");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Ad!);
        await RunDeltaSyncAsync(ctx.Hr);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@emea.corp.local"), "precondition: converged");

        RemoveText(adCso, ctx.AdRegion!);
        await ModifyCsoAsync(adCso);
        await RunDeltaSyncAsync(ctx.Ad!);
        Assert.That(hrCso.DerivedInputChangePending, Is.True, "precondition: withdrawing Region marks HR");

        var first = await RunDeltaSyncAsync(ctx.Hr);
        var second = await RunDeltaSyncAsync(ctx.Hr);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.RunProfileExecutionItems.Count(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.ExpressionMissingInput), Is.EqualTo(1),
                "the derived mapping failed");
            Assert.That(second.RunProfileExecutionItems.Count(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.ExpressionMissingInput), Is.EqualTo(1),
                "the mark was kept, so the next delta selected the object again and reported the error again");
            Assert.That(hrCso.DerivedInputChangePending, Is.True, "still marked while the derived flow keeps failing");
        }

        SetText(adCso, ctx.AdRegion!, "APAC");
        await ModifyCsoAsync(adCso);
        await RunDeltaSyncAsync(ctx.Ad!);
        await RunDeltaSyncAsync(ctx.Hr);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@apac.corp.local"));
            Assert.That(hrCso.DerivedInputChangePending, Is.False, "cleared once the derived pass succeeds");
        }
    }

    [Test]
    public async Task DeltaSync_MarkedObjectWithOnlyAnOrdinaryMappingError_IsClearedAsBeforeAsync()
    {
        // An ordinary (non-derived) mapping's error keeps today's ScopeReviewPending-style behaviour: the object was
        // processed, its derived flows evaluated cleanly, so the mark is cleared.
        var ctx = await SetUpAsync(emailExpression: EmailFromAccountNameAndRegion, withUpn: false, withAd: true,
            emailMissingInputBehaviour: MissingInputBehaviour.ContributeNoValue, withOrdinaryFailingMapping: true);
        var hrCso = SeedHr(ctx, "E1", "jbloggs");
        SeedAd(ctx, "E1", "EMEA");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Ad!);
        Assert.That(hrCso.DerivedInputChangePending, Is.True, "precondition: marked");

        var activity = await RunDeltaSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.ExpressionMissingInput), Is.True,
                "precondition: the ordinary mapping failed");
            Assert.That(Text(SyncRepo.MetaverseObjects.Values.Single(), ctx.Email), Is.EqualTo("jbloggs@emea.corp.local"));
            Assert.That(hrCso.DerivedInputChangePending, Is.False, "an ordinary mapping error does not keep the mark");
        }
    }

    [Test]
    public async Task DeltaSync_ObjectReMarkedAfterThePageLoad_StaysMarkedAndTheNextDeltaSeesTheNewInputAsync()
    {
        // In-memory model of two runs in parallel: after HR's delta has loaded and evaluated the object, AD's run
        // persists a new Region and re-marks the object, just before HR's page flush clears the marks it processed.
        var ctx = await SetUpScenario2Async();
        var hrCso = SeedHr(ctx, "E1", "jbloggs");
        var adCso = SeedAd(ctx, "E1", "EMEA");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Ad!);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(hrCso.DerivedInputChangePending, Is.True, "precondition: marked");

        var raced = false;
        var racingRepository = InterceptingSyncRepositoryProxy.Create(SyncRepo,
            nameof(ISyncRepository.ClearConnectedSystemObjectDerivedInputChangePendingAsync), () =>
            {
                if (raced)
                    return;
                raced = true;
                // AD's concurrent run: its Region change reaches the Metaverse, then it marks HR's object.
                SetText(adCso, ctx.AdRegion!, "AMER");
                mvo.AttributeValues.Single(av => av.AttributeId == ctx.Region.Id).StringValue = "AMER";
                SyncRepo.MarkConnectedSystemObjectsDerivedInputChangePendingAsync([new DerivedInputChangeMark(mvo.Id, ctx.Hr.Id)]).GetAwaiter().GetResult();
            });

        await RunDeltaSyncAsync(ctx.Hr, racingRepository);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(raced, Is.True, "precondition: the concurrent re-mark happened between load and clear");
            Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@emea.corp.local"), "HR evaluated the Region it had read");
            Assert.That(hrCso.DerivedInputChangePending, Is.True, "the re-mark moved the row version, so HR's clear left it in place");
        }

        await RunDeltaSyncAsync(ctx.Hr);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(mvo, ctx.Email), Is.EqualTo("jbloggs@amer.corp.local"), "the next delta re-derives from the newer Region");
            Assert.That(hrCso.DerivedInputChangePending, Is.False);
        }
    }

    [Test]
    public async Task DeltaSync_MarkedObjectAlsoWrittenByItsOwnRun_ConvergesOnTheNextRunAsync()
    {
        // The run's own write to the row (here the Temporal Scope Reconciler flag's clear, at the same page flush and
        // before the derived clear) also moves the row version, so the first run leaves the mark in place. That write
        // does not recur, so the next run clears it: one extra evaluation, never a loop.
        var ctx = await SetUpScenario2Async();
        var hrCso = SeedHr(ctx, "E1", "jbloggs");
        SeedAd(ctx, "E1", "EMEA");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Ad!);
        hrCso.ScopeReviewPending = true;

        await RunDeltaSyncAsync(ctx.Hr);
        var afterFirst = (Marked: hrCso.DerivedInputChangePending, ScopeFlag: hrCso.ScopeReviewPending);
        await RunDeltaSyncAsync(ctx.Hr);
        var afterSecond = hrCso.DerivedInputChangePending;
        var third = await RunDeltaSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterFirst.ScopeFlag, Is.False, "precondition: the first run cleared the scope flag, writing the row");
            Assert.That(afterFirst.Marked, Is.True, "that write moved the row version, so the derived clear was skipped (fail-safe)");
            Assert.That(afterSecond, Is.False, "the next run writes nothing else to the row, so the clear applies");
            Assert.That(third.RunProfileExecutionItems, Is.Empty, "and the object is not selected again: it has converged");
            Assert.That(Text(SyncRepo.MetaverseObjects.Values.Single(), ctx.Email), Is.EqualTo("jbloggs@emea.corp.local"));
        }
    }

    // ---- Server recall paths: re-election skips derived flows ----

    [Test]
    public async Task ExecuteSyncRuleDeletionRecallAsync_DerivedFlowIsTheSurvivingContributor_IsNotReFlowedWithoutAMetaverseViewAsync()
    {
        // AD contributes Email directly (priority 1) over HR's derived Email (priority 2). Deleting AD's rule with
        // recall re-elects the surviving contributor. A derived flow must never be re-flowed there as an ordinary
        // one: it would read no mv["..."] inputs and write "@corp.local" over the Metaverse (decision 5).
        var ctx = await SetUpAsync(emailExpression: EmailFromAccountName, withUpn: false, withAd: true, adContributesEmail: true);
        SeedHr(ctx, "E1", "jbloggs");
        SeedAd(ctx, "E1", "EMEA", mail: "joe.bloggs@ad.corp.local");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Ad!);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(Text(mvo, ctx.Email), Is.EqualTo("joe.bloggs@ad.corp.local"), "precondition: AD's higher-priority Email wins");

        var task = await DisableRuleAndBuildRecallTaskAsync(ctx.AdImport!);
        await Jim.ConnectedSystems.ExecuteSyncRuleDeletionRecallAsync(task);

        mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(Text(mvo, ctx.Email), Is.Null,
            "the derived Email must not be evaluated by re-election, where no Metaverse view exists (it would write " +
            "\"@corp.local\"); it is re-derived by HR's own synchronisation, which Phase 4's marking schedules");
    }

    // ---- Helpers ----

    private sealed record Context(
        ConnectedSystem Hr,
        ConnectedSystemObjectType HrType,
        ConnectedSystemObjectTypeAttribute HrAccountName,
        SyncRule HrImport,
        SyncRuleMapping EmailMapping,
        MetaverseAttribute AccountName,
        MetaverseAttribute Region,
        MetaverseAttribute Email,
        MetaverseAttribute Upn,
        ConnectedSystem? Ad,
        ConnectedSystemObjectType? AdType,
        ConnectedSystemObjectTypeAttribute? AdRegion,
        SyncRule? AdImport,
        ConnectedSystem? Directory,
        ConnectedSystemObjectTypeAttribute? DirectoryMail,
        ConnectedSystemObjectTypeAttribute? DirectoryUpn);

    private Task<Context> SetUpScenario2Async(MissingInputBehaviour emailMissingInputBehaviour = MissingInputBehaviour.ContributeNoValue) =>
        SetUpAsync(emailExpression: EmailFromAccountNameAndRegion, withUpn: false, withAd: true, emailMissingInputBehaviour: emailMissingInputBehaviour);

    private async Task<Context> SetUpAsync(
        string emailExpression,
        bool withUpn,
        bool withAd = false,
        bool withDirectoryExport = false,
        bool flagOn = true,
        MissingInputBehaviour emailMissingInputBehaviour = MissingInputBehaviour.EvaluateAnyway,
        string? generatedAccountNameBase = null,
        bool adContributesEmail = false,
        bool withOrdinaryFailingMapping = false)
    {
        if (flagOn)
            await EnableAllFeatureFlagsAsync();

        var mvType = await CreateMvObjectTypeAsync("Person");
        var employeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var accountName = await AddMvAttributeAsync(mvType, "Account Name");
        var region = await AddMvAttributeAsync(mvType, "Region");
        var email = await AddMvAttributeAsync(mvType, "Email");
        var upn = await AddMvAttributeAsync(mvType, "User Principal Name");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "HrPerson", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "employeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "accountName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "department", Type = AttributeDataType.Text, Selected = true }
        });
        var hrEmployeeId = hrType.Attributes.Single(a => a.Name == "employeeId");
        var hrAccountName = hrType.Attributes.Single(a => a.Name == "accountName");

        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        FromAttribute(hrImport, employeeId, hrEmployeeId);
        if (generatedAccountNameBase == null)
        {
            FromAttribute(hrImport, accountName, hrAccountName);
        }
        else
        {
            hrImport.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = hrImport,
                SyncRuleId = hrImport.Id,
                TargetMetaverseAttribute = accountName,
                TargetMetaverseAttributeId = accountName.Id,
                Generation = new SyncRuleMappingGeneration
                {
                    TokenKind = GeneratedValueTokenKind.OnlyIfTaken,
                    SuffixStyle = GeneratedValueSuffixStyle.Number,
                    SuffixStart = 1,
                    AttemptLimit = 100,
                    NeverReuse = true
                },
                Sources = { new SyncRuleMappingSource { Order = 0, Expression = generatedAccountNameBase } }
            });
        }

        var emailMapping = FromExpression(hrImport, email, emailExpression);
        emailMapping.Sources[0].MissingInputBehaviour = emailMissingInputBehaviour;
        if (adContributesEmail)
            emailMapping.Priority = 2;
        if (withUpn)
            FromExpression(hrImport, upn, UpnFromEmail);
        if (withOrdinaryFailingMapping)
        {
            // An ordinary (non-derived) flow whose Connected System input no seeded object carries, failing only
            // the mapping on every evaluation.
            var department = await AddMvAttributeAsync(mvType, "Department");
            FromExpression(hrImport, department, "cs[\"department\"]").Sources[0].MissingInputBehaviour = MissingInputBehaviour.FailMapping;
        }
        await DbContext.SaveChangesAsync();

        ConnectedSystem? ad = null;
        ConnectedSystemObjectType? adType = null;
        ConnectedSystemObjectTypeAttribute? adRegion = null;
        SyncRule? adImportRule = null;
        if (withAd)
        {
            ad = await CreateConnectedSystemAsync("AD");
            adType = await CreateCsoTypeAsync(ad.Id, "AdUser", new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                new() { Name = "employeeId", Type = AttributeDataType.Text, Selected = true },
                new() { Name = "region", Type = AttributeDataType.Text, Selected = true },
                new() { Name = "mail", Type = AttributeDataType.Text, Selected = true }
            });
            var adEmployeeId = adType.Attributes.Single(a => a.Name == "employeeId");
            adRegion = adType.Attributes.Single(a => a.Name == "region");

            var adImport = await CreateImportSyncRuleAsync(ad.Id, adType, mvType, "AD Import", enableProjection: false);
            adImportRule = adImport;
            FromAttribute(adImport, region, adRegion);
            if (adContributesEmail)
                FromAttribute(adImport, email, adType.Attributes.Single(a => a.Name == "mail")).Priority = 1;
            adImport.ObjectMatchingRules.Add(new ObjectMatchingRule
            {
                SyncRule = adImport,
                SyncRuleId = adImport.Id,
                Order = 0,
                CaseSensitive = true,
                TargetMetaverseAttribute = employeeId,
                TargetMetaverseAttributeId = employeeId.Id,
                Sources = new List<ObjectMatchingRuleSource>
                {
                    new() { Order = 0, ConnectedSystemAttribute = adEmployeeId, ConnectedSystemAttributeId = adEmployeeId.Id }
                }
            });
            await DbContext.SaveChangesAsync();
        }

        ConnectedSystem? directory = null;
        ConnectedSystemObjectTypeAttribute? directoryMail = null;
        ConnectedSystemObjectTypeAttribute? directoryUpn = null;
        if (withDirectoryExport)
        {
            directory = await CreateConnectedSystemAsync("Directory");
            var directoryType = await CreateCsoTypeAsync(directory.Id, "DirectoryUser", new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                new() { Name = "mail", Type = AttributeDataType.Text, Selected = true },
                new() { Name = "userPrincipalName", Type = AttributeDataType.Text, Selected = true }
            });
            directoryMail = directoryType.Attributes.Single(a => a.Name == "mail");
            directoryUpn = directoryType.Attributes.Single(a => a.Name == "userPrincipalName");

            var export = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export", enableProvisioning: true);
            ToAttribute(export, directoryMail, email);
            ToAttribute(export, directoryUpn, upn);
            await DbContext.SaveChangesAsync();
        }

        return new Context(hr, hrType, hrAccountName, hrImport, emailMapping, accountName, region, email, upn,
            ad, adType, adRegion, adImportRule, directory, directoryMail, directoryUpn);
    }

    /// <summary>
    /// Tests run as though the features had shipped (test/CLAUDE.md): every catalogued flag on, taken from the
    /// shared <see cref="InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled"/> fixture. The workflow harness
    /// reads Service Settings from its database, so the rows are stored there rather than substituting a repository.
    /// </summary>
    private async Task EnableAllFeatureFlagsAsync()
    {
        DbContext.ServiceSettingItems.AddRange(await InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled().GetAllSettingsAsync());
        await DbContext.SaveChangesAsync();
    }

    private async Task<MetaverseAttribute> AddMvAttributeAsync(MetaverseObjectType mvType, string name)
    {
        var attribute = new MetaverseAttribute
        {
            Name = name,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(attribute);
        await DbContext.SaveChangesAsync();
        if (!mvType.Attributes.Contains(attribute))
            mvType.Attributes.Add(attribute);
        return attribute;
    }

    private static SyncRuleMapping FromAttribute(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source)
    {
        var mapping = new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        };
        rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }

    private static SyncRuleMapping FromExpression(SyncRule rule, MetaverseAttribute target, string expression)
    {
        var mapping = new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = expression } }
        };
        rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }

    private static void ToAttribute(SyncRule rule, ConnectedSystemObjectTypeAttribute target, MetaverseAttribute source) =>
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });

    private ConnectedSystemObject SeedHr(Context ctx, string employeeId, string? accountName) =>
        SeedCso(ctx.Hr, ctx.HrType, ("employeeId", employeeId), ("accountName", accountName));

    private ConnectedSystemObject SeedAd(Context ctx, string employeeId, string? region, string? mail = null) =>
        SeedCso(ctx.Ad!, ctx.AdType!, ("employeeId", employeeId), ("region", region), ("mail", mail));

    private ConnectedSystemObject SeedCso(ConnectedSystem system, ConnectedSystemObjectType type, params (string Name, string? Value)[] values)
    {
        var externalId = type.Attributes.Single(a => a.IsExternalId);
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            ConnectedSystem = SyncRepo.ConnectedSystems[system.Id],
            TypeId = type.Id,
            Type = type,
            Created = DateTime.UtcNow.AddMinutes(-10)
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalId.Id, Attribute = externalId, GuidValue = Guid.NewGuid() });
        foreach (var (name, value) in values.Where(v => v.Value != null))
            SetText(cso, type.Attributes.Single(a => a.Name == name), value!);

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static void SetText(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string value)
    {
        RemoveText(cso, attribute);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attribute.Id, Attribute = attribute, StringValue = value });
    }

    private static void RemoveText(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute) =>
        cso.AttributeValues.RemoveAll(av => av.AttributeId == attribute.Id);

    private static string? Text(MetaverseObject mvo, MetaverseAttribute attribute) =>
        mvo.AttributeValues
            .Where(av => (av.AttributeId == attribute.Id || av.Attribute?.Id == attribute.Id) && !av.NullValue)
            .Select(av => av.StringValue)
            .SingleOrDefault();

    private async Task SetSyncPageSizeAsync(int pageSize)
    {
        var setting = DbContext.ServiceSettingItems.Single(s => s.Key == "Sync.PageSize");
        setting.Value = pageSize.ToString();
        await DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Disables <paramref name="rule"/> and builds its deletion recall task, as TaskingServer does at queue time
    /// (the SyncRuleDeletionRecallWorkflowTests pattern).
    /// </summary>
    private async Task<DeleteSyncRuleWorkerTask> DisableRuleAndBuildRecallTaskAsync(SyncRule rule)
    {
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Modified).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        rule.Enabled = false;
        rule.DisabledReason = "Deletion in progress: contributed attribute values are being recalled.";
        DbContext.Entry(rule).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
        var activity = new Activity
        {
            TargetName = rule.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.RecallAttributeValues,
            Status = ActivityStatus.InProgress,
            ConnectedSystemId = rule.ConnectedSystemId,
            Executed = DateTime.UtcNow
        };
        DbContext.Activities.Add(activity);
        await DbContext.SaveChangesAsync();

        return new DeleteSyncRuleWorkerTask(rule.Id, recallContributedValues: true) { Activity = activity };
    }

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem, ISyncRepository? repository = null)
    {
        var (run, activity) = await PrepareFullSyncAsync(connectedSystem, repository);
        await run();
        return activity;
    }

    private async Task<Activity> RunDeltaSyncAsync(ConnectedSystem connectedSystem, ISyncRepository? repository = null)
    {
        var (run, activity) = await PrepareDeltaSyncAsync(connectedSystem, repository);
        await run();
        return activity;
    }

    /// <summary>
    /// A Full Synchronisation ready to run, with its Activity, so a test can inspect the Activity of a run that throws.
    /// </summary>
    private async Task<(Func<Task> Run, Activity Activity)> PrepareFullSyncAsync(ConnectedSystem connectedSystem, ISyncRepository? repository = null)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        var processor = new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), repository ?? SyncRepo, reloaded, profile, activity, new CancellationTokenSource());
        return (processor.PerformFullSyncAsync, activity);
    }

    private async Task<(Func<Task> Run, Activity Activity)> PrepareDeltaSyncAsync(ConnectedSystem connectedSystem, ISyncRepository? repository = null)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        var processor = new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), repository ?? SyncRepo, reloaded, profile, activity, new CancellationTokenSource());
        return (processor.PerformDeltaSyncAsync, activity);
    }

    /// <summary>
    /// Fails <paramref name="activity"/> with <paramref name="exception"/> exactly as the Worker's sync-run boundary
    /// does (Worker.SafeFailActivityAsync's first and ordinary attempt), so a test can assert what an administrator
    /// sees on the Activity.
    /// </summary>
    private async Task FailRunLikeTheWorkerAsync(Activity activity, Exception exception) =>
        await Jim.Activities.FailActivityWithErrorAsync(activity, exception);
}
