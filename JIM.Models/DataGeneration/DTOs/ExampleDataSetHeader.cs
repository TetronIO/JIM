namespace JIM.Models.DataGeneration.DTOs;

public class ExampleDataSetHeader
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public bool BuiltIn { get; set; }
    public DateTime Created { set; get; }
    public string Culture { get; set; } = null!;
    public int Values { get; set; }
}