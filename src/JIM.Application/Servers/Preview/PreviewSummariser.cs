// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Turns a stream of per-object deltas into the exact summary an administrator lands on, and decides which delta
/// rows are worth keeping behind it.
///
/// A preview affecting 40,000 objects is unreadable as a list and perfectly readable as "38,900 would have their
/// email domain changed; 1,100 would fall out of scope". Producing that reading is the whole job here, and the one
/// rule it may never break is that **the counts are exact**: they come from every delta the adapter yielded, not
/// from the subset that was persisted. An administrator who is told 1,000 because that is how many rows fitted has
/// been given a wrong number that looks like a right one.
///
/// Memory is bounded by the grouping dimensions rather than by the population: at most
/// <see cref="_maximumDeltasPerGroup"/> deltas are held per group, and v1's dimensions (transition, object type,
/// Connected System, attribute) cannot produce more than a handful of groups for any real surface. Grouping by
/// distinct old-to-new value pairs, which can produce one group per object, arrives with the cardinality guard that
/// makes it safe (plan Phase 4a) and not before.
/// </summary>
public class PreviewSummariser
{
    private readonly int? _maximumDeltasPerGroup;
    private readonly Dictionary<GroupKey, GroupAccumulator> _groups = [];

    /// <param name="maximumDeltasPerGroup">
    /// How many delta rows to keep per group, or null to keep every one. Null is the administrator's informed
    /// choice on a large preview, made against a stated row count and storage cost; it is honoured literally,
    /// because a "full data set" that quietly still capped would send them hunting through a drill-down for objects
    /// it had dropped.
    /// </param>
    public PreviewSummariser(int? maximumDeltasPerGroup)
    {
        if (maximumDeltasPerGroup is < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumDeltasPerGroup), maximumDeltasPerGroup,
                "A preview must keep at least one delta per group, or its summary can never be drilled into.");

        _maximumDeltasPerGroup = maximumDeltasPerGroup;
    }

    /// <summary>Every delta seen, including those not kept. The unit the Activity reports progress in.</summary>
    public long TotalDeltas { get; private set; }

    /// <summary>True when any group saw more deltas than it kept, which is what makes the result a sample.</summary>
    public bool AnyGroupCapped { get; private set; }

    public void Add(PreviewDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        TotalDeltas++;

        var key = new GroupKey(delta.TransitionType, delta.MetaverseObjectTypeId, delta.ObjectTypeName,
            delta.ConnectedSystemId, delta.AttributeName);

        if (!_groups.TryGetValue(key, out var accumulator))
        {
            accumulator = new GroupAccumulator();
            _groups.Add(key, accumulator);
        }

        // The count moves for every delta; the kept rows stop at the cap. That asymmetry is the point.
        accumulator.ObjectCount++;
        if (_maximumDeltasPerGroup is null || accumulator.Kept.Count < _maximumDeltasPerGroup)
            accumulator.Kept.Add(delta);
        else
            AnyGroupCapped = true;
    }

    /// <summary>
    /// The summary groups, largest first, each carrying the delta rows kept for it.
    /// </summary>
    /// <param name="activityId">The preview's Activity, which owns every row produced here.</param>
    /// <param name="connectedSystemNames">
    /// Connected System names by id, resolved once by the caller. Snapshotted onto the group rather than joined at
    /// read time, so a summary still reads correctly after the system is renamed and still renders at all after it
    /// is deleted.
    /// </param>
    public List<ConfigurationChangePreviewGroup> BuildGroups(Guid activityId, IReadOnlyDictionary<int, string> connectedSystemNames)
    {
        ArgumentNullException.ThrowIfNull(connectedSystemNames);

        return
        [
            .. _groups
                // Deterministic beyond the obvious sort: two groups of equal size must land in the same order on
                // every run, or the same preview re-read looks like a different one.
                .OrderByDescending(g => g.Value.ObjectCount)
                .ThenBy(g => g.Key.TransitionType)
                .ThenBy(g => g.Key.ObjectTypeName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key.AttributeName, StringComparer.OrdinalIgnoreCase)
                .Select(g => BuildGroup(activityId, g.Key, g.Value, connectedSystemNames))
        ];
    }

    private ConfigurationChangePreviewGroup BuildGroup(Guid activityId, GroupKey key, GroupAccumulator accumulator,
        IReadOnlyDictionary<int, string> connectedSystemNames)
    {
        var group = new ConfigurationChangePreviewGroup
        {
            ActivityId = activityId,
            TransitionType = key.TransitionType,
            MetaverseObjectTypeId = key.MetaverseObjectTypeId,
            MetaverseObjectTypeName = key.ObjectTypeName,
            ConnectedSystemId = key.ConnectedSystemId,
            ConnectedSystemName = key.ConnectedSystemId.HasValue && connectedSystemNames.TryGetValue(key.ConnectedSystemId.Value, out var name)
                ? name
                : null,
            AttributeName = key.AttributeName,
            ObjectCount = accumulator.ObjectCount,
            DeltasSampled = accumulator.ObjectCount > accumulator.Kept.Count
        };

        // GroupId is left alone deliberately: the group has no id until it is inserted, and EF fills the foreign key
        // from this navigation when it saves the two together.
        group.Deltas = [.. accumulator.Kept.Select(d => new ConfigurationChangePreviewDelta
        {
            ActivityId = activityId,
            TransitionType = d.TransitionType,
            MetaverseObjectId = d.MetaverseObjectId,
            ConnectedSystemObjectId = d.ConnectedSystemObjectId,
            ConnectedSystemId = d.ConnectedSystemId,
            ObjectDisplayName = d.ObjectDisplayName,
            ObjectTypeName = d.ObjectTypeName,
            AttributeName = d.AttributeName,
            OldValue = d.OldValue,
            NewValue = d.NewValue
        })];

        return group;
    }

    /// <summary>
    /// The Connected Systems any group refers to, so the caller resolves their names in one query instead of one
    /// per group.
    /// </summary>
    public IReadOnlyCollection<int> ReferencedConnectedSystemIds =>
        [.. _groups.Keys.Where(k => k.ConnectedSystemId.HasValue).Select(k => k.ConnectedSystemId!.Value).Distinct()];

    /// <summary>
    /// v1's grouping dimensions. A dimension the adapter left null is part of the key as null, so "no attribute"
    /// groups separately from "the Email attribute" rather than silently merging with it.
    /// </summary>
    private record GroupKey(
        ActivityRunProfileExecutionItemSyncOutcomeType TransitionType,
        int? MetaverseObjectTypeId,
        string? ObjectTypeName,
        int? ConnectedSystemId,
        string? AttributeName);

    private sealed class GroupAccumulator
    {
        public int ObjectCount { get; set; }

        public List<PreviewDelta> Kept { get; } = [];
    }
}
