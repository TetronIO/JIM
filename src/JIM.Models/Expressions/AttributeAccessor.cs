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
    /// Gets the value of an attribute by name.
    /// Returns null if the attribute is not found.
    /// </summary>
    /// <param name="attributeName">The name of the attribute to retrieve.</param>
    /// <returns>The attribute value, or null if not found.</returns>
    public object? this[string attributeName]
    {
        get => _attributes.TryGetValue(attributeName, out var value) ? value : null;
    }

    /// <summary>
    /// Checks if an attribute exists in the accessor.
    /// </summary>
    /// <param name="attributeName">The name of the attribute to check.</param>
    /// <returns>True if the attribute exists, false otherwise.</returns>
    public bool HasAttribute(string attributeName)
    {
        return _attributes.ContainsKey(attributeName);
    }

    /// <summary>
    /// Gets all attribute names available in this accessor.
    /// </summary>
    public IEnumerable<string> AttributeNames => _attributes.Keys;
}
