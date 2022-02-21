using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Core
{
    [Index(nameof(StringValue))]
    public class MetaverseObjectAttributeValue
    {
        public Guid Id { get; set; }
        public MetaverseAttribute Attribute { get; set; }
        public string? StringValue { get; set; }
        public DateTime DateTimeValue { get; set; }
        public int IntValue { get; set; }
        public byte[] ByteValue { get; set; }

        /// <summary>
        /// If this attribute value was contributed to the Metaverse by a connected system, then this identifies that system.
        /// </summary>
        public ConnectedSystem? ContributedBySystem { get; set; }

        public MetaverseObject MetaverseObject { get; set; }

        public MetaverseObjectAttributeValue()
        {
            ByteValue = Array.Empty<byte>();
        }
    }
}
