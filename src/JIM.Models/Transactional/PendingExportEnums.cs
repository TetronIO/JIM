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
    /// Will be retried on the next export run, however many times it has been sent before.
    /// </summary>
    ExportedNotConfirmed = 2

    // 3 was Failed, given to a change still unconfirmed after five attempts. JIM no longer gives up on a change
    // (#2119); the value is retired and must not be reused, since it is persisted by ordinal.
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
    // 3 was Failed, given to an export after five failed attempts and never attempted again. JIM no longer gives up
    // on a Pending Export (#2119): one whose attempt failed stays Pending, with its failure recorded in
    // PendingExport.FailingSince. The value is retired and must not be reused, since it is persisted by ordinal.
    /// <summary>
    /// The Pending Export was successfully applied to the Connected System.
    /// </summary>
    Exported = 4,
    /// <summary>
    /// The Pending Export is held back because its generated value needs an administrator's decision:
    /// the target rejected the value and another Connected System already holds it (Unique Value
    /// Generation release 4). A Parked export is not attempted, and not counted as a failure; it clears once
    /// the decision is made.
    /// </summary>
    Parked = 5
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

/// <summary>
/// Why Pending Export reconciliation loads a multi-valued reference attribute of an object in full rather than asking the
/// database about it (#2669, #2809). Reported in the import's log, so a run can see why a page fell back.
/// </summary>
public enum ReconciliationProbeFallbackReason
{
    /// <summary>
    /// The attribute is read beyond the verdict (a naming attribute, an External ID or a Secondary External ID), or its
    /// definition is not loaded.
    /// </summary>
    IdentifierOrNamingAttribute = 0,

    /// <summary>
    /// A change names no Connected System Object, so it matches resolved rows by the distinguished name they keep, which
    /// no index answers: only outstanding references are found by their distinguished name.
    /// </summary>
    NamesNoObject = 1,

    /// <summary>
    /// The database's <c>lower()</c> does not fold case as JIM does, so nothing is compared ignoring case there.
    /// </summary>
    DatabaseFoldsCaseDifferently = 2,

    /// <summary>
    /// A value compared ignoring case is not ASCII, whose folding the database need not share.
    /// </summary>
    NonAsciiValue = 3
}

/// <summary>
/// Why export execution loads a large multi-valued reference attribute of an object whole rather than in part (#2816).
/// Reported on the batch's load span, so a run can see how often, and why, a group's whole membership was read.
/// </summary>
public enum ExportValueLoadFallbackReason
{
    /// <summary>
    /// A change replaces or clears the attribute's whole value set (an Update or a RemoveAll), so every value it holds
    /// is needed to record what was written.
    /// </summary>
    WholeSetChange = 0,

    /// <summary>
    /// A change names a reference still awaiting resolution, which the deferred pass may resolve after the load, so what
    /// it matches could change between the load and its use.
    /// </summary>
    UnresolvedReference = 1,

    /// <summary>
    /// A change names no Connected System Object, so it matches resolved rows by the distinguished name they keep, which
    /// no index answers: only outstanding references are found by their distinguished name.
    /// </summary>
    NamesNoObject = 2,

    /// <summary>
    /// The database's <c>lower()</c> does not fold case as JIM does, so nothing is compared ignoring case there.
    /// </summary>
    DatabaseFoldsCaseDifferently = 3,

    /// <summary>
    /// A value compared ignoring case is not ASCII, whose folding the database need not share.
    /// </summary>
    NonAsciiValue = 4,

    /// <summary>
    /// A change of a kind the planner does not know how to put as a question.
    /// </summary>
    UnrecognisedChangeType = 5
}
