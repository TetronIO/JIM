using JIM.Models.Core;

namespace JIM.Models.DataGeneration
{
    public class DataGenerationObjectType
    {
        public int Id { get; set; }
        public MetaverseObjectType MetaverseObjectType { get; set; }
        public List<DataGenerationTemplateAttribute> TemplateAttributes { get; set; }
        public int ObjectsToCreate { get; set; }

        public DataGenerationObjectType()
        {
            TemplateAttributes = new List<DataGenerationTemplateAttribute>();
        }
    }
}
