using JIM.Models.Core;

namespace JIM.Models.Staging
{
    public class ConnectedSystemObject
    {
        public long Id { get; set; }
        public DateTime Created { get; set; }
        public DateTime? LastUpdated { get; set; }
        public ConnectedSystemObjectType Type { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }
        /// <summary>
        /// The attribute that uniquely identifies this object in the connected system.
        /// It should be immutable (not change for the lifetime of the object). The connected system may author it, or you may specify it at provisioning time. It depends on the system.
        /// </summary>
        public ConnectedSystemAttribute UniqueIdentifierAttribute { get; set; }
        public List<ConnectedSystemAttributeValue> AttributeValues { get; set; }
        public ConnectedSystemObjectStatus Status { get; set; }
        /// <summary>
        /// If there's a link to a MetaverseObject here, then this is a connected object,
        /// </summary>
        public MetaverseObject? MetaverseObject { get; set; }
        /// <summary>
        /// How was this CSO joined to an MVO, if at all?
        /// </summary>
        public ConnectedSystemObjectJoinType JoinType { get; set; }
        /// <summary>
        /// When this Connector Space Object was joined to the Metaverse.
        /// </summary>
        public DateTime? DateJoined { get; set; }

        public ConnectedSystemObject()
        {
            Created = DateTime.Now;
            AttributeValues = new List<ConnectedSystemAttributeValue>();
            Status = ConnectedSystemObjectStatus.Normal;
            JoinType = ConnectedSystemObjectJoinType.NotJoined;
        }
    }
}
