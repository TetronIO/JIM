// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// Provides dictionary-style attribute access for expressions.
/// Supports mv["Display Name"] and cs["employeeId"] syntax in expressions.
/// </summary>
public class AttributeAccessor
{
    private readonly Dictionary<string, object?> _attributes;

    /// <summary>
    /// Creates a new attribute accessor with the specified attribute values.
    /// </summary>
    /// <param name="attributes">Dictionary of attribute values keyed by attribute name.</param>
    /// <remarks>
    /// Attribute name lookups are case-insensitive by design. Under JIM's case sensitivity strategy,
    /// schema and identifier lookups are forgiving (only attribute <em>values</em> are matched
    /// case-sensitively). The incoming dictionary is normalised into an <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// dictionary here so the behaviour holds no matter how the caller built it; callers that supply a
    /// default (case-sensitive) dictionary, such as the Test Expression API, resolve identically to sync.
    /// </remarks>
    public AttributeAccessor(IDictionary<string, object?> attributes)
    {
        if (attributes == null)
        {
            _attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        // Fast path: the hot synchronisation paths already build their dictionaries with the
        // case-insensitive comparer, so reuse the instance and avoid an allocation+copy per object.
        if (attributes is Dictionary<string, object?> dictionary &&
            ReferenceEquals(dictionary.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            _attributes = dictionary;
            return;
        }

        // Otherwise normalise into a case-insensitive dictionary so name lookups stay forgiving no
        // matter how the caller built it (for example the Test Expression API, which deserialises into
        // a default, case-sensitive dictionary). Copy via the indexer rather than the dictionary-copy
        // constructor so case-variant duplicate keys ("Department" and "department") collapse
        // last-write-wins instead of throwing.
        _attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes)
            _attributes[attribute.Key] = attribute.Value;
    }

    /// <summary>
    /// Gets the value of an attribute by name: null for no value, the value itself for one, and
    /// <see cref="ExpressionValues"/> for several (#2053). A list or array supplied by the caller reads exactly as the
    /// synchronisation builders' values do (<see cref="ExpressionValues.FromAttribute"/>).
    /// </summary>
    /// <param name="attributeName">The name of the attribute to retrieve.</param>
    /// <returns>The attribute's value or values, or null if not found. A Binary value is a copy (#2153, N6): an
    /// expression is handed bytes of its own, so nothing it does can change the attribute value they came from.</returns>
    public object? this[string attributeName]
    {
        get
        {
            if (!_attributes.TryGetValue(attributeName, out var value))
                return null;

            return value switch
            {
                byte[] bytes => (byte[])bytes.Clone(),
                ExpressionValues values when values.Any(v => v is byte[]) => ExpressionValues.FromAttribute(values.SourceName, values),
                ExpressionValues values => values,
                string => value,
                System.Collections.IEnumerable collection => ExpressionValues.FromAttribute(attributeName, collection.Cast<object?>()),
                _ => value
            };
        }
    }
}
