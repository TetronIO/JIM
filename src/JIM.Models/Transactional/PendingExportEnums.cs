// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

public enum PendingExportChangeType
{
    /// <summary>
    /// Create an object in a Connected System.
    /// </summary>
    Create = 0,
    /// <summary>
    /// Perform updates to attribute values on an object in a Connected System.
    /// </summary>
    Update = 1,
    /// <summary>
    /// Delete an object in a Connected System.
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

/// <summary>
/// Tracks the confirmation status of individual attribute changes within a PendingExport.
/// </summary>
public enum PendingExportAttributeChangeStatus
{
    /// <summary>
    /// The attribute change has not yet been exported.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The attribute change has been exported and is awaiting confirmation via a confirming import.
    /// </summary>
    ExportedPendingConfirmation = 1,

    /// <summary>
    /// The attribute change was exported, but the confirming import returned a different value.
    /// Will be retried on the next export run.
    /// </summary>
    ExportedNotConfirmed = 2,

    /// <summary>
    /// The attribute change failed after maximum retry attempts and requires manual intervention.
    /// </summary>
    Failed = 3
}

public enum PendingExportStatus
{
    /// <summary>
    /// The Pending Export has not yet been applied against the Connected System.
    /// </summary>
    Pending = 0,
    /// <summary>
    /// The Pending Export was applied against the Connected System, but one or more attribute values were not confirmed
    /// during the confirming import. Will be retried on the next export run.
    /// </summary>
    ExportNotConfirmed = 1,
    /// <summary>
    /// The Pending Export is currently being processed by a connector.
    /// </summary>
    Executing = 2,
    /// <summary>
    /// The Pending Export failed after maximum retry attempts and requires manual intervention.
    /// </summary>
    Failed = 3,
    /// <summary>
    /// The Pending Export was successfully applied to the Connected System.
    /// </summary>
    Exported = 4
}

/// <summary>
/// Specifies the run mode for synchronisation operations.
/// </summary>
public enum SyncRunMode
{
    /// <summary>
    /// Evaluates Synchronisation Rules and shows what changes would be made, but does not persist
    /// any Pending Exports or execute changes.
    /// </summary>
    PreviewOnly = 0,
    /// <summary>
    /// Evaluates Synchronisation Rules, shows the preview, then persists Pending Exports and executes them.
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

/// <summary>
/// Why a reference attribute change on a Pending Export has not been written yet (issue #1398).
/// </summary>
public enum UnresolvedReferenceReason
{
    /// <summary>
    /// The referenced Metaverse Object has a Connected System Object in the target and that object now
    /// carries its anchor, so the reference will resolve and be written on the next export run.
    /// </summary>
    Resolvable = 0,

    /// <summary>
    /// The referenced Metaverse Object has a Connected System Object in the target but it does not
    /// carry an anchor yet: typically its own Create has not been executed or confirmed. The reference
    /// is waiting, not failing.
    /// </summary>
    AwaitingAnchor = 1,

    /// <summary>
    /// The referenced Metaverse Object has no Connected System Object in the target at all: it is out
    /// of scope for every rule into this system, or has not been provisioned. The reference cannot
    /// resolve as things stand and is surfaced per the Connected System's Unresolved Reference Handling.
    /// </summary>
    NotInTargetSystem = 2
}
