namespace JIM.Models.DataGeneration.DTOs;

/// <summary>
/// Lightweight representation of an ExampleDataSet for list views.
/// </summary>
public class ExampleDataSetHeader
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public bool BuiltIn { get; set; }
    public DateTime Created { set; get; }
    public string Culture { get; set; } = null!;
    public int ValueCount { get; set; }

    /// <summary>
    /// Creates a header from an ExampleDataSet entity.
    /// </summary>
    public static ExampleDataSetHeader FromEntity(ExampleDataSet entity)
    {
        return new ExampleDataSetHeader
        {
            Id = entity.Id,
            Name = entity.Name,
            BuiltIn = entity.BuiltIn,
            Created = entity.Created,
            Culture = entity.Culture,
            ValueCount = entity.Values?.Count ?? 0
        };
    }
}