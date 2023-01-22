using JIM.Models.Core;

namespace JIM.Models.Staging
{
    public class ConnectedSystemAttribute
    {
        public int Id { get; set; }
        public DateTime Created { set; get; }
        public string Name { get; set; }
        public string? Description { get; set; }
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
