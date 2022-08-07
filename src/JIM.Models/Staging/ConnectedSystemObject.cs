namespace JIM.Models.Staging
{
    public class ConnectedSystemObject
    {
        public long Id { get; set; }
        public DateTime Created { get; set; }
        public DateTime? LastUpdated { get; set; }
        public ConnectedSystemObjectType Type { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }
        public ConnectedSystemAttribute UniqueIdentifierAttribute { get; set; }
        public List<ConnectedSystemAttributeValue> AttributeValues { get; set; }
        public ConnectedSystemObjectStatus Status { get; set; }

        public ConnectedSystemObject()
        {
            Created = DateTime.Now;
            AttributeValues = new List<ConnectedSystemAttributeValue>();
            Status = ConnectedSystemObjectStatus.Normal;
        }
    }
}
