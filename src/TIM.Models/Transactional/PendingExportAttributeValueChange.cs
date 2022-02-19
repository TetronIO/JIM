using TIM.Models.Core;

namespace TIM.Models.Transactional
{
    public class PendingExportAttributeValueChange : BaseAttributeValue
    {
        public PendingExportAttributeChangeType ChangeType { get; set; }

        public PendingExportAttributeValueChange(ConnectedSystemAttribute attribute) : base(attribute)
        {
        }
    }
}
