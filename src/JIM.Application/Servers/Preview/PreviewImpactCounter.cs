// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// The two ways an adapter counts its deltas: one per delta, or one per distinct object.
/// </summary>
public static class PreviewImpactCounter
{
    /// <summary>
    /// One per delta, per transition: for an adapter that yields at most one delta per object and transition.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System every count is scoped to, if any.</param>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type every count is scoped to, if any.</param>
    public static IPreviewImpactCounter PerDelta(int? connectedSystemId = null, int? metaverseObjectTypeId = null) =>
        new PerDeltaCounter(connectedSystemId, metaverseObjectTypeId);

    /// <summary>
    /// Distinct objects per transition: for an adapter that may yield several deltas for one object and transition
    /// (one per attribute, say). A delta whose subject is null names no object and is not counted.
    /// </summary>
    /// <param name="subjectOf">
    /// The object a delta counts against: usually its id, or a composite where an id alone does not tell two objects
    /// apart (one identity's accounts in two Connected Systems are two objects).
    /// </param>
    public static IPreviewImpactCounter PerSubject<TSubject>(Func<PreviewDelta, TSubject?> subjectOf) where TSubject : struct
    {
        ArgumentNullException.ThrowIfNull(subjectOf);
        return new PerSubjectCounter<TSubject>(subjectOf);
    }

    /// <summary>
    /// Counts a whole delta stream: stage 2 run on its own, for a caller that wants the counts without the deltas.
    /// </summary>
    public static async Task<List<PreviewImpactCount>> CountAsync(IPreviewImpactCounter counter, IAsyncEnumerable<PreviewDelta> deltas,
        CancellationToken cancellationToken = default)
    {
        await foreach (var delta in deltas.WithCancellation(cancellationToken))
            counter.Add(delta);
        return counter.Build();
    }

    /// <summary>
    /// Largest transition first, then by transition, so the same change previews the same way twice.
    /// </summary>
    private static List<PreviewImpactCount> Order(IEnumerable<KeyValuePair<ActivityRunProfileExecutionItemSyncOutcomeType, int>> counts,
        int? connectedSystemId = null, int? metaverseObjectTypeId = null) =>
    [
        .. counts
            .OrderByDescending(count => count.Value)
            .ThenBy(count => count.Key)
            .Select(count => new PreviewImpactCount(count.Key, count.Value, connectedSystemId, metaverseObjectTypeId))
    ];

    private sealed class PerDeltaCounter(int? connectedSystemId, int? metaverseObjectTypeId) : IPreviewImpactCounter
    {
        private readonly Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int> _counts = [];

        public void Add(PreviewDelta delta) =>
            _counts[delta.TransitionType] = _counts.GetValueOrDefault(delta.TransitionType) + 1;

        public List<PreviewImpactCount> Build() => Order(_counts, connectedSystemId, metaverseObjectTypeId);
    }

    private sealed class PerSubjectCounter<TSubject>(Func<PreviewDelta, TSubject?> subjectOf) : IPreviewImpactCounter where TSubject : struct
    {
        private readonly Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, HashSet<TSubject>> _subjects = [];

        public void Add(PreviewDelta delta)
        {
            if (subjectOf(delta) is not { } subject)
                return;

            if (!_subjects.TryGetValue(delta.TransitionType, out var subjects))
                _subjects[delta.TransitionType] = subjects = [];
            subjects.Add(subject);
        }

        public List<PreviewImpactCount> Build() =>
            Order(_subjects.Select(pair => KeyValuePair.Create(pair.Key, pair.Value.Count)));
    }
}
