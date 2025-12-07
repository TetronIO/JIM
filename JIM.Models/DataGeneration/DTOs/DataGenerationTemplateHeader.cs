namespace JIM.Models.DataGeneration.DTOs;

/// <summary>
/// Lightweight representation of a DataGenerationTemplate for list views.
/// </summary>
public class DataGenerationTemplateHeader
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public bool BuiltIn { get; set; }
    public DateTime Created { set; get; }
    public int ObjectTypeCount { get; set; }

    /// <summary>
    /// Creates a header from a DataGenerationTemplate entity.
    /// </summary>
    public static DataGenerationTemplateHeader FromEntity(DataGenerationTemplate entity)
    {
        return new DataGenerationTemplateHeader
        {
            Id = entity.Id,
            Name = entity.Name,
            BuiltIn = entity.BuiltIn,
            Created = entity.Created,
            ObjectTypeCount = entity.ObjectTypes?.Count ?? 0
        };
    }
}