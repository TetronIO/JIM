using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// Detailed API representation of a MetaverseObjectType.
/// </summary>
public class MetaverseObjectTypeDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public DateTime Created { get; set; }
    public bool BuiltIn { get; set; }
    public MetaverseObjectDeletionRule DeletionRule { get; set; }
    public int? DeletionGracePeriodDays { get; set; }
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
            Created = entity.Created,
            BuiltIn = entity.BuiltIn,
            DeletionRule = entity.DeletionRule,
            DeletionGracePeriodDays = entity.DeletionGracePeriodDays,
            Attributes = entity.Attributes?
                .Select(MetaverseAttributeSummaryDto.FromEntity)
                .ToList() ?? new()
        };
    }
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
