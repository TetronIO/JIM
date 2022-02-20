using JIM.Models.Core;

namespace JIM.Models.Transactional
{
    public class PendingExportAttributeValueChange : BaseAttributeValue
    {
        public PendingExportAttributeChangeType ChangeType { get; set; }

        public PendingExportAttributeValueChange()
        {
        }
    }
}
