using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Core
{
    [Index(nameof(StringValue))]
    [Index(nameof(ReferenceValue))]
    [Index(nameof(DateTimeValue))]
    [Index(nameof(IntValue))]
    [Index(nameof(ReferenceValue))]
    [Index(nameof(GuidValue))]
    public class MetaverseObjectAttributeValue
    {
        public int Id { get; set; }
        public MetaverseAttribute Attribute { get; set; }
        public MetaverseObject MetaverseObject { get; set; }
        public string? StringValue { get; set; }
        public DateTime? DateTimeValue { get; set; }
        public int? IntValue { get; set; }
        public byte[]? ByteValue { get; set; }
        public MetaverseObject? ReferenceValue { get; set; }
        public Guid? GuidValue { get; set; }
        public bool? BoolValue { get; set; }

        /// <summary>
        /// If this attribute value was contributed to the Metaverse by a connected system, then this identifies that system.
        /// </summary>
        public ConnectedSystem? ContributedBySystem { get; set; }

        public MetaverseObjectAttributeValue()
        {
        }
    }
}
