using System.ComponentModel.DataAnnotations;
using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a SyncRuleMapping.
/// </summary>
public class SyncRuleMappingDto
{
    public int Id { get; set; }
    public DateTime Created { get; set; }
    public int? TargetMetaverseAttributeId { get; set; }
    public string? TargetMetaverseAttributeName { get; set; }
    public int? TargetConnectedSystemAttributeId { get; set; }
    public string? TargetConnectedSystemAttributeName { get; set; }
    public string SourceType { get; set; } = null!;
    public List<SyncRuleMappingSourceDto> Sources { get; set; } = new();

    public static SyncRuleMappingDto FromEntity(SyncRuleMapping entity)
    {
        return new SyncRuleMappingDto
        {
            Id = entity.Id,
            Created = entity.Created,
            TargetMetaverseAttributeId = entity.TargetMetaverseAttributeId,
            TargetMetaverseAttributeName = entity.TargetMetaverseAttribute?.Name,
            TargetConnectedSystemAttributeId = entity.TargetConnectedSystemAttributeId,
            TargetConnectedSystemAttributeName = entity.TargetConnectedSystemAttribute?.Name,
            SourceType = entity.GetSourceType().ToString(),
            Sources = entity.Sources.Select(SyncRuleMappingSourceDto.FromEntity).ToList()
        };
    }
}

/// <summary>
/// API representation of a SyncRuleMappingSource.
/// </summary>
public class SyncRuleMappingSourceDto
{
    public int Id { get; set; }
    public int Order { get; set; }
    public int? MetaverseAttributeId { get; set; }
    public string? MetaverseAttributeName { get; set; }
    public int? ConnectedSystemAttributeId { get; set; }
    public string? ConnectedSystemAttributeName { get; set; }

    public static SyncRuleMappingSourceDto FromEntity(SyncRuleMappingSource entity)
    {
        return new SyncRuleMappingSourceDto
        {
            Id = entity.Id,
            Order = entity.Order,
            MetaverseAttributeId = entity.MetaverseAttributeId,
            MetaverseAttributeName = entity.MetaverseAttribute?.Name,
            ConnectedSystemAttributeId = entity.ConnectedSystemAttributeId,
            ConnectedSystemAttributeName = entity.ConnectedSystemAttribute?.Name
        };
    }
}

/// <summary>
/// Request DTO for creating a new SyncRuleMapping.
/// </summary>
public class CreateSyncRuleMappingRequest
{
    /// <summary>
    /// For import rules: The target Metaverse Attribute ID.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// For export rules: The target Connected System Attribute ID.
    /// </summary>
    public int? TargetConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The sources for this mapping (attribute mappings or function calls).
    /// </summary>
    [Required]
    public List<CreateSyncRuleMappingSourceRequest> Sources { get; set; } = new();
}

/// <summary>
/// Request DTO for creating a SyncRuleMappingSource.
/// </summary>
public class CreateSyncRuleMappingSourceRequest
{
    /// <summary>
    /// The order of this source in the mapping (for chained function calls).
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// For export rules: The source Metaverse Attribute ID.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// For import rules: The source Connected System Attribute ID.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }
}
