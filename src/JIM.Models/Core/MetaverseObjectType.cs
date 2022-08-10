using JIM.Models.DataGeneration;
using JIM.Models.Search;
using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Core
{
    [Index(nameof(Name))]
    public class MetaverseObjectType
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public List<MetaverseAttribute> Attributes { get; set; }
        public bool BuiltIn { get; set; }
        public List<DataGenerationTemplateAttribute> DataGenerationTemplateAttributes { get; set; }
        public List<PredefinedSearch> PredefinedSearches { get; set; }

        public MetaverseObjectType()
        {
            Created = DateTime.Now;
            Attributes = new List<MetaverseAttribute>();
        }
    }
}
