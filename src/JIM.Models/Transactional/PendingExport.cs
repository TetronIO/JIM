using JIM.Models.Staging;

namespace JIM.Models.Transactional
{
    public class PendingExport
    {
        // this object will get created when a synchronisation is run against a connector space object
        // and it's determined a change needs to be made to the corresponding object in the connected system.
        // the pending export would get processed on an export run, with the change being attempted against
        // the connected system. changes would be made and where possible, the PendingExport or 
        // PendingExportAttributeValueChange objects would be deleted, leaving ones remaining only if they had
        // failed and needed administrator intervention to remediate.

        // confirming imports will be needed to confirm that the export operation was successful.

        // expected results for export operations:
        // - create object: success or fail
        // - delete object: success or fail
        // - update attribute: atomic, some can succeed, some can fail

        public long Id { get; set; }
        public ConnectedSystemObject ConnectedSystemObject { get; set; }
        public PendingExportChangeType ChangeType { get; set; }
        public List<PendingExportAttributeValueChange> AttributeValueChanges { get; set; }
        public PendingExportStatus Status { get; set; }
        /// <summary>
        /// How many times have we encounted an error whilst trying to export this change?
        /// </summary>
        public int? ErrorCount { get; set; }

        public PendingExport()
        {
            Status = PendingExportStatus.Pending;
            AttributeValueChanges = new List<PendingExportAttributeValueChange>();
        }
    }
}
