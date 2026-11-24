// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections;

namespace JIM.TestGraphService;

/// <summary>
/// A list in insertion order that also finds, and removes, an item by its key in constant time, so a simulated tenant
/// holding a million objects (or a group holding a million members) answers each request without scanning.
/// </summary>
/// <remarks>
/// A removed item leaves an empty slot rather than shifting what follows, so a page's position (the <c>$skiptoken</c>
/// Graph issues) still points where it did when the page was read, however many items are removed meanwhile.
/// </remarks>
public abstract class IndexedList<TKey, TItem> : IReadOnlyCollection<TItem> where TKey : notnull
{
    private readonly List<(bool Live, TItem Item)> _slots = [];
    private readonly Dictionary<TKey, int> _positions;

    protected IndexedList(IEqualityComparer<TKey>? comparer = null)
    {
        _positions = new Dictionary<TKey, int>(comparer);
    }

    /// <summary>How many items are held.</summary>
    public int Count => _positions.Count;

    /// <summary>How many positions a page can start from: every item ever added, removed ones included.</summary>
    public int Extent => _slots.Count;

    protected abstract TKey KeyOf(TItem item);

    /// <summary>Adds an item, or replaces the one with its key in place.</summary>
    public void Add(TItem item)
    {
        var key = KeyOf(item);
        if (_positions.TryGetValue(key, out var position))
        {
            _slots[position] = (true, item);
            return;
        }

        _positions[key] = _slots.Count;
        _slots.Add((true, item));
    }

    public bool Contains(TKey key) => _positions.ContainsKey(key);

    public bool TryGet(TKey key, out TItem item)
    {
        if (_positions.TryGetValue(key, out var position))
        {
            item = _slots[position].Item;
            return true;
        }

        item = default!;
        return false;
    }

    public bool Remove(TKey key)
    {
        if (!_positions.Remove(key, out var position))
            return false;

        _slots[position] = (false, default!);
        return true;
    }

    public int RemoveAll(Func<TItem, bool> match)
    {
        var keys = this.Where(match).Select(KeyOf).ToList();
        foreach (var key in keys)
            Remove(key);
        return keys.Count;
    }

    /// <summary>
    /// Up to <paramref name="top"/> items from <paramref name="start"/>, and the position the next page starts from:
    /// <see cref="Extent"/> when this page reaches the end.
    /// </summary>
    public List<TItem> Page(int start, int top, out int next)
    {
        var page = new List<TItem>(Math.Min(top, Count));
        var position = Math.Max(start, 0);
        while (position < _slots.Count && page.Count < top)
        {
            var (live, item) = _slots[position++];
            if (live)
                page.Add(item);
        }

        // Past trailing removed slots, so a page that ends on the last item reports no further page.
        while (position < _slots.Count && !_slots[position].Live)
            position++;

        next = position;
        return page;
    }

    public IEnumerator<TItem> GetEnumerator()
    {
        foreach (var (live, item) in _slots)
        {
            if (live)
                yield return item;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Users or groups, found by id.</summary>
public sealed class GraphObjectList : IndexedList<string, System.Text.Json.Nodes.JsonObject>
{
    protected override string KeyOf(System.Text.Json.Nodes.JsonObject item) => (string)item["id"]!;
}

/// <summary>A group's members, each a (type, id) pair, found by id.</summary>
public sealed class MemberList : IndexedList<string, (string Type, string Id)>
{
    protected override string KeyOf((string Type, string Id) item) => item.Id;
}

/// <summary>A group's owners, by id.</summary>
public sealed class OwnerList : IndexedList<string, string>
{
    protected override string KeyOf(string item) => item;
}
