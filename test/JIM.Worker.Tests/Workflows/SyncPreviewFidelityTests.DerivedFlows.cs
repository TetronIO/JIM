// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.TestSupport;
using JIM.Worker.Processors;
using JIM.Worker.Tests.UniqueValues;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Metaverse-Derived Attribute Flows in Sync Preview (#1750, plan Phase 5, FR 11): the preview runs the same level loop
/// the worker runs (derived levels interleaved with dry-run generation, each level once), so what it says a
/// synchronisation would write is what the synchronisation then writes. Each test previews first and then runs the real
/// synchronisation over the same data, comparing the outcome tree's shape and the values, for PRD Scenario 1's shapes.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    private const string EmailFromAccountName = "mv[\"Account Name\"] + \"@corp.local\"";
    private const string UpnFromEmail = "mv[\"Email\"]";

    [Test]
    public async Task PreviewSyncForCsoAsync_EmailAndUpnDerivedAcrossTwoLevels_PreviewAgreesWithTheRealRunAsync()
    {
        var ctx = await SetUpDerivedAsync(withDirectoryExport: true);
        var cso = SeedDerivedHr(ctx, "E1", "jbloggs");

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);
        var activity = await RunDerivedFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var realTree = MapRealOutcomeTree(activity);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Errors, Is.Empty);
            Assert.That(PreviewedValue(preview, "Email"), Is.EqualTo("jbloggs@corp.local"), "level 1 read the Account Name flowed in the same pass");
            Assert.That(PreviewedValue(preview, "User Principal Name"), Is.EqualTo("jbloggs@corp.local"), "level 2 read the Email derived at level 1");
            Assert.That(DerivedText(mvo, ctx.Email), Is.EqualTo(PreviewedValue(preview, "Email")), "the real run wrote what the preview said");
            Assert.That(DerivedText(mvo, ctx.Upn), Is.EqualTo(PreviewedValue(preview, "User Principal Name")));
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(realTree)),
                "derived results count in the Attribute Flow node exactly as the real run records them");
            Assert.That(preview.Outbound.ObjectsToCreate, Is.EqualTo(1), "the export provisions the object with the derived values");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_JoinedObjectWhoseInputChanges_PreviewAgreesWithTheRealRunAsync()
    {
        var ctx = await SetUpDerivedAsync();
        var cso = SeedDerivedHr(ctx, "E1", "jbloggs");
        await RunDerivedFullSyncAsync(ctx.Hr);
        SetDerivedText(cso, ctx.HrAccountName, "jsmith");

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);
        var activity = await RunDerivedFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PreviewedValue(preview, "Email"), Is.EqualTo("jsmith@corp.local"));
            Assert.That(PreviewedValue(preview, "User Principal Name"), Is.EqualTo("jsmith@corp.local"));
            Assert.That(DerivedText(mvo, ctx.Email), Is.EqualTo("jsmith@corp.local"));
            Assert.That(DerivedText(mvo, ctx.Upn), Is.EqualTo("jsmith@corp.local"));
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_GeneratedAttributeAtLevelOne_PreviewAgreesWithTheRealRunAsync()
    {
        // FR 10: Account Name is generated from a base reading mv["EmployeeId"], so it sits at level 1; Email reads it
        // at level 2 and User Principal Name at level 3. The preview resolves the level-1 generation (dry run) before
        // level 2 reads it, exactly as the worker does.
        var ctx = await SetUpDerivedAsync(generatedAccountNameBase: "Lower(mv[\"EmployeeId\"])");
        var cso = SeedDerivedHr(ctx, "E1", accountName: null);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);
        var activity = await RunDerivedFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Errors, Is.Empty);
            Assert.That(PreviewedValue(preview, "Account Name"), Is.EqualTo("e1"), "the generated value, resolved at level 1");
            Assert.That(PreviewedValue(preview, "Email"), Is.EqualTo("e1@corp.local"), "level 2 read the resolved generated value");
            Assert.That(PreviewedValue(preview, "User Principal Name"), Is.EqualTo("e1@corp.local"));
            Assert.That(DerivedText(mvo, ctx.AccountName), Is.EqualTo("e1"));
            Assert.That(DerivedText(mvo, ctx.Email), Is.EqualTo("e1@corp.local"));
            Assert.That(DerivedText(mvo, ctx.Upn), Is.EqualTo("e1@corp.local"));
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))),
                "the generated value's node and the derived values' count match the real run");
            Assert.That(DescribeTree(preview.OutcomeTree), Does.Contain(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned)));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_GeneratedAttributeOnProjection_TreeMatchesTheRealRunAsync()
    {
        // Unique Value Generation (#242) with no derived flow in play: the real run records the generated value's node
        // on the root BEFORE the Attribute Flow child, so the preview must too (found while pairing FR 10 above).
        var ctx = await SetUpDerivedAsync(generatedAccountNameBase: "Lower(cs[\"employeeId\"])", emailExpression: "\"fixed@corp.local\"", withUpn: false);
        var cso = SeedDerivedHr(ctx, "E1", accountName: null);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);
        var activity = await RunDerivedFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PreviewedValue(preview, "Account Name"), Is.EqualTo("e1"));
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_DerivedFlowWinsAttributePriority_PreviewAgreesWithTheRealRunAsync()
    {
        var ctx = await SetUpDerivedAsync(withAd: true, derivedEmailPriority: 1, adEmailPriority: 2);
        var cso = SeedDerivedHr(ctx, "E1", "jbloggs");
        SeedDerivedAd(ctx, "E1", mail: "joe.bloggs@ad.corp.local");
        await RunDerivedFullSyncAsync(ctx.Hr);
        await RunDerivedFullSyncAsync(ctx.Ad!);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(DerivedText(mvo, ctx.Email), Is.EqualTo("jbloggs@corp.local"), "precondition: the derived Email holds the attribute");
        SetDerivedText(cso, ctx.HrAccountName, "jsmith");

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);
        var activity = await RunDerivedFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PreviewedValue(preview, "Email"), Is.EqualTo("jsmith@corp.local"), "the derived flow wins, so the preview shows its new value");
            Assert.That(DerivedText(mvo, ctx.Email), Is.EqualTo("jsmith@corp.local"));
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_DerivedFlowLosesAttributePriority_PreviewAgreesWithTheRealRunAsync()
    {
        var ctx = await SetUpDerivedAsync(withAd: true, derivedEmailPriority: 2, adEmailPriority: 1);
        var cso = SeedDerivedHr(ctx, "E1", "jbloggs");
        SeedDerivedAd(ctx, "E1", mail: "joe.bloggs@ad.corp.local");
        await RunDerivedFullSyncAsync(ctx.Hr);
        await RunDerivedFullSyncAsync(ctx.Ad!);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(DerivedText(mvo, ctx.Email), Is.EqualTo("joe.bloggs@ad.corp.local"), "precondition: AD's higher-priority Email holds the attribute");
        SetDerivedText(cso, ctx.HrAccountName, "jsmith");

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);
        var activity = await RunDerivedFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Inbound!.AttributeFlowChanges.Where(c => c.AttributeName == "Email"), Is.Empty,
                "the derived flow is evaluated and loses, so the preview shows no Email change");
            Assert.That(PreviewedValue(preview, "User Principal Name"), Is.EqualTo("joe.bloggs@ad.corp.local"),
                "User Principal Name, hosted on HR, reads AD's winning Email: HR's own synchronisation is the first to re-derive it since AD took Email over");
            Assert.That(DerivedText(mvo, ctx.Upn), Is.EqualTo("joe.bloggs@ad.corp.local"));
            Assert.That(PreviewedValue(preview, "Account Name"), Is.EqualTo("jsmith"));
            Assert.That(DerivedText(mvo, ctx.Email), Is.EqualTo("joe.bloggs@ad.corp.local"));
            Assert.That(DerivedText(mvo, ctx.AccountName), Is.EqualTo("jsmith"));
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_FlagOff_PreviewAndRealRunBothKeepTheLegacyBehaviourAsync()
    {
        // Flag off, no graph: an import expression reading mv["..."] is an ordinary flow reading nothing, in the preview
        // exactly as in the run.
        var ctx = await SetUpDerivedAsync(flagOn: false, withUpn: false);
        var cso = SeedDerivedHr(ctx, "E1", "jbloggs");

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);
        var activity = await RunDerivedFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PreviewedValue(preview, "Email"), Is.EqualTo("@corp.local"), "legacy: mv[\"...\"] in an import expression reads null");
            Assert.That(DerivedText(mvo, ctx.Email), Is.EqualTo("@corp.local"));
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
        }
    }

    // ---- A cycle in the preview's rule set ----

    [Test]
    public async Task PreviewSyncForCsoAsync_EnabledDerivedFlowsFormACycle_ReportsTheCycleAsABlockingErrorAsync()
    {
        // The worker refuses the whole run on a cycle (decision 11); the preview says so, as a blocking error naming
        // the cycle, rather than throwing or evaluating on a guessed order.
        var ctx = await SetUpDerivedAsync(emailExpression: "mv[\"User Principal Name\"]");
        var cso = SeedDerivedHr(ctx, "E1", "jbloggs");

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);

        var error = preview.Errors.SingleOrDefault(e => e.Code == SyncPreviewMessageCode.DerivedFlowCycle);
        Assert.That(error, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.HasBlockingErrors, Is.True);
            Assert.That(error!.Detail, Does.Contain("cycle").And.Contain("Email").And.Contain("User Principal Name").And.Contain("HR Import"));
            Assert.That(preview.Inbound, Is.Null, "no object is evaluated on a guessed order");
            Assert.That(preview.OutcomeTree, Is.Empty);
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_EnabledDerivedFlowsFormACycle_ReportsTheCycleOnceAndEvaluatesNothingAsync()
    {
        var ctx = await SetUpDerivedAsync(emailExpression: "mv[\"User Principal Name\"]");
        SeedDerivedHr(ctx, "E1", "jbloggs");
        SeedDerivedHr(ctx, "E2", "asmith");

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Hr.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Errors.Select(e => e.Code), Is.EqualTo(new[] { SyncPreviewMessageCode.DerivedFlowCycle }));
            Assert.That(preview.Errors.Single().Detail, Does.Contain("HR Import"));
            Assert.That(preview.EvaluatedObjectCount, Is.Zero, "the run would process no object, so neither does its preview");
            Assert.That(preview.Samples, Is.Empty);
        }
    }

    // ---- Dry run never writes a derived-input mark ----

    [Test]
    public async Task PreviewSyncForCsoAsync_InputChangeTheRealRunWouldMark_NeitherMarksNorClearsAnythingAsync()
    {
        // HR's derived Email reads Region, contributed by AD. A real AD synchronisation changing Region marks HR's
        // object; its preview must not, and previewing HR's marked object must not clear the mark. Every preview read
        // goes through ReadOnlySyncRepositoryGuard, which throws on either write, so a preview that completes cleanly
        // has attempted neither; the counts and flags prove nothing slipped past by another route.
        var ctx = await SetUpDerivedAsync(withAd: true, withUpn: false,
            emailExpression: "mv[\"Account Name\"] + \"@\" + Lower(mv[\"Region\"]) + \".corp.local\"");
        var hrCso = SeedDerivedHr(ctx, "E1", "jbloggs");
        var adCso = SeedDerivedAd(ctx, "E1", region: "EMEA");
        await RunDerivedFullSyncAsync(ctx.Hr);
        await RunDerivedFullSyncAsync(ctx.Ad!);
        Assert.That(hrCso.DerivedInputChangePending, Is.True, "precondition: the real AD run marked HR's object");

        await RunDerivedDeltaSyncAsync(ctx.Hr);
        Assert.That(hrCso.DerivedInputChangePending, Is.False, "precondition: HR's delta cleared it");
        var markCallsBefore = SyncRepo.DerivedInputMarkCalls.Count;

        SetDerivedText(adCso, ctx.AdRegion!, "APAC");
        var (countingRepository, counts) = CountingSyncRepositoryProxy.Create(SyncRepo);
        var adPreview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Ad!.Id, adCso.Id,
            repositoryFactory: () => new SyncRepositoryScope(countingRepository));

        hrCso.DerivedInputChangePending = true;
        var hrPreview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, hrCso.Id,
            repositoryFactory: () => new SyncRepositoryScope(countingRepository));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(adPreview.Errors, Is.Empty);
            Assert.That(PreviewedValue(adPreview, "Region"), Is.EqualTo("APAC"), "precondition: the preview saw the Region change a real run would mark for");
            Assert.That(hrPreview.Errors, Is.Empty);
            Assert.That(SyncRepo.DerivedInputMarkCalls, Has.Count.EqualTo(markCallsBefore), "the preview marked nothing");
            Assert.That(counts.GetValueOrDefault(nameof(ISyncRepository.MarkConnectedSystemObjectsDerivedInputChangePendingAsync)), Is.Zero);
            Assert.That(counts.GetValueOrDefault(nameof(ISyncRepository.ClearConnectedSystemObjectDerivedInputChangePendingAsync)), Is.Zero);
            Assert.That(hrCso.DerivedInputChangePending, Is.True, "the preview cleared nothing");
            Assert.That(adCso.DerivedInputChangePending, Is.False);
        }

        // The backstop the preview runs behind: either write through the guard throws.
        var guard = new ReadOnlySyncRepositoryGuard(SyncRepo);
        Assert.That(() => guard.MarkConnectedSystemObjectsDerivedInputChangePendingAsync([new DerivedInputChangeMark(Guid.NewGuid(), ctx.Hr.Id)]),
            Throws.InstanceOf<PreviewWriteAttemptedException>());
        Assert.That(() => guard.ClearConnectedSystemObjectDerivedInputChangePendingAsync([new DerivedInputChangeClear(hrCso.Id, 0)]),
            Throws.InstanceOf<PreviewWriteAttemptedException>());
    }

    #region derived flow helpers

    private sealed record DerivedContext(
        ConnectedSystem Hr,
        ConnectedSystemObjectType HrType,
        ConnectedSystemObjectTypeAttribute HrAccountName,
        MetaverseAttribute AccountName,
        MetaverseAttribute Region,
        MetaverseAttribute Email,
        MetaverseAttribute Upn,
        ConnectedSystem? Ad,
        ConnectedSystemObjectType? AdType,
        ConnectedSystemObjectTypeAttribute? AdRegion);

    /// <summary>
    /// PRD Scenario 1's topology: HR projects a Person and flows Employee Id and Account Name; its rule derives Email
    /// from Account Name and User Principal Name from Email. Optionally AD joins on Employee Id and flows Region and a
    /// competing Email; optionally a Directory export provisions mail and userPrincipalName.
    /// </summary>
    private async Task<DerivedContext> SetUpDerivedAsync(
        string emailExpression = EmailFromAccountName,
        bool withUpn = true,
        bool withAd = false,
        bool withDirectoryExport = false,
        bool flagOn = true,
        string? generatedAccountNameBase = null,
        int derivedEmailPriority = int.MaxValue,
        int adEmailPriority = int.MaxValue)
    {
        if (flagOn)
        {
            DbContext.ServiceSettingItems.AddRange(await InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled().GetAllSettingsAsync());
            await DbContext.SaveChangesAsync();
        }

        var mvType = await CreateMvObjectTypeAsync("Person");
        var employeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var accountName = await AddDerivedMvAttributeAsync(mvType, "Account Name");
        var region = await AddDerivedMvAttributeAsync(mvType, "Region");
        var email = await AddDerivedMvAttributeAsync(mvType, "Email");
        var upn = await AddDerivedMvAttributeAsync(mvType, "User Principal Name");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "HrPerson", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "employeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "accountName", Type = AttributeDataType.Text, Selected = true }
        });
        var hrAccountName = hrType.Attributes.Single(a => a.Name == "accountName");

        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        FlowFromAttribute(hrImport, employeeId, hrType.Attributes.Single(a => a.Name == "employeeId"));
        if (generatedAccountNameBase == null)
        {
            FlowFromAttribute(hrImport, accountName, hrAccountName);
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

        FlowFromExpression(hrImport, email, emailExpression).Priority = derivedEmailPriority;
        if (withUpn)
            FlowFromExpression(hrImport, upn, UpnFromEmail);
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
                new() { Name = "region", Type = AttributeDataType.Text, Selected = true },
                new() { Name = "mail", Type = AttributeDataType.Text, Selected = true }
            });
            var adEmployeeId = adType.Attributes.Single(a => a.Name == "employeeId");
            adRegion = adType.Attributes.Single(a => a.Name == "region");

            var adImport = await CreateImportSyncRuleAsync(ad.Id, adType, mvType, "AD Import", enableProjection: false);
            FlowFromAttribute(adImport, region, adRegion);
            FlowFromAttribute(adImport, email, adType.Attributes.Single(a => a.Name == "mail")).Priority = adEmailPriority;
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

        if (withDirectoryExport)
        {
            var directory = await CreateConnectedSystemAsync("Directory");
            var directoryType = await CreateCsoTypeAsync(directory.Id, "DirectoryUser", new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                new() { Name = "mail", Type = AttributeDataType.Text, Selected = true },
                new() { Name = "userPrincipalName", Type = AttributeDataType.Text, Selected = true }
            });
            var export = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export", enableProvisioning: true);
            FlowToAttribute(export, directoryType.Attributes.Single(a => a.Name == "mail"), email);
            FlowToAttribute(export, directoryType.Attributes.Single(a => a.Name == "userPrincipalName"), upn);
            await DbContext.SaveChangesAsync();
        }

        return new DerivedContext(hr, hrType, hrAccountName, accountName, region, email, upn, ad, adType, adRegion);
    }

    private async Task<MetaverseAttribute> AddDerivedMvAttributeAsync(MetaverseObjectType mvType, string name)
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

    private static SyncRuleMapping FlowFromAttribute(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source)
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

    private static SyncRuleMapping FlowFromExpression(SyncRule rule, MetaverseAttribute target, string expression)
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

    private static void FlowToAttribute(SyncRule rule, ConnectedSystemObjectTypeAttribute target, MetaverseAttribute source) =>
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });

    private ConnectedSystemObject SeedDerivedHr(DerivedContext ctx, string employeeId, string? accountName) =>
        SeedDerivedCso(ctx.Hr, ctx.HrType, ("employeeId", employeeId), ("accountName", accountName));

    private ConnectedSystemObject SeedDerivedAd(DerivedContext ctx, string employeeId, string? region = null, string? mail = null) =>
        SeedDerivedCso(ctx.Ad!, ctx.AdType!, ("employeeId", employeeId), ("region", region), ("mail", mail));

    private ConnectedSystemObject SeedDerivedCso(ConnectedSystem system, ConnectedSystemObjectType type, params (string Name, string? Value)[] values)
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
            SetDerivedText(cso, type.Attributes.Single(a => a.Name == name), value!);

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static void SetDerivedText(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string value)
    {
        cso.AttributeValues.RemoveAll(av => av.AttributeId == attribute.Id);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attribute.Id, Attribute = attribute, StringValue = value });
    }

    private static string? DerivedText(MetaverseObject mvo, MetaverseAttribute attribute) =>
        mvo.AttributeValues
            .Where(av => (av.AttributeId == attribute.Id || av.Attribute?.Id == attribute.Id) && !av.NullValue)
            .Select(av => av.StringValue)
            .SingleOrDefault();

    /// <summary>
    /// The value the preview says the attribute would be given, or null when it would not be written.
    /// </summary>
    private static string? PreviewedValue(SyncPreviewResult preview, string attributeName) =>
        preview.Inbound?.AttributeFlowChanges
            .Where(change => change.IsAddition && change.AttributeName == attributeName)
            .Select(change => change.Value)
            .SingleOrDefault();

    private async Task<Activity> RunDerivedFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    private async Task<Activity> RunDerivedDeltaSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
        return activity;
    }

    #endregion
}
