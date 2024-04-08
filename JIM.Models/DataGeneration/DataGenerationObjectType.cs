using JIM.Models.Core;

namespace JIM.Models.DataGeneration
{
    public class DataGenerationObjectType
    {
        public int Id { get; set; }
        public MetaverseObjectType MetaverseObjectType { get; set; } = null!;
        public List<DataGenerationTemplateAttribute> TemplateAttributes { get; } = new();
        public int ObjectsToCreate { get; set; }
    }
}
