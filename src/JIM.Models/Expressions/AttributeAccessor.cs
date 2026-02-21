namespace JIM.Models.Expressions;

/// <summary>
/// Provides dictionary-style attribute access for expressions.
/// Supports mv["Display Name"] and cs["employeeId"] syntax in expressions.
/// </summary>
public class AttributeAccessor
{
    private readonly IDictionary<string, object?> _attributes;

    /// <summary>
    /// Creates a new attribute accessor with the specified attribute values.
    /// </summary>
    /// <param name="attributes">Dictionary of attribute values keyed by attribute name.</param>
    public AttributeAccessor(IDictionary<string, object?> attributes)
    {
        _attributes = attributes ?? new Dictionary<string, object?>();
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
