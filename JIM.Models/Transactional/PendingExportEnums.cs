namespace JIM.Models.Transactional;

public enum PendingExportChangeType
{
    /// <summary>
    /// Create an object in a connected system.
    /// </summary>
    Create = 0,
    /// <summary>
    /// Perform updates to attribute values on an object in a connected system.
    /// </summary>
    Update = 1,
    /// <summary>
    /// Delete an object in a connected system.
    /// </summary>
    Delete = 2
}

public enum PendingExportAttributeChangeType
{
    /// <summary>
    /// Add a value to a multi-valued attribute
    /// </summary>
    Add = 0,
    /// <summary>
    /// Set, or change an attribute value on a single-valued attribute
    /// </summary>
    Update = 1,
    /// <summary>
    /// Remove a single value, from either a single-valued, or multi-valued attribute
    /// </summary>
    Remove = 2,
    /// <summary>
    /// Remove all values from a multi-valued attribute, i.e. clear the attribute
    /// </summary>
    RemoveAll = 3
}

public enum PendingExportStatus
{
    /// <summary>
    /// The pending export has not yet been applied against the connected system.
    /// </summary>
    Pending = 0,
    /// <summary>
    /// The pending export was applied against the connected system, but one or more attribute values were not persisted.
    /// </summary>
    ExportNotImported = 1,
    /// <summary>
    /// The pending export is currently being processed by a connector.
    /// </summary>
    Executing = 2,
    /// <summary>
    /// The pending export failed after maximum retry attempts and requires manual intervention.
    /// </summary>
    Failed = 3,
    /// <summary>
    /// The pending export was successfully applied to the connected system.
    /// </summary>
    Exported = 4
}

/// <summary>
/// Specifies the run mode for synchronisation operations.
/// </summary>
public enum SyncRunMode
{
    /// <summary>
    /// Evaluates sync rules and shows what changes would be made, but does not persist
    /// any Pending Exports or execute changes.
    /// </summary>
    PreviewOnly = 0,
    /// <summary>
    /// Evaluates sync rules, shows the preview, then persists Pending Exports and executes them.
    /// </summary>
    PreviewAndSync = 1
}

/// <summary>
/// Phases of export execution.
/// </summary>
public enum ExportPhase
{
    /// <summary>
    /// Preparing exports (loading, generating previews).
    /// </summary>
    Preparing,

    /// <summary>
    /// Executing exports via connector.
    /// </summary>
    Executing,

    /// <summary>
    /// Resolving deferred references.
    /// </summary>
    ResolvingReferences,

    /// <summary>
    /// Export execution completed.
    /// </summary>
    Completed
}