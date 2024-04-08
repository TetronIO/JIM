using JIM.Models.Core;

namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectTypeAttribute
    {
        public int Id { get; set; }
        
        public DateTime Created { set; get; } = DateTime.UtcNow;

        public string Name { get; set; } = null!;

        public string? Description { get; set; }

        /// <summary>
        /// Some types of connected systems have a concept of hierarchy where an attribute is inherited from a class that the object type inherits, i.e. an LDAP object class.
        /// Storing this information in JIM and presenting it to the user when configuring a Connected System can help them with understanding what might or might not need managing, attribute wise.
        /// </summary>
        public string? ClassName { get; set; }

        public AttributeDataType Type { get; set; }
        
        public AttributePlurality AttributePlurality { get; set; }

        /// <summary>
        /// The Connected System Object Type this attribute belongs to.
        /// </summary>
        public ConnectedSystemObjectType ConnectedSystemObjectType { get; set; } = null!;

        /// <summary>
        /// Whether or not an administrator has selected this attribute to be synchronised by JIM.
        /// </summary>
        public bool Selected { get; set; }

        /// <summary>
        /// Indicates if this attribute is a unique identifier for the object type in a connected system.
        /// </summary>
        public bool IsExternalId { get; set; }

        /// <summary>
        /// Indicates if this attribute is used as a secondary identifier by the connected system, i.e. how a DN is used as such in an LDAP system.
        /// </summary>
        public bool IsSecondaryExternalId { get; set; }
    }
}
