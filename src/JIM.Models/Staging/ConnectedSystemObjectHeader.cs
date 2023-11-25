using JIM.Models.Core;
using System.ComponentModel.DataAnnotations.Schema;

namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectHeader
    {
        #region accessors
        public Guid Id { get; set; }

        public string? DisplayName { get; set; }
        //public string DisplayName { get; set; }

        public ConnectedSystemObjectAttributeValue? UniqueIdentifierAttributeValue { get; set; }

        public DateTime Created { get; set; }

        public DateTime? LastUpdated { get; set; }

        //public ConnectedSystemObjectType Type { get; set; }

        public int TypeId { get; set; }

        public string TypeName { get; set; } = string.Empty;

        ///// <summary>
        ///// The attribute that uniquely identifies this object in the connected system.
        ///// It should be immutable (not change for the lifetime of the object). 
        ///// The connected system may author it and be made available to JIM after import, or you may specify it at provisioning time, depending on the needs of the connected system.
        ///// This is a convenience accessor. It's defined as a property on one of the connected system object type attributes. i.e. ConnectedSystemObjectTypeAttribute.IsUniqueIDentifier
        ///// </summary>
        //public int UniqueIdentifierAttributeId { get; set; }

        //public List<ConnectedSystemObjectAttributeValue> AttributeValues { get; set; }

        public ConnectedSystemObjectStatus Status { get; set; }

        /// <summary>
        /// How was this CSO joined to an MVO, if at all?
        /// </summary>
        public ConnectedSystemObjectJoinType JoinType { get; set; }

        /// <summary>
        /// When this Connector Space Object was joined to the Metaverse.
        /// </summary>
        public DateTime? DateJoined { get; set; }
        #endregion

        #region constructors
        public ConnectedSystemObjectHeader()
        {
            Created = DateTime.UtcNow;
            Status = ConnectedSystemObjectStatus.Normal;
            JoinType = ConnectedSystemObjectJoinType.NotJoined;
        }
        #endregion
    }
}
