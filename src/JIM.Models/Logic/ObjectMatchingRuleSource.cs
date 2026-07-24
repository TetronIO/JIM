// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// Defines a source for an Object Matching Rule. Can hold either a Connected System attribute or an expression.
/// The Metaverse side of the match always comes from the parent rule's Target Metaverse Attribute
/// (<see cref="ObjectMatchingRule.TargetMetaverseAttribute"/>), for both import and export matching.
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
    /// If populated, denotes that an expression should be used to determine the match value.
    /// Expressions use DynamicExpresso syntax and can reference mv["AttributeName"] and cs["AttributeName"].
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// Validates that the source is correctly configured.
    /// Must have exactly one of: attribute (CS), or expression.
    /// </summary>
    public bool IsValid()
    {
        var hasAttribute = ConnectedSystemAttribute != null;
        var hasExpression = !string.IsNullOrWhiteSpace(Expression);

        // Must have exactly one source type
        if (hasExpression)
            return !hasAttribute; // Expression cannot coexist with attributes

        return hasAttribute; // Must have an attribute if no expression
    }
}
