using Microsoft.EntityFrameworkCore;
using TIM.Models.Staging;

namespace TIM.Models.Core
{
    [Index(nameof(StringValue))]
    public class MetaverseObjectAttributeValue : BaseAttributeValue
    {
        /// <summary>
        /// If this attribute value was contributed to the Metaverse by a connected system, then this identifies that system.
        /// </summary>
        public ConnectedSystem? ContributedBySystem { get; set; }

        public MetaverseObject MetaverseObject { get; set; }

        public MetaverseObjectAttributeValue(MetaverseObject metaverseObject, MetaverseAttribute attribute) : base(attribute)
        {
            MetaverseObject = metaverseObject;
        }
    }
}
