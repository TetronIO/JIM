// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Models.Sync;

/// <summary>
/// One outbound decision record from a synchronisation preview (#288 plan Phase 2): what one export
/// Synchronisation Rule would do for one Metaverse Object, decided by the pure engine and persisted nowhere.
/// A Staging entry carries the engine's staging verdict and the attribute changes a real evaluation would
/// stage; a Deprovisioning entry carries the out-of-scope verdict for a joined target object.
/// </summary>
/// <remarks>
/// The attribute changes are unpersisted <see cref="PendingExportAttributeValueChange"/> instances, true to
/// the decision records the real pipeline stages; the serialisable outcome tree the PRD's preview surface
/// needs is composed from these in the plan's Phase 3, not here.
/// </remarks>
public class OutboundPreviewEntry
{
    /// <summary>
    /// Whether this entry records a staging or a deprovisioning decision.
    /// </summary>
    public OutboundPreviewEntryKind Kind { get; init; }

    /// <summary>
    /// The Metaverse Object the decision is about.
    /// </summary>
    public Guid MetaverseObjectId { get; init; }

    /// <summary>
    /// The export Synchronisation Rule that produced the decision.
    /// </summary>
    public int SyncRuleId { get; init; }

    /// <summary>
    /// The rule's name, carried so a preview reads without a rule lookup.
    /// </summary>
    public string SyncRuleName { get; init; } = string.Empty;

    /// <summary>
    /// The rule's target Connected System.
    /// </summary>
    public int ConnectedSystemId { get; init; }

    /// <summary>
    /// The engine's staging verdict, for a Staging entry.
    /// </summary>
    public OutboundStagingOutcome? StagingOutcome { get; init; }

    /// <summary>
    /// The change type the preview concludes would be staged: Create for provisioning (or Update when
    /// read-only export matching found an existing object to join), Update for an existing presence; null
    /// when nothing would be staged.
    /// </summary>
    public PendingExportChangeType? EffectiveChangeType { get; init; }

    /// <summary>
    /// The Metaverse Object's existing CSO in the rule's target system, when one exists.
    /// </summary>
    public Guid? ExistingTargetCsoId { get; init; }

    /// <summary>
    /// The CSO read-only export matching found for a provisioning verdict, when one matched. A real run
    /// would claim it and stage an Update instead of provisioning; the preview never claims.
    /// </summary>
    public Guid? WouldJoinCsoId { get; init; }

    /// <summary>
    /// The attribute changes a real evaluation would stage, unpersisted.
    /// </summary>
    public List<PendingExportAttributeValueChange> AttributeChanges { get; init; } = [];

    /// <summary>
    /// How many attribute changes were skipped because the target already holds the value (no net change).
    /// </summary>
    public int NoNetChangeSkippedCount { get; init; }

    /// <summary>
    /// The changes counted by <see cref="NoNetChangeSkippedCount"/>, unpersisted (#1443).
    /// </summary>
    /// <remarks>
    /// The value a change was skipped for is the target's CURRENT state for that attribute, which is what lets a
    /// configuration change preview state an old-to-new pair. A preview diffs what two configurations would stage,
    /// and a value the target already holds is staged by neither, so reading <see cref="AttributeChanges"/> alone
    /// would report "would now write X" with nothing to compare X against.
    /// </remarks>
    public List<PendingExportAttributeValueChange> NoNetChangeSkippedChanges { get; init; } = [];

    /// <summary>
    /// The out-of-scope deprovisioning verdict, for a Deprovisioning entry.
    /// </summary>
    public OutOfScopeDeprovisioningDecision? DeprovisioningDecision { get; init; }
}
