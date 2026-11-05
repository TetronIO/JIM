// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// When a configuration change's predicted counts may be shown beside what the applying synchronisation did (#2618).
/// The policy exists to withhold: a comparison of two numbers that do not measure the same thing is worse than none.
/// </summary>
[TestFixture]
public class ConfigurationChangePredictionPolicyTests
{
    private const int SystemId = 3;
    private const int RuleId = 11;

    private static ConfigurationChangePreview CompletePreview(
        ConfigurationChangePreviewSurface surface = ConfigurationChangePreviewSurface.SynchronisationRuleScope,
        params PreviewImpactCount[] counts) =>
        new()
        {
            Surface = surface,
            ValidationStatus = ConfigurationChangePreviewStageStatus.Complete,
            ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Complete,
            SummaryStatus = ConfigurationChangePreviewStageStatus.Complete,
            DeltasStatus = ConfigurationChangePreviewStageStatus.Complete,
            ImpactCounts = System.Text.Json.JsonSerializer.Serialize(counts.Length > 0 ? counts : DefaultCounts())
        };

    private static PreviewImpactCount[] DefaultCounts() =>
    [
        new(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, 40, SystemId),
        new(ActivityRunProfileExecutionItemSyncOutcomeType.Joined, 10, SystemId),
        new(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope, 5, SystemId)
    ];

    private static Activity SaveActivity(Guid? previewId = null, DateTime? overtakenAt = null) =>
        new() { PreviewActivityId = previewId ?? Guid.NewGuid(), PreviewOvertakenAt = overtakenAt };

    private static ConfigurationChangeSynchronisationSummary Change(int? syncRuleId = RuleId) =>
        new() { ConnectedSystemId = SystemId, SyncRuleId = syncRuleId, AppliedAt = DateTime.UtcNow, ClaimedByActivityId = Guid.NewGuid() };

    private static Activity PreviewActivity(int? syncRuleId = RuleId) => new() { SyncRuleId = syncRuleId };

    private static IReadOnlyList<ActivityRunProfileExecutionItemSyncOutcomeType> Eligible(
        Activity? save = null, ConfigurationChangePreview? preview = null, Activity? previewActivity = null,
        ConfigurationChangeSynchronisationSummary? change = null, int changesBehindRun = 1) =>
        ConfigurationChangePredictionPolicy.ComparableOutcomes(
            save ?? SaveActivity(), preview ?? CompletePreview(), previewActivity ?? PreviewActivity(), change ?? Change(), changesBehindRun);

    [Test]
    public void ComparableOutcomes_ScopingPreviewOfTheSavedRule_ReturnsOnlyTheOutcomesBothSidesCount()
    {
        Assert.That(Eligible(), Is.EquivalentTo(new[]
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            ActivityRunProfileExecutionItemSyncOutcomeType.Joined
        }));
    }

    [Test]
    public void ComparableOutcomes_NoPreviewInformedTheSave_ReturnsNothing()
    {
        Assert.That(ConfigurationChangePredictionPolicy.ComparableOutcomes(
            new Activity(), CompletePreview(), PreviewActivity(), Change(), 1), Is.Empty);
    }

    [Test]
    public void ComparableOutcomes_PreviewWasOvertakenBeforeTheSave_ReturnsNothing()
    {
        Assert.That(Eligible(save: SaveActivity(overtakenAt: DateTime.UtcNow)), Is.Empty);
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(5)]
    public void ComparableOutcomes_RunAppliedAnyNumberOfChangesOtherThanOne_ReturnsNothing(int changesBehindRun)
    {
        Assert.That(Eligible(changesBehindRun: changesBehindRun), Is.Empty);
    }

    [Test]
    public void ComparableOutcomes_PreviewFailed_ReturnsNothing()
    {
        var preview = CompletePreview();
        preview.DeltasStatus = ConfigurationChangePreviewStageStatus.Failed;

        Assert.That(Eligible(preview: preview), Is.Empty);
    }

    [Test]
    public void ComparableOutcomes_PreviewStillCounting_ReturnsNothing()
    {
        var preview = CompletePreview();
        preview.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.InProgress;

        Assert.That(Eligible(preview: preview), Is.Empty);
    }

    [TestCase(ConfigurationChangePreviewSurface.ObjectMatching)]
    [TestCase(ConfigurationChangePreviewSurface.SynchronisationRuleAttributeFlow)]
    [TestCase(ConfigurationChangePreviewSurface.SynchronisationRuleBehaviour)]
    [TestCase(ConfigurationChangePreviewSurface.ConnectedSystem)]
    public void ComparableOutcomes_SurfaceWhoseCountsAreNotTheRunsCounts_ReturnsNothing(ConfigurationChangePreviewSurface surface)
    {
        Assert.That(Eligible(preview: CompletePreview(surface)), Is.Empty);
    }

    [Test]
    public void ComparableOutcomes_PreviewWasOfADifferentRule_ReturnsNothing()
    {
        Assert.That(Eligible(previewActivity: PreviewActivity(syncRuleId: RuleId + 1)), Is.Empty);
    }

    [Test]
    public void ComparableOutcomes_ChangeIsNotToASynchronisationRule_ReturnsNothing()
    {
        Assert.That(Eligible(change: Change(syncRuleId: null)), Is.Empty);
    }

    [Test]
    public void ComparableOutcomes_PreviewActivityIsGone_ReturnsNothing()
    {
        Assert.That(ConfigurationChangePredictionPolicy.ComparableOutcomes(
            SaveActivity(), CompletePreview(), null, Change(), 1), Is.Empty);
    }

    [Test]
    public void Compare_PredictedAndActual_PairsEachComparableOutcomeLargestPredictionFirst()
    {
        var previewId = Guid.NewGuid();
        var comparison = ConfigurationChangePredictionPolicy.Compare(
            previewId, CompletePreview(), SystemId,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Projected, ActivityRunProfileExecutionItemSyncOutcomeType.Joined],
            new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int>
            {
                [ActivityRunProfileExecutionItemSyncOutcomeType.Joined] = 12,
                [ActivityRunProfileExecutionItemSyncOutcomeType.Projected] = 38
            });

        Assert.That(comparison, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(comparison!.PreviewActivityId, Is.EqualTo(previewId));
            Assert.That(comparison.Lines, Is.EqualTo(new[]
            {
                new ConfigurationChangePredictionLine(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, 40, 38),
                new ConfigurationChangePredictionLine(ActivityRunProfileExecutionItemSyncOutcomeType.Joined, 10, 12)
            }));
        }
    }

    [Test]
    public void Compare_OutcomeThePreviewDidNotCountAndTheRunDidNot_IsLeftOut()
    {
        var comparison = ConfigurationChangePredictionPolicy.Compare(
            Guid.NewGuid(),
            CompletePreview(counts: new PreviewImpactCount(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, 4, SystemId)),
            SystemId,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Projected, ActivityRunProfileExecutionItemSyncOutcomeType.Joined],
            new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int>());

        Assert.That(comparison!.Lines, Is.EqualTo(new[]
        {
            new ConfigurationChangePredictionLine(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, 4, 0)
        }));
    }

    [Test]
    public void Compare_PreviewCountedOtherConnectedSystemsOnly_ReturnsNull()
    {
        var comparison = ConfigurationChangePredictionPolicy.Compare(
            Guid.NewGuid(),
            CompletePreview(counts: new PreviewImpactCount(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, 4, SystemId + 1)),
            SystemId,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Projected],
            new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int>());

        Assert.That(comparison, Is.Null);
    }

    [Test]
    public void Compare_NothingPredictedOrDone_ReturnsNullRatherThanAnEmptyColumn()
    {
        var comparison = ConfigurationChangePredictionPolicy.Compare(
            Guid.NewGuid(), CompletePreview(counts: new PreviewImpactCount(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope, 3, SystemId)),
            SystemId, [ActivityRunProfileExecutionItemSyncOutcomeType.Projected],
            new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int>());

        Assert.That(comparison, Is.Null);
    }
}
