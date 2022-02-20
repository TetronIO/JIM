using JIM.Models.Security;

namespace JIM.Models.Core
{
    public class MetaverseObject
    {
        public Guid Id { get; set; }
        public DateTime Created { get; set; }
        public DateTime? LastUpdated { get; set; }
        public MetaverseObjectType Type { get; set; }
        public List<MetaverseObjectAttributeValue> AttributeValues { get; set; }
        /// <summary>
        /// Metaverse objects of type User can have roles.
        /// In the future, more object types may be able to have roles assigned to them.
        /// Roles grant entitlements within JIM.
        /// </summary>
        //public List<Role> Roles { get; set; }

        public MetaverseObject()
        {
            AttributeValues = new List<MetaverseObjectAttributeValue>();
            //Roles = new List<Role>();
        }
    }
}