// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Models.Sync;

/// <summary>
/// The result of deciding what to do with a CSO that has fallen out of an export Synchronisation Rule's scope.
/// Returned by <c>ISyncEngine.DecideOutOfScopeDeprovisioning</c> (#288 outbound extraction): the rule's
/// OutboundDeprovisionAction chooses disconnect or delete, and for delete the one-Pending-Export-per-CSO
/// collision policy chooses reuse, replace or create, exactly as the MVO-deletion decision does.
/// </summary>
/// <remarks>
/// The decision persists nothing itself. The orchestrator applies the join-break mutations, resolves the
/// existing Pending Export (from the run's working set or the database) and stages the Delete export.
/// </remarks>
public readonly struct OutOfScopeDeprovisioningDecision : IEquatable<OutOfScopeDeprovisioningDecision>
{
    /// <summary>
    /// What the matched rule's OutboundDeprovisionAction asks for.
    /// </summary>
    public OutOfScopeDeprovisioningAction Action { get; init; }

    /// <summary>
    /// An existing Delete Pending Export to reuse, when one is already attached to the CSO. PendingExports
    /// carries a unique index on ConnectedSystemObjectId, so creating a second would fail the insert.
    /// </summary>
    public PendingExport? ExistingPendingExportToReuse { get; init; }

    /// <summary>
    /// True when a Pending Export of another change type is attached to the CSO and must be deleted before the
    /// Delete export is created.
    /// </summary>
    public bool MustReplaceExistingPendingExport { get; init; }

    /// <inheritdoc />
    public bool Equals(OutOfScopeDeprovisioningDecision other) =>
        Action == other.Action &&
        ReferenceEquals(ExistingPendingExportToReuse, other.ExistingPendingExportToReuse) &&
        MustReplaceExistingPendingExport == other.MustReplaceExistingPendingExport;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is OutOfScopeDeprovisioningDecision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Action, MustReplaceExistingPendingExport);
}
