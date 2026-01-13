namespace JIM.Models.Enums;

/// <summary>
/// Represents the type of change that occurred during a synchronisation operation.
/// Different run profile types use different subsets of these values.
/// </summary>
public enum ObjectChangeType
{
    NotSet,

    // Import operations (CSO changes)
    /// <summary>
    /// CSO added to staging.
    /// </summary>
    Added,

    /// <summary>
    /// CSO attributes updated.
    /// </summary>
    Updated,

    /// <summary>
    /// Object deleted from source system.
    /// Note: The CSO is marked with ConnectedSystemObjectStatus.Obsolete internally.
    /// </summary>
    Deleted,

    // Sync operations (MVO changes)
    /// <summary>
    /// New MVO created via projection.
    /// </summary>
    Projected,

    /// <summary>
    /// CSO joined to existing MVO.
    /// </summary>
    Joined,

    /// <summary>
    /// Attributes flowed to MVO (no join/projection).
    /// </summary>
    AttributeFlow,

    /// <summary>
    /// CSO disconnected from MVO (out of scope).
    /// </summary>
    Disconnected,

    // Export operations
    /// <summary>
    /// New CSO created in target system (provisioning).
    /// </summary>
    Provisioned,

    /// <summary>
    /// CSO attributes exported to target system.
    /// </summary>
    Exported,

    /// <summary>
    /// CSO deleted from target system (deprovisioning).
    /// </summary>
    Deprovisioned,

    // Shared
    /// <summary>
    /// Indicates that export evaluation detected the CSO already has the target value(s),
    /// so no pending export was created. Used for tracking/reporting purposes.
    /// </summary>
    NoChange,

    // Pending export visibility (surfaced during sync)
    /// <summary>
    /// A pending export exists that is staged for the next export run.
    /// This surfaces unconfirmed exports (ExportNotImported status) so operators
    /// can see what changes will be made to connected systems.
    /// </summary>
    PendingExport
}
