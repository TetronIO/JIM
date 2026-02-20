using JIM.Models.Core;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// Defines a source for an object matching rule. Can hold either an attribute or an expression.
/// If it's an attribute, it will be either a ConnectedSystemAttribute (for CSO → MVO matching)
/// or a MetaverseAttribute (for MVO → CSO matching during export).
/// </summary>
public class ObjectMatchingRuleSource
{
    public int Id { get; set; }

    /// <summary>
    /// If multiple sources are defined, this determines evaluation order.
    /// Sources are evaluated in ascending order (0, 1, 2, etc.).
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Backlink to the parent ObjectMatchingRule.
    /// </summary>
    public int ObjectMatchingRuleId { get; set; }
    public ObjectMatchingRule ObjectMatchingRule { get; set; } = null!;

    /// <summary>
    /// For import matching: The Connected System attribute to use as the source value.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }
    public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }

    /// <summary>
    /// For export matching: The Metaverse attribute to use as the source value.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }
    public MetaverseAttribute? MetaverseAttribute { get; set; }

    /// <summary>
    /// If populated, denotes that an expression should be used to determine the match value.
    /// Expressions use DynamicExpresso syntax and can reference mv["AttributeName"] and cs["AttributeName"].
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// Parameter values for legacy function calls. Not used with expression-based matching.
    /// </summary>
    public List<ObjectMatchingRuleSourceParamValue> ParameterValues { get; set; } = new();

    /// <summary>
    /// Validates that the source is correctly configured.
    /// Must have exactly one of: attribute (CS or MV), or expression.
    /// </summary>
    public bool IsValid()
    {
        var hasAttribute = MetaverseAttribute != null || ConnectedSystemAttribute != null;
        var hasExpression = !string.IsNullOrWhiteSpace(Expression);

        // Must have exactly one source type
        if (hasExpression)
            return !hasAttribute; // Expression cannot coexist with attributes

        return hasAttribute; // Must have an attribute if no expression
    }
}
