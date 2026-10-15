// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Expressions;
using JIM.Utilities;

namespace JIM.Application.Expressions;

/// <summary>
/// Builds the attribute dictionary an expression's <c>cs[...]</c> or <c>mv[...]</c> reads (#2053): every value of each
/// attribute, read as <see cref="ExpressionValues.FromAttribute"/> reads them (no value as null, one value as itself,
/// several as every distinct value in a fixed order). Every synchronisation path builds through here, so import,
/// export, derived flows, drift detection, Object Matching and the tester read a multi-valued attribute the same way.
/// </summary>
/// <remarks>Attribute names are case-insensitive, as everywhere else in Attribute Flow.</remarks>
internal sealed class ExpressionAttributeDictionary
{
    private readonly Dictionary<string, List<object?>> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds one stored value of <paramref name="attributeName"/>; a null value records the attribute with no value. A date
    /// is added in the form JIM holds it (#2153, N4): UTC, to the microsecond, whatever Kind the database read it as, so
    /// two stored forms of one date are one value and an expression never sees the host's offset.
    /// </summary>
    internal void Add(string attributeName, object? value)
    {
        if (value is DateTime date)
            value = DateTimeAttributeValue.ToCanonical(date);

        if (!_values.TryGetValue(attributeName, out var values))
        {
            values = [];
            _values[attributeName] = values;
        }

        values.Add(value);
    }

    /// <summary>The dictionary an <see cref="AttributeAccessor"/> reads.</summary>
    internal Dictionary<string, object?> Build()
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in _values)
            attributes[name] = ExpressionValues.FromAttribute(name, values);

        return attributes;
    }
}
