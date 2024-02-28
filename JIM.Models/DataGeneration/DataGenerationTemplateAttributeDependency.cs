using JIM.Models.Core;

namespace JIM.Models.DataGeneration
{
    public class DataGenerationTemplateAttributeDependency
    {
        public int Id { get; set; }
        public MetaverseAttribute MetaverseAttribute { get; set; } = null!;
        public string StringValue { get; set; } = null!;
        public ComparisonType ComparisonType { get; set; }
    }
}
