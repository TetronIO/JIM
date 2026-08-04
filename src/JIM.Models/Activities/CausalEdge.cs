// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;

namespace JIM.Models.Activities;

/// <summary>
/// One recorded cause-and-effect link across the synchronisation engine's cascade seams, written by
/// the worker at the moment it creates the effect and never reconstructed afterwards.
///
/// A single Run Profile Execution Item describes what happened to one object in one run, which
/// cannot explain a Pending Export whose cause was a Metaverse Object deletion in a different
/// Activity on a different Connected System. This table supplies the missing link, so the Causality
/// panel can walk upward from an effect to its causes and (in Phase 2) downward again.
///
/// The two sides are deliberately asymmetric:
/// <list type="bullet">
/// <item><description>
/// The <b>effect</b> side is a real foreign key that cascades with the Run Profile Execution Item.
/// An edge whose effect is gone is pure garbage; nothing will ever query it.
/// </description></item>
/// <item><description>
/// The <b>cause</b> side is unconstrained snapshot scalars with no foreign key, following the
/// <see cref="ActivityRunProfileExecutionItemSyncOutcome.SyncRuleId"/> /
/// <see cref="ActivityRunProfileExecutionItemSyncOutcome.SyncRuleName"/> precedent. A cascading
/// foreign key here would delete the very edge that explains a still-retained effect the moment its
/// cause aged out of history, reintroducing the "this change has no cause whatsoever" bug this
/// exists to fix. Causes are always older than their effects, so once a deployment has been live
/// longer than one retention window an unresolvable cause is the normal case, not an error; the read
/// path resolves best-effort and renders an explicit "cause no longer retained" terminal state.
/// </description></item>
/// </list>
/// </summary>
public class CausalEdge
{
    /// <summary>
    /// Unique identifier for this edge, assigned in code so the raw-SQL bulk insert path does not
    /// need to round-trip database-generated keys.
    /// </summary>
    public Guid Id { get; set; }

    // ─── Effect side: real foreign key, cascades with the Run Profile Execution Item ───

    /// <summary>
    /// The Run Profile Execution Item this edge explains. Cascades on delete, so purging an Activity
    /// takes its edges with it and no orphans accumulate.
    /// </summary>
    public Guid EffectRunProfileExecutionItemId { get; set; }

    /// <summary>
    /// Foreign key navigation for <see cref="EffectRunProfileExecutionItemId"/>.
    /// </summary>
    public ActivityRunProfileExecutionItem EffectRunProfileExecutionItem { get; set; } = null!;

    /// <summary>
    /// The specific sync outcome node this cause produced, where one exists. Nullable because some
    /// seams attach to the item as a whole, but populated wherever possible: cohort grouping is
    /// computed per effect, so an edge that does not name its outcome cannot be grouped correctly on
    /// an item carrying more than one outcome.
    /// </summary>
    public Guid? EffectSyncOutcomeId { get; set; }

    /// <summary>
    /// Transient reference to the outcome this edge explains, set by the seam and resolved into
    /// <see cref="EffectSyncOutcomeId"/> by the flush. Never persisted.
    /// </summary>
    /// <remarks>
    /// A sync outcome has no id when it is created: ids are assigned when the batch is flushed. The seam
    /// therefore cannot write <see cref="EffectSyncOutcomeId"/> itself, and has to name the outcome by
    /// reference and let the flush resolve it, mirroring how the flush resolves an outcome's own
    /// <c>ConnectedSystemObjectChangeId</c>. Leave null where the edge attaches to the item as a whole.
    /// </remarks>
    [NotMapped]
    public ActivityRunProfileExecutionItemSyncOutcome? EffectSyncOutcome { get; set; }

    // ─── Cause side: snapshot scalars, no foreign key, resolved best-effort at read time ───

    /// <summary>
    /// The Run Profile Execution Item that recorded the cause, when the cause was itself a recorded
    /// event. Not a foreign key; see the type remarks.
    /// </summary>
    public Guid? CauseRunProfileExecutionItemId { get; set; }

    /// <summary>
    /// The specific sync outcome node that was the cause, when known. Not a foreign key.
    /// </summary>
    public Guid? CauseSyncOutcomeId { get; set; }

    /// <summary>
    /// Transient reference to the Run Profile Execution Item recording the cause, resolved into
    /// <see cref="CauseRunProfileExecutionItemId"/> when the edge is persisted. Never persisted itself.
    /// </summary>
    [NotMapped]
    public ActivityRunProfileExecutionItem? CauseRunProfileExecutionItem { get; set; }

    /// <summary>
    /// Transient reference to the outcome node recording the cause, resolved into
    /// <see cref="CauseSyncOutcomeId"/> when the edge is persisted. Never persisted itself.
    /// </summary>
    /// <remarks>
    /// The cause side needs this for the same reason the effect side does, and for one case in particular:
    /// where cause and effect are persisted in the <b>same</b> batch (Metaverse Object Housekeeping deletes an
    /// object and records the removals it caused in one Activity), the causing outcome has no id either when
    /// the edge is built. Resolving eagerly there would silently store no cause at all.
    /// </remarks>
    [NotMapped]
    public ActivityRunProfileExecutionItemSyncOutcome? CauseSyncOutcome { get; set; }

    /// <summary>
    /// The Metaverse Object that was the cause, when the cause is best identified by object rather
    /// than by event (a deletion cascade names the deleted object). Not a foreign key: the object is
    /// routinely already gone by the time anyone reads the edge, which is the point.
    /// </summary>
    public Guid? CauseMetaverseObjectId { get; set; }

    /// <summary>
    /// The Connected System Object that was the cause, when the cause is best identified by object.
    /// Not a foreign key.
    /// </summary>
    public Guid? CauseConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The Pending Export whose execution was the cause. Not a foreign key: a confirmed Pending Export is
    /// deleted by the very reconciliation that writes this edge, so the row is already gone.
    /// </summary>
    /// <remarks>
    /// This is the export cycle's identity, and recording it is the whole reason the export-to-confirmation
    /// hop needs an edge at all. Reconciliation correlates a Pending Export to an imported object by Connected
    /// System Object id alone, and an object cycles through export and import repeatedly, so pairing a
    /// confirmation with its export after the fact can pick the wrong cycle and attribute a confirmation to an
    /// export that did not produce it. The Pending Export row is unique per cycle, so it distinguishes them;
    /// the export's own Run Profile Execution Item carries the same id, which is how the read path resolves
    /// from here back to the export, best-effort like every other cause reference.
    /// </remarks>
    public Guid? CausePendingExportId { get; set; }

    /// <summary>
    /// How the cause was named at the time, so a chain still reads sensibly after the cause itself
    /// has been purged. Without this a truncated chain could only say that something unidentifiable
    /// used to be here.
    /// </summary>
    public string? CauseDisplayName { get; set; }

    // ─── Attribution tuple: what cohort grouping keys on ───

    /// <summary>
    /// Which cascade seam this edge records.
    /// </summary>
    public CausalEdgeType EdgeType { get; set; }

    /// <summary>
    /// Why the cause produced the effect, as a code rather than a sentence. See
    /// <see cref="CausalReasonCode"/> for why this must never be free text.
    /// </summary>
    public CausalReasonCode ReasonCode { get; set; }

    /// <summary>
    /// The Connected System the cause occurred on, when one applies. Not a foreign key, so the
    /// attribution survives the system's later deletion.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The Connected System's name at the time, so the chain reads correctly after a rename or a
    /// deletion.
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>
    /// The Synchronisation Rule responsible, when one applies. Not a foreign key, matching the
    /// existing sync outcome precedent.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The Synchronisation Rule's name at the time.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// When the edge was written (UTC). This is the effect's time, not the cause's: the cause's own
    /// timestamp lives on the record the cause side points at, and may be long purged.
    /// </summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Resolves every transient reference this edge holds into the id column beside it, and stamps the effect
    /// item's id. Called by each persistence path immediately before writing, once ids exist.
    /// </summary>
    /// <remarks>
    /// None of the four ids an edge points at exists when a seam creates the edge: Run Profile Execution Items
    /// and sync outcomes are both assigned ids as they are persisted. Every path that writes edges must
    /// therefore call this, and there is more than one such path (the sync engine's bulk flush and the EF-based
    /// path used by Metaverse Object Housekeeping). Keeping the resolution here rather than in each of them is
    /// what stops the two drifting: a path that resolved three of the four would store an edge that looks
    /// complete and silently names no cause.
    ///
    /// An id of <c>Guid.Empty</c> behind a reference means the record was never persisted, which is not
    /// something anyone can navigate to; those store null rather than a link that resolves to nothing.
    /// Existing ids are left alone, so an edge whose cause was persisted in an earlier run is untouched.
    /// </remarks>
    /// <param name="effectRunProfileExecutionItemId">The id of the item this edge was buffered on.</param>
    public void ResolveTransientReferences(Guid effectRunProfileExecutionItemId)
    {
        if (Id == Guid.Empty)
            Id = Guid.NewGuid();

        EffectRunProfileExecutionItemId = effectRunProfileExecutionItemId;

        if (EffectSyncOutcome != null && EffectSyncOutcome.Id != Guid.Empty)
            EffectSyncOutcomeId = EffectSyncOutcome.Id;

        if (CauseRunProfileExecutionItem != null && CauseRunProfileExecutionItem.Id != Guid.Empty)
            CauseRunProfileExecutionItemId = CauseRunProfileExecutionItem.Id;

        if (CauseSyncOutcome != null && CauseSyncOutcome.Id != Guid.Empty)
            CauseSyncOutcomeId = CauseSyncOutcome.Id;
    }
}
