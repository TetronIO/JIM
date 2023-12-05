using JIM.Models.Core;

namespace JIM.Models.DataGeneration
{
    public class DataGenerationObjectType
    {
        public int Id { get; set; }
        public MetaverseObjectType MetaverseObjectType { get; set; } = null!;
        public List<DataGenerationTemplateAttribute> TemplateAttributes { get; set; } = null!;
        public int ObjectsToCreate { get; set; }

        public DataGenerationObjectType()
        {
            TemplateAttributes = new List<DataGenerationTemplateAttribute>();
        }
    }
}
