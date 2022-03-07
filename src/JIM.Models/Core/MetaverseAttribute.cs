using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Core
{
    [Index(nameof(Name))]
    public class MetaverseAttribute
    {
        public int Id { get; set; }
        public DateTime Created { set; get; }
        public string Name { get; set; }
        public AttributeDataType Type { get; set; }
        public AttributePlurality AttributePlurality { get; set; }
        public bool BuiltIn { get; set; }
        public List<MetaverseObjectType> MetaverseObjectTypes { get; set; }

        public MetaverseAttribute()
        {
            Created = DateTime.Now;
        }
    }
}
