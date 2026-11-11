// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Transactional;

/// <summary>
/// A page-scoped cache of what an import join's per-object lookups would find (#2020): for each value a page's unjoined
/// Connected System Objects look up, the Metaverse Objects holding it; each Metaverse Object those lookups settle on,
/// loaded; and how many Connected System Objects of the run's Connected System each one already has joined. Filled by
/// one batch per page and consulted in place of a database round trip per object.
/// <para>
/// Nothing on a page writes the Metaverse Objects, their values or the join columns before the page flush, so the
/// database answers a per-object lookup anywhere in the page exactly as it answered the page's batch: the cache is
/// read, never kept up to date. Whatever the page changes in memory (a pending disconnection, a join queued earlier in
/// the page) the caller still applies on top, as it does to a per-object answer.
/// </para>
/// <para>
/// Every read distinguishes "not covered" from "found nothing": a value, Metaverse Object or count the batch did not
/// cover returns false or null, and the caller falls back to the per-object query. Under-covering is therefore safe.
/// </para>
/// <para>
/// Value keys use default <see cref="object.Equals(object?)"/> semantics, as <see cref="ExportMatchCandidates"/> does:
/// strings compare ordinally (both case forms of a Text value are separate keys, each with its own database answer,
/// since the database does the folding), and a boxed <see cref="decimal"/> differing only in scale is the same key,
/// matching PostgreSQL's numeric equality.
/// </para>
/// </summary>
public sealed class JoinMatchCandidates
{
    private readonly Dictionary<JoinMatchLookupKey, Dictionary<object, IReadOnlyList<Guid>>> _idsByValue = [];
    private readonly Dictionary<Guid, MetaverseObject?> _loaded = [];
    private readonly Dictionary<Guid, int> _joinCounts = [];

    /// <summary>
    /// Records what the database holds for one sought value: the first Metaverse Objects of the lookup's type holding
    /// it, at most as many as the batch asked for, in the database's id order.
    /// </summary>
    public void AddLookedUp(JoinMatchLookupKey key, object value, IReadOnlyList<Guid> metaverseObjectIdsInIdOrder)
    {
        if (!_idsByValue.TryGetValue(key, out var byValue))
        {
            byValue = [];
            _idsByValue[key] = byValue;
        }

        byValue[value] = metaverseObjectIdsInIdOrder;
    }

    /// <summary>Whether the batch looked <paramref name="value"/> up for <paramref name="key"/>.</summary>
    public bool HasLookedUp(JoinMatchLookupKey key, object value) =>
        _idsByValue.TryGetValue(key, out var byValue) && byValue.ContainsKey(value);

    /// <summary>
    /// The distinct Metaverse Objects holding any of <paramref name="values"/>, the first <paramref name="take"/> in the
    /// database's id order: exactly what one per-object lookup of all the values returns. Null when any value was not
    /// covered, so the caller looks them up instead.
    /// </summary>
    /// <remarks>
    /// Exact as long as each value was covered with at least <paramref name="take"/> ids where it had that many: the
    /// first <paramref name="take"/> of a union are each among the first <paramref name="take"/> of a set holding them.
    /// </remarks>
    public IReadOnlyList<Guid>? GetMetaverseObjectIds(JoinMatchLookupKey key, IReadOnlyList<object> values, int take)
    {
        if (!_idsByValue.TryGetValue(key, out var byValue))
            return null;

        if (values.Count == 1)
        {
            if (!byValue.TryGetValue(values[0], out var single))
                return null;
            return single.Count <= take ? single : single.Take(take).ToList();
        }

        var union = new HashSet<Guid>();
        foreach (var value in values)
        {
            if (!byValue.TryGetValue(value, out var ids))
                return null;
            union.UnionWith(ids);
        }

        return union.Order(DatabaseUuidOrder.Instance).Take(take).ToList();
    }

    /// <summary>Records a Metaverse Object the batch loaded, or that the id has no row (<paramref name="metaverseObject"/> null).</summary>
    public void AddLoaded(Guid metaverseObjectId, MetaverseObject? metaverseObject) => _loaded[metaverseObjectId] = metaverseObject;

    /// <summary>
    /// The Metaverse Object the batch loaded for <paramref name="metaverseObjectId"/>, or null when it has no row; false
    /// when the batch did not load it.
    /// </summary>
    public bool TryGetLoaded(Guid metaverseObjectId, out MetaverseObject? metaverseObject) =>
        _loaded.TryGetValue(metaverseObjectId, out metaverseObject);

    /// <summary>Records how many Connected System Objects of the run's Connected System are joined to the Metaverse Object.</summary>
    public void AddJoinCount(Guid metaverseObjectId, int count) => _joinCounts[metaverseObjectId] = count;

    /// <summary>The joined count the batch read for <paramref name="metaverseObjectId"/>; false when it did not read one.</summary>
    public bool TryGetJoinCount(Guid metaverseObjectId, out int count) => _joinCounts.TryGetValue(metaverseObjectId, out count);

    /// <summary>
    /// PostgreSQL's <c>uuid</c> order: the 16 bytes in their written (big-endian) order, compared bytewise. .NET's own
    /// <see cref="Guid.CompareTo(Guid)"/> compares the first three groups as integers in another order.
    /// </summary>
    private sealed class DatabaseUuidOrder : IComparer<Guid>
    {
        public static readonly DatabaseUuidOrder Instance = new();

        public int Compare(Guid x, Guid y)
        {
            Span<byte> left = stackalloc byte[16];
            Span<byte> right = stackalloc byte[16];
            x.TryWriteBytes(left, bigEndian: true, out _);
            y.TryWriteBytes(right, bigEndian: true, out _);
            return left.SequenceCompareTo(right);
        }
    }
}
