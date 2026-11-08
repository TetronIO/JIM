// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The import's answer to "does JIM already hold an object with this External ID?", keyed by <c>CsoLookupKey</c>.
/// </summary>
/// <remarks>
/// <para>
/// It comes in two forms. A Full Import, which reads every object, loads the import state of every Connected System
/// Object once (<see cref="ForEveryObject"/>): a key it does not hold names a new object. A Delta Import reads only what
/// changed, so loading every object would cost it far more than the handful it returns (#2705: 3.6 s and the whole
/// dictionary in memory at 525,000 objects, for one or two changes). It looks up only the External IDs each page returns
/// (<see cref="ForLookedUpKeys"/>), and holds the answer for exactly those keys.
/// </para>
/// <para>
/// A key the page did not look up is not a key that names no object; it is a key nobody asked the database about, and
/// treating it as "new" would create a duplicate of an object JIM already holds. So asking for one throws, and the import
/// fails, rather than answering "not found". Every key the import asks about is collected before it asks, so this is a
/// guard against a fault in that collection, not a path ordinary running takes.
/// </para>
/// </remarks>
public sealed class CsoImportStateLookup
{
    private readonly Dictionary<string, CsoImportStateLookupEntry> _entries;

    /// <summary>The keys looked up, each answered found or not; null when the lookup holds every object.</summary>
    private readonly HashSet<string>? _lookedUpKeys;

    private CsoImportStateLookup(Dictionary<string, CsoImportStateLookupEntry> entries, HashSet<string>? lookedUpKeys)
    {
        _entries = entries;
        _lookedUpKeys = lookedUpKeys;
    }

    /// <summary>
    /// A lookup holding the import state of every Connected System Object: a key it does not hold names no object.
    /// </summary>
    /// <param name="entries">Every object's entry, keyed by <c>CsoLookupKey</c>, compared as the keys were built to be.</param>
    public static CsoImportStateLookup ForEveryObject(Dictionary<string, CsoImportStateLookupEntry> entries) => new(entries, lookedUpKeys: null);

    /// <summary>
    /// An empty lookup that answers only for the keys later <see cref="Add"/>ed to it.
    /// </summary>
    /// <param name="keyComparer">How keys compare: <c>CsoLookupKey.Comparer</c>.</param>
    public static CsoImportStateLookup ForLookedUpKeys(IEqualityComparer<string> keyComparer) =>
        new(new Dictionary<string, CsoImportStateLookupEntry>(keyComparer), new HashSet<string>(keyComparer));

    /// <summary>
    /// Whether the lookup holds every object JIM holds, so a key it does not hold names a new object.
    /// </summary>
    public bool HoldsEveryObject => _lookedUpKeys == null;

    /// <summary>
    /// How many objects the lookup found.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// How many keys were looked up; for a lookup holding every object, how many objects it holds.
    /// </summary>
    public int LookedUpKeyCount => _lookedUpKeys?.Count ?? _entries.Count;

    /// <summary>
    /// Records the answer for a set of keys looked up together: those in <paramref name="found"/> name the object given,
    /// every other key in <paramref name="lookedUpKeys"/> names none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The lookup holds every object, or an answer is for a key that was not
    /// looked up.</exception>
    public void Add(IEnumerable<string> lookedUpKeys, IReadOnlyDictionary<string, CsoImportStateLookupEntry> found)
    {
        if (_lookedUpKeys == null)
            throw new InvalidOperationException("The External ID lookup already holds every object, so it takes no answers for individual External IDs.");

        _lookedUpKeys.UnionWith(lookedUpKeys);
        foreach (var (key, entry) in found)
        {
            if (!_lookedUpKeys.Contains(key))
                throw new InvalidOperationException("The External ID lookup was given an object for an External ID it did not ask about. The import stopped rather than match an object by it.");

            _entries.TryAdd(key, entry);
        }
    }

    /// <summary>
    /// Whether the lookup can answer for a key: it was looked up (found or not), or the lookup holds every object. Unlike
    /// <see cref="TryGetValue"/>, never throws, so a later pass over the same page can ask only for what is still missing.
    /// </summary>
    public bool WasLookedUp(string key) => _lookedUpKeys == null || _lookedUpKeys.Contains(key);

    /// <summary>
    /// The object a key names, if JIM holds one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key was not looked up, so the lookup cannot say whether JIM holds
    /// an object for it.</exception>
    public bool TryGetValue(string key, out CsoImportStateLookupEntry entry)
    {
        EnsureLookedUp(key);
        return _entries.TryGetValue(key, out entry);
    }

    /// <summary>
    /// Whether JIM holds an object for a key.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key was not looked up, so the lookup cannot say.</exception>
    public bool ContainsKey(string key)
    {
        EnsureLookedUp(key);
        return _entries.ContainsKey(key);
    }

    private void EnsureLookedUp(string key)
    {
        if (_lookedUpKeys != null && !_lookedUpKeys.Contains(key))
            throw new InvalidOperationException(
                "The import asked whether JIM holds an object for an External ID it had not looked up for this page. " +
                "Treating it as a new object could create a duplicate of one JIM already holds, so the import stopped. This is a fault in JIM.");
    }
}
