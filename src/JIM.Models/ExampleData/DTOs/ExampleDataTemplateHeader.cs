namespace JIM.Models.ExampleData.DTOs;

/// <summary>
/// Lightweight representation of a ExampleDataTemplate for list views.
/// </summary>
public class ExampleDataTemplateHeader
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public bool BuiltIn { get; set; }
    public DateTime Created { set; get; }
    public int ObjectTypeCount { get; set; }

    /// <summary>
    /// Creates a header from a ExampleDataTemplate entity.
    /// </summary>
    public static ExampleDataTemplateHeader FromEntity(ExampleDataTemplate entity)
    {
        return new ExampleDataTemplateHeader
        {
            Id = entity.Id,
            Name = entity.Name,
            BuiltIn = entity.BuiltIn,
            Created = entity.Created,
            ObjectTypeCount = entity.ObjectTypes?.Count ?? 0
        };
    }
}