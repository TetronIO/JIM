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
/// Synchronisation Rules are isolated (#2021): an import rule's scope governs only the joins import made and the values
/// the rule brought in. A join an export rule made (provisioning, or an export rule matching an existing object) is that
/// export rule's to end, so an object leaving every import rule's scope keeps such a join; its Out-of-Scope Action
/// decides only whether the values the import rule contributed are withdrawn (Disconnect, with recall on) or kept
/// (Remain Joined).
/// <para>
/// Without this, an import rule added for a forest's own contacts, scoped to its other OUs, disconnected every GAL
/// contact JIM had provisioned into the GAL OU, and the export rule then provisioned each one again on every run.
/// </para>
/// </summary>
[TestFixture]
public class ImportScopeIsolationWorkflowTests : WorkflowTestBase
{
    private const string GalOu = "GALSync";
    private const string SupplierOu = "Suppliers";

    [Test]
    public async Task FullSync_ProvisionedObjectNeverInTheImportRulesScope_KeepsItsJoinAndIsNotProvisionedAgainAsync()
    {
        var ctx = await ArrangeAsync(InboundOutOfScopeAction.Disconnect, recall: true, startingOu: GalOu);

        var activity = await RunFullSyncAsync(ctx.Directory);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr), "HR Full Sync 2");

        var account = await ReloadEntityAsync(ctx.Account);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.MetaverseObjectId, Is.EqualTo(ctx.MvoId),
                "the import rule never took the provisioned account in, so it must not end the export rule's join");
            Assert.That(account.JoinMethod, Is.EqualTo(ConnectedSystemObjectJoinMethod.Provisioning));
            Assert.That(activity.RunProfileExecutionItems.Any(i => i.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope), Is.False);
            Assert.That(CreatesStagedFor(ctx.Directory), Is.Zero,
                "with the join kept, the export rule has nothing to provision again");
        }
    }

    [Test]
    public async Task FullSync_ProvisionedObjectLeavesTheImportRulesScopeWithDisconnectAndRecall_WithdrawsItsValuesAndKeepsTheJoinAsync()
    {
        var ctx = await ArrangeAsync(InboundOutOfScopeAction.Disconnect, recall: true, startingOu: SupplierOu);
        await RunFullSyncAsync(ctx.Directory, "Directory Full Sync 1");
        Assert.That(MailOf(ctx.MvoId), Is.EqualTo("jane@resurgam.local"), "precondition: the import rule flowed Mail in while in scope");

        await MoveAccountToAsync(ctx, GalOu);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Directory), "Directory Full Sync 2");

        var account = await ReloadEntityAsync(ctx.Account);
        var item = activity.RunProfileExecutionItems.Single(i => i.ObjectChangeType != ObjectChangeType.NotSet);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MailOf(ctx.MvoId), Is.Null, "the import rule's values are withdrawn when the account leaves its scope");
            Assert.That(account.MetaverseObjectId, Is.EqualTo(ctx.MvoId), "the export rule's join is kept");
            Assert.That(account.JoinMethod, Is.EqualTo(ConnectedSystemObjectJoinMethod.Provisioning));
            Assert.That(item.ObjectChangeType, Is.EqualTo(ObjectChangeType.OutOfScopeRetainJoin));
            Assert.That(item.AttributeFlowCount, Is.EqualTo(1), "the withdrawn Mail value is the item's one change");
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet));
        }
    }

    [Test]
    public async Task FullSync_ProvisionedObjectLeavesTheImportRulesScopeWithRemainJoined_KeepsItsValuesAndTheJoinAsync()
    {
        var ctx = await ArrangeAsync(InboundOutOfScopeAction.RemainJoined, recall: true, startingOu: SupplierOu);
        await RunFullSyncAsync(ctx.Directory, "Directory Full Sync 1");

        await MoveAccountToAsync(ctx, GalOu);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Directory), "Directory Full Sync 2");

        var account = await ReloadEntityAsync(ctx.Account);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MailOf(ctx.MvoId), Is.EqualTo("jane@resurgam.local"), "Remain Joined keeps the import rule's values");
            Assert.That(account.MetaverseObjectId, Is.EqualTo(ctx.MvoId));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ProvisionedObjectNeverInTheImportRulesScope_PredictsTheRunAsync()
    {
        var ctx = await ArrangeAsync(InboundOutOfScopeAction.Disconnect, recall: true, startingOu: GalOu);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Directory.Id, ctx.Account.Id);
        var activity = await RunFullSyncAsync(ctx.Directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncPreviewFidelityTests.DescribeTree(preview.OutcomeTree),
                Is.EqualTo(SyncPreviewFidelityTests.DescribeTree(SyncPreviewFidelityTests.MapRealOutcomeTree(activity))),
                "the preview's tree is the tree the run records");
            Assert.That(preview.OutcomeTree, Is.Empty, "the run changes nothing about the provisioned account, so nothing is proposed");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ProvisionedObjectLeavesTheImportRulesScopeWithDisconnectAndRecall_PredictsTheRunAsync()
    {
        var ctx = await ArrangeAsync(InboundOutOfScopeAction.Disconnect, recall: true, startingOu: SupplierOu);
        await RunFullSyncAsync(ctx.Directory, "Directory Full Sync 1");
        await MoveAccountToAsync(ctx, GalOu);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Directory.Id, ctx.Account.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Directory), "Directory Full Sync 2");

        var realTree = SyncPreviewFidelityTests.MapRealOutcomeTree(activity);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncPreviewFidelityTests.DescribeTree(realTree), Does.Contain(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin)),
                "precondition: the run kept the join and withdrew the value");
            Assert.That(SyncPreviewFidelityTests.DescribeTree(preview.OutcomeTree), Is.EqualTo(SyncPreviewFidelityTests.DescribeTree(realTree)),
                "the preview's tree is the tree the run records");
        }
    }

    [Test]
    public async Task FullSync_ProvisionedObjectOutOfScopeBesideAnInScopeObjectFromTheSameSystem_LeavesTheInScopeObjectsValuesAsync()
    {
        // A value records the system that contributed it, not the object, so the provisioned account leaving scope cannot
        // tell its values from those of the supplier record, which is still in scope and still flowing Mail.
        var ctx = await ArrangeAsync(InboundOutOfScopeAction.Disconnect, recall: true, startingOu: GalOu);
        var accountType = (await ReloadEntityAsync(ctx.Account)).Type;
        var supplierRecord = await CreateCsoAsync(ctx.Directory.Id, accountType, "Jane Smith (supplier)", "EMP001");
        SetValue(supplierRecord, accountType, "Mail", "jane@supplier.example");
        SetValue(supplierRecord, accountType, "Ou", SupplierOu);
        supplierRecord.MetaverseObjectId = ctx.MvoId;
        supplierRecord.MetaverseObject = SyncRepo.MetaverseObjects[ctx.MvoId];
        supplierRecord.RecordJoin(ConnectedSystemObjectJoinMethod.InboundMatching, null, DateTime.UtcNow);
        await SyncRepo.UpdateConnectedSystemObjectJoinStatesAsync([supplierRecord]);

        await RunFullSyncAsync(ctx.Directory, "Directory Full Sync 1");
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Directory), "Directory Full Sync 2");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MailOf(ctx.MvoId), Is.EqualTo("jane@supplier.example"), "the in-scope object's value stays");
            Assert.That((await ReloadEntityAsync(ctx.Account)).MetaverseObjectId, Is.EqualTo(ctx.MvoId));
        }
    }

    [Test]
    public async Task FullSync_ExportMatchedObjectNeverInTheImportRulesScope_KeepsItsJoinAsync()
    {
        var ctx = await ArrangeAsync(InboundOutOfScopeAction.Disconnect, recall: true, startingOu: GalOu,
            joinMethod: ConnectedSystemObjectJoinMethod.ExportMatching);

        await RunFullSyncAsync(ctx.Directory);

        var account = await ReloadEntityAsync(ctx.Account);
        Assert.That(account.MetaverseObjectId, Is.EqualTo(ctx.MvoId),
            "an export rule matched this object, so the join is the export rule's, as a provisioned one is");
    }

    [Test]
    public async Task FullSync_ImportMatchedObjectLeavesTheImportRulesScopeWithDisconnect_IsStillDisconnectedAsync()
    {
        var ctx = await ArrangeAsync(InboundOutOfScopeAction.Disconnect, recall: true, startingOu: GalOu,
            joinMethod: ConnectedSystemObjectJoinMethod.InboundMatching);

        var activity = await RunFullSyncAsync(ctx.Directory);

        var account = await ReloadEntityAsync(ctx.Account);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.MetaverseObjectId, Is.Null, "a join import made is still the import rule's to end");
            Assert.That(activity.RunProfileExecutionItems.Any(i => i.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope), Is.True);
        }
    }

    private sealed record Arrangement(ConnectedSystem Hr, ConnectedSystem Directory, ConnectedSystemObject Account, Guid MvoId);

    /// <summary>
    /// HR projects Jane. A directory's export rule has provisioned her account (or matched it, per
    /// <paramref name="joinMethod"/>), and its import rule takes in only accounts in the Suppliers OU, flowing Mail.
    /// </summary>
    private async Task<Arrangement> ArrangeAsync(InboundOutOfScopeAction outOfScopeAction, bool recall, string startingOu,
        ConnectedSystemObjectJoinMethod joinMethod = ConnectedSystemObjectJoinMethod.Provisioning)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        mvType.DeletionRule = MetaverseObjectDeletionRule.Manual;
        var mvMail = new MetaverseAttribute
        {
            Name = "Mail",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType],
            PredefinedSearchAttributes = []
        };
        DbContext.MetaverseAttributes.Add(mvMail);
        if (!mvType.Attributes.Contains(mvMail))
            mvType.Attributes.Add(mvMail);
        await DbContext.SaveChangesAsync();

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User");
        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        AddImportFlow(hrImport, hrType, "DisplayName", mvType.Attributes.First(a => a.Name == "DisplayName"));
        AddImportFlow(hrImport, hrType, "EmployeeId", mvType.Attributes.First(a => a.Name == "EmployeeId"));
        var hrCso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "EMP001");

        var directory = await CreateConnectedSystemAsync("RESURGAM AD");
        var accountType = await CreateCsoTypeAsync(directory.Id, "contact",
        [
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "Mail", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "Ou", Type = AttributeDataType.Text, Selected = true }
        ]);
        accountType.RemoveContributedAttributesOnObsoletion = recall;
        var exportRule = await CreateExportSyncRuleAsync(directory.Id, accountType, mvType, "GAL Contacts");
        var directoryImport = await CreateImportSyncRuleAsync(directory.Id, accountType, mvType, "Supplier Contacts", enableProjection: false);
        directoryImport.InboundOutOfScopeAction = outOfScopeAction;
        directoryImport.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    ConnectedSystemAttribute = accountType.Attributes.Single(a => a.Name == "Ou"),
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = SupplierOu,
                    CaseSensitive = true
                }
            ]
        });
        AddImportFlow(directoryImport, accountType, "Mail", mvMail);
        await DbContext.SaveChangesAsync();

        await RunFullSyncAsync(hr, "HR Full Sync 1");
        hrCso = await ReloadEntityAsync(hrCso);
        Assert.That(hrCso.MetaverseObjectId, Is.Not.Null, "precondition: HR projected Jane");
        var mvoId = hrCso.MetaverseObjectId!.Value;

        // The export rule provisioned Jane's account; it now exists in the directory as the confirming import found it.
        var account = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == directory.Id && c.MetaverseObjectId == mvoId);
        account.Status = ConnectedSystemObjectStatus.Normal;
        SetValue(account, accountType, "DisplayName", "Jane Smith");
        SetValue(account, accountType, "EmployeeId", "EMP001");
        SetValue(account, accountType, "Mail", "jane@resurgam.local");
        SetValue(account, accountType, "Ou", startingOu);
        if (joinMethod != ConnectedSystemObjectJoinMethod.Provisioning)
            account.RecordJoin(joinMethod, joinMethod == ConnectedSystemObjectJoinMethod.InboundMatching ? directoryImport : exportRule, DateTime.UtcNow);
        await SyncRepo.UpdateConnectedSystemObjectJoinStatesAsync([account]);
        Assert.That(account.JoinMethod, Is.EqualTo(joinMethod), "precondition: the account's join was made as the test intends");

        // Jane's account is in place, so what HR's first run staged to create it has been carried out.
        await SyncRepo.DeletePendingExportsAsync(SyncRepo.PendingExports.Values.ToList());

        return new Arrangement(await ReloadEntityAsync(hr), await ReloadEntityAsync(directory), account, mvoId);
    }

    private async Task MoveAccountToAsync(Arrangement ctx, string ou)
    {
        var account = await ReloadEntityAsync(ctx.Account);
        account.AttributeValues.Single(av => av.Attribute?.Name == "Ou").StringValue = ou;
        await ModifyCsoAsync(account);
    }

    private string? MailOf(Guid mvoId) =>
        SyncRepo.MetaverseObjects[mvoId].AttributeValues.SingleOrDefault(av => av.Attribute?.Name == "Mail")?.StringValue;

    private int CreatesStagedFor(ConnectedSystem system) =>
        SyncRepo.PendingExports.Values.Count(pe => pe.ConnectedSystemId == system.Id && pe.ChangeType == PendingExportChangeType.Create);

    private static void SetValue(ConnectedSystemObject cso, ConnectedSystemObjectType type, string attributeName, string value)
    {
        var attribute = type.Attributes.Single(a => a.Name == attributeName);
        cso.AttributeValues.RemoveAll(av => av.AttributeId == attribute.Id);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = attribute.Id,
            Attribute = attribute,
            StringValue = value
        });
    }

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

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem, string profileName = "Full Sync")
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, profileName, ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
