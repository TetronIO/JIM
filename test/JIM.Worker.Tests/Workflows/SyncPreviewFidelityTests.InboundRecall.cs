// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Sync Preview fidelity on the in-scope inbound path for the two steps the run takes after the ordinary Attribute Flow
/// pass (#1899): the orphaned-contribution recall (a value whose Attribute Flow mapping no longer exists is recalled,
/// #1533) and the withdrawal re-election (a withdrawn value hands the attribute to the next surviving contributor, #91).
/// Both change Metaverse values, so a preview that skips them disagrees with the run about the values, the exports and
/// anything derived from them. Each test previews first and then runs the real synchronisation over the same data.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    private const string RecallHrDescription = "HR Description";
    private const string RecallTrainingDescription = "Training Description";
    private const string RecallSummaryExpression = "mv[\"Description\"] + \" (summary)\"";

    [Test]
    public async Task PreviewSyncForCsoAsync_WinnerWithdrawsItsValueWithASurvivor_ShowsTheSurvivorsValueAsTheRunWritesAsync()
    {
        var ctx = await SetUpInboundRecallAsync();
        await SynchroniseInboundRecallAsync(ctx);
        await WithdrawHrDescriptionAsync(ctx);
        var training = await ReloadEntityAsync(ctx.TrainingCso!);
        var trainingLinkBefore = training.MetaverseObject;

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);
        var trainingLinkAfterPreview = training.MetaverseObject;
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DescriptionHeld(ctx), Is.EqualTo(RecallTrainingDescription), "arrange: the run hands Description to Training");
            Assert.That(PreviewedValue(preview, "Description"), Is.EqualTo(RecallTrainingDescription),
                "the preview shows the surviving contributor's value, as the run writes it");
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
            Assert.That(trainingLinkAfterPreview, Is.SameAs(trainingLinkBefore),
                "the preview re-flows the survivor against its own copy of the Metaverse Object, and must leave the survivor as it found it");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_WinnerWithdrawsAnInputToADerivedFlow_DerivesFromTheSurvivorsValueAsTheRunDoesAsync()
    {
        var ctx = await SetUpInboundRecallAsync(withDerivedSummary: true);
        await SynchroniseInboundRecallAsync(ctx);
        await WithdrawHrDescriptionAsync(ctx);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ValueHeld(ctx.MvSummaryAttributeId!.Value), Is.EqualTo(RecallTrainingDescription + " (summary)"),
                "arrange: the run derives Summary from the re-elected Description");
            Assert.That(PreviewedValue(preview, "Summary"), Is.EqualTo(RecallTrainingDescription + " (summary)"),
                "the re-election runs before the derived levels, so the derived flow reads the survivor's value");
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_MappingDeletedForASoleContributor_ShowsTheRecallAsTheRunMakesItAsync()
    {
        var ctx = await SetUpInboundRecallAsync(seedTraining: false);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));
        DeleteHrDescriptionMapping(ctx);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DescriptionHeld(ctx), Is.Null, "arrange: the run recalls the orphaned Description");
            Assert.That(preview.Inbound!.AttributeFlowChanges.Any(change => !change.IsAddition && change.AttributeName == "Description"),
                Is.True, "the preview shows the orphaned value being recalled");
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))),
                "the recall clears an attribute nothing else contributes, which the run records as No Contributor");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_MappingDeletedWithASurvivor_ShowsTheSurvivorTakingOverAsTheRunDoesAsync()
    {
        var ctx = await SetUpInboundRecallAsync();
        await SynchroniseInboundRecallAsync(ctx);
        DeleteHrDescriptionMapping(ctx);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DescriptionHeld(ctx), Is.EqualTo(RecallTrainingDescription), "arrange: the run recalls HR's value and re-elects Training's");
            Assert.That(PreviewedValue(preview, "Description"), Is.EqualTo(RecallTrainingDescription),
                "the recall feeds the re-election, so the preview shows Training's value taking over");
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_WinnerWhoseNullIsAValueWithdrawsItsValue_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        var ctx = await SetUpInboundRecallAsync(hrNullIsValue: true);
        await SynchroniseInboundRecallAsync(ctx);
        await WithdrawHrDescriptionAsync(ctx);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DescriptionHeld(ctx), Is.Null, "arrange: HR asserts the blank, so nothing is re-elected");
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))),
                "the run records the asserted null on the object's outcome, so the preview must too");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_SurvivorsExpressionFailsTheObject_ReportsTheRunsFailureAloneAsync()
    {
        // Training's Expression fails the object when its input is missing. Its object has lost that value since it last
        // synchronised, so handing it HR's withdrawn Description fails HR's object in the run, and the run discards
        // everything that object's synchronisation would have done.
        var ctx = await SetUpInboundRecallAsync(trainingFailsWhenItsValueIsMissing: true);
        await SynchroniseInboundRecallAsync(ctx);
        var training = await ReloadEntityAsync(ctx.TrainingCso!);
        training.AttributeValues.RemoveAll(av => av.Attribute?.Name == "TrainingDescription");
        await WithdrawHrDescriptionAsync(ctx);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Select(rpei => rpei.ErrorType),
                Does.Contain(ActivityRunProfileExecutionItemErrorType.ExpressionMissingInput), "arrange: the run fails HR's object");
            Assert.That(preview.Errors.Select(error => error.Code), Is.EqualTo(new[] { SyncPreviewMessageCode.ExpressionEvaluationError }),
                "the preview reports the failure, blocking");
            Assert.That(preview.Inbound!.AttributeFlowChanges, Is.Empty, "and proposes nothing else for an object the run fails");
            Assert.That(preview.OutcomeTree, Is.Empty);
        }
    }

    [Test]
    public async Task PreviewSyncForCsosAsync_WinnerWithdrawsItsValueWithASurvivor_ShowsTheSurvivorsValueAsync()
    {
        // The configuration change previews ask this path of a baseline and a proposal; a withdrawal there hands the
        // attribute to the survivor in the next synchronisation just as it does in a run's own preview.
        var ctx = await SetUpInboundRecallAsync();
        await SynchroniseInboundRecallAsync(ctx);
        await WithdrawHrDescriptionAsync(ctx);

        var previews = await Jim.SyncPreview.PreviewSyncForCsosAsync(ctx.Hr.Id, [ctx.HrCso.Id]);

        Assert.That(PreviewedValue(previews[ctx.HrCso.Id], "Description"), Is.EqualTo(RecallTrainingDescription));
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ObjectLeavingScopeWithASurvivor_LeavesTheSurvivorAsItFoundItAsync()
    {
        // The out-of-scope cascade re-elects too, and the survivors it re-flows are the repository's own instances
        // (change-tracked on the worker's context, where configuration change previews run). Bound to the preview's
        // copy of the Metaverse Object, a later save on that context would find the copy through them.
        var ctx = await SetUpInboundRecallAsync(scopedHr: true, recallWhenHrLeaves: true);
        await SynchroniseInboundRecallAsync(ctx);
        var training = await ReloadEntityAsync(ctx.TrainingCso!);
        var trainingLinkBefore = training.MetaverseObject;
        var trainingValuesBefore = training.AttributeValues;
        var hr = await ReloadEntityAsync(ctx.HrCso);
        hr.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId").StringValue = "OUT_OF_SCOPE";
        hr.LastUpdated = DateTime.UtcNow;

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PreviewedValue(preview, "Description"), Is.EqualTo(RecallTrainingDescription),
                "arrange: the cascade re-elects Training's Description");
            Assert.That(training.MetaverseObject, Is.SameAs(trainingLinkBefore),
                "the survivor must not be left bound to the preview's copy of the Metaverse Object");
            Assert.That(training.AttributeValues, Is.SameAs(trainingValuesBefore));
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_ObsoleteObjectWithASurvivor_LeavesTheSurvivorAsItFoundItAsync()
    {
        // The obsolete object's teardown re-elects through the same core, against the preview's copy of the Metaverse
        // Object, so it must leave the survivor as it found it too.
        var ctx = await SetUpInboundRecallAsync(recallWhenHrLeaves: true);
        await SynchroniseInboundRecallAsync(ctx);
        var training = await ReloadEntityAsync(ctx.TrainingCso!);
        var trainingLinkBefore = training.MetaverseObject;
        var hr = await ReloadEntityAsync(ctx.HrCso);
        hr.Status = ConnectedSystemObjectStatus.Obsolete;
        hr.LastUpdated = DateTime.UtcNow;

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Hr.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.ObsoleteObjectCount, Is.EqualTo(1), "arrange: the walk tears HR's obsolete object down");
            Assert.That(training.MetaverseObject, Is.SameAs(trainingLinkBefore),
                "the survivor must not be left bound to the preview's copy of the Metaverse Object");
        }
    }

    private sealed record InboundRecallContext(
        ConnectedSystem Hr,
        ConnectedSystem Training,
        SyncRule HrImportRule,
        ConnectedSystemObject HrCso,
        ConnectedSystemObject? TrainingCso,
        int MvDescriptionAttributeId,
        int? MvSummaryAttributeId);

    /// <summary>
    /// HR projects John Smith and contributes his Description at priority 1; Training joins him on Employee Id and
    /// contributes its own Description at priority 2, so HR's value holds the attribute while both are joined.
    /// </summary>
    /// <param name="seedTraining">False leaves Training with its rule but no object, so HR is the sole contributor.</param>
    /// <param name="withDerivedSummary">Adds a Summary that HR's rule derives from Description (#1750).</param>
    /// <param name="hrNullIsValue">HR's Description mapping treats a missing value as an asserted null.</param>
    /// <param name="scopedHr">HR's rule only takes an Employee Id other than OUT_OF_SCOPE.</param>
    /// <param name="recallWhenHrLeaves">HR's objects recall what they contributed when they leave scope or are deleted.</param>
    /// <param name="trainingFailsWhenItsValueIsMissing">Training contributes Description through an Expression whose
    /// Missing Input Behaviour fails the object.</param>
    private async Task<InboundRecallContext> SetUpInboundRecallAsync(
        bool seedTraining = true,
        bool withDerivedSummary = false,
        bool hrNullIsValue = false,
        bool scopedHr = false,
        bool recallWhenHrLeaves = false,
        bool trainingFailsWhenItsValueIsMissing = false)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDescription = await AddDerivedMvAttributeAsync(mvType, "Description");
        var mvSummary = withDerivedSummary ? await AddDerivedMvAttributeAsync(mvType, "Summary") : null;

        var hr = await CreateConnectedSystemAsync("HR Source");
        var hrType = await CreateCsoTypeAsync(hr.Id, "HrUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "HrDescription", Type = AttributeDataType.Text, Selected = true }
        });
        hrType.RemoveContributedAttributesOnObsoletion = recallWhenHrLeaves;
        var hrEmployeeId = hrType.Attributes.Single(a => a.Name == "EmployeeId");
        var hrDescription = hrType.Attributes.Single(a => a.Name == "HrDescription");

        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        FlowFromAttribute(hrImport, mvDisplayName, hrType.Attributes.Single(a => a.Name == "DisplayName"));
        FlowFromAttribute(hrImport, mvEmployeeId, hrEmployeeId);
        var hrDescriptionMapping = FlowFromAttribute(hrImport, mvDescription, hrDescription);
        hrDescriptionMapping.Priority = 1;
        hrDescriptionMapping.NullIsValue = hrNullIsValue;
        if (mvSummary != null)
            FlowFromExpression(hrImport, mvSummary, RecallSummaryExpression);
        if (scopedHr)
        {
            hrImport.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria =
                [
                    new SyncRuleScopingCriteria
                    {
                        ConnectedSystemAttribute = hrEmployeeId,
                        ComparisonType = SearchComparisonType.NotEquals,
                        StringValue = "OUT_OF_SCOPE",
                        CaseSensitive = true
                    }
                ]
            });
        }
        await DbContext.SaveChangesAsync();

        var training = await CreateConnectedSystemAsync("Training Source");
        var trainingType = await CreateCsoTypeAsync(training.Id, "TrainingRecord", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "TrainingDescription", Type = AttributeDataType.Text, Selected = true }
        });
        var trainingEmployeeId = trainingType.Attributes.Single(a => a.Name == "EmployeeId");
        var trainingDescription = trainingType.Attributes.Single(a => a.Name == "TrainingDescription");

        var trainingImport = await CreateImportSyncRuleAsync(training.Id, trainingType, mvType, "Training Import", enableProjection: false);
        if (trainingFailsWhenItsValueIsMissing)
        {
            var failing = FlowFromExpression(trainingImport, mvDescription, "cs[\"TrainingDescription\"]");
            failing.Priority = 2;
            failing.Sources.Single().MissingInputBehaviour = MissingInputBehaviour.FailObject;
        }
        else
        {
            FlowFromAttribute(trainingImport, mvDescription, trainingDescription).Priority = 2;
        }
        trainingImport.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = trainingImport,
            SyncRuleId = trainingImport.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeId,
            TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = trainingEmployeeId, ConnectedSystemAttributeId = trainingEmployeeId.Id }]
        });
        await DbContext.SaveChangesAsync();

        var hrCso = await CreateCsoAsync(hr.Id, hrType, "John Smith", "EMP001");
        SetDerivedText(hrCso, hrDescription, RecallHrDescription);

        ConnectedSystemObject? trainingCso = null;
        if (seedTraining)
        {
            trainingCso = await CreateCsoAsync(training.Id, trainingType, "unused", "EMP001");
            SetDerivedText(trainingCso, trainingDescription, RecallTrainingDescription);
        }

        return new InboundRecallContext(hr, training, hrImport, hrCso, trainingCso, mvDescription.Id, mvSummary?.Id);
    }

    /// <summary>
    /// HR projects and wins Description; Training joins, its lower-priority Description suppressed.
    /// </summary>
    private async Task SynchroniseInboundRecallAsync(InboundRecallContext ctx)
    {
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Training));
        Assert.That(DescriptionHeld(ctx), Is.EqualTo(RecallHrDescription), "arrange: HR (priority 1) holds Description while both are joined");
    }

    /// <summary>
    /// HR stays joined but stops supplying its Description, without asserting a null of its own.
    /// </summary>
    private async Task WithdrawHrDescriptionAsync(InboundRecallContext ctx)
    {
        var hrCso = await ReloadEntityAsync(ctx.HrCso);
        hrCso.AttributeValues.RemoveAll(av => av.Attribute?.Name == "HrDescription");
        hrCso.LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// Deletes HR's Description mapping as the REST delete would, stamping the rule so the configuration watermark
    /// advances and the next Full Synchronisation re-evaluates every object.
    /// </summary>
    private void DeleteHrDescriptionMapping(InboundRecallContext ctx)
    {
        var mapping = ctx.HrImportRule.AttributeFlowRules.Single(m => m.TargetMetaverseAttributeId == ctx.MvDescriptionAttributeId);
        foreach (var source in mapping.Sources)
            DbContext.Entry(source).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        DbContext.Entry(mapping).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        ctx.HrImportRule.AttributeFlowRules.Remove(mapping);
        ctx.HrImportRule.LastUpdated = DateTime.UtcNow;
    }

    private string? DescriptionHeld(InboundRecallContext ctx) => ValueHeld(ctx.MvDescriptionAttributeId);

    private string? ValueHeld(int attributeId) =>
        SyncRepo.MetaverseObjects.Values.Single().AttributeValues
            .Where(av => av.AttributeId == attributeId && !av.NullValue)
            .Select(av => av.StringValue)
            .SingleOrDefault();
}
