using JIM.Models.Security;

namespace JIM.Models.Core
{
    public class MetaverseObject
    {
        public Guid Id { get; set; }

        public DateTime Created { get; set; }

        public DateTime? LastUpdated { get; set; }

        public MetaverseObjectType Type { get; set; } = null!;

        public List<MetaverseObjectAttributeValue> AttributeValues { get; set; }

        public List<Role> Roles { get; set; } = null!;

        public MetaverseObjectStatus Status { get; set; }

        public List<MetaverseObjectChange> Changes { get; set; }

        public string? DisplayName 
        { 
            get
            {
                if (AttributeValues == null || AttributeValues.Count == 0)
                    return null;

                // as a built-in attribute, we know DisplayName is a single-valued attribute, so no need to do a attribute plurality check
                var av = AttributeValues.SingleOrDefault(q => q.Attribute.Name == Constants.BuiltInAttributes.DisplayName);
                if (av != null && ! string.IsNullOrEmpty(av.StringValue))
                    return av.StringValue;

                return null;
            } 
        }

        public MetaverseObject()
        {
            Created = DateTime.UtcNow;
            Status = MetaverseObjectStatus.Normal;
            AttributeValues = new List<MetaverseObjectAttributeValue>();
            Changes = new List<MetaverseObjectChange>();
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