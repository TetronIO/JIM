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
    /// The underlying sync outcome type.
    /// </summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType { get; init; }

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
    /// The event rendered as a sentence for the Timeline view: plain label first, then entity
    /// mentions as segments.
    /// </summary>
    public IReadOnlyList<SummarySegment> SentenceSegments { get; init; } = [];

    /// <summary>
    /// Normalised attribute change rows for events that expose attribute detail; empty otherwise.
    /// </summary>
    public IReadOnlyList<CausalityAttributeRow> AttributeRows { get; init; } = [];

    /// <summary>
    /// Child events ordered by Ordinal.
    /// </summary>
    public IReadOnlyList<CausalityEvent> Children { get; init; } = [];
}
