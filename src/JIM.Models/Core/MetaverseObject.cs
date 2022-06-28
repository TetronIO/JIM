using JIM.Models.Security;

namespace JIM.Models.Core
{
    public class MetaverseObject
    {
        public int Id { get; set; }
        public DateTime Created { get; set; }
        public DateTime? LastUpdated { get; set; }
        public MetaverseObjectType Type { get; set; }
        public List<MetaverseObjectAttributeValue> AttributeValues { get; set; }
        public List<Role> Roles { get; set; }

        public MetaverseObject()
        {
            Created = DateTime.Now;
            AttributeValues = new List<MetaverseObjectAttributeValue>();
        }

        public MetaverseObjectAttributeValue? GetAttributeValue(string name)
        {
            return AttributeValues.SingleOrDefault(q => q.Attribute.Name == name);
        }

        public bool HasAttributeValue(string name)
        {
            return AttributeValues.Any(q => q.Attribute.Name == name);
        }
    }
}