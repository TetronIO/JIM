using JIM.Models.Staging;
namespace JIM.Models.Transactional;

public class PendingExport
{
    // this object will get created/updated when:
    // - a synchronisation is performed on a Connected System Object not in the Connected System this Pending Export
    //   is for, and where that sync results in changes to this Connected System.
    // - a Metaverse Object change is commited to an object along the same lines. If the change would result in this
    //   Connected System being updated, then a Pending Export object is needed (essentially, an MVO change results
    //   in the same outcome, a change to this Connected System).
    
    // the pending export is processed on an export sync run, with the change being attempted against
    // the connected system. Changes would be made and then when a confirming import and sync is performed, the sync
    // would work out if the Pending Export was fully applied. If only partially applied, then the Pending Export
    // will have the relevant committed changes removed and any error count necessary, increased.
    
    // this allows the admin to see what exports are failing, whether it's just once, or multiple times.

    // expected results for export operations:
    // - create object: success or fail
    // - delete object: success or fail
    // - update attribute: atomic, some can succeed, some can fail

    public Guid Id { get; set; }

    /// <summary>
    /// If the change type is create, then it's essential we know what connected system this applies to :)
    /// </summary>
    public ConnectedSystem ConnectedSystem { get; set; } = null!;
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// If the change type is delete or update, then we can link an existing connector space object.
    /// If the change type is create, then there won't be a connector space object yet that we can link.
    /// </summary>
    public ConnectedSystemObject? ConnectedSystemObject { get; set; }

    public PendingExportChangeType ChangeType { get; set; }

    public List<PendingExportAttributeValueChange> AttributeValueChanges { get; set; } = new();

    public PendingExportStatus Status { get; set; } = PendingExportStatus.Pending;

    /// <summary>
    /// How many times have we encountered an error whilst trying to export this change?
    /// </summary>
    public int? ErrorCount { get; set; }
}