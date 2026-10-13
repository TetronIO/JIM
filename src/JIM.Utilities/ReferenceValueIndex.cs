// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities;

/// <summary>
/// Maps the reference values a target system uses for objects (a DN, an External ID) to what they identify, answering
/// "does this held reference name that object?" as the referenced anchor compares its values (#2051): ignoring case
/// unless the anchor is case-exact.
/// </summary>
/// <remarks>
/// A database query narrows by <see cref="Values"/> ignoring case (the database folding both sides), a superset of
/// both kinds, so the answer for each held value is given here, by <see cref="TryMatch"/>.
/// </remarks>
/// <typeparam name="T">What a value identifies.</typeparam>
public sealed class ReferenceValueIndex<T>
{
    private readonly Dictionary<string, T> _ignoringCase = new(StringComparer.Ordinal);
    private readonly Dictionary<string, T> _caseExact = new(StringComparer.Ordinal);
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// Records that <paramref name="value"/> identifies <paramref name="target"/>; a later registration of the same
    /// value replaces an earlier one.
    /// </summary>
    /// <param name="caseExact">Whether the referenced anchor attribute is declared case-exact.</param>
    public void Register(string value, bool caseExact, T target)
    {
        _values.Add(value);
        if (caseExact)
            _caseExact[value] = target;
        else
            _ignoringCase[IdentifierKey.For(value, caseExact: false)] = target;
    }

    /// <summary>
    /// Every registered value as registered, for narrowing a query ignoring case.
    /// </summary>
    public IReadOnlyCollection<string> Values => _values;

    /// <summary>
    /// What a held reference value identifies, if anything.
    /// </summary>
    public bool TryMatch(string heldValue, out T target) =>
        _caseExact.TryGetValue(heldValue, out target!) ||
        _ignoringCase.TryGetValue(IdentifierKey.For(heldValue, caseExact: false), out target!);
}
