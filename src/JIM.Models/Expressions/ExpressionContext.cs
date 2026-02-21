namespace JIM.Models.Expressions;

/// <summary>
/// Context for expression evaluation containing attribute accessors.
/// Provides access to Metaverse Object (mv) and Connected System Object (cs) attributes.
/// </summary>
public class ExpressionContext
{
    /// <summary>
    /// Metaverse Object attribute accessor.
    /// Use mv["Attribute Name"] in expressions to access MVO attributes.
    /// </summary>
    public AttributeAccessor Mv { get; }

    /// <summary>
    /// Connected System Object attribute accessor.
    /// Use cs["Attribute Name"] in expressions to access CSO attributes.
    /// </summary>
    public AttributeAccessor Cs { get; }

    /// <summary>
    /// Creates a new expression context with the specified attribute values.
    /// </summary>
    /// <param name="metaverseAttributes">Dictionary of Metaverse Object attribute values keyed by attribute name.</param>
    /// <param name="connectedSystemAttributes">Dictionary of Connected System Object attribute values keyed by attribute name.</param>
    public ExpressionContext(
        IDictionary<string, object?>? metaverseAttributes = null,
        IDictionary<string, object?>? connectedSystemAttributes = null)
    {
        Mv = new AttributeAccessor(metaverseAttributes ?? new Dictionary<string, object?>());
        Cs = new AttributeAccessor(connectedSystemAttributes ?? new Dictionary<string, object?>());
    }
}
