// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Models.Sync;

/// <summary>
/// The result of deciding whether deleting a Metaverse Object stages a Delete export for one of its joined CSOs.
/// Returned by <c>ISyncEngine.DecideMvoDeletionExport</c> (#288 outbound extraction; the #655 semantics: the
/// matching export Synchronisation Rules' OutboundDeprovisionAction drives the verdict, Delete wins a conflict,
/// and the one-Pending-Export-per-CSO collision policy chooses reuse, replace or create).
/// </summary>
/// <remarks>
/// The decision carries everything the apply step needs and persists nothing itself: the identifier payload is
/// captured at decision time because the CSO is disconnected immediately afterwards and may be gone before the
/// export runs, and the conflict facts are carried so the orchestrator can log what the engine resolved.
/// </remarks>
public readonly struct MvoDeletionExportDecision : IEquatable<MvoDeletionExportDecision>
{
    /// <summary>
    /// Whether a Delete export should be staged for the CSO. False always means disconnect-only, never "do
    /// nothing": disconnection is unconditional on MVO deletion.
    /// </summary>
    public bool ShouldStageDeleteExport { get; init; }

    /// <summary>
    /// Why the verdict came out this way, in terms the orchestrator logs and a preview can display.
    /// </summary>
    public MvoDeletionExportReason Reason { get; init; }

    /// <summary>
    /// The rule whose Delete action won, when <see cref="ShouldStageDeleteExport"/> is true.
    /// </summary>
    public SyncRule? WinningRule { get; init; }

    /// <summary>
    /// How many enabled export Synchronisation Rules matched the CSO's (system, object type) pair.
    /// </summary>
    public int MatchingRuleCount { get; init; }

    /// <summary>
    /// True when matching rules disagreed about the action and Delete won (#655). Surfaced rather than silently
    /// resolved, because a hidden conflict is how two administrators each believe their rule is in charge.
    /// </summary>
    public bool RulesConflicted { get; init; }

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

    /// <summary>
    /// The CSO's secondary external identifier (the Distinguished Name, for LDAP), captured at decision time so
    /// the connector can still resolve the object after the CSO is disconnected or deleted. Null when the CSO
    /// carries none; the export is staged regardless, because refusing would leave the object undeleted silently.
    /// </summary>
    public string? SecondaryExternalIdValue { get; init; }

    /// <summary>
    /// The attribute the secondary external identifier belongs to, when <see cref="SecondaryExternalIdValue"/>
    /// is present.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? SecondaryExternalIdAttribute { get; init; }

    /// <summary>
    /// Creates a disconnect-only decision.
    /// </summary>
    public static MvoDeletionExportDecision DisconnectOnly(MvoDeletionExportReason reason, int matchingRuleCount = 0) => new()
    {
        ShouldStageDeleteExport = false,
        Reason = reason,
        MatchingRuleCount = matchingRuleCount
    };

    /// <inheritdoc />
    public bool Equals(MvoDeletionExportDecision other) =>
        ShouldStageDeleteExport == other.ShouldStageDeleteExport &&
        Reason == other.Reason &&
        ReferenceEquals(WinningRule, other.WinningRule) &&
        MatchingRuleCount == other.MatchingRuleCount &&
        RulesConflicted == other.RulesConflicted &&
        ReferenceEquals(ExistingPendingExportToReuse, other.ExistingPendingExportToReuse) &&
        MustReplaceExistingPendingExport == other.MustReplaceExistingPendingExport &&
        SecondaryExternalIdValue == other.SecondaryExternalIdValue &&
        ReferenceEquals(SecondaryExternalIdAttribute, other.SecondaryExternalIdAttribute);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MvoDeletionExportDecision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ShouldStageDeleteExport, Reason, MatchingRuleCount);
}
