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
    #endregion

    #region Export Stats
    /// <summary>
    /// Count of new objects provisioned to target systems.
    /// </summary>
    public int TotalProvisioned { get; set; }

    /// <summary>
    /// Count of existing objects exported with updated attributes.
    /// </summary>
    public int TotalExported { get; set; }

    /// <summary>
    /// Count of objects deprovisioned from target systems.
    /// </summary>
    public int TotalDeprovisioned { get; set; }
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

    #region Aggregate Stats (for backward compatibility)
    /// <summary>
    /// Aggregate count of all "create" operations (CSO adds, projections, provisioning).
    /// </summary>
    public int TotalObjectCreates => TotalCsoAdds + TotalProjections + TotalProvisioned;

    /// <summary>
    /// Aggregate count of all "update" operations (CSO updates, joins, attribute flows, exports).
    /// </summary>
    public int TotalObjectUpdates => TotalCsoUpdates + TotalJoins + TotalAttributeFlows + TotalExported;

    /// <summary>
    /// Aggregate count of all "delete" operations (CSO deletes, disconnections, deprovisioning).
    /// </summary>
    public int TotalObjectDeletes => TotalCsoDeletes + TotalDisconnections + TotalDeprovisioned;
    #endregion
}
