// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Application.Servers;

/// <summary>
/// Answers whether a Metaverse Object's pending removals hold a value, as they stand at the moment of asking, without
/// scanning them each time once they are many. <see cref="SyncEngine.GetEffectiveAttributeValues"/> asks it of every
/// value it reads, and asked by a scan that cost the values times the removals: a group whose membership is replaced
/// wholesale stages a removal per member, so taking over provenance on the survivors scanned 50,000 removals for each
/// of 50,000 members (#2664).
/// </summary>
/// <remarks>
/// <para>
/// The answer is exactly <see cref="List{T}.Contains"/>'s on the object's current
/// <see cref="MetaverseObject.PendingAttributeValueRemovals"/> list, by the same equality (the value type does not
/// override <see cref="object.Equals(object)"/>, so that is by reference). At or below <see cref="IndexThreshold"/>
/// removals it is that call. Above it, the removals are indexed, and the index takes in whatever has been appended
/// since it last answered, because a caller may append to the removals while it is still reading the values (the typed
/// writer stages each value it reads that it does not keep).
/// </para>
/// <para>
/// The index is exact while the removals list only grows between two questions, which is all any caller does. It is
/// rebuilt when it can see the list changed in another way: a different list on the object, fewer removals than it has
/// taken in, or a different removal where the last one it took in was. An edit that leaves all three alone between two
/// questions (removing an earlier removal and appending the last one again, say) is not seen; nothing does that.
/// </para>
/// </remarks>
internal sealed class PendingRemovalLookup
{
    /// <summary>
    /// The number of pending removals above which they are indexed rather than scanned.
    /// </summary>
    internal const int IndexThreshold = 32;

    private readonly MetaverseObject _mvo;

    // The list the index was built from, the index itself (null while the removals are few enough to scan), how many of
    // the list's removals it has taken in, and the last of those, by which an edit before the end of the list is
    // noticed.
    private List<MetaverseObjectAttributeValue>? _indexedRemovals;
    private HashSet<MetaverseObjectAttributeValue>? _index;
    private int _indexedCount;
    private MetaverseObjectAttributeValue? _lastIndexed;

    internal PendingRemovalLookup(MetaverseObject mvo)
    {
        _mvo = mvo;
    }

    /// <summary>
    /// Whether the object's pending removals hold <paramref name="value"/>.
    /// </summary>
    internal bool Contains(MetaverseObjectAttributeValue value)
    {
        var removals = _mvo.PendingAttributeValueRemovals;
        if (removals.Count <= IndexThreshold)
        {
            _index = null;
            return removals.Contains(value);
        }

        if (_index == null ||
            !ReferenceEquals(removals, _indexedRemovals) ||
            removals.Count < _indexedCount ||
            !ReferenceEquals(removals[_indexedCount - 1], _lastIndexed))
        {
            _index = new HashSet<MetaverseObjectAttributeValue>(removals.Count);
            _indexedRemovals = removals;
            _indexedCount = 0;
        }

        for (; _indexedCount < removals.Count; _indexedCount++)
            _index.Add(removals[_indexedCount]);
        _lastIndexed = removals[_indexedCount - 1];

        return _index.Contains(value);
    }
}
