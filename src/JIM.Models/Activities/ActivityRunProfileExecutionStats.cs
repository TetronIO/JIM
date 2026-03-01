using System.ComponentModel.DataAnnotations.Schema;

namespace JIM.Models.Activities;

/// <summary>
/// Represents statistics for an activity's run profile execution items, categorised by change type.
/// Not persisted.
/// </summary>
[NotMapped]
public class ActivityRunProfileExecutionStats
{
    public Guid ActivityId { get; set; }

    #region Import Stats (CSO operations)
    /// <summary>
    /// Count of new CSOs added to staging during import.
    /// </summary>
    public int TotalCsoAdds { get; set; }

    /// <summary>
    /// Count of existing CSOs updated during import.
    /// </summary>
    public int TotalCsoUpdates { get; set; }

    /// <summary>
    /// Count of CSOs marked as deleted (source system deletion detected).
    /// </summary>
    public int TotalCsoDeletes { get; set; }
    #endregion

    #region Sync Stats (MVO operations)
    /// <summary>
    /// Count of new MVOs created via projection.
    /// </summary>
    public int TotalProjections { get; set; }

    /// <summary>
    /// Count of CSOs joined to existing MVOs.
    /// </summary>
    public int TotalJoins { get; set; }

    /// <summary>
    /// Count of attribute flows between CSOs and MVOs.
    /// </summary>
    public int TotalAttributeFlows { get; set; }

    /// <summary>
    /// Count of CSOs disconnected from MVOs.
    /// </summary>
    public int TotalDisconnections { get; set; }

    /// <summary>
    /// Count of CSOs disconnected from MVOs because they fell out of scope of import sync rule scoping criteria.
    /// </summary>
    public int TotalDisconnectedOutOfScope { get; set; }

    /// <summary>
    /// Count of CSOs that fell out of scope but remained joined (InboundOutOfScopeAction = RemainJoined).
    /// Attribute flow stopped but join preserved ("once managed, always managed" pattern).
    /// </summary>
    public int TotalOutOfScopeRetainJoin { get; set; }

    /// <summary>
    /// Count of CSOs where drift was detected and corrective pending exports were created.
    /// Drift occurs when target system attributes differ from expected MVO values.
    /// </summary>
    public int TotalDriftCorrections { get; set; }
    #endregion

    #region Export Stats
    /// <summary>
    /// Count of objects exported to target systems (includes both initial creation and subsequent updates).
    /// </summary>
    public int TotalExported { get; set; }

    /// <summary>
    /// Count of objects deprovisioned from target systems.
    /// </summary>
    public int TotalDeprovisioned { get; set; }
    #endregion

    #region Pending Export Stats (surfaced during sync)
    /// <summary>
    /// Count of pending exports staged for the next export run.
    /// These are exports that were previously executed but not yet confirmed (ExportNotConfirmed status),
    /// giving operators visibility into what changes will be made to connected systems.
    /// </summary>
    public int TotalPendingExports { get; set; }
    #endregion

    #region Pending Export Reconciliation Stats (populated during confirming import)
    /// <summary>
    /// Count of pending exports that were fully confirmed and deleted.
    /// The exported attribute values matched the imported values.
    /// </summary>
    public int TotalPendingExportsConfirmed { get; set; }

    /// <summary>
    /// Count of pending exports with unconfirmed attributes that will be retried.
    /// Some attribute values did not match; they will be re-exported on the next export run.
    /// </summary>
    public int TotalPendingExportsRetrying { get; set; }

    /// <summary>
    /// Count of pending exports that failed after maximum retry attempts.
    /// Manual intervention may be required to resolve these exports.
    /// </summary>
    public int TotalPendingExportsFailed { get; set; }
    #endregion

    #region Shared Stats
    /// <summary>
    /// Total number of objects that were in scope for processing during the run.
    /// Used to calculate unchanged count: TotalObjectsProcessed - TotalObjectChangeCount.
    /// </summary>
    public int TotalObjectsProcessed { get; set; }

    /// <summary>
    /// Total count of all run profile execution items (objects that had changes).
    /// </summary>
    public int TotalObjectChangeCount { get; set; }

    /// <summary>
    /// Count of objects where no changes were needed.
    /// Calculated as TotalObjectsProcessed - TotalObjectChangeCount, but never negative.
    /// </summary>
    public int TotalUnchanged => Math.Max(0, TotalObjectsProcessed - TotalObjectChangeCount);

    /// <summary>
    /// Count of objects where no changes were needed.
    /// </summary>
    [Obsolete("Use TotalUnchanged instead")]
    public int TotalNoChanges { get; set; }

    /// <summary>
    /// Count of objects that encountered errors during processing.
    /// </summary>
    public int TotalObjectErrors { get; set; }

    /// <summary>
    /// Count of distinct object types processed.
    /// </summary>
    public int TotalObjectTypes { get; set; }

    /// <summary>
    /// Dictionary of object type names and their counts.
    /// Key is the object type name, value is the count of objects of that type.
    /// </summary>
    public Dictionary<string, int> ObjectTypeCounts { get; set; } = new();

    /// <summary>
    /// Dictionary of error type names and their counts.
    /// Key is the error type enum value, value is the count of objects with that error type.
    /// Only includes error types with count > 0 (excludes NotSet).
    /// </summary>
    public Dictionary<ActivityRunProfileExecutionItemErrorType, int> ErrorTypeCounts { get; set; } = new();
    #endregion

    #region NoChange Reason Stats
    /// <summary>
    /// Count of objects where no MVO attributes relevant to export rules changed.
    /// </summary>
    public int TotalMvoNoAttributeChanges { get; set; }

    /// <summary>
    /// Count of objects where the CSO already had the target value(s).
    /// </summary>
    public int TotalCsoAlreadyCurrent { get; set; }
    #endregion

    #region Direct Creation Stats
    /// <summary>
    /// Count of MVOs directly created (e.g. via admin UI or data generation), not through sync operations.
    /// </summary>
    public int TotalCreated { get; set; }
    #endregion
}
