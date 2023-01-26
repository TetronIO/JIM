using JIM.Models.Core;

namespace JIM.Models.Staging
{
    public class ConnectedSystemAttribute
    {
        public int Id { get; set; }
        
        public DateTime Created { set; get; }
        
        public string Name { get; set; }
        
        public string? Description { get; set; }

        /// <summary>
        /// Some types of connected systems have a concept of heirarchy where an attribute is inherited from a class that the object type inherits, i.e. an LDAP object class.
        /// Storing this information in JIM and presenting it to the user when configuring a Connected System can help them with understanding what might or might not need managing, attribute wise.
        /// </summary>
        public string? ClassName { get; set; }

        public AttributeDataType Type { get; set; }
        
        public AttributePlurality AttributePlurality { get; set; }

        /// <summary>
        /// The parent for this attribute.
        /// </summary>
        public ConnectedSystemObjectType ConnectedSystemObjectType { get; set; }

        /// <summary>
        /// Whether or not an administrator has selected this attribute to be synchronised by JIM.
        /// </summary>
        public bool Selected { get; set; }

        public ConnectedSystemAttribute()
        {
            Created = DateTime.Now;
        }
    }
}
