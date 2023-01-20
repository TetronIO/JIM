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

        public ConnectedSystem ConnectedSystem { get; set; }

        public ConnectedSystemAttribute()
        {
            Created = DateTime.Now;
        }
    }
}
