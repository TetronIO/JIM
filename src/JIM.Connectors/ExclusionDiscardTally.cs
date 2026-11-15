// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using System.Collections.Concurrent;

namespace JIM.Connectors;

/// <summary>
/// Counts the entries an import read from a Connected System and discarded because an excluded Container carved
/// them out (#1255), per Container, until the import reports them.
/// </summary>
/// <remarks>
/// A directory cannot express "this subtree except that branch" in one search, and decomposing the searches to
/// avoid the excluded branch was rejected in the design: it would make import scope depend on how recently the
/// hierarchy was refreshed, so a Container created since would be silently skipped and its objects obsoleted. The
/// cost of the choice taken instead is entries transferred only to be thrown away, and the design accepted that
/// cost on one condition: that it is reported rather than hidden. This is where a Connector keeps the count.
///
/// Shared rather than per-Connector because every Connector filtering client-side owes the same report, and
/// because the Activity reads one shape. Thread-safe: the LDAP Connector converts the entries its parallel
/// searches return concurrently.
/// </remarks>
public sealed class ExclusionDiscardTally
{
    private readonly ConcurrentDictionary<int, int> _discardsByContainerId = new();

    /// <summary>
    /// Whether any entry has been discarded, which is the ordinary case on a Connected System carrying no
    /// exclusions, and on one whose exclusions covered nothing this call read.
    /// </summary>
    public bool IsEmpty => _discardsByContainerId.IsEmpty;

    /// <summary>
    /// Entries discarded across every excluded Container.
    /// </summary>
    public int Total => _discardsByContainerId.Values.Sum();

    /// <summary>
    /// Records one entry as read and discarded because <paramref name="excludedBy"/> carved it out.
    /// </summary>
    public void RecordDiscard(ConnectedSystemContainer excludedBy)
    {
        ArgumentNullException.ThrowIfNull(excludedBy);
        _discardsByContainerId.AddOrUpdate(excludedBy.Id, 1, (_, count) => count + 1);
    }

    /// <summary>
    /// What was discarded since the last take, heaviest exclusion first, removing it from the tally: for a Connector
    /// whose reading carries on between import calls (the LDAP Connector's full read of a directory whose paging
    /// cookies are scoped to the connection, #2521), so each call reports what it adds and JIM, which adds up every
    /// call's counts, counts each discard once.
    /// </summary>
    /// <remarks>
    /// Each Container's count is removed atomically, so a discard recorded while the take runs is either in this take
    /// or left for the next, never lost.
    /// </remarks>
    public List<ExclusionDiscardCount> TakeCounts()
    {
        var taken = new List<ExclusionDiscardCount>();
        foreach (var containerId in _discardsByContainerId.Keys)
        {
            if (_discardsByContainerId.TryRemove(containerId, out var count))
                taken.Add(new ExclusionDiscardCount(containerId, count));
        }

        return [.. taken.OrderByDescending(count => count.EntriesDiscarded).ThenBy(count => count.ContainerId)];
    }

    /// <summary>
    /// What was discarded, heaviest exclusion first, for reporting onto the import result and the Activity.
    /// </summary>
    /// <remarks>
    /// Ordered rather than left to the dictionary's own enumeration because these counts exist to be read by an
    /// administrator asking why an import is slow, and the exclusion sitting in front of the largest branch is
    /// the answer. It is also what makes the log line's ordering deterministic.
    /// </remarks>
    public List<ExclusionDiscardCount> ToCounts() =>
    [
        .. _discardsByContainerId
            .Select(entry => new ExclusionDiscardCount(entry.Key, entry.Value))
            .OrderByDescending(count => count.EntriesDiscarded)
            .ThenBy(count => count.ContainerId)
    ];
}
