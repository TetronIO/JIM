// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
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

    private sealed record Arrangement(ConnectedSystem Hr, ConnectedSystemObject JaneHrObject, ConnectedSystem Ad, SyncRule BaseRule, SyncRule AddOnRule);

    /// <summary>
    /// HR projects Jane. Both AD rules cover everyone. "AD Base Accounts" provisions and writes DisplayName; "AD Manager
    /// Attributes" writes EmployeeId (and DisplayName too, from EmployeeId, when <paramref name="addOnAlsoWritesDisplayName"/>).
    /// </summary>
    private async Task<Arrangement> ArrangeAsync(bool addOnAlsoWritesDisplayName = false, bool addOnProvisions = false)
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
