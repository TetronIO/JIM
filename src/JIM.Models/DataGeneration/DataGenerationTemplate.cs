using JIM.Models.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace JIM.Models.DataGeneration
{
    [Index(nameof(Name))]
    public class DataGenerationTemplate
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { set; get; }
        public List<DataGenerationObjectType> ObjectTypes { get; set; }

        public DataGenerationTemplate()
        {
            Created = DateTime.Now;
            ObjectTypes = new List<DataGenerationObjectType>();
        }

        public void Validate()
        {
            if (string.IsNullOrEmpty(Name))
                throw new DataGeneratationTemplateException($"Null or empty {nameof(Name)}");

            if (ObjectTypes == null || ObjectTypes.Count == 0)
                throw new DataGeneratationTemplateException("Null or empty ObjectTypes");

            foreach (var type in ObjectTypes)
                foreach (var attribute in type.TemplateAttributes)
                    attribute.Validate();
        }
    }
}
