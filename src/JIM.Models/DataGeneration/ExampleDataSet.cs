using System.Text.Json.Serialization;
namespace JIM.Models.DataGeneration;

public class ExampleDataSet
{
    public int Id { get; set; }
        
    public string Name { get; set; } = null!;

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public bool BuiltIn { get; set; }
        
    /// <summary>
    /// The .NET Culture, i.e. "en-GB" the example data set values are in.
    /// More info: https://www.venea.net/web/culture_code
    /// </summary>
    public string Culture { get; set; } = null!;

    public List<ExampleDataSetValue> Values { get; set; } = new();

    [JsonIgnore]
    public List<ExampleDataSetInstance> ExampleDataSetInstances { get; set; } = null!;
}