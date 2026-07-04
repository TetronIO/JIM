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
/// Workflow tests for the priority-aware next-contributor recall fallback (#91, Phase 2c). When the winning
/// contributor's CSO is obsoleted and its value recalled, a still-joined lower-priority contributor must be
/// re-elected (its previously-suppressed value flows into the MVO) rather than the attribute being blanked.
///
/// Topology: HR Source (priority 1, recall enabled) and Training Source (priority 2) both contribute Description
/// to the same Person MVO. HR wins while joined; the priority gate suppresses Training's value. When HR's CSO is
/// obsoleted, the surviving Training contribution must take over.
/// </summary>
[TestFixture]
public class AttributePriorityRecallWorkflowTests : WorkflowTestBase
{
    private const string HrDescription = "HR Description";
    private const string TrainingDescription = "Training Description";
    private const string SharedEmployeeId = "EMP001";

    [Test]
    public async Task Recall_HigherPriorityContributorObsoleted_ReElectsLowerPrioritySurvivorAsync()
    {
        var ctx = await SetUpTwoContributorsToDescriptionAsync(hrDescriptionPriority: 1, trainingDescriptionPriority: 2);

        // HR projects and wins Description; Training joins but its lower-priority Description is suppressed.
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var description = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(description?.StringValue, Is.EqualTo(HrDescription), "HR (priority 1) should win Description while joined");

        // HR's CSO is obsoleted: its Description is recalled, and the surviving Training contribution must be re-elected.
        await MarkCsoObsoleteAsync(ctx.HrCso);
        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);

        // The re-election must not corrupt the run: no unhandled errors (the recall path swallows exceptions into
        // UnhandledError RPEIs, so a silent throw would otherwise leave the recall half-applied).
        Assert.That(deltaActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "re-election must complete without unhandled errors");

        mvo = SyncRepo.MetaverseObjects.Values.Single();
        var reElected = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(reElected, Is.Not.Null,
            "Description must not be blanked: the surviving Training contributor (priority 2) should be re-elected");
        Assert.That(reElected!.StringValue, Is.EqualTo(TrainingDescription),
            "the recalled Description should be replaced by the surviving Training value, not cleared");
        Assert.That(reElected.ContributedBySyncRuleId, Is.EqualTo(ctx.TrainingImportRuleId),
            "the re-elected value must carry the surviving Training rule's provenance");
    }

    [Test]
    public async Task Recall_ClearedAttributesWithNoSurvivor_EmitNoContributorSyncOutcomeAsync()
    {
        var ctx = await SetUpTwoContributorsToDescriptionAsync(hrDescriptionPriority: 1, trainingDescriptionPriority: 2);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        // HR leaves: Description is re-elected to the surviving Training source (a change of value, not a clear),
        // while the single-source DisplayName and EmployeeId have no surviving contributor and are genuinely
        // cleared. Those clears must surface as a NoContributor outcome on the disconnection RPEI.
        await MarkCsoObsoleteAsync(ctx.HrCso);
        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);

        var noContributorOutcomes = deltaActivity.RunProfileExecutionItems
            .SelectMany(r => r.SyncOutcomes)
            .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor)
            .ToList();
        Assert.That(noContributorOutcomes, Is.Not.Empty,
            "recalled attributes with no surviving contributor should emit a NoContributor sync outcome");
        Assert.That(noContributorOutcomes.Sum(o => o.DetailCount ?? 0), Is.EqualTo(2),
            "DisplayName and EmployeeId are cleared with no survivor; the re-elected Description must not be counted");
    }

    [Test]
    public async Task Recall_ReferenceAttributeWithSurvivor_ReElectsSurvivingReferenceAsync()
    {
        var ctx = await SetUpTwoContributorsToManagerReferenceAsync();

        // HR projects everyone and wins Manager (priority 1): John's Manager references Mary's MVO.
        await RunFullSyncAsync(ctx.Hr);
        var johnMvo = ctx.HrJohnCso.MetaverseObject;
        var maryMvoId = ctx.HrMaryCso.MetaverseObject?.Id;
        var bobMvoId = ctx.HrBobCso.MetaverseObject?.Id;
        Assert.That(johnMvo, Is.Not.Null, "HR full sync should project John's MVO");
        Assert.That(maryMvoId, Is.Not.Null, "HR full sync should project Mary's MVO");
        Assert.That(bobMvoId, Is.Not.Null, "HR full sync should project Bob's MVO");

        // Training joins on EmployeeId; its lower-priority Manager reference (to Bob's MVO) is suppressed.
        await RunFullSyncAsync(ctx.Training);
        var manager = johnMvo!.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvManagerAttributeId && !av.NullValue);
        Assert.That(GetReferencedMvoId(manager), Is.EqualTo(maryMvoId),
            "HR (priority 1) should win the Manager reference while joined");

        // HR's John CSO is obsoleted: the recalled Manager reference must be re-elected to the surviving
        // Training contribution in the recall pass, not left blank until a later sync of the Training system.
        await MarkCsoObsoleteAsync(ctx.HrJohnCso);
        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);
        Assert.That(deltaActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "reference re-election must complete without unhandled errors");

        johnMvo = SyncRepo.MetaverseObjects[johnMvo.Id];
        var reElected = johnMvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvManagerAttributeId && !av.NullValue);
        Assert.That(reElected, Is.Not.Null,
            "the Manager reference must not be blanked: the surviving Training contributor should be re-elected in the recall pass");
        Assert.That(GetReferencedMvoId(reElected), Is.EqualTo(bobMvoId),
            "the recalled Manager reference should be replaced by the surviving Training reference (Bob's MVO)");
        Assert.That(reElected!.ContributedBySyncRuleId, Is.EqualTo(ctx.TrainingImportRuleId),
            "the re-elected reference must carry the surviving Training rule's provenance");
    }

    [Test]
    public async Task Recall_SurvivorContributesIdenticalValue_ValueRetainedWithSurvivorProvenanceAsync()
    {
        // Both contributors agree on the Description value. When the winning HR contributor leaves, the value
        // must survive the recall (handed to the identical Training contribution with its provenance), not be
        // cleared because the survivor's value looked "already present" while the leaver's row awaited removal.
        const string sharedDescription = "Shared Description";
        var ctx = await SetUpTwoContributorsToDescriptionAsync(
            hrDescriptionPriority: 1, trainingDescriptionPriority: 2,
            hrDescriptionValue: sharedDescription, trainingDescriptionValue: sharedDescription);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        await MarkCsoObsoleteAsync(ctx.HrCso);
        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);
        Assert.That(deltaActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "identical-value re-election must complete without unhandled errors");

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var reElected = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(reElected, Is.Not.Null,
            "Description must not be blanked when the surviving contributor holds the identical value");
        Assert.That(reElected!.StringValue, Is.EqualTo(sharedDescription),
            "the identical surviving value should be retained");
        Assert.That(reElected.ContributedBySyncRuleId, Is.EqualTo(ctx.TrainingImportRuleId),
            "provenance must hand over to the surviving Training rule");
    }

    [Test]
    public async Task Recall_HigherPriorityObsoletedUnderGracePeriod_ReElectsSurvivorButFreezesSingleSourceAttributesAsync()
    {
        // A deletion grace period is configured. Today the grace period freezes all recall, so a multi-source
        // attribute would be left stale at the departed source's value. With per-attribute re-election: the
        // multi-source Description is handed to the surviving Training source, while single-source HR attributes
        // (no survivor) are still frozen (preserved) for the grace window rather than cleared.
        var ctx = await SetUpTwoContributorsToDescriptionAsync(
            hrDescriptionPriority: 1, trainingDescriptionPriority: 2, deletionGracePeriod: TimeSpan.FromDays(7));

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        await MarkCsoObsoleteAsync(ctx.HrCso);
        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);
        Assert.That(deltaActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "re-election under a grace period must complete without unhandled errors");

        var mvo = SyncRepo.MetaverseObjects.Values.Single();

        var description = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(description?.StringValue, Is.EqualTo(TrainingDescription),
            "the multi-source Description must be re-elected to the surviving Training source even under a grace period");

        var displayName = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDisplayNameAttributeId && !av.NullValue);
        Assert.That(displayName, Is.Not.Null,
            "single-source HR DisplayName has no surviving contributor, so it is frozen (preserved) for the grace window, not cleared");
    }

    private sealed record TwoContributorContext(
        ConnectedSystem Hr,
        ConnectedSystem Training,
        ConnectedSystemObject HrCso,
        int MvDescriptionAttributeId,
        int MvDisplayNameAttributeId,
        int TrainingImportRuleId);

    private sealed record ReferenceContributorContext(
        ConnectedSystem Hr,
        ConnectedSystem Training,
        ConnectedSystemObject HrJohnCso,
        ConnectedSystemObject HrMaryCso,
        ConnectedSystemObject HrBobCso,
        int MvManagerAttributeId,
        int TrainingImportRuleId);

    /// <summary>
    /// Resolves the Metaverse Object a reference attribute value points at, via the scalar FK or navigation.
    /// </summary>
    private static Guid? GetReferencedMvoId(MetaverseObjectAttributeValue? av)
        => av?.ReferenceValueId ?? av?.ReferenceValue?.Id;

    /// <summary>
    /// Builds a reference-attribute topology: HR (projects, contributes DisplayName/EmployeeId and a Manager
    /// reference at priority 1, recall enabled) and Training (joins on EmployeeId, contributes Manager from its
    /// Mentor reference at priority 2). HR's John references Mary; Training's John record references Bob's
    /// Training record, whose person is also projected by HR so both references resolve to Metaverse Objects.
    /// </summary>
    private async Task<ReferenceContributorContext> SetUpTwoContributorsToManagerReferenceAsync()
    {
        // --- HR source: primary, recall enabled, with a Manager reference attribute ---
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrManagerAttr = new ConnectedSystemObjectTypeAttribute { Name = "Manager", Type = AttributeDataType.Reference, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr, hrManagerAttr });
        hrType.RemoveContributedAttributesOnObsoletion = true;

        // --- Training source: supplemental, joins on EmployeeId, with a Mentor reference attribute ---
        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        trainingSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var trainingMentorAttr = new ConnectedSystemObjectTypeAttribute { Name = "Mentor", Type = AttributeDataType.Reference, Selected = true };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute> { trainingExternalIdAttr, trainingEmployeeIdAttr, trainingMentorAttr });
        trainingType.RemoveContributedAttributesOnObsoletion = true;

        // --- MV type with a single-valued Manager reference attribute ---
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvManagerAttr = new MetaverseAttribute
        {
            Name = "Manager",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvManagerAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvManagerAttr);

        // --- HR import rule: DisplayName, EmployeeId, Manager@1 ---
        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDisplayNameAttr, hrDisplayNameAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvEmployeeIdAttr, hrEmployeeIdAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvManagerAttr, hrManagerAttr, priority: 1));
        await DbContext.SaveChangesAsync();

        // --- Training import rule: Manager <- Mentor@2, join on EmployeeId ---
        var trainingImportRule = await CreateImportSyncRuleAsync(trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        trainingImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(trainingImportRule, mvManagerAttr, trainingMentorAttr, priority: 2));
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

        // --- HR CSOs: Mary and Bob (reference targets), John (subject, Manager -> Mary) ---
        var hrMaryCso = await CreateCsoAsync(hrSystem.Id, hrType, "Mary Manager", "EMP002");
        var hrBobCso = await CreateCsoAsync(hrSystem.Id, hrType, "Bob Mentor", "EMP003");
        var hrJohnCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrJohnCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrManagerAttr.Id, Attribute = hrManagerAttr,
            ReferenceValueId = hrMaryCso.Id, ReferenceValue = hrMaryCso, ConnectedSystemObject = hrJohnCso
        });

        // --- Training CSOs: Bob's record (reference target, joins Bob's MVO), John's record (Mentor -> Bob) ---
        var trainingBobCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", "EMP003");
        var trainingJohnCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", SharedEmployeeId);
        trainingJohnCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = trainingMentorAttr.Id, Attribute = trainingMentorAttr,
            ReferenceValueId = trainingBobCso.Id, ReferenceValue = trainingBobCso, ConnectedSystemObject = trainingJohnCso
        });

        return new ReferenceContributorContext(hrSystem, trainingSystem, hrJohnCso, hrMaryCso, hrBobCso, mvManagerAttr.Id, trainingImportRule.Id);
    }

    /// <summary>
    /// Builds the topology: HR (projects, contributes DisplayName/EmployeeId/Description at
    /// <paramref name="hrDescriptionPriority"/>, recall enabled) and Training (joins on EmployeeId, contributes
    /// Description at <paramref name="trainingDescriptionPriority"/>). Both source CSOs share an EmployeeId.
    /// </summary>
    private async Task<TwoContributorContext> SetUpTwoContributorsToDescriptionAsync(
        int hrDescriptionPriority, int trainingDescriptionPriority, TimeSpan? deletionGracePeriod = null,
        string hrDescriptionValue = HrDescription, string trainingDescriptionValue = TrainingDescription)
    {
        // --- HR source: primary, recall enabled ---
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "HrDescription", Type = AttributeDataType.Text, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr, hrDescriptionAttr });
        hrType.RemoveContributedAttributesOnObsoletion = true;

        // --- Training source: supplemental, joins on EmployeeId ---
        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        trainingSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var trainingDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "TrainingDescription", Type = AttributeDataType.Text, Selected = true };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute> { trainingExternalIdAttr, trainingEmployeeIdAttr, trainingDescriptionAttr });
        trainingType.RemoveContributedAttributesOnObsoletion = true;

        // --- MV type with Description (and an optional deletion grace period) ---
        var mvType = await CreateMvObjectTypeAsync("Person");
        if (deletionGracePeriod.HasValue)
            mvType.DeletionGracePeriod = deletionGracePeriod.Value;
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

        // --- HR import rule: DisplayName, EmployeeId, Description@hrDescriptionPriority ---
        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDisplayNameAttr, hrDisplayNameAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvEmployeeIdAttr, hrEmployeeIdAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDescriptionAttr, hrDescriptionAttr, hrDescriptionPriority));
        await DbContext.SaveChangesAsync();

        // --- Training import rule: Description@trainingDescriptionPriority, join on EmployeeId ---
        var trainingImportRule = await CreateImportSyncRuleAsync(trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        trainingImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(trainingImportRule, mvDescriptionAttr, trainingDescriptionAttr, trainingDescriptionPriority));
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

        // --- Source CSOs (sharing an EmployeeId so Training joins HR's projected MVO) ---
        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDescriptionAttr.Id, Attribute = hrDescriptionAttr, StringValue = hrDescriptionValue, ConnectedSystemObject = hrCso
        });

        var trainingCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", SharedEmployeeId);
        trainingCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = trainingDescriptionAttr.Id, Attribute = trainingDescriptionAttr, StringValue = trainingDescriptionValue, ConnectedSystemObject = trainingCso
        });

        return new TwoContributorContext(hrSystem, trainingSystem, hrCso, mvDescriptionAttr.Id, mvDisplayNameAttr.Id, trainingImportRule.Id);
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
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }

    private async Task<JIM.Models.Activities.Activity> RunDeltaSyncReturningActivityAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
        return activity;
    }

    private static Task MarkCsoObsoleteAsync(ConnectedSystemObject cso)
    {
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;
        return Task.CompletedTask;
    }
}
