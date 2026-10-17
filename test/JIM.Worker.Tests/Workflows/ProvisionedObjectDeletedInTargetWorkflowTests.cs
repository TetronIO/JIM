// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
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
/// Issue #2318: when JIM is authoritative for provisioning (an export Synchronisation Rule with provisioning enabled
/// has the Metaverse Object in scope) and the account is deleted directly in the target Connected System, the next
/// import marks the Connected System Object Obsolete, and the next synchronisation of that system must not only
/// disconnect it but also put the account back: it stages a provisioning Create, so the next export recreates it.
/// Nothing else about the person changes, so the change-driven export path never revisits them; only the
/// disconnection can.
///
/// Topology: an "HR" source projecting a Person (EmployeeId, DisplayName, and Title into the Metaverse's Type
/// attribute) for Alice and Bob (Employees) and Carol (a Contractor), and a "Directory" target holding a joined
/// account for each, with an export rule that provisions.
/// </summary>
[TestFixture]
public class ProvisionedObjectDeletedInTargetWorkflowTests : WorkflowTestBase
{
    #region Re-provisioning

    [TestCase(true)]
    [TestCase(false)]
    public async Task DeltaSync_ProvisionedAccountDeletedInTarget_StagesProvisioningCreateAsync(bool removeContributedAttributesOnObsoletion)
    {
        var ctx = await SetUpAsync(provisioning: true, removeContributedAttributesOnObsoletion: removeContributedAttributesOnObsoletion);

        MarkDeletedInDirectory(ctx, "Alice");
        await RunDeltaSyncAsync(ctx.Directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DirectoryAccountNames(ctx), Is.EquivalentTo(new[] { "Alice", "Bob", "Carol" }),
                "the deleted account is disconnected and a new one is provisioned in its place");
            Assert.That(CreatesFor(ctx), Is.EqualTo(new[] { "Alice" }), "the next export recreates Alice's account");
            Assert.That(ProvisioningAccountOf(ctx, "Alice").MetaverseObjectId, Is.EqualTo(MetaverseObjectOf(ctx, "Alice").Id),
                "the new account is joined to the same Metaverse Object");
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task FullSync_ProvisionedAccountDeletedInTarget_StagesProvisioningCreateAsync(bool removeContributedAttributesOnObsoletion)
    {
        var ctx = await SetUpAsync(provisioning: true, removeContributedAttributesOnObsoletion: removeContributedAttributesOnObsoletion);

        MarkDeletedInDirectory(ctx, "Alice");
        await RunFullSyncAsync(ctx.Directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CreatesFor(ctx), Is.EqualTo(new[] { "Alice" }), "the next export recreates Alice's account");
            Assert.That(ProvisioningAccountOf(ctx, "Alice").MetaverseObjectId, Is.EqualTo(MetaverseObjectOf(ctx, "Alice").Id));
        }
    }

    [Test]
    public async Task DeltaSync_AfterTheReProvisioningCreateIsStaged_StagesNothingFurtherAsync()
    {
        // The re-provisioning is a one-off consequence of the disconnection, not a standing review: once the Create is
        // staged, a later synchronisation with nothing new must not stage another.
        var ctx = await SetUpAsync(provisioning: true);
        MarkDeletedInDirectory(ctx, "Alice");
        await RunDeltaSyncAsync(ctx.Directory);
        Assert.That(CreatesFor(ctx), Is.EqualTo(new[] { "Alice" }), "arrange: the Create is staged");

        await RunDeltaSyncAsync(ctx.Directory);
        await RunFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CreatesFor(ctx), Is.EqualTo(new[] { "Alice" }), "still exactly one Create");
            Assert.That(MetaverseObjectOf(ctx, "Alice").ScopeReviewPending, Is.False, "nothing is left waiting for review");
        }
    }

    [Test]
    public async Task DeltaSync_AccountDeletedInTargetWhoseImportRuleRemainsJoined_StagesProvisioningCreateAsync()
    {
        // Remain Joined on the target's own import rule keeps the Metaverse Object as it is (no Deletion Rule, no
        // recall), but the deleted account's object is still removed, so the person is left without one all the same.
        var ctx = await SetUpAsync(provisioning: true, directoryOutOfScopeAction: InboundOutOfScopeAction.RemainJoined);

        MarkDeletedInDirectory(ctx, "Alice");
        await RunDeltaSyncAsync(ctx.Directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CreatesFor(ctx), Is.EqualTo(new[] { "Alice" }), "the next export recreates Alice's account");
            Assert.That(ProvisioningAccountOf(ctx, "Alice").MetaverseObjectId, Is.EqualTo(MetaverseObjectOf(ctx, "Alice").Id));
        }
    }

    #endregion

    #region Preview

    [Test]
    public async Task PreviewFullSyncAsync_OfATargetWhoseProvisionedAccountWasDeleted_ProposesTheCreateTheRunStagesAsync()
    {
        // A Full Synchronisation preview of the target must propose what its run will do, the re-provisioning included.
        var ctx = await SetUpAsync(provisioning: true);
        MarkDeletedInDirectory(ctx, "Alice");

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Directory.Id);
        Assert.That(PendingExportsFor(ctx.Directory), Is.Empty, "the preview changes nothing");
        await RunFullSyncAsync(ctx.Directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CreatesFor(ctx), Has.Count.EqualTo(1), "arrange: the run stages Alice's Create");
            Assert.That(preview.Counts.ObjectsToCreate, Is.EqualTo(1), "the preview proposes the Create the run stages");
        }
    }

    #endregion

    #region Where nothing is re-provisioned

    [Test]
    public async Task DeltaSync_AccountDeletedInTargetWithNoProvisioningRule_DisconnectsAndStagesNothingAsync()
    {
        var ctx = await SetUpAsync(provisioning: false);

        MarkDeletedInDirectory(ctx, "Alice");
        await RunDeltaSyncAsync(ctx.Directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DirectoryAccountNames(ctx), Is.EquivalentTo(new[] { "Bob", "Carol" }), "Alice's account is disconnected and removed");
            Assert.That(PendingExportsFor(ctx.Directory), Is.Empty, "with no provisioning rule, leaving it disconnected is correct");
            Assert.That(MetaverseObjectOf(ctx, "Alice").ScopeReviewPending, Is.False);
        }
    }

    [Test]
    public async Task DeltaSync_AccountDeletedInTargetOutsideTheProvisioningRulesScope_StagesNothingAsync()
    {
        var ctx = await SetUpAsync(provisioning: true, scopedToEmployees: true);
        Assert.That(PendingExportsFor(ctx.Directory).Where(pe => pe.ChangeType == PendingExportChangeType.Create), Is.Empty,
            "arrange: every in-scope person already has an account");

        MarkDeletedInDirectory(ctx, "Carol");
        await RunDeltaSyncAsync(ctx.Directory);

        Assert.That(CreatesFor(ctx), Is.Empty, "Carol is a Contractor, outside the provisioning rule's scope");
    }

    [Test]
    public async Task DeltaSync_AccountDeletedInTargetThatSchedulesTheMetaverseObjectsDeletion_StagesNothingAsync()
    {
        var ctx = await SetUpAsync(provisioning: true);
        ctx.MvType.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
        ctx.MvType.DeletionTriggerConnectedSystemIds.Add(ctx.Directory.Id);
        ctx.MvType.DeletionGracePeriod = TimeSpan.FromDays(30);

        MarkDeletedInDirectory(ctx, "Alice");
        await RunDeltaSyncAsync(ctx.Directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MetaverseObjectOf(ctx, "Alice").DeletionEligibleDate, Is.Not.Null, "arrange: the disconnection scheduled the deletion");
            Assert.That(CreatesFor(ctx), Is.Empty, "an object on its way out is not put back");
            Assert.That(MetaverseObjectOf(ctx, "Alice").ScopeReviewPending, Is.False);
        }
    }

    [Test]
    public async Task DeltaSync_AccountDeletedInTargetThatDeletesTheMetaverseObject_StagesNothingAsync()
    {
        var ctx = await SetUpAsync(provisioning: true);
        ctx.MvType.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
        ctx.MvType.DeletionTriggerConnectedSystemIds.Add(ctx.Directory.Id);
        ctx.MvType.DeletionGracePeriod = TimeSpan.Zero;
        var aliceId = MetaverseObjectOf(ctx, "Alice").Id;

        MarkDeletedInDirectory(ctx, "Alice");
        await RunDeltaSyncAsync(ctx.Directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.MetaverseObjects.ContainsKey(aliceId), Is.False, "arrange: the disconnection deleted the Metaverse Object");
            Assert.That(CreatesFor(ctx), Is.Empty);
        }
    }

    #endregion

    #region Topology

    private sealed record Context(
        ConnectedSystem Hr,
        ConnectedSystem Directory,
        ConnectedSystemObjectType DirectoryType,
        MetaverseObjectType MvType,
        MetaverseAttribute MvDisplayName);

    private async Task<Context> SetUpAsync(bool provisioning, bool scopedToEmployees = false, bool removeContributedAttributesOnObsoletion = true,
        InboundOutOfScopeAction directoryOutOfScopeAction = InboundOutOfScopeAction.Disconnect)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvTitle = mvType.Attributes.First(a => a.Name == "Type");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User", Attributes());
        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User", Attributes());
        directoryType.RemoveContributedAttributesOnObsoletion = removeContributedAttributesOnObsoletion;

        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        AddImportFlow(hrImport, hrType, "EmployeeId", mvEmployeeId);
        AddImportFlow(hrImport, hrType, "DisplayName", mvDisplayName);
        AddImportFlow(hrImport, hrType, "Title", mvTitle);

        // The Directory contributes the person's Title too (from its Department), so with recall on its departure
        // takes the attribute-recall branch of the disconnection, and with recall off it takes the branch that recalls
        // nothing; both must re-provision.
        var directoryImport = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import", enableProjection: false);
        directoryImport.InboundOutOfScopeAction = directoryOutOfScopeAction;
        AddImportFlow(directoryImport, directoryType, "Department", mvTitle);
        var directoryEmployeeId = directoryType.Attributes.Single(a => a.Name == "EmployeeId");
        directoryImport.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = directoryImport,
            SyncRuleId = directoryImport.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeId,
            TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = directoryEmployeeId, ConnectedSystemAttributeId = directoryEmployeeId.Id } }
        });

        var exportRule = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export",
            enableProvisioning: false, deprovisionAction: OutboundDeprovisionAction.Disconnect);
        AddExportFlow(exportRule, directoryType, "EmployeeId", mvEmployeeId);
        AddExportFlow(exportRule, directoryType, "DisplayName", mvDisplayName);
        if (scopedToEmployees)
        {
            exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria = new List<SyncRuleScopingCriteria>
                {
                    new() { MetaverseAttribute = mvTitle, MetaverseAttributeId = mvTitle.Id, ComparisonType = SearchComparisonType.Equals, StringValue = "Employee", CaseSensitive = true }
                }
            });
        }

        await DbContext.SaveChangesAsync();

        SeedCso(hr, hrType, "E1", "Alice", "Employee");
        SeedCso(hr, hrType, "E2", "Bob", "Employee");
        SeedCso(hr, hrType, "E3", "Carol", "Contractor");
        SeedCso(directory, directoryType, "E1", "Alice", "Employee");
        SeedCso(directory, directoryType, "E2", "Bob", "Employee");
        SeedCso(directory, directoryType, "E3", "Carol", "Contractor");

        await RunFullSyncAsync(hr);
        await RunFullSyncAsync(directory);
        Assert.That(SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == directory.Id).Select(c => c.MetaverseObjectId),
            Has.All.Not.Null, "arrange: every Directory account is joined");

        // Provisioning is switched on only now, as it stands once JIM has provisioned every account: switched on before
        // the joins, the HR synchronisation would provision accounts of its own ahead of the Directory's being joined.
        // Set directly rather than saved through the configuration path, which would flag every person for an export
        // scope review and so mask what the disconnection itself does.
        exportRule.ProvisionToConnectedSystem = provisioning;

        // Nothing changes in either system after this, so a Delta Synchronisation below sees only what a test changes.
        await Task.Delay(5);
        SyncRepo.ConnectedSystems[hr.Id].LastSyncCompletedAt = DateTime.UtcNow;
        SyncRepo.ConnectedSystems[directory.Id].LastSyncCompletedAt = DateTime.UtcNow;
        await Task.Delay(5);

        return new Context(hr, directory, directoryType, mvType, mvDisplayName);
    }

    private static List<ConnectedSystemObjectTypeAttribute> Attributes() =>
    [
        new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
        new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "Title", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "Department", Type = AttributeDataType.Text, Selected = true }
    ];

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

    private void SeedCso(ConnectedSystem system, ConnectedSystemObjectType type, string employeeId, string displayName, string title)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            TypeId = type.Id,
            Type = type,
            ConnectedSystem = SyncRepo.ConnectedSystems[system.Id],
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        var externalId = type.Attributes.Single(a => a.IsExternalId);
        cso.ExternalIdAttributeId = externalId.Id;
        AddValue(cso, externalId, guidValue: Guid.NewGuid());
        AddValue(cso, type.Attributes.Single(a => a.Name == "EmployeeId"), employeeId);
        AddValue(cso, type.Attributes.Single(a => a.Name == "DisplayName"), displayName);
        AddValue(cso, type.Attributes.Single(a => a.Name == "Title"), title);
        AddValue(cso, type.Attributes.Single(a => a.Name == "Department"), title);
        SyncRepo.SeedConnectedSystemObject(cso);
    }

    private static void AddValue(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string? stringValue = null, Guid? guidValue = null) =>
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attribute.Id, Attribute = attribute,
            StringValue = stringValue, GuidValue = guidValue
        });

    /// <summary>What a Delta Import does when the Connector reports the account deleted: the object turns Obsolete, still joined.</summary>
    private void MarkDeletedInDirectory(Context ctx, string displayName)
    {
        var account = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Directory.Id
            && c.Status == ConnectedSystemObjectStatus.Normal
            && c.AttributeValues.Any(av => av.Attribute?.Name == "DisplayName" && av.StringValue == displayName));
        Assert.That(account.MetaverseObjectId, Is.Not.Null, "arrange: the account is joined");
        account.Status = ConnectedSystemObjectStatus.Obsolete;
        account.LastUpdated = DateTime.UtcNow;
    }

    private MetaverseObject MetaverseObjectOf(Context ctx, string displayName) => SyncRepo.MetaverseObjects.Values
        .Single(mvo => mvo.Type?.Id == ctx.MvType.Id
            && mvo.AttributeValues.Any(av => av.AttributeId == ctx.MvDisplayName.Id && av.StringValue == displayName));

    /// <summary>The Directory accounts, by the DisplayName they hold or (for one awaiting provisioning) their person's.</summary>
    private List<string?> DirectoryAccountNames(Context ctx) => SyncRepo.ConnectedSystemObjects.Values
        .Where(c => c.ConnectedSystemId == ctx.Directory.Id)
        .Select(c => c.Status == ConnectedSystemObjectStatus.PendingProvisioning && c.MetaverseObjectId.HasValue
            ? SyncRepo.MetaverseObjects[c.MetaverseObjectId.Value].AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDisplayName.Id)?.StringValue
            : c.AttributeValues.SingleOrDefault(av => av.Attribute?.Name == "DisplayName")?.StringValue)
        .ToList();

    private ConnectedSystemObject ProvisioningAccountOf(Context ctx, string displayName)
    {
        var mvoId = MetaverseObjectOf(ctx, displayName).Id;
        return SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Directory.Id
            && c.Status == ConnectedSystemObjectStatus.PendingProvisioning && c.MetaverseObjectId == mvoId);
    }

    private List<PendingExport> PendingExportsFor(ConnectedSystem system) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == system.Id).ToList();

    /// <summary>The people a Create is staged for in the Directory, by the DisplayName it would write.</summary>
    private List<string?> CreatesFor(Context ctx) => PendingExportsFor(ctx.Directory)
        .Where(pe => pe.ChangeType == PendingExportChangeType.Create)
        .Select(pe => pe.AttributeValueChanges.SingleOrDefault(c => c.Attribute?.Name == "DisplayName")?.StringValue)
        .ToList();

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }

    private async Task RunDeltaSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
    }

    #endregion
}
