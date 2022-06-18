using JIM.Models.DataGeneration;
using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Core
{
    [Index(nameof(Name))]
    public class MetaverseObjectType
    {
        public int Id { get; set; }
        /// <summary>
        /// MetaverseObjectTypes are logically grouped to aid with navigation, i.e. users are identities, same for service accounts and groups as entitlements.
        /// </summary>
        public MetaverseObjectTypeGroup Group { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public List<MetaverseAttribute> Attributes { get; set; }
        public bool BuiltIn { get; set; }
        public List<DataGenerationTemplateAttribute> DataGenerationTemplateAttributes { get; set; }
        /// <summary>
        /// The order in which to show this MetaverseObjectType in relation to others in any parent MetaverseObjectTypeGroup.
        /// </summary>
        public int Order { get; set; }

        public MetaverseObjectType()
        {
            Created = DateTime.Now;
            Attributes = new List<MetaverseAttribute>();
        }
    }
}
