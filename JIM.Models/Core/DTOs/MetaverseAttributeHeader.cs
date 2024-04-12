namespace JIM.Models.Core.DTOs;

public class MetaverseAttributeHeader
{
    public int Id { get; set; }

    public DateTime Created { set; get; }

    public string Name { get; set; } = null!;

    public AttributeDataType Type { get; set; }

    public AttributePlurality AttributePlurality { get; set; }

    public bool BuiltIn { get; set; }

    public IEnumerable<KeyValuePair<int, string>>? MetaverseObjectTypes { get; set; }
}