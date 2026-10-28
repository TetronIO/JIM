// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A group often references objects that will never be joined to a Metaverse Object: a member out of every
/// Synchronisation Rule's scope (a leaver), or an object of a type no Synchronisation Rule projects or joins (a contact or
/// a computer in a directory group). Such a reference cannot flow, and it must not stop the rest of the group's members
/// flowing. Once the referenced object's type has been walked in the run, and the object existed before the run began,
/// nothing later in the run can join it, so the reference is final: it neither holds back the group's member removals nor
/// sends the group to the cross-page reference step to be redone.
/// </summary>
[TestFixture]
public class UnresolvableReferenceWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_MemberRemovedFromAGroupThatAlsoReferencesAnObjectThatNeverJoins_RemovesTheMemberAsync()
    {
        var ctx = await SetUpAsync();
        await RunAsync(ctx.Source, ConnectedSystemRunType.FullSynchronisation);
        Assert.That(MemberNames(ctx), Is.EquivalentTo(new[] { "Alice", "Bob" }), "precondition: both people flowed; the contact cannot");

        RemoveMember(ctx, ctx.Bob);
        await RunAsync(ctx.Source, ConnectedSystemRunType.FullSynchronisation);

        Assert.That(MemberNames(ctx), Is.EquivalentTo(new[] { "Alice" }),
            "a member removed in the source must be removed from the Metaverse although the group still references the contact");
    }

    [Test]
    public async Task DeltaSync_MemberRemovedFromAGroupThatAlsoReferencesAnObjectThatNeverJoins_RemovesTheMemberAsync()
    {
        var ctx = await SetUpAsync();
        await RunAsync(ctx.Source, ConnectedSystemRunType.FullSynchronisation);

        RemoveMember(ctx, ctx.Bob);
        await RunAsync(ctx.Source, ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(MemberNames(ctx), Is.EquivalentTo(new[] { "Alice" }));
    }

    [Test]
    public async Task FullSync_RepeatedOverAGroupReferencingAnObjectThatNeverJoins_DoesNotRedoTheGroupInTheCrossPageStepAsync()
    {
        var ctx = await SetUpAsync();
        await RunAsync(ctx.Source, ConnectedSystemRunType.FullSynchronisation);

        var (processor, activity) = await RunAsync(ctx.Source, ConnectedSystemRunType.FullSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.CrossPageReferenceObjectCount, Is.Zero,
                "the contact's type was walked before the group's and the contact is still not joined, so it never will be this run");
            Assert.That(processor.UnresolvableReferenceValueCount, Is.EqualTo(1), "the run counts the reference that could not flow");
            Assert.That(activity.ObjectsProcessed, Is.EqualTo(4), "two people, one contact and one group");
            Assert.That(MemberNames(ctx), Is.EquivalentTo(new[] { "Alice", "Bob" }));
        }
    }

    [Test]
    public async Task FullSync_GroupReferencingAnObjectCreatedTooRecentlyToBeSureOf_StillDefersItToTheCrossPageStepAsync()
    {
        // An object created inside the clock-skew margin before the run began could have been missed by its type's walk
        // and be handed out by the closing sweep, which could still join it; the group must wait for the cross-page step.
        var ctx = await SetUpAsync(contactCreated: DateTime.UtcNow);

        var (processor, _) = await RunAsync(ctx.Source, ConnectedSystemRunType.FullSynchronisation);

        Assert.That(processor.CrossPageReferenceObjectCount, Is.EqualTo(1));
    }

    private sealed record Context(ConnectedSystem Source, ConnectedSystemObject Group, ConnectedSystemObject Bob,
        ConnectedSystemObjectTypeAttribute GroupMemberAttr, int MvMemberAttributeId, int MvDisplayNameAttributeId);

    /// <summary>
    /// A directory of people, contacts and one group whose Member references two people and a contact. People and groups
    /// project; no Synchronisation Rule covers contacts, so the contact is never joined.
    /// </summary>
    private async Task<Context> SetUpAsync(DateTime? contactCreated = null)
    {
        var source = await CreateConnectedSystemAsync("Directory");
        var userType = await CreateCsoTypeAsync(source.Id, "User", DisplayNameAttributes());
        var contactType = await CreateCsoTypeAsync(source.Id, "Contact", DisplayNameAttributes());
        var groupMemberAttr = new ConnectedSystemObjectTypeAttribute { Name = "Member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true };
        var groupAttributes = DisplayNameAttributes();
        groupAttributes.Add(groupMemberAttr);
        var groupType = await CreateCsoTypeAsync(source.Id, "Group", groupAttributes);

        var mvPersonType = await CreateMvObjectTypeAsync("Person");
        var mvGroupType = await CreateMvObjectTypeAsync("Group");
        var mvGroupDisplayName = mvGroupType.Attributes.First(a => a.Name == "DisplayName");
        var mvMember = new MetaverseAttribute
        {
            Name = "Member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvGroupType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvMember);
        await DbContext.SaveChangesAsync();
        mvGroupType.Attributes.Add(mvMember);

        var userRule = await CreateImportSyncRuleAsync(source.Id, userType, mvPersonType, "User Import");
        userRule.AttributeFlowRules.Add(Mapping(userRule, mvPersonType.Attributes.First(a => a.Name == "DisplayName"), userType.Attributes.First(a => a.Name == "DisplayName")));
        var groupRule = await CreateImportSyncRuleAsync(source.Id, groupType, mvGroupType, "Group Import");
        groupRule.AttributeFlowRules.Add(Mapping(groupRule, mvGroupDisplayName, groupType.Attributes.First(a => a.Name == "DisplayName")));
        groupRule.AttributeFlowRules.Add(Mapping(groupRule, mvMember, groupMemberAttr));
        await DbContext.SaveChangesAsync();

        // Created well before the run, as every object of an established system is.
        var established = DateTime.UtcNow.AddHours(-1);
        var alice = Seed(source.Id, userType, "Alice", established);
        var bob = Seed(source.Id, userType, "Bob", established);
        var contact = Seed(source.Id, contactType, "Contact", contactCreated ?? established);
        var group = Seed(source.Id, groupType, "Team", established);
        foreach (var member in new[] { alice, bob, contact })
        {
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = groupMemberAttr.Id, Attribute = groupMemberAttr,
                ReferenceValueId = member.Id, ReferenceValue = member, ConnectedSystemObject = group
            });
        }

        return new Context(source, group, bob, groupMemberAttr, mvMember.Id, mvPersonType.Attributes.First(a => a.Name == "DisplayName").Id);
    }

    private static List<ConnectedSystemObjectTypeAttribute> DisplayNameAttributes() =>
    [
        new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
        new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
    ];

    private ConnectedSystemObject Seed(int connectedSystemId, ConnectedSystemObjectType type, string displayName, DateTime created)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            TypeId = type.Id,
            Type = type,
            ConnectedSystem = SyncRepo.ConnectedSystems[connectedSystemId],
            Created = created
        };
        var externalId = type.Attributes.First(a => a.IsExternalId);
        var name = type.Attributes.First(a => a.Name == "DisplayName");
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalId.Id, Attribute = externalId, GuidValue = Guid.NewGuid(), ConnectedSystemObject = cso });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = name.Id, Attribute = name, StringValue = displayName, ConnectedSystemObject = cso });
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static void RemoveMember(Context ctx, ConnectedSystemObject member)
    {
        ctx.Group.AttributeValues.RemoveAll(av => av.AttributeId == ctx.GroupMemberAttr.Id && av.ReferenceValueId == member.Id);
        ctx.Group.LastUpdated = DateTime.UtcNow;
    }

    private List<string?> MemberNames(Context ctx)
    {
        var groupMvo = SyncRepo.MetaverseObjects[ctx.Group.MetaverseObjectId!.Value];
        return groupMvo.AttributeValues
            .Where(av => av.AttributeId == ctx.MvMemberAttributeId)
            .Select(av => SyncRepo.MetaverseObjects[av.ReferenceValueId ?? av.ReferenceValue!.Id].AttributeValues
                .Single(v => v.AttributeId == ctx.MvDisplayNameAttributeId).StringValue)
            .ToList();
    }

    private static SyncRuleMapping Mapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source) => new()
    {
        SyncRule = rule,
        SyncRuleId = rule.Id,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
    };

    private async Task<(SyncTaskProcessorBase Processor, Activity Activity)> RunAsync(ConnectedSystem connectedSystem, ConnectedSystemRunType runType)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{runType} {Guid.NewGuid():N}", runType);
        var activity = await CreateActivityAsync(reloaded.Id, profile, runType);
        var cts = new CancellationTokenSource();
        if (runType == ConnectedSystemRunType.FullSynchronisation)
        {
            var full = new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, cts);
            await full.PerformFullSyncAsync();
            return (full, activity);
        }

        var delta = new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, cts);
        await delta.PerformDeltaSyncAsync();
        return (delta, activity);
    }
}
