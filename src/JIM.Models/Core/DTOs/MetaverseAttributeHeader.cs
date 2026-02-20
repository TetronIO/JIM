namespace JIM.Models.Core.DTOs;

/// <summary>
/// Lightweight representation of a MetaverseAttribute for list views.
/// </summary>
public class MetaverseAttributeHeader
{
    public int Id { get; set; }

    public DateTime Created { set; get; }

    public string Name { get; set; } = null!;

    public AttributeDataType Type { get; set; }

    public AttributePlurality AttributePlurality { get; set; }

    public bool BuiltIn { get; set; }

    public IEnumerable<KeyValuePair<int, string>>? MetaverseObjectTypes { get; set; }

    /// <summary>
    /// Creates a header from a MetaverseAttribute entity.
    /// </summary>
    public static MetaverseAttributeHeader FromEntity(MetaverseAttribute entity)
    {
        return new MetaverseAttributeHeader
        {
            Id = entity.Id,
            Created = entity.Created,
            Name = entity.Name,
            Type = entity.Type,
            AttributePlurality = entity.AttributePlurality,
            BuiltIn = entity.BuiltIn,
            MetaverseObjectTypes = entity.MetaverseObjectTypes?
                .Select(ot => new KeyValuePair<int, string>(ot.Id, ot.Name))
                .ToList()
        };
    }
}