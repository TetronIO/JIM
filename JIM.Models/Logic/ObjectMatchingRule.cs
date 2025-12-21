using JIM.Models.Core;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// Defines how objects should be matched/correlated between a Connected System and the Metaverse.
/// Object matching rules are used during import (to join CSOs to MVOs) and during export evaluation
/// (to find existing CSOs for provisioning).
///
/// Rules can belong to EITHER:
/// - A ConnectedSystemObjectType (Mode A - default): Used for all sync rules of that object type
/// - A SyncRule (Mode B - advanced): Used only for that specific sync rule
///
/// Multiple rules can be defined with different Order values for cascading/fallback matching.
/// </summary>
public class ObjectMatchingRule
{
    public int Id { get; set; }

    /// <summary>
    /// The order in which this rule should be evaluated relative to other rules.
    /// Rules are evaluated in ascending order (0, 1, 2, etc.) until a match is found.
    /// </summary>
    public int Order { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional backlink to a SyncRule when this rule is defined at the sync rule level (Mode B).
    /// Mutually exclusive with ConnectedSystemObjectType.
    /// </summary>
    public int? SyncRuleId { get; set; }
    public SyncRule? SyncRule { get; set; }

    /// <summary>
    /// Optional backlink to a ConnectedSystemObjectType when this rule is defined at the object type level (Mode A).
    /// Mutually exclusive with SyncRule.
    /// </summary>
    public int? ConnectedSystemObjectTypeId { get; set; }
    public ConnectedSystemObjectType? ConnectedSystemObjectType { get; set; }

    /// <summary>
    /// The sources that provide the value(s) to match against. Typically a Connected System attribute
    /// or a function that transforms attribute values.
    /// </summary>
    public List<ObjectMatchingRuleSource> Sources { get; set; } = new();

    /// <summary>
    /// The Metaverse attribute to match against. The value from Sources will be compared
    /// to this attribute's value on Metaverse Objects to find a match.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }
    public MetaverseAttribute? TargetMetaverseAttribute { get; set; }

    /// <summary>
    /// When true (default), attribute value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// Validates that the rule is correctly configured.
    /// </summary>
    public bool IsValid()
    {
        // Must belong to exactly one parent (SyncRule XOR ConnectedSystemObjectType)
        var hasSyncRule = SyncRuleId.HasValue || SyncRule != null;
        var hasObjectType = ConnectedSystemObjectTypeId.HasValue || ConnectedSystemObjectType != null;

        if (hasSyncRule == hasObjectType)
            return false; // Must have exactly one, not both or neither

        // Must have at least one source
        if (Sources.Count == 0)
            return false;

        // Must have a target attribute
        if (TargetMetaverseAttributeId == null && TargetMetaverseAttribute == null)
            return false;

        return true;
    }

    /// <summary>
    /// Helper method to provide a description for the user on what type of source configuration this is.
    /// </summary>
    public SyncRuleMappingSourcesType GetSourceType()
    {
        if (Sources.Count == 0)
            return SyncRuleMappingSourcesType.NotSet;

        if (Sources.All(s => s.ConnectedSystemAttribute != null || s.MetaverseAttribute != null))
            return SyncRuleMappingSourcesType.AttributeMapping;

        if (Sources.All(s => !string.IsNullOrWhiteSpace(s.Expression)))
            return SyncRuleMappingSourcesType.ExpressionMapping;

        return SyncRuleMappingSourcesType.AdvancedMapping;
    }
}
