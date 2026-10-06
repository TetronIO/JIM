// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;

namespace JIM.Models.Transactional;

/// <summary>
/// A generated value Collision Remediation has revised on a Metaverse Object, waiting for the next synchronisation
/// to carry the revision to the queued exports (Unique Value Generation, #242, release 4; plan decision 8 as revised at
/// the release 4 plan review). Written by the export run in the same transaction as the Metaverse write it describes;
/// every synchronisation, full or delta, drains these records after its page loop, evaluating the object's exports
/// with the revised attribute as changed (so the queued Pending Export of every target, the rejecting system's
/// included, is updated to the new value), writes the causal edge from the remediating export item to the item that
/// queued the update, and deletes the record.
/// <para>
/// A record per revision, not a flag on the object: the Temporal Scope Reconciler's review flag (#892) re-evaluates
/// exports with no changed attributes, which stages nothing for an object already in scope, so it cannot carry a value
/// change. Import mode only: an export-mode revision rewrites the Connected System Object's queued export directly,
/// since no Metaverse Object is involved.
/// </para>
/// </summary>
public class GeneratedValueRevisionPending
{
    public Guid Id { get; set; }

    /// <summary>
    /// The Metaverse Object whose generated value was revised. Cascade delete: a deleted object has nothing left to
    /// carry to its exports.
    /// </summary>
    public Guid MetaverseObjectId { get; set; }

    public MetaverseObject? MetaverseObject { get; set; }

    /// <summary>
    /// The Metaverse attribute whose value was revised: what the drain treats as changed. Cascade delete with the
    /// attribute.
    /// </summary>
    public int MetaverseAttributeId { get; set; }

    public MetaverseAttribute? MetaverseAttribute { get; set; }

    /// <summary>
    /// The export Run Profile Execution Item whose rejection caused the revision: the cause side of the
    /// <see cref="CausalEdgeType.ExportRejectionCausedGeneratedValueRevision"/> edge the drain writes. Plain Guid, not a
    /// foreign key: execution items are bulk-managed, and the export item is persisted after this record.
    /// </summary>
    public Guid? RemediatingActivityRunProfileExecutionItemId { get; set; }

    /// <summary>
    /// Why the value was revised: <see cref="CausalReasonCode.GeneratedValueAlreadyInUse"/> or
    /// <see cref="CausalReasonCode.GeneratedValueRenameAuthorised"/>. Carried onto the causal edge.
    /// </summary>
    public CausalReasonCode ReasonCode { get; set; }

    /// <summary>
    /// The Connected System whose export rejected the previous value. Plain int, so the edge still names it after the
    /// system is deleted.
    /// </summary>
    public int? RejectedByConnectedSystemId { get; set; }

    /// <summary>
    /// The rejecting Connected System's name at the time, for the causal edge.
    /// </summary>
    public string? RejectedByConnectedSystemName { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;
}
