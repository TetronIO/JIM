// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Application.Services;

/// <summary>
/// Finds the Connected System Objects joined to a Metaverse Object for contributor re-election
/// (<see cref="ContributorReElectionService"/>), a synchronisation page at a time (#3059).
/// <para>
/// Re-election reads each object's joined objects, and a read's cost is almost all fixed per query: under the Merge
/// contribution mode, where every withdrawn value is re-elected, a run where many objects change paid that cost once per
/// changed object, the largest cost Merge added. A page now names the objects predicted to need re-election
/// (<see cref="SyncEngine.MayWithdrawMergeValues"/>), and the first of them to ask reads them all in one query. An object
/// not predicted, or one the bulk read left out (over its value budget), is read singly, as every object was before, so
/// the answer is always the one a single read would give and a page with no prediction costs nothing extra.
/// </para>
/// </summary>
public sealed class ReElectionSurvivorLookup
{
    /// <summary>
    /// The most values an object's joined objects may hold to be read in a page's bulk read; one holding more (a large
    /// group) is read singly, so the bulk read stays bounded.
    /// </summary>
    public const int MaxValuesPerObjectInBatch = 2_000;

    private readonly Func<Guid, Task<List<ConnectedSystemObject>>> _single;
    private readonly Func<IReadOnlyCollection<Guid>, Task<Dictionary<Guid, List<ConnectedSystemObject>>>> _batch;
    private HashSet<Guid> _predicted = [];
    private Dictionary<Guid, List<ConnectedSystemObject>>? _batched;

    /// <param name="single">Reads one Metaverse Object's joined Connected System Objects.</param>
    /// <param name="batch">Reads several Metaverse Objects' joined Connected System Objects in one query, keyed by
    /// Metaverse Object id; an object it leaves out is read singly.</param>
    public ReElectionSurvivorLookup(
        Func<Guid, Task<List<ConnectedSystemObject>>> single,
        Func<IReadOnlyCollection<Guid>, Task<Dictionary<Guid, List<ConnectedSystemObject>>>> batch)
    {
        _single = single;
        _batch = batch;
    }

    /// <summary>
    /// Starts a page: forgets the last page's reads, whose entities the change tracker no longer holds, and records the
    /// Metaverse Objects predicted to need re-election in this page.
    /// </summary>
    public void BeginPage(IEnumerable<Guid> predictedMetaverseObjectIds)
    {
        _predicted = predictedMetaverseObjectIds.ToHashSet();
        _batched = null;
    }

    /// <summary>The Connected System Objects joined to <paramref name="metaverseObjectId"/>.</summary>
    public async Task<List<ConnectedSystemObject>> GetAsync(Guid metaverseObjectId)
    {
        if (_batched == null && _predicted.Contains(metaverseObjectId))
            _batched = await _batch(_predicted);

        if (_batched != null && _batched.TryGetValue(metaverseObjectId, out var joined))
            return joined;

        return await _single(metaverseObjectId);
    }
}
