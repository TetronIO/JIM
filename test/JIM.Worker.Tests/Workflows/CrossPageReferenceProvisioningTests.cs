// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Utilities;
using JIM.Models.Activities;
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
/// Scenario 008 (cross-domain entitlement synchronisation) found the cross-page pass replacing that unsent
/// Create with an Update carrying only the member references: the export then tried to modify a group
/// that had never been created and the directory refused it.
///
/// Since synchronisation pages Object Types in dependency order (#2492), a group's user members are projected before
/// any group is processed, so they no longer defer it. A nested group does: it is the same type, and here it sits on a
/// later page than the group containing it. That is what keeps this test on the cross-page pass.
/// </summary>
[TestFixture]
public class CrossPageReferenceProvisioningTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_GroupOnEarlierPageThanItsMembers_KeepsOneUnsentCreateCarryingEveryAttributeAsync()
    {
        // Two objects per page: the group and one member on page one, the remaining members on later pages.
        await SetSyncPageSizeAsync(2);

        var (source, target, userType, groupType, groupMemberAttr, targetGroupType, targetGroupDisplayNameAttr, targetGroupMemberAttr) =
            await BuildDirectoriesAsync();

        // --- Source objects. Users are paged before groups; within the group type pages are ordered by Id, two to a
        // page, so Team Alpha (page one) contains Team Beta, which only Alpha's own page has not reached yet. ---
        var group = SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000001"), "Team Alpha");
        SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000002"), "Team Gamma");
        var nestedGroup = SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000003"), "Team Beta");
        var members = Enumerable.Range(1, 4)
            .Select(i => SeedSourceCso(source.Id, userType, new Guid($"10000000-0000-0000-0000-00000000000{i}"), $"Member {i}"))
            .Append(nestedGroup)
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
        var processor = new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloadedSource, profile, activity, new CancellationTokenSource());
        await processor.PerformFullSyncAsync();

        // --- Assert: the group's provisioning survived the cross-page pass intact ---
        Assert.That(processor.CrossPageReferenceObjectCount, Is.EqualTo(1),
            "Team Alpha, and only Alpha, should have been deferred to the cross-page reference pass");

        var targetGroupCso = SyncRepo.ConnectedSystemObjects.Values
            .Single(c => c.ConnectedSystemId == target.Id && c.TypeId == targetGroupType.Id && c.MetaverseObjectId == group.MetaverseObjectId);
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
            "The rebuilt Create carries every member, the nested group included, resolved across pages");
    }

    /// <summary>
    /// The change snapshot each staged export records resolves its references to the target objects; every group sharing
    /// a member, and every report sharing a manager, used to query the same object again (#2492). Members provisioned
    /// earlier in the run are named from the target objects their page wrote, so neither group's snapshot looks them up
    /// (#2939).
    /// </summary>
    [Test]
    public async Task FullSync_TwoGroupsSharingMembersProvisionedEarlierInTheRun_NamesThemWithoutLookingThemUpAsync()
    {
        await SetSyncPageSizeAsync(10);
        var (source, target, userType, groupType, groupMemberAttr, _, _, targetGroupMemberAttr) = await BuildDirectoriesAsync();

        var members = Enumerable.Range(1, 4)
            .Select(i => SeedSourceCso(source.Id, userType, new Guid($"10000000-0000-0000-0000-00000000000{i}"), $"Member {i}"))
            .ToList();
        foreach (var group in new[]
                 {
                     SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000001"), "Team Alpha"),
                     SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000002"), "Team Gamma")
                 })
        {
            foreach (var member in members)
            {
                group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(), AttributeId = groupMemberAttr.Id, Attribute = groupMemberAttr,
                    ReferenceValueId = member.Id, ReferenceValue = member, ConnectedSystemObject = group
                });
            }
        }

        var activity = await RunFullSyncAsync(source);

        var memberMvoIds = members.Select(m => m.MetaverseObjectId!.Value).ToHashSet();
        var memberLookups = SyncRepo.ConnectedSystemObjectLookupsByMetaverseObjectId
            .Where(lookup => lookup.ConnectedSystemId == target.Id && memberMvoIds.Contains(lookup.MetaverseObjectId))
            .ToList();
        var memberTargetNames = SyncRepo.ConnectedSystemObjects.Values
            .Where(c => c.ConnectedSystemId == target.Id && c.MetaverseObjectId.HasValue && memberMvoIds.Contains(c.MetaverseObjectId.Value))
            .Select(ExportChangeHistoryBuilder.GetCsoDisplayIdentifier)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(memberLookups, Is.Empty, "the members' names are known from the target objects their page wrote");
            Assert.That(MemberValueChanges(activity, targetGroupMemberAttr).Select(v => v.StringValue),
                Is.EquivalentTo(memberTargetNames.Concat(memberTargetNames)), "both groups' snapshots name every member by its target object");
            Assert.That(SyncRepo.PendingExports.Values.Count(pe => pe.ConnectedSystemId == target.Id), Is.EqualTo(6),
                "Four users and two groups are provisioned.");
        }
    }

    /// <summary>
    /// Characterises how the change snapshot of a staged export names a member that is being provisioned on the same page
    /// (#2861): the member's target object is not stored yet, so the snapshot takes its name from the objects the page has
    /// collected for creation, and records it as a stub naming the object by display text rather than the Metaverse
    /// Object id the reference was staged with.
    /// </summary>
    [Test]
    public async Task FullSync_GroupWhoseNestedGroupIsProvisionedOnTheSamePage_SnapshotNamesTheNestedGroupByItsTargetObjectAsync()
    {
        await SetSyncPageSizeAsync(10);
        var (source, target, _, groupType, groupMemberAttr, targetGroupType, _, _) = await BuildDirectoriesAsync();

        // Beta sorts first, so it is projected and its provisioning collected before Alpha, which contains it, is processed.
        var nested = SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000001"), "Team Beta");
        var group = SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000002"), "Team Alpha");
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = groupMemberAttr.Id, Attribute = groupMemberAttr,
            ReferenceValueId = nested.Id, ReferenceValue = nested, ConnectedSystemObject = group
        });

        var reloadedSource = await ReloadEntityAsync(source);
        var profile = await CreateRunProfileAsync(reloadedSource.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloadedSource.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloadedSource, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        var nestedTarget = SyncRepo.ConnectedSystemObjects.Values
            .Single(c => c.ConnectedSystemId == target.Id && c.TypeId == targetGroupType.Id && c.MetaverseObjectId == nested.MetaverseObjectId);
        var memberValues = AllOutcomes(activity)
            .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated)
            .SelectMany(o => o.ConnectedSystemObjectChange?.AttributeChanges ?? [])
            .Where(a => a.AttributeName == "Member")
            .SelectMany(a => a.ValueChanges)
            .ToList();

        Assert.That(memberValues, Has.Count.EqualTo(1), "Alpha's Create carries its one member");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.ConnectedSystemObjectLookupsByMetaverseObjectId.Where(l => l.ConnectedSystemId == target.Id),
                Is.Empty, "the name comes from the objects the page collected, without looking anything up");
            Assert.That(memberValues[0].StringValue, Is.EqualTo(ExportChangeHistoryBuilder.GetCsoDisplayIdentifier(nestedTarget)),
                "the member is named by the target object being provisioned for it");
            Assert.That(memberValues[0].StringValue, Is.Not.EqualTo(nested.MetaverseObjectId!.Value.ToString()),
                "not by the Metaverse Object id the reference was staged with");
            Assert.That(memberValues[0].IsPendingExportStub, Is.True);
        }
    }

    /// <summary>
    /// A name found for a referenced object is kept for the whole run, not only its page (#2939): the groups of a large
    /// directory share most of their members, and asking again on every page looked up each member about six times in a
    /// 500,000-person synchronisation. The members' target objects were provisioned by an earlier run, so their names
    /// come from the database.
    /// </summary>
    [Test]
    public async Task FullSync_GroupsOnTwoPagesSharingMembersHeldSinceAnEarlierRun_LooksUpEachMembersTargetOnceAsync()
    {
        await SetSyncPageSizeAsync(10);
        var (source, target, userType, groupType, groupMemberAttr, _, _, targetGroupMemberAttr) = await BuildDirectoriesAsync();
        var members = Enumerable.Range(1, 4)
            .Select(i => SeedSourceCso(source.Id, userType, new Guid($"10000000-0000-0000-0000-00000000000{i}"), $"Member {i}"))
            .ToList();
        var groups = new[]
        {
            SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000001"), "Team Alpha"),
            SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000002"), "Team Gamma")
        };
        await RunFullSyncAsync(source);

        // The groups gain their members, and each group now has a page of its own.
        foreach (var group in groups)
            AddMembers(group, groupMemberAttr, members);
        // The first run leaves entries tracked that its own work has since removed; start the second from a clean context.
        DbContext.ChangeTracker.Clear();
        await SetSyncPageSizeAsync(1);
        SyncRepo.ConnectedSystemObjectLookupsByMetaverseObjectId.Clear();
        var activity = await RunFullSyncAsync(source);

        var memberMvoIds = members.Select(m => m.MetaverseObjectId!.Value).ToHashSet();
        var memberLookups = SyncRepo.ConnectedSystemObjectLookupsByMetaverseObjectId
            .Where(lookup => lookup.ConnectedSystemId == target.Id && memberMvoIds.Contains(lookup.MetaverseObjectId))
            .Select(lookup => lookup.MetaverseObjectId)
            .ToList();
        var memberTargetNames = SyncRepo.ConnectedSystemObjects.Values
            .Where(c => c.ConnectedSystemId == target.Id && c.MetaverseObjectId.HasValue && memberMvoIds.Contains(c.MetaverseObjectId.Value))
            .Select(ExportChangeHistoryBuilder.GetCsoDisplayIdentifier)
            .ToList();
        var snapshotMemberNames = MemberValueChanges(activity, targetGroupMemberAttr).Select(v => v.StringValue).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(memberTargetNames, Has.Count.EqualTo(4), "precondition: the first run provisioned every member");
            Assert.That(snapshotMemberNames, Is.EquivalentTo(memberTargetNames.Concat(memberTargetNames)),
                "both groups' snapshots name every member by its target object");
            Assert.That(memberLookups, Is.EquivalentTo(memberMvoIds), "each member's target object is looked up once for both pages");
        }
    }

    /// <summary>
    /// A lookup that found no target object is not kept past its page (#2939): the object may be provisioned later in the
    /// run, so each page asks again. Here the members are never provisioned, so both groups' pages ask.
    /// </summary>
    [Test]
    public async Task FullSync_GroupsOnTwoPagesSharingMembersWithNoTargetObject_AsksAgainOnEachPageAsync()
    {
        await SetSyncPageSizeAsync(1);
        var (source, target, userType, groupType, groupMemberAttr, _, _, _) = await BuildDirectoriesAsync(exportUsers: false);
        var members = Enumerable.Range(1, 2)
            .Select(i => SeedSourceCso(source.Id, userType, new Guid($"10000000-0000-0000-0000-00000000000{i}"), $"Member {i}"))
            .ToList();
        foreach (var group in new[]
                 {
                     SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000001"), "Team Alpha"),
                     SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000002"), "Team Gamma")
                 })
        {
            AddMembers(group, groupMemberAttr, members);
        }

        await RunFullSyncAsync(source);

        var memberMvoIds = members.Select(m => m.MetaverseObjectId!.Value).ToHashSet();
        var lookupsPerMember = SyncRepo.ConnectedSystemObjectLookupsByMetaverseObjectId
            .Where(lookup => lookup.ConnectedSystemId == target.Id && memberMvoIds.Contains(lookup.MetaverseObjectId))
            .GroupBy(lookup => lookup.MetaverseObjectId)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.That(memberMvoIds.Select(id => lookupsPerMember.GetValueOrDefault(id)), Is.All.GreaterThanOrEqualTo(2),
            "a member with no target object is asked about again on the second group's page");
    }

    /// <summary>
    /// The pass that names, once the page's new objects are written, the references a snapshot could not name earlier
    /// takes a name the run already knows instead of asking again (#2939). Alpha is processed before Beta, which it
    /// contains, so Alpha's snapshot cannot name Beta until Beta's target object is written with the page.
    /// </summary>
    [Test]
    public async Task FullSync_GroupProcessedBeforeTheNestedGroupProvisionedOnItsPage_NamesItAfterTheFlushWithoutAskingAgainAsync()
    {
        await SetSyncPageSizeAsync(10);
        var (source, target, _, groupType, groupMemberAttr, targetGroupType, _, targetGroupMemberAttr) = await BuildDirectoriesAsync();
        var group = SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000001"), "Team Alpha");
        var nested = SeedSourceCso(source.Id, groupType, new Guid("00000000-0000-0000-0000-000000000002"), "Team Beta");
        AddMembers(group, groupMemberAttr, [nested]);

        var activity = await RunFullSyncAsync(source);

        var nestedTarget = SyncRepo.ConnectedSystemObjects.Values
            .Single(c => c.ConnectedSystemId == target.Id && c.TypeId == targetGroupType.Id && c.MetaverseObjectId == nested.MetaverseObjectId);
        var memberValues = MemberValueChanges(activity, targetGroupMemberAttr);
        var nestedLookups = SyncRepo.ConnectedSystemObjectLookupsByMetaverseObjectId
            .Count(l => l.ConnectedSystemId == target.Id && l.MetaverseObjectId == nested.MetaverseObjectId);
        Assert.That(memberValues, Has.Count.EqualTo(1), "Alpha's Create carries its one member");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(memberValues[0].StringValue, Is.EqualTo(ExportChangeHistoryBuilder.GetCsoDisplayIdentifier(nestedTarget)),
                "the member is named by its target object once the page's objects are written");
            Assert.That(memberValues[0].IsPendingExportStub, Is.True);
            Assert.That(nestedLookups, Is.EqualTo(1),
                "asked once, by Alpha's snapshot; after the flush its name is known from the object the page wrote");
        }
    }

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem source)
    {
        var reloadedSource = await ReloadEntityAsync(source);
        var profile = await CreateRunProfileAsync(reloadedSource.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloadedSource.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloadedSource, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    private static void AddMembers(ConnectedSystemObject group, ConnectedSystemObjectTypeAttribute memberAttr, IEnumerable<ConnectedSystemObject> members)
    {
        foreach (var member in members)
        {
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr,
                ReferenceValueId = member.Id, ReferenceValue = member, ConnectedSystemObject = group
            });
        }
    }

    /// <summary>The member values every Pending Export snapshot of the run recorded for the target group's Member attribute.</summary>
    private static List<ConnectedSystemObjectChangeAttributeValue> MemberValueChanges(Activity activity, ConnectedSystemObjectTypeAttribute targetMemberAttr) =>
        AllOutcomes(activity)
            .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated)
            .SelectMany(o => o.ConnectedSystemObjectChange?.AttributeChanges ?? [])
            .Where(a => a.AttributeName == targetMemberAttr.Name)
            .SelectMany(a => a.ValueChanges)
            .ToList();

    /// <summary>Every outcome the run recorded, roots and descendants, once each.</summary>
    private static List<ActivityRunProfileExecutionItemSyncOutcome> AllOutcomes(Activity activity)
    {
        var seen = new HashSet<ActivityRunProfileExecutionItemSyncOutcome>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<ActivityRunProfileExecutionItemSyncOutcome>(activity.RunProfileExecutionItems.SelectMany(i => i.SyncOutcomes));
        while (pending.Count > 0)
        {
            var outcome = pending.Pop();
            if (!seen.Add(outcome))
                continue;
            foreach (var child in outcome.Children)
                pending.Push(child);
        }
        return seen.ToList();
    }

    /// <summary>
    /// A source directory of users and groups (Member flowing to a multi-valued Metaverse reference), projecting into the
    /// Metaverse, and a target directory provisioning both, the group carrying DisplayName and Member.
    /// </summary>
    private async Task<(ConnectedSystem Source, ConnectedSystem Target, ConnectedSystemObjectType UserType, ConnectedSystemObjectType GroupType,
        ConnectedSystemObjectTypeAttribute GroupMemberAttr, ConnectedSystemObjectType TargetGroupType,
        ConnectedSystemObjectTypeAttribute TargetGroupDisplayNameAttr, ConnectedSystemObjectTypeAttribute TargetGroupMemberAttr)> BuildDirectoriesAsync(
        bool exportUsers = true)
    {
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

        if (exportUsers)
        {
            var userExportRule = await CreateExportSyncRuleAsync(target.Id, targetUserType, mvPersonType, "User Export");
            userExportRule.AttributeFlowRules.Add(BuildExportMapping(userExportRule, targetUserDisplayNameAttr, mvPersonDisplayNameAttr));
        }
        var groupExportRule = await CreateExportSyncRuleAsync(target.Id, targetGroupType, mvGroupType, "Group Export");
        groupExportRule.AttributeFlowRules.Add(BuildExportMapping(groupExportRule, targetGroupDisplayNameAttr, mvGroupDisplayNameAttr));
        groupExportRule.AttributeFlowRules.Add(BuildExportMapping(groupExportRule, targetGroupMemberAttr, mvMemberAttr));
        await DbContext.SaveChangesAsync();

        return (source, target, userType, groupType, groupMemberAttr, targetGroupType, targetGroupDisplayNameAttr, targetGroupMemberAttr);
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
