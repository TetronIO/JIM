using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// Defines how data should flow from JIM to a connected system (or vice versa) for a specific attribute.
/// There can only be one mapping per target attribute.
/// i.e. you might be mapping a single attribute source attribute to a target attribute, or you might use a function or expression as a
/// source and in that be using multiple attributes as sources (parameters).
/// </summary>
public class SyncRuleMapping : IAuditable
{
    public int Id { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The type of security principal that created this entity.
    /// </summary>
    public ActivityInitiatorType CreatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that created this entity.
    /// Null for system-created (seeded) entities.
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of creation.
    /// Retained even if the principal is later deleted.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the entity was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The type of security principal that last modified this entity.
    /// </summary>
    public ActivityInitiatorType LastUpdatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that last modified this entity.
    /// </summary>
    public Guid? LastUpdatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of the last modification.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// A backlink to the parent SynchronisationRule.
    /// </summary>
    public SyncRule? SyncRule { get; set; }

    /// <summary>
    /// The sources that provide the value for the target attribute when the mapping is evaluated. 
    /// Supported scenarios:
    /// - Just one: for mapping a single attribute to the target attribute. i.e. attribute_1 => attribute
    /// - Just one: for using a single function to generate a value for the target attribute, i.e. Trim(attribute) => attribute
    /// - Just one: for using an expression to generate a value for the target attribute. i.e. attribute_1 ?? attribute_2 => attribute
    /// - Multiple: for using multiple function calls that chain through each other to generate a value for the target attribute.
    /// </summary>
    public List<SyncRuleMappingSource> Sources { get; } = new();

    /// <summary>
    /// For an import rule, this is where the imported attribute value ends up being assigned to in the Metaverse.
    /// </summary>
    public MetaverseAttribute? TargetMetaverseAttribute { get; set; }
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// For an export rule, this is where the exported attribute value ends up being assigned to in the Connected System.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? TargetConnectedSystemAttribute { get; set; }
    public int? TargetConnectedSystemAttributeId { get; set; }

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
