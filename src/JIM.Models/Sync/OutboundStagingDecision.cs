// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Models.Sync;

/// <summary>
/// The result of deciding what kind of export, if any, a Metaverse Object change stages against one export
/// Synchronisation Rule's target. Returned by <c>ISyncEngine.DecideOutboundStaging</c> (#288 outbound
/// extraction): the verdict behind the three <c>CreateOrUpdatePendingExport*</c> entry points.
/// </summary>
/// <remarks>
/// The decision persists nothing itself. Export matching, provisioning-CSO creation, attribute delta
/// computation and Pending Export construction are the orchestrator's to perform, informed by this verdict.
/// </remarks>
public readonly struct OutboundStagingDecision : IEquatable<OutboundStagingDecision>
{
    /// <summary>
    /// The verdict.
    /// </summary>
    public OutboundStagingOutcome Outcome { get; init; }

    /// <summary>
    /// The Pending Export change type the verdict implies: Create for the provisioning outcomes, Update for an
    /// existing target presence, null when nothing is staged. The orchestrator may still turn a
    /// <see cref="OutboundStagingOutcome.ProvisionNewCso"/> Create into an Update when export matching finds
    /// and claims an existing object.
    /// </summary>
    public PendingExportChangeType? ChangeType { get; init; }

    /// <summary>
    /// The Object Type conflict, when <see cref="Outcome"/> is
    /// <see cref="OutboundStagingOutcome.ObjectTypeConflict"/>; carried so the orchestrator can report it
    /// (RPEI or log) exactly as the braided implementation did.
    /// </summary>
    public ExportObjectTypeConflict? Conflict { get; init; }

    /// <inheritdoc />
    public bool Equals(OutboundStagingDecision other) =>
        Outcome == other.Outcome &&
        ChangeType == other.ChangeType &&
        ReferenceEquals(Conflict, other.Conflict);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is OutboundStagingDecision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Outcome, ChangeType);
}
