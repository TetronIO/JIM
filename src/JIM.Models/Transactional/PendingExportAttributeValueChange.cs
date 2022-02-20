using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Transactional
{
    public class PendingExportAttributeValueChange : BaseAttributeValue
    {
        public PendingExportAttributeChangeType ChangeType { get; set; }

        public PendingExportAttributeValueChange(ConnectedSystemAttribute attribute) : base(attribute)
        {
        }
    }
}
