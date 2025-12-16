using JIM.Models.Core;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// Defines a source for a sync rule mapping. Can hold either an attribute or an expression.
/// If it is an attribute, then depending on the direction of the sync rule (import/export), then it'll
/// be either the ConnectedSystemAttribute or MetaverseAttribute that needs to be populated.
/// </summary>
public class SyncRuleMappingSource
{
    public int Id { get; set; }

    /// <summary>
    /// If multiple sources are defined against a mapping (for chaining scenarios), the order matters.
    /// Sources will be evaluated in order, i.e order 0 item will be evaluated first, then 1, etc.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// For Export sync rules only: If populated, denotes that a Metaverse Attribute should be used to set the target attribute value.
    /// </summary>
    public MetaverseAttribute? MetaverseAttribute { get; set; }
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// For Import sync rules only: If populated, denotes that a Connected System Attribute should be used to set the target attribute value.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// If populated, denotes that an expression should be used to determine the target attribute value.
    /// Expressions use DynamicExpresso syntax and can reference mv["AttributeName"] and cs["AttributeName"].
    /// Example: "CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"
    /// </summary>
    public string? Expression { get; set; }

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