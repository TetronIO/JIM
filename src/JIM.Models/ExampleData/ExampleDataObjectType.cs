using JIM.Models.Core;
namespace JIM.Models.ExampleData;

public class ExampleDataObjectType
{
    public int Id { get; set; }
    public MetaverseObjectType MetaverseObjectType { get; set; } = null!;
    public List<ExampleDataTemplateAttribute> TemplateAttributes { get; } = new();
    public int ObjectsToCreate { get; set; }
}