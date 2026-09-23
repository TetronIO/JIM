// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for attribute priority while a Synchronisation Rule deletion is queued (#1597). Deleting a
/// rule that still contributes Metaverse attribute values disables it and queues a recall task that deletes it
/// as its final step. In that window an administrator tidying the attribute's priority order to the surviving
/// contributors must not be refused for omitting the rule they have just deleted.
/// </summary>
[TestFixture]
public class AttributePriorityPendingDeletionWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task SetAttributePriorityOrderAsync_OmitsRuleQueuedForDeletion_AcceptsSurvivorsOnlyOrderAsync()
    {
        var ctx = await SetUpThreeContributorsAsync();
        var deletion = await Jim.ConnectedSystems.DeleteSyncRuleAsync(ctx.HrRule, ctx.User);
        Assert.That(deletion.RecallQueued, Is.True, "precondition: the deletion must be queued behind a recall");

        // The administrator lists only the two survivors, in a new order.
        var result = await Jim.ConnectedSystems.SetAttributePriorityOrderAsync(ctx.MvTypeId, ctx.DescriptionAttributeId,
            [(ctx.PayrollMappingId, false), (ctx.TrainingMappingId, false)], ctx.User);

        Assert.That(result.Select(m => m.Id), Is.EqualTo(new[] { ctx.PayrollMappingId, ctx.TrainingMappingId, ctx.HrMappingId }),
            "the survivors take the order given, and the rule being deleted sits beneath them");
        Assert.That(await GetPersistedOrderAsync(ctx), Is.EqualTo(new[] { ctx.PayrollMappingId, ctx.TrainingMappingId, ctx.HrMappingId }),
            "the order must be persisted, not just returned");
    }

    [Test]
    public async Task SetAttributePriorityOrderAsync_IncludesRuleQueuedForDeletion_HonoursItsPositionAsync()
    {
        // The portal loads the list with the disabled rule in it and saves every row, as does a script that reads
        // the order and writes it back; that must keep working in the window, wherever the rule was placed.
        var ctx = await SetUpThreeContributorsAsync();
        await Jim.ConnectedSystems.DeleteSyncRuleAsync(ctx.HrRule, ctx.User);

        var result = await Jim.ConnectedSystems.SetAttributePriorityOrderAsync(ctx.MvTypeId, ctx.DescriptionAttributeId,
            [(ctx.PayrollMappingId, false), (ctx.HrMappingId, false), (ctx.TrainingMappingId, false)], ctx.User);

        Assert.That(result.Select(m => m.Id), Is.EqualTo(new[] { ctx.PayrollMappingId, ctx.HrMappingId, ctx.TrainingMappingId }));
    }

    [Test]
    public async Task SetAttributePriorityOrderAsync_OmitsLiveContributor_ThrowsNamingTheMissingMappingAsync()
    {
        // Only a rule whose deletion is queued may be left out; omitting a live contributor is still refused, and the
        // refusal says which mapping is missing rather than leaving the administrator to diff the lists.
        var ctx = await SetUpThreeContributorsAsync();
        await Jim.ConnectedSystems.DeleteSyncRuleAsync(ctx.HrRule, ctx.User);

        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await Jim.ConnectedSystems.SetAttributePriorityOrderAsync(ctx.MvTypeId, ctx.DescriptionAttributeId,
                [(ctx.PayrollMappingId, false)], ctx.User));

        Assert.That(ex!.Message, Does.Contain($"{ctx.TrainingMappingId}").And.Contain("Training Import"));
        Assert.That(ex.Message, Does.Not.Contain("HR Import"),
            "the rule being deleted was allowed to be left out, so it must not be reported as missing");
    }

    [Test]
    public async Task SetAttributePriorityOrderAsync_RecallNoLongerQueued_RequiresTheRuleAgainAsync()
    {
        // A worker task row is removed when the recall completes AND when it fails; after a failure the rule survives,
        // still disabled with its deletion-in-progress reason, and is an ordinary contributor again until the deletion
        // is retried. The queued task, not the disabled reason, is what makes it optional.
        var ctx = await SetUpThreeContributorsAsync();
        await Jim.ConnectedSystems.DeleteSyncRuleAsync(ctx.HrRule, ctx.User);
        DbContext.DeleteSyncRuleWorkerTasks.RemoveRange(DbContext.DeleteSyncRuleWorkerTasks);
        await DbContext.SaveChangesAsync();

        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await Jim.ConnectedSystems.SetAttributePriorityOrderAsync(ctx.MvTypeId, ctx.DescriptionAttributeId,
                [(ctx.PayrollMappingId, false), (ctx.TrainingMappingId, false)], ctx.User));

        Assert.That(ex!.Message, Does.Contain("HR Import"));
    }

    [Test]
    public async Task DeleteSyncRuleAsync_RecallQueued_MovesTheRulesMappingsToTheBottomOfThePriorityOrderAsync()
    {
        // Once its deletion is queued the rule is disabled, and a disabled rule's mappings take no part in priority
        // resolution, so moving them to the bottom changes no value. It does mean the survivors hold positions 1..N
        // as the administrator now sees them, for reads, the portal and positional moves alike.
        var ctx = await SetUpThreeContributorsAsync();

        await Jim.ConnectedSystems.DeleteSyncRuleAsync(ctx.HrRule, ctx.User);

        var order = await Jim.ConnectedSystems.GetAttributePriorityOrderAsync(ctx.MvTypeId, ctx.DescriptionAttributeId);
        Assert.That(order.Select(m => m.Id), Is.EqualTo(new[] { ctx.TrainingMappingId, ctx.PayrollMappingId, ctx.HrMappingId }),
            "the survivors keep their relative order and the rule being deleted drops beneath them");
        Assert.That(order.Select(m => m.Priority), Is.EqualTo(new[] { 1, 2, 3 }), "the order stays dense");
    }

    [Test]
    public async Task MoveAttributePriorityAsync_AfterRecallQueued_PositionsCountTheSurvivorsFirstAsync()
    {
        // Survivors are Training (1) and Payroll (2). "Put Training at position 2" must swap them; were the rule being
        // deleted still at the top it would already be at position 2 and the move would silently do nothing.
        var ctx = await SetUpThreeContributorsAsync();
        await Jim.ConnectedSystems.DeleteSyncRuleAsync(ctx.HrRule, ctx.User);

        var result = await Jim.ConnectedSystems.MoveAttributePriorityAsync(ctx.MvTypeId, ctx.DescriptionAttributeId,
            ctx.TrainingMappingId, targetPosition: 2, nullIsValue: null, ctx.User);

        Assert.That(result.Select(m => m.Id), Is.EqualTo(new[] { ctx.PayrollMappingId, ctx.TrainingMappingId, ctx.HrMappingId }));
    }

    private async Task<int[]> GetPersistedOrderAsync(ThreeContributorContext ctx)
    {
        var order = await Jim.ConnectedSystems.GetAttributePriorityOrderAsync(ctx.MvTypeId, ctx.DescriptionAttributeId);
        return order.Select(m => m.Id).ToArray();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Topology
    // -----------------------------------------------------------------------------------------------------------------

    private sealed record ThreeContributorContext(
        MetaverseObject User,
        int MvTypeId,
        int DescriptionAttributeId,
        SyncRule HrRule,
        int HrMappingId,
        int TrainingMappingId,
        int PayrollMappingId);

    /// <summary>
    /// Three import Synchronisation Rules (HR, Training, Payroll) contributing Description at priorities 1, 2 and 3.
    /// HR has a contributed value on one Metaverse Object, so deleting it queues a recall rather than deleting
    /// synchronously.
    /// </summary>
    private async Task<ThreeContributorContext> SetUpThreeContributorsAsync()
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDescriptionAttr = new MetaverseAttribute
        {
            Name = "Description",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvDescriptionAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvDescriptionAttr);

        var (hrSystem, hrRule, hrMapping) = await AddDescriptionContributorAsync("HR", mvType, mvDescriptionAttr, priority: 1);
        var (_, _, trainingMapping) = await AddDescriptionContributorAsync("Training", mvType, mvDescriptionAttr, priority: 2);
        var (_, _, payrollMapping) = await AddDescriptionContributorAsync("Payroll", mvType, mvDescriptionAttr, priority: 3);

        var user = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow, CachedDisplayName = "Test Administrator" };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow };
        DbContext.MetaverseObjects.AddRange(user, mvo);
        DbContext.MetaverseObjectAttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = mvDescriptionAttr,
            AttributeId = mvDescriptionAttr.Id,
            StringValue = "HR Description",
            ContributedBySyncRuleId = hrRule.Id,
            ContributedBySystemId = hrSystem.Id
        });
        await DbContext.SaveChangesAsync();

        return new ThreeContributorContext(user, mvType.Id, mvDescriptionAttr.Id, hrRule, hrMapping.Id, trainingMapping.Id, payrollMapping.Id);
    }

    private async Task<(ConnectedSystem System, SyncRule Rule, SyncRuleMapping Mapping)> AddDescriptionContributorAsync(
        string name, MetaverseObjectType mvType, MetaverseAttribute mvDescriptionAttr, int priority)
    {
        var system = await CreateConnectedSystemAsync($"{name} Source");
        var descriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = $"{name}Description", Type = AttributeDataType.Text, Selected = true };
        var csoType = await CreateCsoTypeAsync(system.Id, $"{name}User", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            descriptionAttr
        });

        var rule = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, $"{name} Import");
        var mapping = new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            Priority = priority,
            TargetMetaverseAttribute = mvDescriptionAttr,
            TargetMetaverseAttributeId = mvDescriptionAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = descriptionAttr, ConnectedSystemAttributeId = descriptionAttr.Id } }
        };
        rule.AttributeFlowRules.Add(mapping);
        await DbContext.SaveChangesAsync();

        return (system, rule, mapping);
    }
}
