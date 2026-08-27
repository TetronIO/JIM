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
/// Workflow tests for issue #1533: deleting (or disabling, #1485) an import Attribute Flow mapping must not
/// leave the Metaverse values it contributed in place forever. A Full Synchronisation of the contributing
/// Connected System re-evaluates every joined Connected System Object; when the value's mapping no longer
/// exists in the priority contributor cache, the value is recalled and the next surviving contributor is
/// re-elected in the same run, or the attribute is genuinely cleared (a NoContributor outcome) when no
/// contributor survives. This mirrors the disabled-rule posture Scenario 14's DisabledRuleNoOpinion proves.
/// </summary>
[TestFixture]
public class RemovedMappingRecallWorkflowTests : WorkflowTestBase
{
    private const string HrDescription = "HR Description";
    private const string TrainingDescription = "Training Description";
    private const string SharedEmployeeId = "EMP001";

    [Test]
    public async Task FullSync_MappingDeleted_SoleContributor_RecallsValueAsync()
    {
        // The issue's exact repro shape: one import rule flows Description; the mapping is deleted; a Full
        // Synchronisation of the contributing system must recall the value rather than leave it dangling.
        var ctx = await SetUpSoleContributorAsync();

        await RunFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription),
            "Description should flow while the mapping exists");

        // Delete the Description mapping, as Remove-JIMSyncRuleMapping / the REST delete would.
        DeleteMappingFromRule(ctx.HrImportRule, ctx.MvDescriptionAttributeId);

        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);
        Assert.That(activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "the recall must complete without unhandled errors");

        mvo = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId), Is.Null,
                "the deleted mapping's Description value must be recalled by the contributing system's Full Synchronisation");
            Assert.That(GetAttributeValue(mvo, ctx.MvDisplayNameAttributeId), Is.Not.Null,
                "the surviving DisplayName mapping's value must be untouched (control)");
        }

        // The clear must be observable: no surviving contributor means a NoContributor outcome.
        var noContributorDetailCount = activity.RunProfileExecutionItems
            .SelectMany(r => r.SyncOutcomes)
            .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor)
            .Sum(o => o.DetailCount ?? 0);
        Assert.That(noContributorDetailCount, Is.EqualTo(1),
            "the cleared Description must surface as a NoContributor outcome");
    }

    [Test]
    public async Task FullSync_MappingDeleted_WithSurvivor_ReElectsNextContributorAsync()
    {
        // Two systems contribute Description (HR at priority 1, Training at priority 2). Deleting HR's mapping
        // and running a Full Synchronisation of HR must hand the attribute to the surviving Training
        // contributor in the same run, not blank it until Training next synchronises.
        var ctx = await SetUpTwoContributorsAsync();

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription),
            "HR (priority 1) should win Description while its mapping exists");

        DeleteMappingFromRule(ctx.HrImportRule, ctx.MvDescriptionAttributeId);

        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);
        Assert.That(activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "the recall and re-election must complete without unhandled errors");

        mvo = SyncRepo.MetaverseObjects.Values.Single();
        var reElected = GetAttributeValue(mvo, ctx.MvDescriptionAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reElected, Is.Not.Null,
                "Description must not be blanked: the surviving Training contributor should be re-elected in the same run");
            Assert.That(reElected!.StringValue, Is.EqualTo(TrainingDescription),
                "the recalled Description should be replaced by the surviving Training value");
            Assert.That(reElected!.ContributedBySyncRuleId, Is.EqualTo(ctx.TrainingImportRuleId),
                "the re-elected value must carry the surviving Training rule's provenance");
        }

        var noContributorDetailCount = activity.RunProfileExecutionItems
            .SelectMany(r => r.SyncOutcomes)
            .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor)
            .Sum(o => o.DetailCount ?? 0);
        Assert.That(noContributorDetailCount, Is.EqualTo(0),
            "a re-elected attribute must not be reported as cleared with no contributor");
    }

    [Test]
    public async Task FullSync_MappingDisabled_SoleContributor_RecallsValueAsync()
    {
        // The sibling case (#1485): the mapping is disabled via its own Enabled flag while the rule stays
        // enabled. Its previous contribution must be recalled exactly like a deleted mapping's, because a
        // disabled mapping contributes nothing and leaves the contributor cache.
        var ctx = await SetUpSoleContributorAsync();

        await RunFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription),
            "Description should flow while the mapping is enabled");

        // Disable the mapping, as Set-JIMSyncRuleMapping -Enabled:$false would (the audited update path
        // stamps the mapping's LastUpdated, advancing the configuration watermark).
        var mapping = ctx.HrImportRule.AttributeFlowRules.Single(m => m.TargetMetaverseAttributeId == ctx.MvDescriptionAttributeId);
        mapping.Enabled = false;
        mapping.LastUpdated = DateTime.UtcNow;

        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);
        Assert.That(activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "the recall must complete without unhandled errors");

        mvo = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId), Is.Null,
                "the disabled mapping's Description value must be recalled by the contributing system's Full Synchronisation");
            Assert.That(GetAttributeValue(mvo, ctx.MvDisplayNameAttributeId), Is.Not.Null,
                "the enabled DisplayName mapping's value must be untouched (control)");
        }
    }

    [Test]
    public async Task FullSync_MappingReCreated_ValueFlowsAgainAsync()
    {
        // Round trip: after a recall, re-creating an equivalent mapping and running a Full Synchronisation
        // must restore the value. This guards against the recall over-reaching (e.g. marking the object
        // unprocessable or leaving state that blocks a later flow).
        var ctx = await SetUpSoleContributorAsync();

        await RunFullSyncAsync(ctx.Hr);
        var deletedMapping = DeleteMappingFromRule(ctx.HrImportRule, ctx.MvDescriptionAttributeId);
        await RunFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId), Is.Null, "the recall should have cleared Description");

        // Re-create the mapping (a new row, as the portal or New-JIMSyncRuleMapping would create).
        ctx.HrImportRule.AttributeFlowRules.Add(deletedMapping);
        ctx.HrImportRule.LastUpdated = DateTime.UtcNow;

        await RunFullSyncAsync(ctx.Hr);

        mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription),
            "re-creating the mapping must restore the value on the next Full Synchronisation");
    }

    [Test]
    public async Task DeleteSyncRuleMapping_StampsParentRuleSoConfigurationWatermarkAdvancesAsync()
    {
        // Deleting a mapping removes the only row carrying its timestamps, so without a stamp on the parent
        // rule the configuration watermark (GetLatestSyncRuleConfigurationChangeAsync) never advances and the
        // next Full Synchronisation keeps its unchanged-object optimisation on, skipping the very objects the
        // recall must visit (#1533).
        var system = await CreateConnectedSystemAsync("HR");
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var descriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "Description", Type = AttributeDataType.Text, Selected = true };
        var csoType = await CreateCsoTypeAsync(system.Id, "User",
            new List<ConnectedSystemObjectTypeAttribute> { externalIdAttr, descriptionAttr });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");

        var rule = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, "HR Import");
        var mapping = new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            Priority = 1,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = descriptionAttr, ConnectedSystemAttributeId = descriptionAttr.Id } }
        };
        rule.AttributeFlowRules.Add(mapping);
        await DbContext.SaveChangesAsync();

        Assert.That(rule.LastUpdated, Is.Null, "precondition: the rule has never been updated");
        var beforeDelete = DateTime.UtcNow;

        await Repository.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping);

        var persistedRule = await DbContext.SyncRules.FindAsync(rule.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedRule, Is.Not.Null);
            Assert.That(persistedRule!.LastUpdated, Is.Not.Null,
                "deleting a mapping must stamp the parent Synchronisation Rule so the configuration watermark advances");
            Assert.That(persistedRule!.LastUpdated!.Value, Is.GreaterThanOrEqualTo(beforeDelete),
                "the stamp must be the deletion time");
        }
    }

    private sealed record SoleContributorContext(
        ConnectedSystem Hr,
        SyncRule HrImportRule,
        ConnectedSystemObject HrCso,
        int MvDescriptionAttributeId,
        int MvDisplayNameAttributeId);

    private sealed record TwoContributorContext(
        ConnectedSystem Hr,
        ConnectedSystem Training,
        SyncRule HrImportRule,
        ConnectedSystemObject HrCso,
        int MvDescriptionAttributeId,
        int MvDisplayNameAttributeId,
        int TrainingImportRuleId);

    private static MetaverseObjectAttributeValue? GetAttributeValue(MetaverseObject mvo, int attributeId) =>
        mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == attributeId && !av.NullValue);

    /// <summary>
    /// Simulates an administrator deleting a mapping (Remove-JIMSyncRuleMapping / the REST delete) against the
    /// shared in-memory rule store the processors read. The mapping and its sources are detached from the EF
    /// change tracker first, because merely removing a still-tracked mapping from the rule's collection lets
    /// EF's navigation fix-up quietly re-attach it on the next SaveChanges; a real deletion removes the row, so
    /// nothing can come back. The rule's LastUpdated stamp mirrors what the repository's
    /// DeleteSyncRuleMappingAsync writes so the configuration watermark advances (#1533); that repository
    /// behaviour is covered directly by DeleteSyncRuleMapping_StampsParentRuleSoConfigurationWatermarkAdvancesAsync.
    /// </summary>
    private SyncRuleMapping DeleteMappingFromRule(SyncRule rule, int targetMetaverseAttributeId)
    {
        var mapping = rule.AttributeFlowRules.Single(m => m.TargetMetaverseAttributeId == targetMetaverseAttributeId);
        foreach (var source in mapping.Sources)
            DbContext.Entry(source).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        DbContext.Entry(mapping).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        rule.AttributeFlowRules.Remove(mapping);
        rule.LastUpdated = DateTime.UtcNow;
        return mapping;
    }

    /// <summary>
    /// Builds the sole-contributor topology: HR projects and flows DisplayName, EmployeeId and Description
    /// from its own attributes. No other system contributes Description.
    /// </summary>
    private async Task<SoleContributorContext> SetUpSoleContributorAsync()
    {
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "HrDescription", Type = AttributeDataType.Text, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr, hrDescriptionAttr });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
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

        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDisplayNameAttr, hrDisplayNameAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvEmployeeIdAttr, hrEmployeeIdAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDescriptionAttr, hrDescriptionAttr, priority: 1));
        await DbContext.SaveChangesAsync();

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDescriptionAttr.Id, Attribute = hrDescriptionAttr, StringValue = HrDescription, ConnectedSystemObject = hrCso
        });

        return new SoleContributorContext(hrSystem, hrImportRule, hrCso, mvDescriptionAttr.Id, mvDisplayNameAttr.Id);
    }

    /// <summary>
    /// Builds the two-contributor topology: HR (projects, Description at priority 1) and Training (joins on
    /// EmployeeId, Description at priority 2), matching the shape AttributePriorityRecallWorkflowTests uses.
    /// </summary>
    private async Task<TwoContributorContext> SetUpTwoContributorsAsync()
    {
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "HrDescription", Type = AttributeDataType.Text, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr, hrDescriptionAttr });

        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        trainingSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var trainingDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "TrainingDescription", Type = AttributeDataType.Text, Selected = true };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute> { trainingExternalIdAttr, trainingEmployeeIdAttr, trainingDescriptionAttr });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
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

        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDisplayNameAttr, hrDisplayNameAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvEmployeeIdAttr, hrEmployeeIdAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDescriptionAttr, hrDescriptionAttr, priority: 1));
        await DbContext.SaveChangesAsync();

        var trainingImportRule = await CreateImportSyncRuleAsync(trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        trainingImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(trainingImportRule, mvDescriptionAttr, trainingDescriptionAttr, priority: 2));
        trainingImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = trainingImportRule,
            SyncRuleId = trainingImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new() { Order = 0, ConnectedSystemAttribute = trainingEmployeeIdAttr, ConnectedSystemAttributeId = trainingEmployeeIdAttr.Id }
            }
        });
        await DbContext.SaveChangesAsync();

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDescriptionAttr.Id, Attribute = hrDescriptionAttr, StringValue = HrDescription, ConnectedSystemObject = hrCso
        });

        var trainingCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", SharedEmployeeId);
        trainingCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = trainingDescriptionAttr.Id, Attribute = trainingDescriptionAttr, StringValue = TrainingDescription, ConnectedSystemObject = trainingCso
        });

        return new TwoContributorContext(hrSystem, trainingSystem, hrImportRule, hrCso, mvDescriptionAttr.Id, mvDisplayNameAttr.Id, trainingImportRule.Id);
    }

    private static SyncRuleMapping BuildDirectImportMapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source, int priority = int.MaxValue)
    {
        return new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            Priority = priority,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        };
    }

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        await RunFullSyncReturningActivityAsync(connectedSystem);
    }

    private async Task<Activity> RunFullSyncReturningActivityAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
