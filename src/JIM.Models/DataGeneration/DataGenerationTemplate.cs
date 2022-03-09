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

        public bool IsValid()
        {
            if (string.IsNullOrEmpty(Name))
                return false;

            if (ObjectTypes == null || ObjectTypes.Count == 0)
                return false;

            foreach (var type in ObjectTypes)
                if (type.TemplateAttributes.Any(q => q.IsValid() == false))
                    return false;

            return true;
        }
    }
}
