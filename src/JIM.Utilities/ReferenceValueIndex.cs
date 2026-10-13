// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities;

/// <summary>
/// Maps the reference values a target system uses for objects (a DN, an External ID) to what they identify, answering
/// "does this held reference name that object?" as the referenced anchor compares its values (#2051): ignoring case
/// unless the anchor is case-exact.
/// </summary>
/// <remarks>
/// A database query can only narrow by <see cref="LoweredValues"/> (the superset ignoring case), so the exact answer
/// for a case-exact value is given here, by <see cref="TryMatch"/>.
/// </remarks>
/// <typeparam name="T">What a value identifies.</typeparam>
public sealed class ReferenceValueIndex<T>
{
    private readonly Dictionary<string, T> _ignoringCase = new(StringComparer.Ordinal);
    private readonly Dictionary<string, T> _caseExact = new(StringComparer.Ordinal);

    /// <summary>
    /// Records that <paramref name="value"/> identifies <paramref name="target"/>; a later registration of the same
    /// value replaces an earlier one.
    /// </summary>
    /// <param name="caseExact">Whether the referenced anchor attribute is declared case-exact.</param>
    public void Register(string value, bool caseExact, T target)
    {
        if (caseExact)
            _caseExact[value] = target;
        else
            _ignoringCase[IdentifierKey.For(value, caseExact: false)] = target;
    }

    /// <summary>
    /// Every registered value, lower-cased as SQL's <c>lower()</c> lowers them, for narrowing a query.
    /// </summary>
    public IReadOnlyCollection<string> LoweredValues =>
        _ignoringCase.Keys.Concat(_caseExact.Keys.Select(value => IdentifierKey.For(value, caseExact: false))).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// What a held reference value identifies, if anything.
    /// </summary>
    public bool TryMatch(string heldValue, out T target) =>
        _caseExact.TryGetValue(heldValue, out target!) ||
        _ignoringCase.TryGetValue(IdentifierKey.For(heldValue, caseExact: false), out target!);
}
