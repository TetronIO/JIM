// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Web.Causality;

/// <summary>
/// One node in the causality event tree: a sync outcome enriched with everything the visualisation
/// needs to render it (labels, tone, icon, lane, owning system, badge, entity links, sentence
/// segments and normalised attribute rows). Children are ordered by the outcome Ordinal.
/// </summary>
public sealed class CausalityEvent
{
    /// <summary>
    /// The underlying sync outcome type, or null for a synthetic event.
    /// </summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType? OutcomeType { get; init; }

    /// <summary>
    /// Whether this event stands for something the run decided rather than something it recorded.
    /// </summary>
    /// <remarks>
    /// A Deletion Rule that evaluates and declines produces no outcome: nothing happened, so there is nothing
    /// to record. "The Metaverse Object was not deleted" is nonetheless one of the more important things the
    /// page can say, and it is derivable from the decision-time policy snapshot the item already carries.
    ///
    /// Built once here rather than per view. The Flow and Graph views each construct their own "Source record"
    /// root, which is the precedent for a synthetic node, but that precedent is also why
    /// <see cref="CausalitySourceLabels"/> exists: three copies of one node drifted apart. A synthetic event
    /// that all three views must agree on belongs in the model.
    ///
    /// A synthetic event has no <see cref="OutcomeType"/>, is never selectable, and carries no attribute rows;
    /// consumers keying off the outcome type must treat null as "not a recorded outcome" rather than defaulting.
    /// </remarks>
    public bool IsSynthetic { get; init; }

    /// <summary>
    /// Plain-language label (e.g. "Identity created").
    /// </summary>
    public string PlainLabel { get; init; } = string.Empty;

    /// <summary>
    /// Technical label (e.g. "MVO Projected").
    /// </summary>
    public string TechnicalLabel { get; init; } = string.Empty;

    /// <summary>
    /// Visual tone for colour coding.
    /// </summary>
    public CausalityTone Tone { get; init; }

    /// <summary>
    /// Material icon string.
    /// </summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>
    /// The Flow view column this event belongs to.
    /// </summary>
    public CausalityLane Lane { get; init; }

    /// <summary>
    /// Id of the Connected System this event belongs to, for downstream grouping. Null for
    /// Identity-lane events (they belong to JIM itself) or when the id is unknown.
    /// </summary>
    public int? SystemId { get; init; }

    /// <summary>
    /// Name of the Connected System this event belongs to, for downstream group captions.
    /// </summary>
    public string? SystemName { get; init; }

    /// <summary>
    /// Attention badge: "Destructive" for Identity deletions, "Needs attention" for export
    /// failures, else null.
    /// </summary>
    public string? Badge { get; init; }

    /// <summary>
    /// Quantitative detail carried by the outcome (e.g. the number of attributes that flowed).
    /// </summary>
    public int? DetailCount { get; init; }

    /// <summary>
    /// Plain contextual message for display (e.g. deletion reasoning, connector error). Never the
    /// overloaded "csId|csoTypeName" link channel; that is parsed into <see cref="SystemId"/> and
    /// the entity links instead.
    /// </summary>
    public string? DetailMessage { get; init; }

    /// <summary>
    /// Id of the Synchronisation Rule attributed to this event, when recorded (#1085).
    /// </summary>
    public int? SyncRuleId { get; init; }

    /// <summary>
    /// Name snapshot of the attributed Synchronisation Rule, when recorded (#1085).
    /// </summary>
    public string? SyncRuleName { get; init; }

    /// <summary>
    /// Entity links (and unlinked mentions) for this event.
    /// </summary>
    public IReadOnlyList<CausalityEntityLink> Links { get; init; } = [];

    /// <summary>
    /// Normalised attribute change rows for events that expose attribute detail; empty otherwise.
    /// </summary>
    public IReadOnlyList<CausalityAttributeRow> AttributeRows { get; init; } = [];

    /// <summary>
    /// What this event's <see cref="AttributeRows"/> are, when they are not attribute changes; null for
    /// every event whose rows genuinely are changes, which then label themselves by count ("3 attributes").
    ///
    /// A queued deprovision is the one case: its rows are the target's secondary external ID (the DN, for
    /// LDAP), carried on the delete Pending Export so the connector can still resolve the entry after the
    /// Connected System Object is disconnected. Counted as changes, a deprovisioning cascade announced
    /// itself as "1 attribute", which read as an attribute update rather than an account being removed.
    /// </summary>
    public string? AttributeRowsCaption =>
        OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued
            ? "Target identified by"
            : null;

    /// <summary>
    /// Child events ordered by Ordinal.
    /// </summary>
    public IReadOnlyList<CausalityEvent> Children { get; init; } = [];
}
