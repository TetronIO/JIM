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
}
