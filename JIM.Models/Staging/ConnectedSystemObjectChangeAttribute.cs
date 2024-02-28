namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectChangeAttribute
    {
        public Guid Id { get; set; }

        /// <summary>
        /// The parent for this object.
        /// Required for establishing an Entity Framework relationship.
        /// </summary>
        public ConnectedSystemObjectChange ConnectedSystemChange { get; set; } = null!;

        /// <summary>
        /// The connected system attribute these value changes relates to.
        /// </summary>
        public ConnectedSystemObjectTypeAttribute Attribute { get; set; } = null!;

        /// <summary>
        /// A list of what values were added to or removed from this attribute.
        /// </summary>
        public List<ConnectedSystemObjectChangeAttributeValue> ValueChanges { get; set; } = new List<ConnectedSystemObjectChangeAttributeValue>();
    }
}
