using JIM.Models.Activities;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.DataGeneration;

[Index(nameof(Name))]
public class DataGenerationTemplate : IAuditable
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public bool BuiltIn { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;
    public ActivityInitiatorType CreatedByType { get; set; }
    public Guid? CreatedById { get; set; }
    public string? CreatedByName { get; set; }

    public DateTime? LastUpdated { get; set; }
    public ActivityInitiatorType LastUpdatedByType { get; set; }
    public Guid? LastUpdatedById { get; set; }
    public string? LastUpdatedByName { get; set; }

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
