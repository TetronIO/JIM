// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// The result of projecting a batch of successfully exported Pending Exports' attribute changes
/// onto their Connected System Objects' current in-memory attribute values (issue #1079:
/// optimistic export apply). Produced by <c>OptimisticExportApplyCalculator</c>; consumed by the
/// sync hot-path repository to persist the delta, and by the caller to keep the in-memory
/// Connected System Object graph consistent for later passes in the same export run.
/// </summary>
public class OptimisticExportApplyDelta
{
    /// <summary>
    /// New <see cref="ConnectedSystemObjectAttributeValue"/> rows to insert. Each row's
    /// <see cref="ConnectedSystemObjectAttributeValue.ConnectedSystemObject"/> navigation identifies
    /// the owning Connected System Object.
    /// </summary>
    public List<ConnectedSystemObjectAttributeValue> Additions { get; set; } = [];

    /// <summary>
    /// The Ids of existing <see cref="ConnectedSystemObjectAttributeValue"/> rows to delete.
    /// </summary>
    public List<Guid> RemovalValueIds { get; set; } = [];

    /// <summary>
    /// Number of Pending Export attribute changes that resulted in no work (the value already
    /// matched the Connected System Object's current state, the change carried no value payload, or
    /// its attribute's data type is not one optimistic apply maps). A change that could not be
    /// recorded is not counted here; see <see cref="UnrecordedChanges"/>.
    /// </summary>
    public int SkippedChangeCount { get; set; }

    /// <summary>
    /// The exported changes that could not be recorded, because each holds its value in another data
    /// type's column than its attribute's (#2183). The target holds a value JIM has no record of, so a
    /// Pending Export carrying one is not complete: a confirming import records what the target holds,
    /// and an auto-confirmed export is left unconfirmed rather than confirmed.
    /// </summary>
    public List<OptimisticExportUnrecordedChange> UnrecordedChanges { get; set; } = [];

    /// <summary>
    /// Number of Reference attribute changes that were applied with <c>UnresolvedReferenceValue</c>
    /// populated but <c>ReferenceValueId</c> left null, because the referenced Connected System
    /// Object could not be resolved from the in-run transient hint or the batched database lookup.
    /// These rows still confirm and still diff clean on the confirming import; they simply remain
    /// unresolved until a future import touches the object.
    /// </summary>
    public int UnresolvedReferenceCount { get; set; }
}
