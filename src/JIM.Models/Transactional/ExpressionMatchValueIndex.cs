// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What an Object Matching Rule's expression computes for each unjoined object of a type in a target Connected System
/// (#2052): the index export matching looks a Metaverse Object's values up in, when the rule computes the Connected
/// System side with an expression rather than reading an attribute a query could compare.
/// </summary>
/// <remarks>
/// <para>
/// Built once per run, on the first provisioning decision that needs it, and kept for the run in
/// <see cref="ExportEvaluationCache.ExpressionMatchIndexes"/>. Objects claimed during the run are removed, and every
/// candidate is reloaded and checked to be still unjoined before it is used, so an entry that has gone stale can never
/// be joined. What the index cannot see is an object that became unjoined after it was built; such an object is
/// considered by the next run's index.
/// </para>
/// <para>
/// An object whose expression could not be worked out is counted rather than indexed: the index cannot say what that
/// object would match, so a lookup that finds nothing cannot be trusted while any are counted
/// (<see cref="IsComplete"/>).
/// </para>
/// <para>
/// Values compare as <see cref="ExportMatchCandidates"/> compares them: boxed values by their own equality (so
/// <c>5.0m</c> and <c>5.00m</c> are one value), and text ordinally, ignoring case when the rule does.
/// </para>
/// </remarks>
public sealed class ExpressionMatchValueIndex
{
    private readonly Dictionary<object, List<Guid>> _idsByValue;
    private readonly Dictionary<Guid, List<object>> _valuesById = [];
    private int _uncheckableCount;

    /// <param name="caseSensitive">Whether text values compare case-sensitively, as the rule says.</param>
    public ExpressionMatchValueIndex(bool caseSensitive)
    {
        _idsByValue = new Dictionary<object, List<Guid>>(caseSensitive ? EqualityComparer<object>.Default : CaseInsensitiveTextComparer.Instance);
    }

    /// <summary>How many objects could not be checked because the expression could not be worked out for them.</summary>
    public int UncheckableCount => _uncheckableCount;

    /// <summary>Whether every object the index was built from could be checked, so that a value it does not hold is
    /// held by none of them.</summary>
    public bool IsComplete => _uncheckableCount == 0;

    /// <summary>The first object that could not be checked, and why: what an error points the administrator at.</summary>
    public (Guid ConnectedSystemObjectId, string Reason)? FirstUncheckable { get; private set; }

    /// <summary>Records that the expression computes <paramref name="value"/> for the object. Repeats are ignored.</summary>
    public void Add(object value, Guid connectedSystemObjectId)
    {
        if (!_idsByValue.TryGetValue(value, out var ids))
        {
            ids = [];
            _idsByValue[value] = ids;
        }

        if (ids.Contains(connectedSystemObjectId))
            return;

        ids.Add(connectedSystemObjectId);
        if (!_valuesById.TryGetValue(connectedSystemObjectId, out var values))
        {
            values = [];
            _valuesById[connectedSystemObjectId] = values;
        }

        values.Add(value);
    }

    /// <summary>Records an object the expression could not be worked out for, and why.</summary>
    public void MarkUncheckable(Guid connectedSystemObjectId, string reason)
    {
        _uncheckableCount++;
        FirstUncheckable ??= (connectedSystemObjectId, reason);
    }

    /// <summary>The objects the expression computes <paramref name="value"/> for, in the order added.</summary>
    public IReadOnlyList<Guid> GetCandidates(object value) =>
        _idsByValue.TryGetValue(value, out var ids) ? ids : [];

    /// <summary>Removes an object from every value it was indexed under, once it has been claimed or found no longer
    /// eligible, so no later lookup in the run is offered it.</summary>
    public void Remove(Guid connectedSystemObjectId)
    {
        if (!_valuesById.Remove(connectedSystemObjectId, out var values))
            return;

        foreach (var value in values)
        {
            var ids = _idsByValue[value];
            ids.Remove(connectedSystemObjectId);
            if (ids.Count == 0)
                _idsByValue.Remove(value);
        }
    }

    /// <summary>Text compared ordinally ignoring case; every other value by its own equality.</summary>
    private sealed class CaseInsensitiveTextComparer : IEqualityComparer<object>
    {
        public static readonly CaseInsensitiveTextComparer Instance = new();

        public new bool Equals(object? x, object? y) => x is string a && y is string b
            ? StringComparer.OrdinalIgnoreCase.Equals(a, b)
            : EqualityComparer<object>.Default.Equals(x, y);

        public int GetHashCode(object obj) => obj is string text
            ? StringComparer.OrdinalIgnoreCase.GetHashCode(text)
            : obj.GetHashCode();
    }
}
