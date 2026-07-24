// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Unit tests for the pure delta calculation behind the Activity stat counter table (#1078).
/// The calculator turns a flush batch of Run Profile Execution Items (and their Sync Outcomes)
/// into per-(Activity, Dimension, Key) count deltas, which the repositories upsert so the
/// Activity detail page reads a handful of counter rows instead of aggregating millions.
/// </summary>
[TestFixture]
public class ActivityStatCounterCalculatorTests
{
    private static string EnumKey<T>(T value) where T : Enum =>
        Convert.ToInt32(value).ToString(CultureInfo.InvariantCulture);

    private static ActivityRunProfileExecutionItem NewRpei(
        Guid activityId,
        ObjectChangeType changeType,
        string? objectTypeSnapshot = "user",
        ActivityRunProfileExecutionItemErrorType? errorType = null,
        NoChangeReason? noChangeReason = null)
    {
        return new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = changeType,
            ObjectTypeSnapshot = objectTypeSnapshot,
            ErrorType = errorType,
            NoChangeReason = noChangeReason
        };
    }

    #region CalculateRpeiInsertDeltas

    [Test]
    public void CalculateRpeiInsertDeltas_MixedChangeTypes_CountsPerObjectChangeType()
    {
        var activityId = Guid.NewGuid();
        var rpeis = new List<ActivityRunProfileExecutionItem>
        {
            NewRpei(activityId, ObjectChangeType.Added),
            NewRpei(activityId, ObjectChangeType.Added),
            NewRpei(activityId, ObjectChangeType.Updated),
            NewRpei(activityId, ObjectChangeType.NoChange)
        };

        var deltas = ActivityStatCounterCalculator.CalculateRpeiInsertDeltas(rpeis, []);

        Assert.That(deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Added))], Is.EqualTo(2));
        Assert.That(deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Updated))], Is.EqualTo(1));
        Assert.That(deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.NoChange))], Is.EqualTo(1));
    }

    [Test]
    public void CalculateRpeiInsertDeltas_ErrorTypes_ExcludesNotSetAndNull()
    {
        var activityId = Guid.NewGuid();
        var rpeis = new List<ActivityRunProfileExecutionItem>
        {
            NewRpei(activityId, ObjectChangeType.Added, errorType: ActivityRunProfileExecutionItemErrorType.UnhandledError),
            NewRpei(activityId, ObjectChangeType.Added, errorType: ActivityRunProfileExecutionItemErrorType.UnhandledError),
            NewRpei(activityId, ObjectChangeType.Added, errorType: ActivityRunProfileExecutionItemErrorType.NotSet),
            NewRpei(activityId, ObjectChangeType.Added)
        };

        var deltas = ActivityStatCounterCalculator.CalculateRpeiInsertDeltas(rpeis, []);

        Assert.That(deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.ErrorType, EnumKey(ActivityRunProfileExecutionItemErrorType.UnhandledError))], Is.EqualTo(2));
        var errorRows = deltas.Keys.Where(k => k.Dimension == ActivityStatDimension.ErrorType).ToList();
        Assert.That(errorRows, Has.Count.EqualTo(1), "NotSet and null error types must not produce counter rows");
    }

    [Test]
    public void CalculateRpeiInsertDeltas_NoChangeReason_CountedOnlyForNoChangeItems()
    {
        var activityId = Guid.NewGuid();
        var rpeis = new List<ActivityRunProfileExecutionItem>
        {
            NewRpei(activityId, ObjectChangeType.NoChange, noChangeReason: NoChangeReason.CsoAlreadyCurrent),
            NewRpei(activityId, ObjectChangeType.NoChange, noChangeReason: NoChangeReason.MvoNoAttributeChanges),
            NewRpei(activityId, ObjectChangeType.NoChange, noChangeReason: NoChangeReason.MvoNoAttributeChanges),
            // A non-NoChange item carrying a reason must not be counted in the reason dimension,
            // mirroring how the stats query only consumes reasons for NoChange items.
            NewRpei(activityId, ObjectChangeType.Updated, noChangeReason: NoChangeReason.CsoAlreadyCurrent)
        };

        var deltas = ActivityStatCounterCalculator.CalculateRpeiInsertDeltas(rpeis, []);

        Assert.That(deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.NoChangeReason, EnumKey(NoChangeReason.CsoAlreadyCurrent))], Is.EqualTo(1));
        Assert.That(deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.NoChangeReason, EnumKey(NoChangeReason.MvoNoAttributeChanges))], Is.EqualTo(2));
    }

    [Test]
    public void CalculateRpeiInsertDeltas_ObjectTypeName_PrefersCsoTypeNameOverSnapshot()
    {
        var activityId = Guid.NewGuid();
        var withLiveType = NewRpei(activityId, ObjectChangeType.Added, objectTypeSnapshot: "staleSnapshotName");
        withLiveType.ConnectedSystemObject = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = new ConnectedSystemObjectType { Id = 1, Name = "group" }
        };
        var rpeis = new List<ActivityRunProfileExecutionItem>
        {
            withLiveType,
            NewRpei(activityId, ObjectChangeType.Added, objectTypeSnapshot: "user"),
            NewRpei(activityId, ObjectChangeType.Added, objectTypeSnapshot: null)
        };

        var deltas = ActivityStatCounterCalculator.CalculateRpeiInsertDeltas(rpeis, []);

        Assert.That(deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.ObjectTypeName, "group")], Is.EqualTo(1));
        Assert.That(deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.ObjectTypeName, "user")], Is.EqualTo(1));
        var typeRows = deltas.Keys.Where(k => k.Dimension == ActivityStatDimension.ObjectTypeName).ToList();
        Assert.That(typeRows, Has.Count.EqualTo(2), "an item with no resolvable type name must not produce a counter row");
    }

    [Test]
    public void CalculateRpeiInsertDeltas_Outcomes_CountedPerOutcomeType()
    {
        var activityId = Guid.NewGuid();
        var rpei = NewRpei(activityId, ObjectChangeType.Updated);
        var outcomes = new List<ActivityRunProfileExecutionItemSyncOutcome>
        {
            new() { ActivityRunProfileExecutionItemId = rpei.Id, OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated },
            new() { ActivityRunProfileExecutionItemId = rpei.Id, OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow },
            new() { ActivityRunProfileExecutionItemId = rpei.Id, OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow }
        };

        var deltas = ActivityStatCounterCalculator.CalculateRpeiInsertDeltas([rpei], outcomes);

        Assert.That(deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.OutcomeType, EnumKey(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated))], Is.EqualTo(1));
        Assert.That(deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.OutcomeType, EnumKey(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow))], Is.EqualTo(2));
    }

    [Test]
    public void CalculateRpeiInsertDeltas_MultipleActivities_GroupsByActivityId()
    {
        var activityA = Guid.NewGuid();
        var activityB = Guid.NewGuid();
        var rpeis = new List<ActivityRunProfileExecutionItem>
        {
            NewRpei(activityA, ObjectChangeType.Added),
            NewRpei(activityB, ObjectChangeType.Added),
            NewRpei(activityB, ObjectChangeType.Added)
        };

        var deltas = ActivityStatCounterCalculator.CalculateRpeiInsertDeltas(rpeis, []);

        Assert.That(deltas[new ActivityStatCounterKey(activityA, ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Added))], Is.EqualTo(1));
        Assert.That(deltas[new ActivityStatCounterKey(activityB, ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Added))], Is.EqualTo(2));
    }

    #endregion

    #region CalculateOutcomeInsertDeltas

    [Test]
    public void CalculateOutcomeInsertDeltas_ProducesOnlyOutcomeDimensionRows()
    {
        var activityId = Guid.NewGuid();
        var rpei = NewRpei(activityId, ObjectChangeType.Updated, errorType: ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed);
        var newOutcomes = new List<ActivityRunProfileExecutionItemSyncOutcome>
        {
            new() { ActivityRunProfileExecutionItemId = rpei.Id, OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed },
            new() { ActivityRunProfileExecutionItemId = rpei.Id, OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed }
        };

        var deltas = ActivityStatCounterCalculator.CalculateOutcomeInsertDeltas([rpei], newOutcomes);

        Assert.That(deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.OutcomeType, EnumKey(ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed))], Is.EqualTo(2));
        Assert.That(deltas.Keys.All(k => k.Dimension == ActivityStatDimension.OutcomeType), Is.True,
            "outcome-only deltas must not recount RPEI dimensions for already-persisted items");
    }

    [Test]
    public void CalculateOutcomeInsertDeltas_OutcomeForUnknownRpei_IsIgnored()
    {
        var activityId = Guid.NewGuid();
        var rpei = NewRpei(activityId, ObjectChangeType.Updated);
        var newOutcomes = new List<ActivityRunProfileExecutionItemSyncOutcome>
        {
            new() { ActivityRunProfileExecutionItemId = Guid.NewGuid(), OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed }
        };

        var deltas = ActivityStatCounterCalculator.CalculateOutcomeInsertDeltas([rpei], newOutcomes);

        Assert.That(deltas, Is.Empty);
    }

    #endregion
}
