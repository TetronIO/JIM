// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A Full Sync pages its Connected System Objects, and a group whose members sit on a later page than the
/// group itself cannot resolve those member references while its own page is processed: the members'
/// Metaverse Objects do not exist yet. The group is parked and re-processed once every page is done
/// (<c>SyncTaskProcessorBase.ResolveCrossPageReferencesAsync</c>), which re-evaluates its exports with the
/// references resolved.
///
/// That re-evaluation must leave the group's provisioning intact. Its Create Pending Export was staged on
/// the group's own page and has never been sent, so after the cross-page pass the group must still hold
/// exactly one Pending Export, still a Create, carrying every mapped attribute plus the resolved members.
/// Scenario 8 (cross-domain entitlement synchronisation) found the cross-page pass replacing that unsent
/// Create with an Update carrying only the member references: the export then tried to modify a group
/// that had never been created and the directory refused it.
/// </summary>
[TestFixture]
public class CrossPageReferenceProvisioningTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_GroupOnEarlierPageThanItsMembers_KeepsOneUnsentCreateCarryingEveryAttributeAsync()
    {
        // Two objects per page: the group and one member on page one, the remaining members on later pages.
        await SetSyncPageSizeAsync(2);

        // --- Source directory: users, and a group whose Member attribute references them ---
        var source = await CreateConnectedSystemAsync("Directory Source");
        var userExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var userDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var userType = await CreateCsoTypeAsync(source.Id, "User",
            new List<ConnectedSystemObjectTypeAttribute> { userExternalIdAttr, userDisplayNameAttr });

        var groupExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var groupDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var groupMemberAttr = new ConnectedSystemObjectTypeAttribute { Name = "Member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true };
        var groupType = await CreateCsoTypeAsync(source.Id, "Group",
            new List<ConnectedSystemObjectTypeAttribute> { groupExternalIdAttr, groupDisplayNameAttr, groupMemberAttr });

        // --- Metaverse: Person, and Group with a multi-valued Member reference ---
        var mvPersonType = await CreateMvObjectTypeAsync("Person");
        var mvPersonDisplayNameAttr = mvPersonType.Attributes.First(a => a.Name == "DisplayName");
        var mvGroupType = await CreateMvObjectTypeAsync("Group");
        var mvGroupDisplayNameAttr = mvGroupType.Attributes.First(a => a.Name == "DisplayName");
        var mvMemberAttr = new MetaverseAttribute
        {
            Name = "Member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvGroupType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvMemberAttr);
        await DbContext.SaveChangesAsync();
        mvGroupType.Attributes.Add(mvMemberAttr);

        // --- Import rules: users project as Person, groups project as Group with Member flowing ---
        var userImportRule = await CreateImportSyncRuleAsync(source.Id, userType, mvPersonType, "User Import");
        userImportRule.AttributeFlowRules.Add(BuildImportMapping(userImportRule, mvPersonDisplayNameAttr, userDisplayNameAttr));
        var groupImportRule = await CreateImportSyncRuleAsync(source.Id, groupType, mvGroupType, "Group Import");
        groupImportRule.AttributeFlowRules.Add(BuildImportMapping(groupImportRule, mvGroupDisplayNameAttr, groupDisplayNameAttr));
        groupImportRule.AttributeFlowRules.Add(BuildImportMapping(groupImportRule, mvMemberAttr, groupMemberAttr));
        await DbContext.SaveChangesAsync();

        // --- Target directory: provisions users and groups, the group carrying DisplayName and Member ---
        var target = await CreateConnectedSystemAsync("Directory Target");
        var targetUserExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var targetUserDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var targetUserType = await CreateCsoTypeAsync(target.Id, "TargetUser",
            new List<ConnectedSystemObjectTypeAttribute> { targetUserExternalIdAttr, targetUserDisplayNameAttr });

        var targetGroupExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var targetGroupDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var targetGroupMemberAttr = new ConnectedSystemObjectTypeAttribute { Name = "Member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true };
        var targetGroupType = await CreateCsoTypeAsync(target.Id, "TargetGroup",
            new List<ConnectedSystemObjectTypeAttribute> { targetGroupExternalIdAttr, targetGroupDisplayNameAttr, targetGroupMemberAttr });

        var userExportRule = await CreateExportSyncRuleAsync(target.Id, targetUserType, mvPersonType, "User Export");
        userExportRule.AttributeFlowRules.Add(BuildExportMapping(userExportRule, targetUserDisplayNameAttr, mvPersonDisplayNameAttr));
        var groupExportRule = await CreateExportSyncRuleAsync(target.Id, targetGroupType, mvGroupType, "Group Export");
        groupExportRule.AttributeFlowRules.Add(BuildExportMapping(groupExportRule, targetGroupDisplayNameAttr, mvGroupDisplayNameAttr));
        groupExportRule.AttributeFlowRules.Add(BuildExportMapping(groupExportRule, targetGroupMemberAttr, mvMemberAttr));
        await DbContext.SaveChangesAsync();

        // --- Source objects. Pages are ordered by Id, so the group's Id sorts before every member's. ---
        var group = SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000001"), "Team Alpha");
        var members = Enumerable.Range(1, 4)
            .Select(i => SeedSourceCso(source.Id, userType, new Guid($"10000000-0000-0000-0000-00000000000{i}"), $"Member {i}"))
            .ToList();
        foreach (var member in members)
        {
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = groupMemberAttr.Id,
                Attribute = groupMemberAttr,
                ReferenceValueId = member.Id,
                ReferenceValue = member,
                ConnectedSystemObject = group
            });
        }

        // --- Act: one Full Sync over three pages, ending in the cross-page reference pass ---
        var reloadedSource = await ReloadEntityAsync(source);
        var profile = await CreateRunProfileAsync(reloadedSource.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloadedSource.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloadedSource, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // --- Assert: the group's provisioning survived the cross-page pass intact ---
        var targetGroupCso = SyncRepo.ConnectedSystemObjects.Values
            .Single(c => c.ConnectedSystemId == target.Id && c.TypeId == targetGroupType.Id);
        Assert.That(targetGroupCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            "The group has not been exported yet, so its target object is still awaiting provisioning");

        var groupPendingExports = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemObjectId == targetGroupCso.Id)
            .ToList();
        Assert.That(groupPendingExports, Has.Count.EqualTo(1), "One Pending Export per target object");

        var groupExport = groupPendingExports[0];
        Assert.That(groupExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create),
            "The Create was never sent, so the cross-page pass must not turn it into an Update");
        Assert.That(groupExport.Status, Is.EqualTo(PendingExportStatus.Pending));
        Assert.That(groupExport.AttributeValueChanges.Any(avc => avc.AttributeId == targetGroupDisplayNameAttr.Id && avc.StringValue == "Team Alpha"),
            "The rebuilt Create carries the non-reference attributes staged on the group's own page");
        Assert.That(groupExport.AttributeValueChanges.Count(avc => avc.AttributeId == targetGroupMemberAttr.Id), Is.EqualTo(members.Count),
            "The rebuilt Create carries every member, resolved across pages");
    }

    /// <summary>
    /// Seeds a source Connected System Object with a chosen Id: the Full Sync pages by Id, so the Id decides
    /// which page the object lands on.
    /// </summary>
    private ConnectedSystemObject SeedSourceCso(int connectedSystemId, ConnectedSystemObjectType type, Guid id, string displayName)
    {
        var externalIdAttr = type.Attributes.First(a => a.IsExternalId);
        var displayNameAttr = type.Attributes.First(a => a.Name == "DisplayName");
        var cso = new ConnectedSystemObject
        {
            Id = id,
            ConnectedSystemId = connectedSystemId,
            TypeId = type.Id,
            Type = type,
            ConnectedSystem = SyncRepo.ConnectedSystems[connectedSystemId],
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = externalIdAttr.Id,
            Attribute = externalIdAttr,
            GuidValue = Guid.NewGuid(),
            ConnectedSystemObject = cso
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = displayNameAttr.Id,
            Attribute = displayNameAttr,
            StringValue = displayName,
            ConnectedSystemObject = cso
        });
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private async Task SetSyncPageSizeAsync(int pageSize)
    {
        var setting = DbContext.ServiceSettingItems.First(s => s.Key == Constants.SettingKeys.SyncPageSize);
        setting.Value = pageSize.ToString();
        await DbContext.SaveChangesAsync();
    }

    private static SyncRuleMapping BuildImportMapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source) => new()
    {
        SyncRule = rule,
        SyncRuleId = rule.Id,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
    };

    private static SyncRuleMapping BuildExportMapping(SyncRule rule, ConnectedSystemObjectTypeAttribute target, MetaverseAttribute source) => new()
    {
        SyncRule = rule,
        SyncRuleId = rule.Id,
        TargetConnectedSystemAttribute = target,
        TargetConnectedSystemAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
    };
}
