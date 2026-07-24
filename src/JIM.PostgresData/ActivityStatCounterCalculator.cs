// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Activities;
using JIM.Models.Enums;

namespace JIM.PostgresData;

/// <summary>
/// Pure delta calculation for the Activity stat counter table (#1078). Turns a persistence batch
/// of Run Profile Execution Items (and their Sync Outcomes) into per-(Activity, Dimension, Key)
/// count deltas for the repositories to upsert alongside the batch.
/// </summary>
public static class ActivityStatCounterCalculator
{
    /// <summary>
    /// Encodes an enum member as a counter row key: its integer value in invariant culture.
    /// </summary>
    public static string EnumKey<T>(T value) where T : Enum =>
        Convert.ToInt32(value).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Calculates the counter deltas for a batch of newly inserted RPEIs and their newly inserted
    /// Sync Outcomes: one count per RPEI in the ObjectChangeType dimension, plus ObjectTypeName
    /// (live Connected System Object type name when loaded, else the RPEI's type snapshot),
    /// ErrorType (excluding NotSet, mirroring the stats query's error predicate), NoChangeReason
    /// (only for NoChange items, mirroring how the stats consume reasons), and one count per
    /// outcome in the OutcomeType dimension.
    /// </summary>
    public static Dictionary<ActivityStatCounterKey, long> CalculateRpeiInsertDeltas(
        IReadOnlyCollection<ActivityRunProfileExecutionItem> rpeis,
        IReadOnlyCollection<ActivityRunProfileExecutionItemSyncOutcome> outcomes)
    {
        var deltas = new Dictionary<ActivityStatCounterKey, long>();

        foreach (var rpei in rpeis)
        {
            Increment(deltas, rpei.ActivityId, ActivityStatDimension.ObjectChangeType, EnumKey(rpei.ObjectChangeType));

            var objectTypeName = rpei.ConnectedSystemObject?.Type?.Name ?? rpei.ObjectTypeSnapshot;
            if (objectTypeName != null)
                Increment(deltas, rpei.ActivityId, ActivityStatDimension.ObjectTypeName, objectTypeName);

            if (rpei.ErrorType.HasValue && rpei.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
                Increment(deltas, rpei.ActivityId, ActivityStatDimension.ErrorType, EnumKey(rpei.ErrorType.Value));

            if (rpei.ObjectChangeType == ObjectChangeType.NoChange && rpei.NoChangeReason.HasValue)
                Increment(deltas, rpei.ActivityId, ActivityStatDimension.NoChangeReason, EnumKey(rpei.NoChangeReason.Value));
        }

        AddOutcomeDeltas(deltas, rpeis, outcomes);
        return deltas;
    }

    /// <summary>
    /// Calculates the counter deltas for new Sync Outcomes added to already-persisted RPEIs
    /// (reconciliation and cross-page merge paths): OutcomeType counts only, so the RPEI-level
    /// dimensions counted at insert time are not double-counted. The RPEIs are used solely to
    /// map each outcome to its owning Activity; outcomes for unknown RPEIs are ignored.
    /// </summary>
    public static Dictionary<ActivityStatCounterKey, long> CalculateOutcomeInsertDeltas(
        IReadOnlyCollection<ActivityRunProfileExecutionItem> rpeis,
        IReadOnlyCollection<ActivityRunProfileExecutionItemSyncOutcome> newOutcomes)
    {
        var deltas = new Dictionary<ActivityStatCounterKey, long>();
        AddOutcomeDeltas(deltas, rpeis, newOutcomes);
        return deltas;
    }

    private static void AddOutcomeDeltas(
        Dictionary<ActivityStatCounterKey, long> deltas,
        IReadOnlyCollection<ActivityRunProfileExecutionItem> rpeis,
        IReadOnlyCollection<ActivityRunProfileExecutionItemSyncOutcome> outcomes)
    {
        if (outcomes.Count == 0)
            return;

        var activityIdsByRpeiId = new Dictionary<Guid, Guid>(rpeis.Count);
        foreach (var rpei in rpeis)
            activityIdsByRpeiId[rpei.Id] = rpei.ActivityId;

        foreach (var outcome in outcomes.Where(o => activityIdsByRpeiId.ContainsKey(o.ActivityRunProfileExecutionItemId)))
            Increment(deltas, activityIdsByRpeiId[outcome.ActivityRunProfileExecutionItemId], ActivityStatDimension.OutcomeType, EnumKey(outcome.OutcomeType));
    }

    private static void Increment(
        Dictionary<ActivityStatCounterKey, long> deltas,
        Guid activityId,
        ActivityStatDimension dimension,
        string key)
    {
        var counterKey = new ActivityStatCounterKey(activityId, dimension, key);
        deltas[counterKey] = deltas.TryGetValue(counterKey, out var existing) ? existing + 1 : 1;
    }
}
