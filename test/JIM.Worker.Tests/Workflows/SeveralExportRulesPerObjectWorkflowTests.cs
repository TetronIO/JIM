// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Several export Synchronisation Rules covering one account, each writing its own attributes (#2826). "AD Base Accounts"
/// provisions Jane's AD <c>user</c> and writes DisplayName; "AD Manager Attributes" does not provision and writes EmployeeId.
/// Their changes reach the account as one Pending Export; an attribute both write is staged by neither and reported.
/// </summary>
[TestFixture]
public class SeveralExportRulesPerObjectWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_NewStarterBothRulesCover_ProvisionsOneCreateCarryingBothRulesAttributesAsync()
    {
        var arrangement = await ArrangeAsync();

        var activity = await RunHrFullSyncAsync(arrangement, "Joiner");

        var exports = AdPendingExports(arrangement);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exports.Select(pe => pe.ChangeType), Is.EqualTo(new[] { PendingExportChangeType.Create }), "one Create, never a second export");
            Assert.That(ChangedAttributeNames(exports.Single()), Is.SupersetOf(new[] { "DisplayName", "EmployeeId" }),
                "the add-on rule's attribute joins the base rule's Create, so the account is complete from its first export");
            Assert.That(exports.Single().AttributeValueChanges.Single(c => c.Attribute?.Name == "EmployeeId").SyncRuleId,
                Is.EqualTo(arrangement.AddOnRule.Id), "each change records the rule that wrote it");
            Assert.That(AdAccount(arrangement).JoinSyncRuleId, Is.EqualTo(arrangement.BaseRule.Id), "the provisioning rule manages the account");
            Assert.That(OverlapItemsIn(activity), Is.Empty);
        }
    }

    [Test]
    public async Task FullSync_AChangeToBothRulesSources_StagesOneUpdateCarryingBothAsync()
    {
        var arrangement = await ArrangeAsync();
        await ProvisionAndConfirmAsync(arrangement);

        SetHrValue(arrangement, "DisplayName", "Jane Doe");
        SetHrValue(arrangement, "EmployeeId", "EMP002");
        await ModifyCsoAsync(arrangement.JaneHrObject);
        var activity = await RunHrFullSyncAsync(arrangement, "Rename");

        var exports = AdPendingExports(arrangement);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exports.Select(pe => pe.ChangeType), Is.EqualTo(new[] { PendingExportChangeType.Update }));
            Assert.That(ChangedAttributeNames(exports.Single()), Is.EquivalentTo(new[] { "DisplayName", "EmployeeId" }));
            Assert.That(OverlapItemsIn(activity), Is.Empty);
        }
    }

    [Test]
    public async Task FullSync_AChangeToOneRulesSource_StagesOnlyThatRulesAttributeAsync()
    {
        var arrangement = await ArrangeAsync();
        await ProvisionAndConfirmAsync(arrangement);

        SetHrValue(arrangement, "DisplayName", "Jane Doe");
        await ModifyCsoAsync(arrangement.JaneHrObject);
        await RunHrFullSyncAsync(arrangement, "Rename");

        Assert.That(ChangedAttributeNames(AdPendingExports(arrangement).Single()), Is.EquivalentTo(new[] { "DisplayName" }));
    }

    [Test]
    public async Task FullSync_BothRulesWriteDisplayName_StagesEverythingElseAndReportsTheClashAsync()
    {
        var arrangement = await ArrangeAsync(addOnAlsoWritesDisplayName: true);
        await ProvisionAndConfirmAsync(arrangement);

        SetHrValue(arrangement, "DisplayName", "Jane Doe");
        SetHrValue(arrangement, "EmployeeId", "EMP002");
        await ModifyCsoAsync(arrangement.JaneHrObject);
        var activity = await RunHrFullSyncAsync(arrangement, "Rename");

        var overlap = OverlapItemsIn(activity).SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ChangedAttributeNames(AdPendingExports(arrangement).Single()), Is.EquivalentTo(new[] { "EmployeeId" }),
                "the contested attribute is staged by neither rule; the rest still goes");
            Assert.That(overlap, Is.Not.Null, "the clash is reported");
            Assert.That(overlap?.ErrorMessage, Does.Contain("'DisplayName'").And.Contain("AD Base Accounts").And.Contain("AD Manager Attributes"));
        }
    }

    [Test]
    public async Task FullSync_TwoProvisioningRulesWritingDifferentAttributes_ProvisionOnceAsync()
    {
        var arrangement = await ArrangeAsync(addOnProvisions: true);

        await RunHrFullSyncAsync(arrangement, "Joiner");

        var exports = AdPendingExports(arrangement);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exports.Select(pe => pe.ChangeType), Is.EqualTo(new[] { PendingExportChangeType.Create }));
            Assert.That(ChangedAttributeNames(exports.Single()), Is.SupersetOf(new[] { "DisplayName", "EmployeeId" }));
            Assert.That(AdAccounts(arrangement), Has.Count.EqualTo(1));
            Assert.That(AdAccount(arrangement).JoinSyncRuleId, Is.EqualTo(arrangement.BaseRule.Id), "the lower-numbered provisioning rule leads");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_OfANewStarterBothRulesCover_ProposesOneCreateCarryingBothRulesAttributesAsync()
    {
        var arrangement = await ArrangeAsync();

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(arrangement.Hr.Id, arrangement.JaneHrObject.Id);

        var proposed = preview.Outbound.ProposedExports.Where(pe => pe.ConnectedSystemId == arrangement.Ad.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposed.Select(pe => pe.ChangeType), Is.EqualTo(new[] { PendingExportChangeType.Create }),
                "the preview proposes the one Create the run stages");
            Assert.That(ChangedAttributeNames(proposed.Single()), Is.SupersetOf(new[] { "DisplayName", "EmployeeId" }));
            Assert.That(preview.Errors.Where(e => e.Code == SyncPreviewMessageCode.ExportRuleOverlap), Is.Empty);
        }
    }

    [Test]
    public async Task FullSync_NewStarterBothRulesCover_RecordsTheAddOnRulesCoverageButNotTheManagingRulesAsync()
    {
        var arrangement = await ArrangeAsync();

        await RunHrFullSyncAsync(arrangement, "Joiner");

        Assert.That(CoverageOf(AdAccount(arrangement)), Is.EquivalentTo(new[] { arrangement.AddOnRule.Id }),
            "the account's join names the managing rule; the coverage record lists the rules it does not");
    }

    [Test]
    public async Task FullSync_AnAddOnRuleStartsCoveringAnAccount_SendsEveryAttributeItWritesAsync()
    {
        // The add-on rule is switched on once Jane's account exists. Its EmployeeId has not changed, yet it has never been
        // written to the account, so the rule's first evaluation sends it along with the DisplayName change that triggered it.
        var arrangement = await ArrangeAsync(addOnEnabled: false);
        await ProvisionAndConfirmAsync(arrangement);
        arrangement.AddOnRule.Enabled = true; // the run reads the rule the in-memory repository holds

        SetHrValue(arrangement, "DisplayName", "Jane Doe");
        await ModifyCsoAsync(arrangement.JaneHrObject);
        await RunHrFullSyncAsync(arrangement, "Rename");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ChangedAttributeNames(AdPendingExports(arrangement).Single()), Is.EquivalentTo(new[] { "DisplayName", "EmployeeId" }));
            Assert.That(CoverageOf(AdAccount(arrangement)), Is.EquivalentTo(new[] { arrangement.AddOnRule.Id }));
        }
    }

    [Test]
    public async Task FullSync_AnAddOnRuleStartsCoveringAnAccountThatHoldsItsValue_SendsNothingForItAsync()
    {
        var arrangement = await ArrangeAsync(addOnEnabled: false);
        await ProvisionAndConfirmAsync(arrangement);
        HoldAdValue(arrangement, "EmployeeId", "EMP001");
        arrangement.AddOnRule.Enabled = true;

        SetHrValue(arrangement, "DisplayName", "Jane Doe");
        await ModifyCsoAsync(arrangement.JaneHrObject);
        await RunHrFullSyncAsync(arrangement, "Rename");

        Assert.That(ChangedAttributeNames(AdPendingExports(arrangement).Single()), Is.EquivalentTo(new[] { "DisplayName" }),
            "the full send still drops a value the account already holds");
    }

    [Test]
    public async Task FullSync_ACoverageAlreadyRecorded_SendsOnlyWhatChangedAsync()
    {
        var arrangement = await ArrangeAsync(addOnEnabled: false);
        await ProvisionAndConfirmAsync(arrangement);
        arrangement.AddOnRule.Enabled = true;
        SetHrValue(arrangement, "DisplayName", "Jane Doe");
        await ModifyCsoAsync(arrangement.JaneHrObject);
        await RunHrFullSyncAsync(arrangement, "Rename");
        await SyncRepo.DeletePendingExportsAsync(AdPendingExports(arrangement));

        SetHrValue(arrangement, "DisplayName", "Jane Q Doe");
        await ModifyCsoAsync(arrangement.JaneHrObject);
        await RunHrFullSyncAsync(arrangement, "Second rename");

        Assert.That(ChangedAttributeNames(AdPendingExports(arrangement).Single()), Is.EquivalentTo(new[] { "DisplayName" }),
            "a rule sends everything once, when it starts covering the account");
    }

    [Test]
    public async Task FullSync_ARuleFromBeforeTheCoverageRecord_RecordsItsCoverageWithoutSendingAsync()
    {
        // A rule that existed before the upgrade cannot tell an account it has always covered from one it has just started
        // covering, so it records the coverage and sends only what changed, as it always has.
        var arrangement = await ArrangeAsync(addOnEnabled: false, addOnCoverageRecorded: false);
        await ProvisionAndConfirmAsync(arrangement);
        arrangement.AddOnRule.Enabled = true;

        SetHrValue(arrangement, "DisplayName", "Jane Doe");
        await ModifyCsoAsync(arrangement.JaneHrObject);
        await RunHrFullSyncAsync(arrangement, "Rename");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ChangedAttributeNames(AdPendingExports(arrangement).Single()), Is.EquivalentTo(new[] { "DisplayName" }));
            Assert.That(CoverageOf(AdAccount(arrangement)), Is.EquivalentTo(new[] { arrangement.AddOnRule.Id }));
        }
    }

    [Test]
    public async Task FullSync_AnAddOnRuleStopsCoveringAnAccount_RemovesItsCoverageAsync()
    {
        var arrangement = await ArrangeAsync(addOnScope: "EMP001");
        await ProvisionAndConfirmAsync(arrangement);
        Assert.That(CoverageOf(AdAccount(arrangement)), Is.EquivalentTo(new[] { arrangement.AddOnRule.Id }), "precondition: the coverage was recorded");

        SetHrValue(arrangement, "EmployeeId", "EMP009");
        await ModifyCsoAsync(arrangement.JaneHrObject);
        await RunHrFullSyncAsync(arrangement, "Out of the add-on's scope");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CoverageOf(AdAccount(arrangement)), Is.Empty);
            Assert.That(AdAccount(arrangement).MetaverseObjectId, Is.EqualTo(JaneMvoId()), "the managing rule still covers Jane");
        }
    }

    [Test]
    public async Task FullSync_ADisabledAddOnRule_KeepsItsCoverageAsync()
    {
        // Switching a rule off is a pause: what it covered is still recorded when it is switched back on.
        var arrangement = await ArrangeAsync();
        await ProvisionAndConfirmAsync(arrangement);
        arrangement.AddOnRule.Enabled = false;

        SetHrValue(arrangement, "DisplayName", "Jane Doe");
        await ModifyCsoAsync(arrangement.JaneHrObject);
        await RunHrFullSyncAsync(arrangement, "Rename");

        Assert.That(CoverageOf(AdAccount(arrangement)), Is.EquivalentTo(new[] { arrangement.AddOnRule.Id }));
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ANewExportRule_RecordsWhenItsCoverageRecordBeganAsync()
    {
        var arrangement = await ArrangeAsync();
        var rule = new SyncRule
        {
            Name = "AD Location Attributes",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemId = arrangement.Ad.Id,
            ConnectedSystem = arrangement.Ad,
            ConnectedSystemObjectTypeId = arrangement.BaseRule.ConnectedSystemObjectTypeId,
            ConnectedSystemObjectType = arrangement.BaseRule.ConnectedSystemObjectType,
            MetaverseObjectTypeId = arrangement.BaseRule.MetaverseObjectTypeId,
            MetaverseObjectType = arrangement.BaseRule.MetaverseObjectType
        };

        Assert.That(await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await AdministratorAsync()), Is.True);

        Assert.That(rule.CoverageRecordedAt, Is.Not.Null, "a rule created now sends everything when it starts covering an account");
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_AnExportRuleFromBeforeTheCoverageRecord_IsLeftUnrecordedAsync()
    {
        var arrangement = await ArrangeAsync(addOnCoverageRecorded: false);
        var rule = await Jim.ConnectedSystems.GetSyncRuleAsync(arrangement.AddOnRule.Id)
            ?? throw new InvalidOperationException("arrange: the add-on rule loads");

        Assert.That(await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await AdministratorAsync()), Is.True);

        Assert.That(rule.CoverageRecordedAt, Is.Null, "editing a rule does not tell JIM which accounts it already covers");
    }

    [Test]
    public async Task AdvanceExportRuleCoverageBaselineAsync_ARuleFromBeforeTheRecord_RecordsWhatItCoversThenSendsAsANewRuleAsync()
    {
        var arrangement = await ArrangeAsync(addOnCoverageRecorded: false);
        await ProvisionAndConfirmAsync(arrangement);
        await ForgetCoverageAsync(); // as the upgrade finds it: the record starts empty

        await RunCoverageBaselineAsync(pageSize: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CoverageOf(AdAccount(arrangement)), Is.EquivalentTo(new[] { arrangement.AddOnRule.Id }),
                "the add-on rule covers the account; the managing rule is never recorded");
            Assert.That(arrangement.AddOnRule.CoverageRecordedAt, Is.Not.Null, "from now on the rule behaves as a new one");
        }
    }

    [Test]
    public async Task AdvanceExportRuleCoverageBaselineAsync_AnAccountOutsideTheRulesScope_RecordsNothingForItAsync()
    {
        var arrangement = await ArrangeAsync(addOnCoverageRecorded: false, addOnScope: "EMP999");
        await ProvisionAndConfirmAsync(arrangement);

        await RunCoverageBaselineAsync(pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CoverageOf(AdAccount(arrangement)), Is.Empty);
            Assert.That(arrangement.AddOnRule.CoverageRecordedAt, Is.Not.Null);
        }
    }

    [Test]
    public async Task AdvanceExportRuleCoverageBaselineAsync_EveryRuleRecorded_HasNoWorkAsync()
    {
        await ArrangeAsync();

        var workRemains = await Jim.ExportEvaluation.AdvanceExportRuleCoverageBaselineAsync(new ExportRuleCoverageBaselineCursor(), pageSize: 10);

        Assert.That(workRemains, Is.False);
    }

    [Test]
    public async Task AdvanceExportRuleCoverageBaselineAsync_ThenTheRuleStartsCoveringAnAccount_SendsEveryAttributeItWritesAsync()
    {
        // The baseline over, the rule is told apart from one it has always covered: Jane joins its scope afterwards, by a
        // rename, and its EmployeeId goes with the base rule's DisplayName though it did not change.
        var arrangement = await ArrangeAsync(addOnCoverageRecorded: false, addOnDisplayNameScope: "Jane Doe");
        await ProvisionAndConfirmAsync(arrangement);
        await RunCoverageBaselineAsync(pageSize: 10);

        SetHrValue(arrangement, "DisplayName", "Jane Doe");
        await ModifyCsoAsync(arrangement.JaneHrObject);
        await RunHrFullSyncAsync(arrangement, "Into the add-on's scope");

        Assert.That(ChangedAttributeNames(AdPendingExports(arrangement).Single()), Is.EquivalentTo(new[] { "DisplayName", "EmployeeId" }));
    }

    private async Task RunCoverageBaselineAsync(int pageSize)
    {
        var cursor = new ExportRuleCoverageBaselineCursor();
        for (var advances = 0; await Jim.ExportEvaluation.AdvanceExportRuleCoverageBaselineAsync(cursor, pageSize); advances++)
            Assert.That(advances, Is.LessThan(100), "the baseline finishes");
    }

    private Task ForgetCoverageAsync() =>
        SyncRepo.DeleteExportRuleCoveragesAsync(SyncRepo.ExportRuleCoverages.Select(c => (c.ConnectedSystemObjectId, c.SyncRuleId)).ToList());

    private async Task<MetaverseObject> AdministratorAsync()
    {
        var administrator = new MetaverseObject
        {
            Id = Guid.NewGuid(), Type = DbContext.MetaverseObjectTypes.First(), Created = DateTime.UtcNow, CachedDisplayName = "Test Administrator"
        };
        DbContext.MetaverseObjects.Add(administrator);
        await DbContext.SaveChangesAsync();
        return administrator;
    }

    private sealed record Arrangement(ConnectedSystem Hr, ConnectedSystemObject JaneHrObject, ConnectedSystem Ad, SyncRule BaseRule, SyncRule AddOnRule);

    /// <summary>
    /// HR projects Jane. Both AD rules cover everyone. "AD Base Accounts" provisions and writes DisplayName; "AD Manager
    /// Attributes" writes EmployeeId (and DisplayName too, from EmployeeId, when <paramref name="addOnAlsoWritesDisplayName"/>).
    /// </summary>
    private async Task<Arrangement> ArrangeAsync(bool addOnAlsoWritesDisplayName = false, bool addOnProvisions = false,
        bool addOnEnabled = true, bool addOnCoverageRecorded = true, string? addOnScope = null, string? addOnDisplayNameScope = null)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User",
        [
            new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true }
        ]);
        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        AddImportFlow(hrImport, hrType, "DisplayName", mvDisplayName);
        AddImportFlow(hrImport, hrType, "EmployeeId", mvEmployeeId);
        var jane = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "EMP001");

        var ad = await CreateConnectedSystemAsync("AD");
        var adType = await CreateCsoTypeAsync(ad.Id, "user");
        var baseRule = await CreateExportSyncRuleAsync(ad.Id, adType, mvType, "AD Base Accounts");
        AddExportFlow(baseRule, adType, "DisplayName", mvDisplayName);
        var addOnRule = await CreateExportSyncRuleAsync(ad.Id, adType, mvType, "AD Manager Attributes", enableProvisioning: addOnProvisions);
        AddExportFlow(addOnRule, adType, "EmployeeId", mvEmployeeId);
        if (addOnAlsoWritesDisplayName)
            AddExportFlow(addOnRule, adType, "DisplayName", mvEmployeeId);
        if (addOnScope != null)
            ScopeTo(addOnRule, mvEmployeeId, addOnScope);
        if (addOnDisplayNameScope != null)
            ScopeTo(addOnRule, mvDisplayName, addOnDisplayNameScope);
        // A rule created since the coverage record began sends everything when it starts covering an account (#2826).
        baseRule.CoverageRecordedAt = DateTime.UtcNow;
        addOnRule.CoverageRecordedAt = addOnCoverageRecorded ? DateTime.UtcNow : null;
        addOnRule.Enabled = addOnEnabled;
        await DbContext.SaveChangesAsync();

        return new Arrangement(hr, jane, ad, baseRule, addOnRule);
    }

    private async Task ProvisionAndConfirmAsync(Arrangement arrangement)
    {
        await RunHrFullSyncAsync(arrangement, "Joiner");
        var account = AdAccount(arrangement);
        account.Status = ConnectedSystemObjectStatus.Normal;
        await SyncRepo.DeletePendingExportsAsync(SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == account.Id).ToList());
    }

    private HashSet<int> CoverageOf(ConnectedSystemObject account) =>
        SyncRepo.ExportRuleCoverages.Where(c => c.ConnectedSystemObjectId == account.Id).Select(c => c.SyncRuleId).ToHashSet();

    private void HoldAdValue(Arrangement arrangement, string attributeName, string value)
    {
        var account = AdAccount(arrangement);
        var attribute = arrangement.BaseRule.ConnectedSystemObjectType!.Attributes.Single(a => a.Name == attributeName);
        account.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = account,
            Attribute = attribute,
            AttributeId = attribute.Id,
            StringValue = value
        });
    }

    private static void ScopeTo(SyncRule rule, MetaverseAttribute attribute, string value) =>
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    MetaverseAttribute = attribute,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = value,
                    CaseSensitive = true
                }
            ]
        });

    private static void SetHrValue(Arrangement arrangement, string attributeName, string value) =>
        arrangement.JaneHrObject.AttributeValues.Single(v => v.Attribute?.Name == attributeName).StringValue = value;

    private Guid JaneMvoId() =>
        SyncRepo.MetaverseObjects.Values.Single(m => m.Type?.Name == "Person").Id;

    private List<ConnectedSystemObject> AdAccounts(Arrangement arrangement) =>
        SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == arrangement.Ad.Id && c.MetaverseObjectId == JaneMvoId()).ToList();

    private ConnectedSystemObject AdAccount(Arrangement arrangement) => AdAccounts(arrangement).Single();

    private List<PendingExport> AdPendingExports(Arrangement arrangement) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == arrangement.Ad.Id).ToList();

    private static List<string?> ChangedAttributeNames(PendingExport pendingExport) =>
        pendingExport.AttributeValueChanges.Select(c => c.Attribute?.Name).Where(name => name is "DisplayName" or "EmployeeId").Distinct().ToList();

    private static List<ActivityRunProfileExecutionItem> OverlapItemsIn(Activity activity) =>
        activity.RunProfileExecutionItems.Where(item => item.ErrorType == ActivityRunProfileExecutionItemErrorType.ExportRuleOverlap).ToList();

    private static void AddImportFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute target)
    {
        var source = csoType.Attributes.Single(a => a.Name == csAttributeName);
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        });
    }

    private static void AddExportFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute source)
    {
        var target = csoType.Attributes.Single(a => a.Name == csAttributeName);
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });
    }

    private async Task<Activity> RunHrFullSyncAsync(Arrangement arrangement, string profileName)
    {
        var profile = await CreateRunProfileAsync(arrangement.Hr.Id, profileName, ConnectedSystemRunType.FullSynchronisation);
        var hr = await ReloadEntityAsync(arrangement.Hr);
        var activity = await CreateActivityAsync(hr.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, hr, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
