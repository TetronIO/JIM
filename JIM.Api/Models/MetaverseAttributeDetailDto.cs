using JIM.Models.Core;

namespace JIM.Api.Models;

/// <summary>
/// Detailed API representation of a MetaverseAttribute.
/// </summary>
public class MetaverseAttributeDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public DateTime Created { get; set; }
    public AttributeDataType Type { get; set; }
    public AttributePlurality AttributePlurality { get; set; }
    public bool BuiltIn { get; set; }
    public List<ObjectTypeReferenceDto> ObjectTypes { get; set; } = new();

    /// <summary>
    /// Creates a detailed DTO from a MetaverseAttribute entity.
    /// </summary>
    public static MetaverseAttributeDetailDto FromEntity(MetaverseAttribute entity)
    {
        return new MetaverseAttributeDetailDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Created = entity.Created,
            Type = entity.Type,
            AttributePlurality = entity.AttributePlurality,
            BuiltIn = entity.BuiltIn,
            ObjectTypes = entity.MetaverseObjectTypes?
                .Select(ot => new ObjectTypeReferenceDto { Id = ot.Id, Name = ot.Name })
                .ToList() ?? new()
        };
    }
}

/// <summary>
/// Lightweight reference to a MetaverseObjectType.
/// </summary>
public class ObjectTypeReferenceDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}
