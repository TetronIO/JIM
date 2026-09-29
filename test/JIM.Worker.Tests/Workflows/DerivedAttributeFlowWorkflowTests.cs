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
using JIM.Models.Transactional;
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

        var thrown = Assert.ThrowsAsync<DerivedFlowCycleException>(async () => await RunFullSyncAsync(ctx.Hr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown!.Message, Does.Contain("cycle"));
            Assert.That(thrown.Message, Does.Contain("Email").And.Contain("User Principal Name").And.Contain("HR Import"));
            Assert.That(SyncRepo.MetaverseObjects, Is.Empty, "no object is processed on a guessed order");
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

        var thrown = Assert.ThrowsAsync<DerivedFlowCycleException>(async () => await RunDeltaSyncAsync(ctx.Hr));
        Assert.That(thrown!.Message, Does.Contain("cycle"));
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
        string? generatedAccountNameBase = null)
    {
        if (flagOn)
            await EnableDerivedFlowsFlagAsync();

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
            new() { Name = "accountName", Type = AttributeDataType.Text, Selected = true }
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
        if (withUpn)
            FromExpression(hrImport, upn, UpnFromEmail);
        await DbContext.SaveChangesAsync();

        ConnectedSystem? ad = null;
        ConnectedSystemObjectType? adType = null;
        ConnectedSystemObjectTypeAttribute? adRegion = null;
        if (withAd)
        {
            ad = await CreateConnectedSystemAsync("AD");
            adType = await CreateCsoTypeAsync(ad.Id, "AdUser", new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                new() { Name = "employeeId", Type = AttributeDataType.Text, Selected = true },
                new() { Name = "region", Type = AttributeDataType.Text, Selected = true }
            });
            var adEmployeeId = adType.Attributes.Single(a => a.Name == "employeeId");
            adRegion = adType.Attributes.Single(a => a.Name == "region");

            var adImport = await CreateImportSyncRuleAsync(ad.Id, adType, mvType, "AD Import", enableProjection: false);
            FromAttribute(adImport, region, adRegion);
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
            ad, adType, adRegion, directory, directoryMail, directoryUpn);
    }

    private async Task EnableDerivedFlowsFlagAsync()
    {
        DbContext.ServiceSettingItems.Add(new ServiceSetting
        {
            Key = FeatureFlagCatalogue.MetaverseDerivedAttributeFlows.Key,
            DisplayName = FeatureFlagCatalogue.MetaverseDerivedAttributeFlows.DisplayName,
            Category = ServiceSettingCategory.FeatureFlags,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "false",
            Value = "true"
        });
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

    private static void FromAttribute(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source) =>
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        });

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

    private ConnectedSystemObject SeedAd(Context ctx, string employeeId, string? region) =>
        SeedCso(ctx.Ad!, ctx.AdType!, ("employeeId", employeeId), ("region", region));

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

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem, ISyncRepository? repository = null)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), repository ?? SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    private async Task<Activity> RunDeltaSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
        return activity;
    }
}
