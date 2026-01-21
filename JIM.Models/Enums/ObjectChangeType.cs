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

    /// <summary>
    /// CSO disconnected from MVO because it fell out of scope of import sync rule scoping criteria.
    /// This provides clear audit trail showing WHY the disconnection occurred, enabling the UI
    /// to explain consequences (attribute removal, MVO deletion rules, etc).
    /// </summary>
    DisconnectedOutOfScope,

    /// <summary>
    /// CSO fell out of scope of import sync rule scoping criteria but remained joined
    /// because InboundOutOfScopeAction was set to RemainJoined. Attribute flow has stopped
    /// but the join is preserved ("once managed, always managed" pattern).
    /// </summary>
    OutOfScopeRetainJoin,

    /// <summary>
    /// Drift was detected during delta sync: the CSO attribute values in the target system
    /// differed from the expected values on the MVO. A corrective pending export was created
    /// to restore the expected state. This provides visibility into drift enforcement.
    /// </summary>
    DriftCorrection,

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
    /// This surfaces unconfirmed exports (ExportNotConfirmed status) so operators
    /// can see what changes will be made to connected systems.
    /// </summary>
    PendingExport,

    // Pending export reconciliation (surfaced during confirming import)
    /// <summary>
    /// A pending export was confirmed during the confirming import.
    /// The exported attribute values matched the imported values.
    /// </summary>
    PendingExportConfirmed
}
