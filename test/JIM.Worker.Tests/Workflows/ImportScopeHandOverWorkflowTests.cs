// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// An object leaves one import Synchronisation Rule's scope while another rule still covers it (#2821). Scope is per
/// rule (#1199), so the departed rule's own Out-of-Scope Action decides what happens to the values it contributed,
/// whatever the other rules do: Disconnect (with recall on) withdraws them, so the next contributor takes the attribute
/// over in the same run; Remain Joined keeps them. The join stays either way, because another rule still covers the
/// object.
///
/// Before the fix, scope-exit handling ran only when an object left every scoped rule, so a narrow, higher-priority
/// rule's value stayed for good: the priority gate refused to let the broad rule overwrite it.
///
/// Topology: one "HR" Connected System with two import rules over one object type. "HR Everyone" is unscoped,
/// projects, and flows DisplayName to Display Name at priority 2. "HR Executives" is scoped to EmployeeType = EXEC and
/// flows FormalName to Display Name at priority 1. Jane starts as an executive, so her Display Name is her formal name.
/// </summary>
[TestFixture]
public class ImportScopeHandOverWorkflowTests : WorkflowTestBase
{
    private const string FormalName = "Dr Jane Smith";
    private const string PlainName = "Jane";

    [Test]
    public async Task FullSync_ObjectLeavesANarrowRuleABroadRuleCovers_HandsTheValueToTheBroadRuleAsync()
    {
        var ctx = await ArrangeAsync();
        Assert.That(DisplayNameOf(ctx), Is.EqualTo(FormalName), "precondition: the executive rule's value wins while Jane is in its scope");

        await SetEmployeeTypeAsync(ctx, "STAFF");
        await RunFullSyncAsync(ctx.Hr);

        var displayName = DisplayNameValueOf(ctx);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(displayName?.StringValue, Is.EqualTo(PlainName),
                "Jane left the executive rule's scope, so its value is withdrawn and the broad rule's value takes over");
            Assert.That(displayName?.ContributedBySyncRuleId, Is.EqualTo(ctx.EveryoneRuleId), "provenance moves to the broad rule");
            Assert.That((await ReloadEntityAsync(ctx.Cso)).MetaverseObjectId, Is.EqualTo(ctx.MvoId), "the broad rule still covers Jane, so the join stays");
        }
    }

    [Test]
    public async Task DeltaSync_ObjectLeavesANarrowRuleABroadRuleCovers_HandsTheValueToTheBroadRuleAsync()
    {
        var ctx = await ArrangeAsync();

        await SetEmployeeTypeAsync(ctx, "STAFF");
        await RunDeltaSyncAsync(ctx.Hr);

        Assert.That(DisplayNameOf(ctx), Is.EqualTo(PlainName), "a scope-only change reaching Delta Synchronisation hands over as a Full one does");
    }

    [Test]
    public async Task FullSync_ObjectLeavesANarrowRuleWithRemainJoined_KeepsThatRulesValueAsync()
    {
        var ctx = await ArrangeAsync(executiveAction: InboundOutOfScopeAction.RemainJoined);

        await SetEmployeeTypeAsync(ctx, "STAFF");
        await RunFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DisplayNameOf(ctx), Is.EqualTo(FormalName), "Remain Joined keeps the values the rule contributed");
            Assert.That(DisplayNameValueOf(ctx)?.ContributedBySyncRuleId, Is.EqualTo(ctx.ExecutivesRuleId));
        }
    }

    [Test]
    public async Task FullSync_ObjectLeavesANarrowRuleWithRecallOff_KeepsThatRulesValueAsync()
    {
        var ctx = await ArrangeAsync(recall: false);

        await SetEmployeeTypeAsync(ctx, "STAFF");
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(DisplayNameOf(ctx), Is.EqualTo(FormalName), "with the Object Type's recall off, nothing a rule contributed is withdrawn");
    }

    [Test]
    public async Task FullSync_ObjectLeavesTheOnlyRuleThatFlowsAnAttribute_ClearsItAsync()
    {
        var ctx = await ArrangeAsync(everyoneFlowsDisplayName: false);
        Assert.That(DisplayNameOf(ctx), Is.EqualTo(FormalName), "precondition");

        await SetEmployeeTypeAsync(ctx, "STAFF");
        await RunFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DisplayNameOf(ctx), Is.Null, "no other rule contributes Display Name, so withdrawing the executive rule's value clears it");
            Assert.That((await ReloadEntityAsync(ctx.Cso)).MetaverseObjectId, Is.EqualTo(ctx.MvoId), "the broad rule still covers Jane");
        }
    }

    [Test]
    public async Task FullSync_ObjectStaysInBothRules_ChangesNothingAsync()
    {
        var ctx = await ArrangeAsync();

        await RunFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DisplayNameOf(ctx), Is.EqualTo(FormalName));
            Assert.That(DisplayNameValueOf(ctx)?.ContributedBySyncRuleId, Is.EqualTo(ctx.ExecutivesRuleId));
        }
    }

    [Test]
    public async Task FullSync_ObjectLeavesANarrowRuleABroadRuleCovers_RecordsTheScopeExitNamingTheRuleAsync()
    {
        var ctx = await ArrangeAsync();

        await SetEmployeeTypeAsync(ctx, "STAFF");
        var activity = await RunFullSyncAsync(ctx.Hr);

        var item = activity.RunProfileExecutionItems.Single(i => i.ConnectedSystemObjectId == ctx.Cso.Id);
        var scopeExit = item.SyncOutcomes.SingleOrDefault(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin);
        Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet));
        Assert.That(scopeExit, Is.Not.Null, "the scope exit is recorded on the object's item, so an administrator can see why the value changed");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scopeExit!.SyncRuleId, Is.EqualTo(ctx.ExecutivesRuleId), "it names the rule the object left");
            Assert.That(scopeExit!.DetailCount, Is.EqualTo(1), "it counts the one value the rule had contributed");
        }
    }

    [Test]
    public async Task FullSync_ObjectLeavesANarrowRuleAndAnotherSystemContributes_HandsTheValueToThatSystemAsync()
    {
        var ctx = await ArrangeAsync(everyoneFlowsDisplayName: false, withBadgeSystem: true);
        await RunFullSyncAsync(ctx.Badge!);
        Assert.That(DisplayNameOf(ctx), Is.EqualTo(FormalName), "precondition: the executive rule outranks the badge system");

        await SetEmployeeTypeAsync(ctx, "STAFF");
        await RunFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DisplayNameOf(ctx), Is.EqualTo(BadgeName), "with no same-system rule writing Display Name, the badge system's value takes over");
            Assert.That(DisplayNameValueOf(ctx)?.ContributedBySystemId, Is.EqualTo(ctx.Badge!.Id));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ObjectLeavesANarrowRuleABroadRuleCovers_PredictsTheRunAsync()
    {
        var ctx = await ArrangeAsync();
        await SetEmployeeTypeAsync(ctx, "STAFF");

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.Cso.Id);
        var activity = await RunFullSyncAsync(ctx.Hr);

        var realTree = SyncPreviewFidelityTests.MapRealOutcomeTree(activity);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncPreviewFidelityTests.DescribeTree(realTree),
                Does.Contain(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin)),
                "precondition: the run records the scope exit");
            Assert.That(SyncPreviewFidelityTests.DescribeTree(preview.OutcomeTree), Is.EqualTo(SyncPreviewFidelityTests.DescribeTree(realTree)),
                "the preview predicts the hand-over the run makes");
        }
    }

    [Test]
    public async Task FullSync_ObjectLeavesANarrowRuleABroadRuleWritesTheSameValue_RecordsTheScopeExitAsTheProvenanceMovesAsync()
    {
        // The broad rule flows the identical value, so nothing about the value changes; only who contributed it does. The
        // scope exit is still an outcome, so it must still be recorded, naming the rule the object left.
        var ctx = await ArrangeAsync(displayName: FormalName);
        Assert.That(DisplayNameValueOf(ctx)?.ContributedBySyncRuleId, Is.EqualTo(ctx.ExecutivesRuleId), "precondition: the executive rule holds the value");

        await SetEmployeeTypeAsync(ctx, "STAFF");
        var activity = await RunFullSyncAsync(ctx.Hr);

        var item = activity.RunProfileExecutionItems.SingleOrDefault(i => i.ConnectedSystemObjectId == ctx.Cso.Id);
        Assert.That(item, Is.Not.Null, "a scope exit is never silent: the object's item is recorded even when no value changes");
        var scopeExit = item!.SyncOutcomes.SingleOrDefault(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin);
        Assert.That(scopeExit, Is.Not.Null, "the item records the scope exit");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scopeExit!.SyncRuleId, Is.EqualTo(ctx.ExecutivesRuleId), "it names the rule the object left");
            Assert.That(item!.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet));
            Assert.That(DisplayNameOf(ctx), Is.EqualTo(FormalName), "the value itself is unchanged");
            Assert.That(DisplayNameValueOf(ctx)?.ContributedBySyncRuleId, Is.EqualTo(ctx.EveryoneRuleId), "the broad rule takes the value over");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ObjectLeavesANarrowRuleABroadRuleWritesTheSameValue_PredictsTheRunAsync()
    {
        var ctx = await ArrangeAsync(displayName: FormalName);
        await SetEmployeeTypeAsync(ctx, "STAFF");

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.Cso.Id);
        Assert.That(DisplayNameValueOf(ctx)?.ContributedBySyncRuleId, Is.EqualTo(ctx.ExecutivesRuleId),
            "a preview changes nothing: the takeover it works out must not stamp the stored value");
        var activity = await RunFullSyncAsync(ctx.Hr);

        var realTree = SyncPreviewFidelityTests.MapRealOutcomeTree(activity);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncPreviewFidelityTests.DescribeTree(realTree),
                Does.Contain(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin)),
                "precondition: the run records the scope exit");
            Assert.That(SyncPreviewFidelityTests.DescribeTree(preview.OutcomeTree), Is.EqualTo(SyncPreviewFidelityTests.DescribeTree(realTree)),
                "the preview predicts the provenance-only scope exit the run records");
        }
    }

    private const string BadgeName = "J. Smith (badge)";

    private sealed record Arrangement(
        ConnectedSystem Hr,
        ConnectedSystem? Badge,
        ConnectedSystemObject Cso,
        Guid MvoId,
        int DisplayNameAttributeId,
        int EveryoneRuleId,
        int ExecutivesRuleId);

    private async Task<Arrangement> ArrangeAsync(
        InboundOutOfScopeAction executiveAction = InboundOutOfScopeAction.Disconnect,
        bool recall = true,
        bool everyoneFlowsDisplayName = true,
        bool withBadgeSystem = false,
        string displayName = PlainName)
    {
        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "FormalName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "EmployeeType", Type = AttributeDataType.Text, Selected = true }
        });
        hrType.RemoveContributedAttributesOnObsoletion = recall;

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var everyone = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Everyone");
        AddFlow(everyone, hrType, "EmployeeId", mvEmployeeId);
        if (everyoneFlowsDisplayName)
            AddFlow(everyone, hrType, "DisplayName", mvDisplayName, priority: 2);

        var executives = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Executives", enableProjection: false);
        executives.InboundOutOfScopeAction = executiveAction;
        AddFlow(executives, hrType, "FormalName", mvDisplayName, priority: 1);
        executives.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    ConnectedSystemAttribute = hrType.Attributes.Single(a => a.Name == "EmployeeType"),
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "EXEC",
                    CaseSensitive = true
                }
            }
        });

        ConnectedSystem? badge = null;
        if (withBadgeSystem)
        {
            badge = await CreateConnectedSystemAsync("Badges");
            badge.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
            var badgeType = await CreateCsoTypeAsync(badge.Id, "Badge", new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
                new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true }
            });
            var badgeRule = await CreateImportSyncRuleAsync(badge.Id, badgeType, mvType, "Badges", enableProjection: false);
            AddFlow(badgeRule, badgeType, "DisplayName", mvDisplayName, priority: 3);
            var badgeEmployeeId = badgeType.Attributes.Single(a => a.Name == "EmployeeId");
            badgeRule.ObjectMatchingRules.Add(new ObjectMatchingRule
            {
                SyncRule = badgeRule,
                SyncRuleId = badgeRule.Id,
                Order = 0,
                CaseSensitive = true,
                TargetMetaverseAttribute = mvEmployeeId,
                TargetMetaverseAttributeId = mvEmployeeId.Id,
                Sources = new List<ObjectMatchingRuleSource>
                {
                    new() { Order = 0, ConnectedSystemAttribute = badgeEmployeeId, ConnectedSystemAttributeId = badgeEmployeeId.Id }
                }
            });
            await CreateCsoAsync(badge.Id, badgeType, BadgeName, "E1");
        }
        await DbContext.SaveChangesAsync();

        var cso = await CreateCsoAsync(hr.Id, hrType, displayName, "E1");
        SetValue(cso, hrType, "FormalName", FormalName);
        SetValue(cso, hrType, "EmployeeType", "EXEC");

        await RunFullSyncAsync(hr, "Initial Full Sync");
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "precondition: HR Everyone projected Jane");

        return new Arrangement(await ReloadEntityAsync(hr), badge, cso, cso.MetaverseObjectId!.Value, mvDisplayName.Id, everyone.Id, executives.Id);
    }

    private async Task SetEmployeeTypeAsync(Arrangement ctx, string employeeType)
    {
        var cso = await ReloadEntityAsync(ctx.Cso);
        cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeType").StringValue = employeeType;
        await ModifyCsoAsync(cso);
    }

    private MetaverseObjectAttributeValue? DisplayNameValueOf(Arrangement ctx) =>
        SyncRepo.MetaverseObjects[ctx.MvoId].AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.DisplayNameAttributeId && !av.NullValue);

    private string? DisplayNameOf(Arrangement ctx) => DisplayNameValueOf(ctx)?.StringValue;

    private static void SetValue(ConnectedSystemObject cso, ConnectedSystemObjectType type, string attributeName, string value)
    {
        var attribute = type.Attributes.Single(a => a.Name == attributeName);
        cso.AttributeValues.RemoveAll(av => av.AttributeId == attribute.Id);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = attribute.Id,
            Attribute = attribute,
            StringValue = value,
            ConnectedSystemObject = cso
        });
    }

    private static void AddFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute target, int priority = int.MaxValue)
    {
        var source = csoType.Attributes.Single(a => a.Name == csAttributeName);
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            Priority = priority,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        });
    }

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem, string profileName = "Full Sync")
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, profileName, ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    private async Task<Activity> RunDeltaSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
        return activity;
    }
}
