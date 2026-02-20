namespace JIM.Models.Search.DTOs;

public class PredefinedSearchHeader
{
    public int Id { get; set; }

    public string MetaverseObjectTypeName { get; set; } = null!;

    public bool IsDefaultForMetaverseObjectType { get; set; }

    public string Name { get; set; } = null!;

    public string Uri { get; set; } = null!;

    public bool BuiltIn { get; set; }

    public int MetaverseAttributeCount { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;
}