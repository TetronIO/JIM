// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview.Patterns;
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
/// Grouping runs at two levels. The coarse level is the transition, object type, Connected System and attribute; the
/// fine level is the distinct old-to-new value pair within it, which is what turns "38,900 would have Email changed"
/// into "38,900 would have Email changed from @contoso.com to @fabrikam.com". Value pairs can produce one group per
/// object, so they are named only while a group has at most <see cref="_maximumValuePairsPerGroup"/> of them; past
/// that the split stops being a summary and the group collapses back to the attribute level. Collapsing changes how
/// a population is described, never how many objects it holds.
///
/// Memory is bounded by the grouping dimensions rather than by the population: at most
/// <see cref="_maximumDeltasPerGroup"/> deltas are held per coarse group, plus a small per-value-pair reserve
/// bounded by the guard, and the coarse dimensions cannot produce more than a handful of groups for any real
/// surface.
/// </summary>
public class PreviewSummariser
{
    /// <summary>
    /// How many distinct old-to-new value pairs may be named within one coarse group before the split is abandoned.
    /// Deliberately small: past ten rows a "summary" is a list, which is the wall of text grouping exists to prevent.
    /// </summary>
    public const int DefaultMaximumValuePairsPerGroup = 10;

    /// <summary>
    /// Delta rows held per value pair over and above the coarse group's kept rows, purely so that no value-pair
    /// group can end up with an empty drill-down. See <see cref="BuildGroups"/> for when the reserve is used.
    /// </summary>
    public const int ValuePairExampleReserve = 10;

    private readonly int? _maximumDeltasPerGroup;
    private readonly int _maximumValuePairsPerGroup;
    private readonly PreviewPatternDetectorRegistry _patternDetectors;
    private readonly Dictionary<GroupKey, GroupAccumulator> _groups = [];

    /// <param name="maximumDeltasPerGroup">
    /// How many delta rows to keep per group, or null to keep every one. Null is the administrator's informed
    /// choice on a large preview, made against a stated row count and storage cost; it is honoured literally,
    /// because a "full data set" that quietly still capped would send them hunting through a drill-down for objects
    /// it had dropped.
    /// </param>
    /// <param name="maximumValuePairsPerGroup">
    /// The value-pair cardinality guard. Overridable for tests; production uses
    /// <see cref="DefaultMaximumValuePairsPerGroup"/>.
    /// </param>
    /// <param name="patternDetectors">
    /// The detectors that name what kind of change a group's deltas describe, or null for the curated set. Injectable
    /// so a test can drive one detector in isolation; production has no reason to vary it, because which patterns
    /// exist and in what order they win is a product decision made once in
    /// <see cref="PreviewPatternDetectorRegistry.Default"/>.
    /// </param>
    public PreviewSummariser(int? maximumDeltasPerGroup, int maximumValuePairsPerGroup = DefaultMaximumValuePairsPerGroup,
        PreviewPatternDetectorRegistry? patternDetectors = null)
    {
        if (maximumDeltasPerGroup is < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumDeltasPerGroup), maximumDeltasPerGroup,
                "A preview must keep at least one delta per group, or its summary can never be drilled into.");

        if (maximumValuePairsPerGroup < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumValuePairsPerGroup), maximumValuePairsPerGroup,
                "A group must be allowed to name at least one value pair, or no preview could ever describe a change by its values.");

        _maximumDeltasPerGroup = maximumDeltasPerGroup;
        _maximumValuePairsPerGroup = maximumValuePairsPerGroup;
        _patternDetectors = patternDetectors ?? PreviewPatternDetectorRegistry.Default;
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

        // Detection is skipped only once neither consumer of the answer can use it: the coarse group's consensus is
        // already broken and its value pairs have collapsed, so nothing is left to label. Everywhere else the answer
        // is wanted, and the detectors are cheap ordinal string work.
        var patternWanted = !accumulator.PatternConflicted || !accumulator.ValuePairsExceededGuard;
        var patternKey = patternWanted ? DetectPattern(delta) : null;

        if (patternWanted)
            accumulator.FoldPattern(patternKey);

        AccumulateValuePair(accumulator, delta, patternKey);
    }

    private string? DetectPattern(PreviewDelta delta) =>
        _patternDetectors.Detect(new PreviewPatternCandidate(delta.AttributeName, delta.OldValue, delta.NewValue));

    private void AccumulateValuePair(GroupAccumulator accumulator, PreviewDelta delta, string? patternKey)
    {
        if (accumulator.ValuePairsExceededGuard)
            return;

        var pair = new ValuePairKey(delta.OldValue, delta.NewValue);
        if (!accumulator.ValuePairs.TryGetValue(pair, out var pairAccumulator))
        {
            if (accumulator.ValuePairs.Count == _maximumValuePairsPerGroup)
            {
                // One pair too many. The decision is final for this group rather than re-evaluated later: a stream
                // cannot un-see the pairs it has already produced, and holding them on the chance that no more
                // arrive is exactly the unbounded memory the guard exists to prevent.
                accumulator.ValuePairsExceededGuard = true;
                accumulator.ValuePairs.Clear();
                return;
            }

            // Every delta in a value pair shares its attribute and both its values, so they all detect the same
            // pattern; recording the first one's answer is recording the pair's.
            pairAccumulator = new PairAccumulator { PatternKey = patternKey };
            accumulator.ValuePairs.Add(pair, pairAccumulator);
        }

        pairAccumulator.ObjectCount++;
        if (pairAccumulator.Reserve.Count < ValuePairExampleReserve)
            pairAccumulator.Reserve.Add(delta);
    }

    /// <summary>
    /// The summary groups, largest first, each carrying the delta rows kept for it.
    ///
    /// A coarse group whose value pairs stayed within the guard is emitted as one group per pair, and the rows kept
    /// for the coarse group are partitioned between them by value. That keeps the number of persisted rows what it
    /// would have been without the split, which matters because the storage estimate an administrator agreed to is
    /// calculated in rows. Where the partition leaves a pair with nothing (an adapter that yields its deltas in
    /// value order fills the coarse group's kept rows from the first pair alone), the pair's own reserve stands in,
    /// so a group that can be seen can always be drilled into.
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
                .SelectMany(g => BuildCandidates(g.Key, g.Value))
                // Deterministic beyond the obvious sort: two groups of equal size must land in the same order on
                // every run, or the same preview re-read looks like a different one. Values sort ordinally, because
                // two values differing only in case are a real difference and must not tie.
                .OrderByDescending(c => c.ObjectCount)
                .ThenBy(c => c.Key.TransitionType)
                .ThenBy(c => c.Key.ObjectTypeName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Key.AttributeName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.OldValue, StringComparer.Ordinal)
                .ThenBy(c => c.NewValue, StringComparer.Ordinal)
                .Select(c => BuildGroup(activityId, c, connectedSystemNames))
        ];
    }

    private static IEnumerable<GroupCandidate> BuildCandidates(GroupKey key, GroupAccumulator accumulator)
    {
        // A collapsed group carries a pattern only where every delta in it agreed on one. Anything less would be a
        // claim about a population from a majority of it, which is the kind of number this framework exists to
        // refuse.
        if (accumulator.ValuePairsExceededGuard)
            return [new GroupCandidate(key, null, null, accumulator.PatternKey, accumulator.ObjectCount, accumulator.Kept)];

        return accumulator.ValuePairs.Select(entry => BuildCandidate(key, accumulator, entry.Key, entry.Value));
    }

    private static GroupCandidate BuildCandidate(GroupKey key, GroupAccumulator accumulator, ValuePairKey pair,
        PairAccumulator pairAccumulator)
    {
        var kept = accumulator.Kept
            .Where(d => d.OldValue == pair.OldValue && d.NewValue == pair.NewValue)
            .ToList();

        return new GroupCandidate(key, pair.OldValue, pair.NewValue, pairAccumulator.PatternKey,
            pairAccumulator.ObjectCount, kept.Count > 0 ? kept : pairAccumulator.Reserve);
    }

    private ConfigurationChangePreviewGroup BuildGroup(Guid activityId, GroupCandidate candidate,
        IReadOnlyDictionary<int, string> connectedSystemNames)
    {
        var key = candidate.Key;
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
            OldValue = candidate.OldValue,
            NewValue = candidate.NewValue,
            PatternKey = candidate.PatternKey,
            ObjectCount = candidate.ObjectCount,
            DeltasSampled = candidate.ObjectCount > candidate.Kept.Count
        };

        // GroupId is left alone deliberately: the group has no id until it is inserted, and EF fills the foreign key
        // from this navigation when it saves the two together.
        group.Deltas = [.. candidate.Kept.Select(d => new ConfigurationChangePreviewDelta
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
            NewValue = d.NewValue,
            // Detected here rather than carried from Add: a collapsed group's rows do not share a pattern, and the
            // rows kept are bounded by the cap, so this is the cheapest place to get each row's own answer right.
            PatternKey = DetectPattern(d)
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
    /// The coarse grouping dimensions. A dimension the adapter left null is part of the key as null, so "no
    /// attribute" groups separately from "the Email attribute" rather than silently merging with it.
    /// </summary>
    private record GroupKey(
        ActivityRunProfileExecutionItemSyncOutcomeType TransitionType,
        int? MetaverseObjectTypeId,
        string? ObjectTypeName,
        int? ConnectedSystemId,
        string? AttributeName);

    /// <summary>
    /// The fine grouping dimension. Compared ordinally (the record's default for strings), so a change that only
    /// alters casing is a distinct pair rather than a group that appears to change nothing.
    /// </summary>
    private record ValuePairKey(string? OldValue, string? NewValue);

    /// <summary>One group as it will be emitted, after the decision to split by value pair or not has been made.</summary>
    private record GroupCandidate(GroupKey Key, string? OldValue, string? NewValue, string? PatternKey, int ObjectCount,
        IReadOnlyList<PreviewDelta> Kept);

    private sealed class GroupAccumulator
    {
        public int ObjectCount { get; set; }

        public List<PreviewDelta> Kept { get; } = [];

        public Dictionary<ValuePairKey, PairAccumulator> ValuePairs { get; } = [];

        /// <summary>True once this group has seen more distinct value pairs than are worth naming.</summary>
        public bool ValuePairsExceededGuard { get; set; }

        /// <summary>The pattern every delta seen so far agreed on, or null where they did not, or none did.</summary>
        public string? PatternKey { get; private set; }

        private bool _patternSeen;

        private bool _patternConflicted;

        /// <summary>True once two deltas in this group disagreed, which settles the group's pattern as "none".</summary>
        public bool PatternConflicted => _patternConflicted;

        /// <summary>
        /// Folds one delta's detected pattern into the group's. Unanimity is required rather than a majority: a
        /// group described as "email domain changed" is read as a statement about every object in it, so one delta
        /// that is something else, or is nothing recognisable, ends the claim rather than being outvoted by it.
        /// </summary>
        public void FoldPattern(string? key)
        {
            if (_patternConflicted)
                return;

            if (!_patternSeen)
            {
                PatternKey = key;
                _patternSeen = true;
                return;
            }

            if (PatternKey == key)
                return;

            _patternConflicted = true;
            PatternKey = null;
        }
    }

    private sealed class PairAccumulator
    {
        public int ObjectCount { get; set; }

        public List<PreviewDelta> Reserve { get; } = [];

        /// <summary>
        /// The pattern this pair's values describe. Fixed when the pair is first seen: every delta in it carries the
        /// same attribute and the same two values, so every one of them detects the same thing.
        /// </summary>
        public string? PatternKey { get; init; }
    }
}
