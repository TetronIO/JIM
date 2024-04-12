using JIM.Models.Exceptions;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.DataGeneration;

[Index(nameof(Name))]
public class DataGenerationTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public bool BuiltIn { get; set; }
    public DateTime Created { set; get; } = DateTime.UtcNow;
    public List<DataGenerationObjectType> ObjectTypes { get; } = new();

    public void Validate()
    {
        if (string.IsNullOrEmpty(Name))
            throw new DataGenerationTemplateException($"Null or empty {nameof(Name)}");

        if (ObjectTypes == null || ObjectTypes.Count == 0)
            throw new DataGenerationTemplateException("Null or empty ObjectTypes");

        foreach (var attribute in ObjectTypes.SelectMany(type => type.TemplateAttributes))
            attribute.Validate();
    }
}