namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectChangeAttribute
    {
        public Guid Id { get; set; }

        /// <summary>
        /// The parent for this object.
        /// Required for establishing an Entity Framework relationship.
        /// </summary>
        public ConnectedSystemObjectChange ConnectedSystemChange { get; set; }

        /// <summary>
        /// The connected system attribute these value changes relates to.
        /// </summary>
        public ConnectedSystemAttribute ConnectedSystemAttribute { get; set; }

        /// <summary>
        /// A list of what values were added to this attribute.
        /// </summary>
        public List<ConnectedSystemObjectChangeAttributeValue> ValuesAdded { get; set; } = new List<ConnectedSystemObjectChangeAttributeValue>();

        /// <summary>
        /// A list of what values were renmoved from this attribute.
        /// </summary>
        public List<ConnectedSystemObjectChangeAttributeValue> ValuesRemoved { get; set; } = new List<ConnectedSystemObjectChangeAttributeValue>();
    }
}
