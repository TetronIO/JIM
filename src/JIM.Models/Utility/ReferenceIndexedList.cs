// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections;

namespace JIM.Models.Utility;

/// <summary>
/// An ordered list of entities that answers <see cref="Contains"/> in constant time once it is large, for a navigation
/// collection that can hold hundreds of thousands of them, such as a group's member values (#2598). EF Core asks whether
/// a collection already holds an entity before adding each one it materialises or fixes up into it, tracked or not, and
/// for a <see cref="List{T}"/> or <see cref="System.Collections.ObjectModel.Collection{T}"/> it answers by scanning the
/// list itself, so loading one group took time that grew with the square of its membership: 41 s for 200,000 members,
/// against 0.3 s to read the same rows. For any other collection type EF Core asks the collection, so this type is
/// deliberately neither.
/// </summary>
/// <remarks>
/// <para>
/// Membership and position are by reference, as EF Core's own check is. The entities held here do not override
/// <see cref="object.Equals(object)"/>, so this is also what <see cref="List{T}"/> gave them. Order, duplicates and
/// indexing behave as a list's.
/// </para>
/// <para>
/// The index is built only once the list passes <see cref="IndexThreshold"/> items, so the many objects with a handful of
/// values pay nothing for it, and it is maintained by the writing methods alone, so concurrent readers of a list nobody
/// is writing to are as safe as they are with a <see cref="List{T}"/>.
/// </para>
/// </remarks>
public class ReferenceIndexedList<T> : IList<T>, IReadOnlyList<T> where T : class
{
    /// <summary>
    /// The size above which membership is answered from the index rather than by a scan.
    /// </summary>
    public const int IndexThreshold = 32;

    private readonly List<T> _items;

    // How many times each item occurs, by reference; null while the list is at or below IndexThreshold.
    private Dictionary<T, int>? _occurrences;

    public ReferenceIndexedList()
    {
        _items = [];
    }

    public ReferenceIndexedList(IEnumerable<T> items)
    {
        _items = [.. items];
        IndexIfLarge();
    }

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public T this[int index]
    {
        get => _items[index];
        set
        {
            var replaced = _items[index];
            _items[index] = value;
            if (_occurrences == null)
                return;
            Unindex(replaced);
            Index(value);
        }
    }

    public void Add(T item)
    {
        _items.Add(item);
        if (_occurrences != null)
            Index(item);
        else
            IndexIfLarge();
    }

    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
            Add(item);
    }

    public void Insert(int index, T item)
    {
        _items.Insert(index, item);
        if (_occurrences != null)
            Index(item);
        else
            IndexIfLarge();
    }

    public bool Contains(T item)
    {
        if (_occurrences != null && item != null)
            return _occurrences.ContainsKey(item);
        return IndexByReference(item) >= 0;
    }

    public int IndexOf(T item) => IndexByReference(item);

    public bool Remove(T item)
    {
        if (_occurrences != null && item != null && !_occurrences.ContainsKey(item))
            return false;
        var index = IndexByReference(item);
        if (index < 0)
            return false;
        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index)
    {
        var item = _items[index];
        _items.RemoveAt(index);
        if (_occurrences == null)
            return;
        Unindex(item);
        DropIndexIfSmall();
    }

    public int RemoveAll(Predicate<T> match)
    {
        if (_occurrences == null)
            return _items.RemoveAll(match);

        // Unindex each match as the list finds it, so that removing a few values from a large list costs what it does
        // on a list rather than a rebuild of the index. A predicate that throws part way leaves the list's contents
        // undefined, so the index is then rebuilt from whatever the list holds.
        var completed = false;
        try
        {
            var removed = _items.RemoveAll(item =>
            {
                if (!match(item))
                    return false;
                Unindex(item);
                return true;
            });
            completed = true;
            DropIndexIfSmall();
            return removed;
        }
        finally
        {
            if (!completed)
            {
                _occurrences = null;
                IndexIfLarge();
            }
        }
    }

    public void Clear()
    {
        _items.Clear();
        _occurrences = null;
    }

    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private int IndexByReference(T? item)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], item))
                return i;
        }
        return -1;
    }

    private void IndexIfLarge()
    {
        if (_items.Count <= IndexThreshold)
            return;
        _occurrences = new Dictionary<T, int>(_items.Count, ReferenceEqualityComparer.Instance);
        foreach (var item in _items)
            Index(item);
    }

    private void DropIndexIfSmall()
    {
        if (_items.Count <= IndexThreshold)
            _occurrences = null;
    }

    private void Index(T item)
    {
        if (item == null)
            return;
        _occurrences![item] = _occurrences.GetValueOrDefault(item) + 1;
    }

    private void Unindex(T item)
    {
        if (item == null || !_occurrences!.TryGetValue(item, out var count))
            return;
        if (count == 1)
            _occurrences.Remove(item);
        else
            _occurrences[item] = count - 1;
    }
}
