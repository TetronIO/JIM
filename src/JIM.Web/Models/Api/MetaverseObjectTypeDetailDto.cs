using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// Detailed API representation of a MetaverseObjectType.
/// </summary>
public class MetaverseObjectTypeDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string PluralName { get; set; } = null!;
    public DateTime Created { get; set; }
    public bool BuiltIn { get; set; }
    public MetaverseObjectDeletionRule DeletionRule { get; set; }
    public TimeSpan? DeletionGracePeriod { get; set; }
    public List<int> DeletionTriggerConnectedSystemIds { get; set; } = new();
    public List<MetaverseAttributeSummaryDto> Attributes { get; set; } = new();

    /// <summary>
    /// Creates a detailed DTO from a MetaverseObjectType entity.
    /// </summary>
    public static MetaverseObjectTypeDetailDto FromEntity(MetaverseObjectType entity)
    {
        return new MetaverseObjectTypeDetailDto
        {
            Id = entity.Id,
            Name = entity.Name,
            PluralName = entity.PluralName,
            Created = entity.Created,
            BuiltIn = entity.BuiltIn,
            DeletionRule = entity.DeletionRule,
            DeletionGracePeriod = entity.DeletionGracePeriod,
            DeletionTriggerConnectedSystemIds = entity.DeletionTriggerConnectedSystemIds ?? new(),
            Attributes = entity.Attributes?
                .Select(MetaverseAttributeSummaryDto.FromEntity)
                .ToList() ?? new()
        };
    }
}

/// <summary>
/// Request DTO for updating a MetaverseObjectType's deletion rules.
/// </summary>
public class UpdateMetaverseObjectTypeRequest
{
    /// <summary>
    /// Determines when Metaverse Objects of this type should be automatically deleted.
    /// </summary>
    public MetaverseObjectDeletionRule? DeletionRule { get; set; }

    /// <summary>
    /// Optional grace period before deletion is executed, as an ISO 8601 duration string (e.g., "00:01:00" for 1 minute, "1.00:00:00" for 1 day).
    /// Set to "00:00:00", TimeSpan.Zero, or null for immediate deletion when conditions are met.
    /// </summary>
    public TimeSpan? DeletionGracePeriod { get; set; }

    /// <summary>
    /// List of connected system IDs that are authoritative sources for deletion.
    /// Required when DeletionRule is WhenAuthoritativeSourceDisconnected.
    /// When set: Delete MVO if ANY of these specific systems disconnect.
    /// Ignored when DeletionRule is Manual or WhenLastConnectorDisconnected.
    /// </summary>
    public List<int>? DeletionTriggerConnectedSystemIds { get; set; }
}

/// <summary>
/// Summary representation of a MetaverseAttribute (used within ObjectType details).
/// </summary>
public class MetaverseAttributeSummaryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public AttributeDataType Type { get; set; }
    public AttributePlurality AttributePlurality { get; set; }
    public bool BuiltIn { get; set; }

    public static MetaverseAttributeSummaryDto FromEntity(MetaverseAttribute entity)
    {
        return new MetaverseAttributeSummaryDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Type = entity.Type,
            AttributePlurality = entity.AttributePlurality,
            BuiltIn = entity.BuiltIn
        };
    }
}
