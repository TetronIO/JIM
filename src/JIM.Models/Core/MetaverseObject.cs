namespace JIM.Models.Core
{
    public class MetaverseObject
    {
        public Guid Id { get; set; }
        public DateTime Created { get; set; }
        public DateTime? LastUpdated { get; set; }
        public MetaverseObjectType Type { get; set; }
        public List<MetaverseObjectAttributeValue> AttributeValues { get; set; }

        public MetaverseObject()
        {
            Created = DateTime.Now;
            AttributeValues = new List<MetaverseObjectAttributeValue>();
        }
    }
}