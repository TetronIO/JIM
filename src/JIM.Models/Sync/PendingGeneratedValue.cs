// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Models.Sync;

/// <summary>
/// A generated mapping's (Unique Value Generation, #242) result from inbound Attribute Flow, recorded in place of
/// a value when a <see cref="SyncRuleMapping"/> carrying a <see cref="SyncRuleMapping.Generation"/> row wins
/// attribute priority for its target Metaverse attribute. The synchronous engine performs no I/O and knows
/// nothing of the unique value generation service that actually produces a value (plan decision 6): it records
/// this request and stops, so a later pass can resolve it.
/// <para>
/// Purely transient: never persisted (see <see cref="Core.MetaverseObject.PendingGeneratedValues"/>). The worker
/// resolves every request on a page, with provenance, the generation gates and an assignment row, before the
/// page is persisted; nothing here is written to the Metaverse Object directly by the engine.
/// </para>
/// </summary>
public sealed class PendingGeneratedValue
{
    /// <summary>
    /// The generated mapping that produced this request, carrying <see cref="SyncRuleMapping.Generation"/> (the
    /// uniqueness token and its settings) and <see cref="SyncRuleMapping.TargetMetaverseAttribute"/>.
    /// </summary>
    public required SyncRuleMapping Mapping { get; init; }

    /// <summary>
    /// The target Metaverse attribute id, duplicated from <see cref="Mapping"/> so callers can filter without
    /// re-deriving it from the navigation.
    /// </summary>
    public required int AttributeId { get; init; }

    /// <summary>
    /// The Connected System that contributed this request.
    /// </summary>
    public int? ContributedBySystemId { get; init; }

    /// <summary>
    /// The Synchronisation Rule that contributed this request: the generated mapping's own rule. What
    /// <c>FindEffectiveIncumbentSyncRuleId</c> reads as the attribute's owner while this request stands.
    /// </summary>
    public int? ContributedBySyncRuleId { get; init; }

    /// <summary>
    /// The Connected System Object whose Attribute Flow recorded this request.
    /// </summary>
    public Guid? SourceConnectedSystemObjectId { get; init; }

    /// <summary>
    /// The generated mapping's base expression, evaluated and inbound-processed exactly as an ordinary expression
    /// mapping's value would be (trim, whitespace collapse, case normalisation). Null when the mapping has no base
    /// expression at all (valid for a Sequence or Random token), or when <see cref="BaseUnavailable"/> is set.
    /// </summary>
    public string? BaseValue { get; init; }

    /// <summary>
    /// True when the base expression could not be evaluated for this object this pass: an input it reads has no
    /// value and the mapping's Missing Input Behaviour is "contribute no value" (the default for a generated
    /// mapping, FR 29), or evaluation produced no usable value. The worker must keep whatever value the object
    /// already holds and generate nothing this pass, never clear it: a committed generated value is sticky and is
    /// never recomputed from its inputs once assigned (FR 10), so a temporarily missing input can only withhold a
    /// new generation, not undo an old one.
    /// </summary>
    public bool BaseUnavailable { get; init; }
}
