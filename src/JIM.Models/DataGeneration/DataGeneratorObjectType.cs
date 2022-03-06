using JIM.Models.Core;

namespace JIM.Models.DataGeneration
{
    public class DataGeneratorObjectType
    {
        public int Id { get; set; }
        public MetaverseObjectType MetaverseObjectType { get; set; }
        public List<DataGeneratorTemplateAttribute> TemplateAttributes { get; set; }
        public int ObjectsToCreate { get; set; }

        public DataGeneratorObjectType()
        {
            TemplateAttributes = new List<DataGeneratorTemplateAttribute>();
        }
    }
}
