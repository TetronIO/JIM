// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Causality;
using JIM.Web.Shared;

namespace JIM.Web.Models;

/// <summary>
/// Everything the Metaverse Object page holds about the one object it is showing: what its tabs have loaded, what
/// they are loading, and what the reader has opened on them.
/// </summary>
/// <remarks>
/// The page is one component instance for every object the reader moves between; following a reference from one
/// person to another changes the route's id and nothing else. The page therefore replaces this whole instance when
/// the object changes, rather than clearing fields one by one, so nothing can be carried over to the next person by
/// being forgotten. Each load writes into the instance it started with, so a load still running for the previous
/// object lands in an instance that is no longer shown.
/// <para>
/// What belongs to the reader rather than the object (the detail view mode, the Inspect grouping, the active tab)
/// stays on the page.
/// </para>
/// </remarks>
/// <param name="objectId">The Metaverse Object this state describes.</param>
public sealed class MetaverseObjectViewState(Guid objectId)
{
    /// <summary>The Metaverse Object this state describes; every load reads this, not the route.</summary>
    public Guid ObjectId { get; } = objectId;

    // Properties tab: the earliest and latest change initiators, absent when none is recorded.
    public string? CreatedByName { get; set; }
    public string? CreatedByHref { get; set; }
    public string? LastUpdatedByName { get; set; }
    public string? LastUpdatedByHref { get; set; }

    // Changes tab: the count is read with the page so the badge renders at once; the rows load on first visit and
    // page via Load more.
    public int ChangeCount { get; set; }
    public int ChangeHistoryTotal { get; set; }
    public int ChangeHistoryNextPage { get; set; } = 1;
    public bool ChangeHistoryLoading { get; set; }
    public List<ChangeHistoryTimeline.ChangeGroup> ChangeHistoryRows { get; } = [];

    /// <summary>The generated values retired from this object (#242): events on the Changes timeline.</summary>
    public List<RetiredGeneratedValueHeader> Retirements { get; set; } = [];

    /// <summary>
    /// This object's generated values held for a decision, or with their rename allowed (#242, release 4): the banner
    /// above the tabs. Read with the page, for administrators only.
    /// </summary>
    public List<GeneratedValueDecisionHeader> GeneratedValueDecisions { get; set; } = [];

    /// <summary>Whether a banner action (Allow the rename, Try again) is in flight.</summary>
    public bool GeneratedValueActionRunning { get; set; }

    // Inspect view (#399): the object's attribute provenance, the source filter chosen from the contribution bar,
    // and the attribute opened in the inspector.
    public MetaverseObjectProvenance? Provenance { get; set; }
    public bool ProvenanceLoading { get; set; }
    public bool ProvenanceLoaded { get; set; }
    public string? ContributionFilterKey { get; set; }
    public int? SelectedAttributeId { get; set; }
    public MetaverseAttributeProvenance? SelectedAttributeProvenance { get; set; }
    public bool AttributeInspectorLoading { get; set; }

    // Connections tab (#1519): the joined Connected System Objects, loaded on first visit, and the per-row and
    // outbound Sync Previews. Each connection carries why it exists, and the Not connected entries why the object is
    // not in each other Connected System an enabled export rule targets, all evaluated at one instant (#348).
    public List<MetaverseObjectConnectionExplanation> Connections { get; set; } = [];
    public List<NotConnectedEntry> NotConnected { get; set; } = [];
    public DateTime? ConnectionsEvaluatedAt { get; set; }
    public int ConnectorCount { get; set; }
    public bool ConnectionsLoading { get; set; }
    public bool ConnectionsLoaded { get; set; }
    public bool ConnectionPreviewLoading { get; set; }
    public SyncPreviewResult? ConnectionPreviewResult { get; set; }
    public CausalityPageContext? ConnectionPreviewContext { get; set; }
    public bool OutboundPreviewLoading { get; set; }
    public SyncPreviewResult? OutboundPreviewResult { get; set; }

    // What the reader has opened on the Connections tab (#348): the joined rows, by Connected System Object id; the
    // Not connected section; and its entries, by Synchronisation Rule id. The rules are the same for every object of
    // a type, so these must go with the object, or the next person would open with the last one's entries expanded.
    public HashSet<Guid> ExpandedConnectionIds { get; } = [];
    public bool NotConnectedOpen { get; set; }
    public HashSet<int> ExpandedNotConnectedRuleIds { get; } = [];

    // Password tab (#1635): the badge's queue rows are read with the page; the history and accounts load on first
    // visit.
    public IReadOnlyList<PasswordSynchronisationEvent> PasswordEvents { get; set; } = [];
    public IReadOnlyList<PendingPasswordChangeHeader> QueuedPasswordChanges { get; set; } = [];
    public IReadOnlyList<MetaverseObjectAccount> PasswordAccounts { get; set; } = [];
    public int PasswordAttentionCount { get; set; }
    public bool PasswordTabLoaded { get; set; }
    public bool PasswordTabLoading { get; set; }
    public bool PasswordActionRunning { get; set; }
}
