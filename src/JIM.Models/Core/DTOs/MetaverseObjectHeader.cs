namespace JIM.Models.Core.DTOs
{
    public class MetaverseObjectHeader
    {
        public Guid Id { get; set; }
        public DateTime Created { get; set; }
        public string TypeName { get; set; }
        public int TypeId { get; set; }
        public List<MetaverseObjectAttributeValue> AttributeValues { get; set; }
        public MetaverseObjectStatus Status { get; set; }
        public string? DisplayName 
        { 
            get
            {
                if (AttributeValues == null || AttributeValues.Count == 0)
                    return null;

                var av = AttributeValues.SingleOrDefault(q => q.Attribute.Name == Constants.BuiltInAttributes.DisplayName);
                if (av != null && ! string.IsNullOrEmpty(av.StringValue))
                    return av.StringValue;

                return null;
            } 
        }

        public MetaverseObjectHeader()
        {
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