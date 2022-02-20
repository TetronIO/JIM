namespace JIM.Models.Staging
{
    public class ConnectedSystemObject
    {
        public Guid Id { get; set; }
        public DateTime Created { get; set; }
        public DateTime? LastUpdated { get; set; }
        public ConnectedSystemObjectType Type { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }
        public ConnectedSystemAttribute UniqueIdentifierAttribute { get; set; }
        public List<ConnectedSystemAttributeValue> AttributeValues { get; set; }

        public ConnectedSystemObject(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, ConnectedSystemAttribute uniqueIdentifierAttribute)
        {
            ConnectedSystem = connectedSystem;
            Type = type;
            Created = DateTime.Now;
            UniqueIdentifierAttribute = uniqueIdentifierAttribute;
            AttributeValues = new List<ConnectedSystemAttributeValue>();
        }
    }
}
