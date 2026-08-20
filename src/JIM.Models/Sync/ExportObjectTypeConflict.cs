// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Records an outbound Synchronisation Rule that could not export a Metaverse Object because the one
/// Connected System Object that Object holds in the target Connected System is of a different Connected
/// System Object Type than the Rule targets. No Pending Export is staged for the Rule; the Metaverse
/// Object's other export Rules are unaffected. Surfaced to the administrator as a
/// <c>CouldNotExportDueToExistingConnectedSystemObject</c> Run Profile Execution Item error.
/// </summary>
/// <remarks>
/// A Metaverse Object holds at most one Connected System Object per Connected System, an invariant the
/// application layer states and IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique
/// backs. Export evaluation resolves the Object to export to by (Metaverse Object, Connected System)
/// alone, so a Rule targeting a second Object Type resolves to whichever Object already occupies that
/// slot. Before #1331 the mismatch went unnoticed: the Rule staged a Pending Export writing its own
/// Object Type's attribute values onto an Object of a different type, and two such Rules collided on
/// IX_PendingExports_ConnectedSystemObjectId_Unique, killing the whole synchronisation run with a raw
/// PostgreSQL error rather than reporting anything an administrator could act on.
/// </remarks>
public class ExportObjectTypeConflict
{
    /// <summary>
    /// The Metaverse Object the Synchronisation Rule was evaluating.
    /// </summary>
    public required Guid MetaverseObjectId { get; set; }

    /// <summary>
    /// The name of the outbound Synchronisation Rule that could not export.
    /// </summary>
    public required string SyncRuleName { get; set; }

    /// <summary>
    /// The name of the Connected System Object Type the Synchronisation Rule targets.
    /// </summary>
    public required string TargetObjectTypeName { get; set; }

    /// <summary>
    /// The Connected System Object already occupying the Metaverse Object's slot in this Connected System.
    /// </summary>
    public required Guid ExistingConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The name of that existing Connected System Object's Object Type.
    /// </summary>
    public required string ExistingObjectTypeName { get; set; }
}
