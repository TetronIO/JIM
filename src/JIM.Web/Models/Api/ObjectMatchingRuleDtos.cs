using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// DTO for an object matching rule source.
/// </summary>
public class ObjectMatchingRuleSourceDto
{
    /// <summary>
    /// The unique identifier of the source.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The order of this source in the rule (for function chaining).
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// The Connected System attribute ID (for import matching).
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The Connected System attribute name (for display).
    /// </summary>
    public string? ConnectedSystemAttributeName { get; set; }

    /// <summary>
    /// The Metaverse attribute ID (for export matching).
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// The Metaverse attribute name (for display).
    /// </summary>
    public string? MetaverseAttributeName { get; set; }

    /// <summary>
    /// Creates a DTO from an ObjectMatchingRuleSource entity.
    /// </summary>
    public static ObjectMatchingRuleSourceDto FromEntity(ObjectMatchingRuleSource source)
    {
        return new ObjectMatchingRuleSourceDto
        {
            Id = source.Id,
            Order = source.Order,
            ConnectedSystemAttributeId = source.ConnectedSystemAttributeId,
            ConnectedSystemAttributeName = source.ConnectedSystemAttribute?.Name,
            MetaverseAttributeId = source.MetaverseAttributeId,
            MetaverseAttributeName = source.MetaverseAttribute?.Name
        };
    }
}

/// <summary>
/// DTO for an object matching rule.
/// </summary>
public class ObjectMatchingRuleDto
{
    /// <summary>
    /// The unique identifier of the matching rule.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The evaluation order for this rule (lower values are evaluated first).
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// The Connected System Object Type this rule belongs to.
    /// </summary>
    public int ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// The name of the Connected System Object Type.
    /// </summary>
    public string? ConnectedSystemObjectTypeName { get; set; }

    /// <summary>
    /// The Metaverse Object Type to search when evaluating this rule (simple mode only).
    /// </summary>
    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The name of the Metaverse Object Type (for display).
    /// </summary>
    public string? MetaverseObjectTypeName { get; set; }

    /// <summary>
    /// The Sync Rule this rule belongs to (advanced mode only).
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The target Metaverse attribute ID to match against.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// The target Metaverse attribute name.
    /// </summary>
    public string? TargetMetaverseAttributeName { get; set; }

    /// <summary>
    /// The sources for this matching rule.
    /// </summary>
    public List<ObjectMatchingRuleSourceDto> Sources { get; set; } = new();

    /// <summary>
    /// When true (default), attribute value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// When the rule was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Creates a DTO from an ObjectMatchingRule entity.
    /// </summary>
    public static ObjectMatchingRuleDto FromEntity(ObjectMatchingRule rule)
    {
        return new ObjectMatchingRuleDto
        {
            Id = rule.Id,
            Order = rule.Order,
            ConnectedSystemObjectTypeId = rule.ConnectedSystemObjectTypeId ?? 0,
            ConnectedSystemObjectTypeName = rule.ConnectedSystemObjectType?.Name,
            MetaverseObjectTypeId = rule.MetaverseObjectTypeId,
            MetaverseObjectTypeName = rule.MetaverseObjectType?.Name,
            SyncRuleId = rule.SyncRuleId,
            TargetMetaverseAttributeId = rule.TargetMetaverseAttributeId,
            TargetMetaverseAttributeName = rule.TargetMetaverseAttribute?.Name,
            Sources = rule.Sources.Select(ObjectMatchingRuleSourceDto.FromEntity).ToList(),
            CaseSensitive = rule.CaseSensitive,
            Created = rule.Created
        };
    }
}
