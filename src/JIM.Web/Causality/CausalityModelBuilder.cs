// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Utilities;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using MudBlazor;
using JimUtilities = JIM.Utilities.Utilities;

namespace JIM.Web.Causality;

/// <summary>
/// Transforms a Run Profile Execution Item and its page context into the <see cref="CausalityModel"/>
/// consumed by the causality visualisation. Pure and side-effect free so the transformation is fully
/// unit-testable; tolerant of legacy data (null Synchronisation Rule attribution, missing detail
/// messages, empty outcome lists, Standard vs Detailed tracking levels).
/// </summary>
public static class CausalityModelBuilder
{
    /// <summary>
    /// Builds the causality model for an execution item. Never throws for missing or legacy data.
    /// </summary>
    /// <param name="item">The execution item whose sync outcomes are being visualised.</param>
    /// <param name="context">The page context supplying run and record identities.</param>
    /// <param name="livePendingExportIds">
    /// The Pending Exports referenced by this item that are still queued, or null when the caller did
    /// not resolve them. A Pending Export row is hard-deleted once it has been exported, while the
    /// causality record that names it is permanent, so a link to the individual row dies for every
    /// item older than the next export run. Passing the live set lets those links degrade to the
    /// target system's queue instead of promising a row that no longer exists. Null means "not
    /// resolved", NOT "none are live": a caller that cannot run the lookup keeps the precise links.
    /// </param>
    /// <param name="deletionPolicySnapshot">
    /// The decision-time Metaverse Object deletion policy recorded on the item, where one was captured. Supplies
    /// the synthetic "Metaverse Object not deleted" event; see <see cref="BuildDeclinedDeletionEvent"/>.
    /// </param>
    /// <param name="isSynchronisationRun">
    /// Whether the Run Profile that produced this item was a Full or Delta Synchronisation. Only a
    /// Synchronisation evaluates a Deletion Rule, so only a Synchronisation can report that one declined.
    /// </param>
    /// <param name="chain">
    /// The item's upward causal walk, or null when the caller did not resolve one. Supplies the one
    /// fact this run cannot know about itself: what an export execution's staged change actually was
    /// (create, update or delete), carried on the queueing edge's reason code, so an Exported outcome
    /// can state its decision rather than a bare "Exported" (#1495). Null keeps the bare label.
    /// </param>
    public static CausalityModel Build(
        ActivityRunProfileExecutionItem item,
        CausalityPageContext context,
        IReadOnlySet<Guid>? livePendingExportIds = null,
        MvoDeletionPolicySnapshot? deletionPolicySnapshot = null,
        bool isSynchronisationRun = false,
        CausalChain? chain = null)
    {
        var outcomes = item.SyncOutcomes;
        var outcomeIds = outcomes.Select(o => o.Id).ToHashSet();

        // Derive the tree from the flat list rather than the Children navigation so the builder
        // works identically for EF-materialised and hand-constructed graphs. An outcome whose
        // parent id does not resolve within the list is treated as a root rather than dropped.
        var childrenByParentId = outcomes
            .Where(o => o.ParentSyncOutcomeId.HasValue && outcomeIds.Contains(o.ParentSyncOutcomeId.Value))
            .GroupBy(o => o.ParentSyncOutcomeId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.Ordinal).ToList());

        var attachedChildIds = childrenByParentId.Values.SelectMany(children => children).Select(o => o.Id).ToHashSet();

        // The item-level changes are two distinct sets with two distinct owners: the record's own
        // attribute changes (ConnectedSystemObjectChange) belong to record-side events, and the
        // Identity's attribute changes (MetaverseObjectChange) belong to Attribute Flow. Keeping
        // them separate stops an event's expander count disagreeing with its outcome's DetailCount
        // when an item carries both sets (e.g. a leaver's record deletion plus attribute recall).
        var recordAttributeRows = NormaliseAttributeRows(item.ConnectedSystemObjectChange?.AttributeChanges, null);
        var identityAttributeRows = NormaliseAttributeRows(null, item.MetaverseObjectChange?.AttributeChanges);

        var roots = outcomes
            .Where(o => !attachedChildIds.Contains(o.Id))
            .OrderBy(o => o.Ordinal)
            .Select(o => BuildEvent(o, childrenByParentId, context, recordAttributeRows, identityAttributeRows,
                livePendingExportIds, chain))
            .ToList();

        if (BuildDeclinedDeletionEvent(item, deletionPolicySnapshot, isSynchronisationRun) is { } declined)
            roots.Add(declined);

        return new CausalityModel { Context = context, Roots = roots };
    }

    /// <summary>
    /// Builds the speculative causality model for a Sync Preview result (#1519, D-S1): the same event
    /// shape <see cref="Build"/> produces for a recorded item, keyed on <see cref="SyncOutcomeNode"/>
    /// instead of <see cref="ActivityRunProfileExecutionItemSyncOutcome"/>, with every label in the
    /// conditional mood (<see cref="ApplySpeculativeLabel"/>) and no execution timestamp: nothing here
    /// has happened.
    /// </summary>
    /// <remarks>
    /// Attribute rows for the speculative tree come from the preview's own change records, which the
    /// recorded tree has no equivalent of: <see cref="SyncPreviewInboundSummary.AttributeFlowChanges"/>
    /// for the inbound Attribute Flow node, and each outbound staging or deprovisioning node's own
    /// <see cref="OutboundPreviewEntry.AttributeChanges"/>, correlated back to its <see cref="SyncOutcomeNode"/>
    /// by <see cref="SyncOutcomeNode.SyncRuleId"/> (unique per rule within one preview, since a preview
    /// always evaluates a single object). The destructive cascade's downstream deprovisioning nodes
    /// (<c>SyncPreviewServer.BuildOutOfScopeCascadeAsync</c>) are the one shape with no Synchronisation
    /// Rule of their own (<c>ISyncEngine.DecideMvoDeletionExport</c> decides per remaining connector, not
    /// per rule); those are matched instead by target Connected System id against
    /// <see cref="SyncPreviewResult.Outbound"/>'s proposed exports, consumed as they are used so two
    /// deletes to the same system are not both attributed to the first node.
    /// </remarks>
    public static CausalityModel BuildSpeculative(SyncPreviewResult preview, CausalityPageContext context)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(context);

        // Keyed by SyncRuleId rather than by node identity: a SyncOutcomeNode carries no id of its own
        // (see its class remarks: children only, no parent pointer, no keys), so the rule id is the only
        // stable join between an outbound node and the decision record that produced it. Grouped
        // defensively rather than a bare ToDictionary: a preview is a read, and a duplicate rule id
        // (which should not occur; one rule evaluates an MVO at most once) must degrade to "use the
        // first" rather than throw and blank the whole panel.
        var entriesBySyncRuleId = preview.OutboundDecisions.Entries
            .GroupBy(e => e.SyncRuleId)
            .ToDictionary(g => g.Key, g => g.First());

        // The cascade's own deletes (added directly to ProposedExports by BuildOutOfScopeCascadeAsync,
        // with no OutboundPreviewEntry and therefore no SyncRuleId); an ordinary out-of-scope
        // deprovisioning's delete is also PendingExportChangeType.Delete here, but its node always
        // carries a SyncRuleId and is matched via entriesBySyncRuleId above first, so it is never drawn
        // from this pool.
        var unmatchedCascadeDeletes = preview.Outbound.ProposedExports
            .Where(pe => pe.ChangeType == PendingExportChangeType.Delete)
            .ToList();

        var roots = preview.OutcomeTree
            .OrderBy(n => n.Ordinal)
            .Select(n => BuildSpeculativeEvent(n, context, entriesBySyncRuleId, unmatchedCascadeDeletes, preview.Inbound))
            .ToList();

        return new CausalityModel { Context = context, Roots = roots, IsSpeculative = true };
    }

    private static CausalityEvent BuildSpeculativeEvent(
        SyncOutcomeNode node,
        CausalityPageContext context,
        IReadOnlyDictionary<int, OutboundPreviewEntry> entriesBySyncRuleId,
        List<PendingExport> unmatchedCascadeDeletes,
        SyncPreviewInboundSummary? inbound,
        int? parentSyncRuleId = null)
    {
        // A queued-export child carries no rule of its own (the engine attributes the rule to the
        // Provisioned parent), so the parent's rule is what keys its attribute changes.
        var effectiveSyncRuleId = node.SyncRuleId ?? parentSyncRuleId;
        var display = ApplySpeculativeLabel(OutcomeDisplayMap.Get(node.OutcomeType), node.OutcomeType, isSpeculative: true);
        var lane = GetLane(node.OutcomeType);
        var (systemId, systemName) = GetSpeculativeOwningSystem(node, lane, context);

        return new CausalityEvent
        {
            OutcomeType = node.OutcomeType,
            Label = display.Label,
            Tone = display.Tone,
            Icon = display.Icon,
            Lane = lane,
            SystemId = systemId,
            SystemName = systemName,
            Badge = GetBadge(node.OutcomeType),
            DetailCount = node.DetailCount,
            // DetailMessage on an outbound node carries the raw target Connected System id (decoded
            // above into SystemId), not display text; every other node's DetailMessage is already plain
            // contextual text, exactly as the recorded tree treats it.
            DetailMessage = SyncOutcomeTypes.IsPendingExport(node.OutcomeType) ? null : node.DetailMessage,
            SyncRuleId = node.SyncRuleId,
            SyncRuleName = node.SyncRuleName,
            Links = BuildSpeculativeLinks(node, lane, systemId, systemName, context),
            AttributeRows = GetSpeculativeAttributeRows(node, effectiveSyncRuleId, inbound, entriesBySyncRuleId, unmatchedCascadeDeletes),
            Operation = OutcomeDisplayMap.GetEventOperation(node.OutcomeType, exportReasonCode: null, node.StagedChangeType),
            Children = node.Children
                .OrderBy(c => c.Ordinal)
                .Select(c => BuildSpeculativeEvent(c, context, entriesBySyncRuleId, unmatchedCascadeDeletes, inbound, effectiveSyncRuleId))
                .ToList()
        };
    }

    /// <summary>
    /// The target Connected System for a Downstream-lane speculative node. Outbound nodes carry their
    /// target's id as a bare int in <see cref="SyncOutcomeNode.DetailMessage"/> (see
    /// <c>SyncPreviewServer.BuildOutboundOutcomeNodes</c>/<c>BuildOutOfScopeCascadeAsync</c>); unlike the
    /// recorded tree's "csId|csoTypeName" channel there is no type name to also carry, since a preview
    /// node describes no persisted record on that system. A Provisioned node's own DetailMessage is
    /// unset (only its nested PendingExportCreated child's carries the id), so it falls back to its
    /// TargetEntityDescription with no id: an honest unlinked mention rather than a wrong link.
    /// </summary>
    private static (int? SystemId, string? SystemName) GetSpeculativeOwningSystem(
        SyncOutcomeNode node, CausalityLane lane, CausalityPageContext context)
    {
        if (lane != CausalityLane.Downstream)
            return (null, null);

        return int.TryParse(node.DetailMessage, out var systemId)
            ? (systemId, node.TargetEntityDescription)
            : (null, node.TargetEntityDescription ?? context.ConnectedSystemName);
    }

    /// <summary>
    /// Entity links for a speculative event: an Identity or Connected System mention plus the
    /// Synchronisation Rule attribution shared with the recorded tree (<see cref="AppendSyncRuleLink"/>).
    /// Deliberately narrower than <see cref="BuildLinks"/>: a preview never destroys anything, so an
    /// Identity mention always links the live object rather than a deletion record (the one behavioural
    /// difference from the recorded tree's MvoDeleted link, which points at a deletion record because the
    /// object genuinely no longer exists there); and no per-target-object "record" link is built for a
    /// Create, since nothing exists yet to link, or for an Update/Delete, to avoid a second per-outcome-
    /// type link shape for a page not yet written.
    /// </summary>
    private static List<CausalityEntityLink> BuildSpeculativeLinks(
        SyncOutcomeNode node, CausalityLane lane, int? systemId, string? systemName, CausalityPageContext context)
    {
        var links = new List<CausalityEntityLink>();

        if (lane == CausalityLane.Identity)
        {
            if (node.TargetEntityId is { } mvoId && mvoId != Guid.Empty)
            {
                links.Add(new CausalityEntityLink(
                    node.TargetEntityDescription ?? "Identity", GetMetaverseObjectHref(mvoId, context), CausalityEntityKind.Identity));
            }
            else if (!string.IsNullOrEmpty(node.TargetEntityDescription))
            {
                links.Add(new CausalityEntityLink(node.TargetEntityDescription, null, CausalityEntityKind.Identity));
            }
        }
        else if (lane == CausalityLane.Downstream)
        {
            if (systemId.HasValue)
            {
                links.Add(new CausalityEntityLink(
                    systemName ?? "Connected System", JimUtilities.GetConnectedSystemHref(systemId.Value), CausalityEntityKind.ConnectedSystem));
            }
            else if (!string.IsNullOrEmpty(node.TargetEntityDescription))
            {
                links.Add(new CausalityEntityLink(node.TargetEntityDescription, null, CausalityEntityKind.ConnectedSystem));
            }
        }

        AppendSyncRuleLink(links, node.SyncRuleId, node.SyncRuleName);

        return links;
    }

    /// <summary>
    /// Attribute rows for a speculative event: the inbound Attribute Flow node reads
    /// <see cref="SyncPreviewInboundSummary.AttributeFlowChanges"/> directly; an outbound staging or
    /// deprovisioning node's rows come from its correlated <see cref="OutboundPreviewEntry.AttributeChanges"/>
    /// (or the cascade's own proposed export, for the rule-less downstream deprovisioning shape),
    /// converted through <see cref="ExportChangeHistoryBuilder.BuildFromPendingExport"/> and
    /// <see cref="NormaliseAttributeRows"/> so the same value-rendering and single-valued Set-collapsing
    /// logic the recorded tree uses applies here too, rather than a second copy of it.
    /// </summary>
    private static IReadOnlyList<CausalityAttributeRow> GetSpeculativeAttributeRows(
        SyncOutcomeNode node,
        int? effectiveSyncRuleId,
        SyncPreviewInboundSummary? inbound,
        IReadOnlyDictionary<int, OutboundPreviewEntry> entriesBySyncRuleId,
        List<PendingExport> unmatchedCascadeDeletes)
    {
        if (node.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow)
            return BuildInboundFlowAttributeRows(inbound?.AttributeFlowChanges);

        if (!SyncOutcomeTypes.IsPendingExport(node.OutcomeType))
            return [];

        List<PendingExportAttributeValueChange>? changes = null;
        if (effectiveSyncRuleId is { } ruleId && entriesBySyncRuleId.TryGetValue(ruleId, out var entry))
        {
            changes = entry.AttributeChanges;
        }
        else if (int.TryParse(node.DetailMessage, out var connectedSystemId))
        {
            var index = unmatchedCascadeDeletes.FindIndex(pe =>
                pe.ConnectedSystemId == connectedSystemId && pe.ChangeType == (node.StagedChangeType ?? PendingExportChangeType.Delete));
            if (index >= 0)
            {
                changes = unmatchedCascadeDeletes[index].AttributeValueChanges;
                unmatchedCascadeDeletes.RemoveAt(index);
            }
        }

        if (changes == null || changes.Count == 0)
            return [];

        var syntheticChange = ExportChangeHistoryBuilder.BuildFromPendingExport(
            new PendingExport { AttributeValueChanges = changes },
            ActivityInitiatorType.System,
            initiatedById: null,
            initiatedByName: null);
        return NormaliseAttributeRows(syntheticChange.AttributeChanges, null);
    }

    /// <summary>
    /// Maps the preview's inbound Attribute Flow deltas into display rows. Unlike
    /// <see cref="NormaliseAttributeRows"/>, a <see cref="SyncPreviewAttributeFlowChange"/> carries no
    /// attribute type or plurality (it is a display-ready delta, not a typed change record), so
    /// <see cref="CausalityAttributeRow.TypeAndPlurality"/> is empty rather than guessed; a table
    /// rendering it shows nothing in that column instead of a fabricated type.
    /// </summary>
    private static IReadOnlyList<CausalityAttributeRow> BuildInboundFlowAttributeRows(
        List<SyncPreviewAttributeFlowChange>? changes)
    {
        if (changes == null || changes.Count == 0)
            return [];

        return changes
            .OrderBy(c => c.AttributeName)
            .Select(c => new CausalityAttributeRow(
                c.IsAddition ? CausalityAttributeOperation.Set : CausalityAttributeOperation.Remove,
                c.AttributeName,
                string.Empty,
                c.Value,
                null,
                c.SyncRuleId,
                c.SyncRuleName))
            .ToList();
    }

    /// <summary>
    /// The synthetic Identity-lane event saying the Deletion Rule evaluated and declined, or null where that is
    /// not what happened.
    /// </summary>
    /// <remarks>
    /// A Deletion Rule that declines records no outcome, because nothing happened. That leaves the most
    /// consequential fact about a disconnection ("the Identity survived, and here is why") as the one thing the
    /// causality views structurally cannot show, which is why it lived in a separate "Metaverse Impact" section
    /// until this replaced it.
    ///
    /// Every condition below is a claim the event would otherwise make falsely:
    /// only a Synchronisation evaluates the rule at all, so an import has no decision to report; a
    /// disconnection is the only change that triggers an evaluation; an item with no outcomes did no work to
    /// explain; a recorded deletion means the rule fired, and a synthetic card would contradict it; and the
    /// snapshot is the only supported source of the explanation, since the object type's current configuration
    /// may have changed since the decision.
    /// </remarks>
    private static CausalityEvent? BuildDeclinedDeletionEvent(
        ActivityRunProfileExecutionItem item,
        MvoDeletionPolicySnapshot? snapshot,
        bool isSynchronisationRun)
    {
        if (!isSynchronisationRun || snapshot == null)
            return null;

        if (item.ObjectChangeType != ObjectChangeType.Disconnected || item.SyncOutcomes.Count == 0)
            return null;

        var deletionRecorded = item.SyncOutcomes.Any(o =>
            o.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
                or ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled);
        if (deletionRecorded)
            return null;

        return new CausalityEvent
        {
            IsSynthetic = true,
            Lane = CausalityLane.Identity,
            // Neutral, not success: whether an Identity surviving a disconnection is the wanted outcome depends
            // entirely on the deployment's intent, and the panel does not know it.
            Tone = CausalityTone.Secondary,
            Icon = Icons.Material.Filled.ShieldMoon,
            Label = "Metaverse Object not deleted",
            DetailMessage = DeclinedDeletionDetail(snapshot)
        };
    }

    /// <summary>
    /// The one-line reason the rule declined, derived from the decision-time snapshot.
    /// </summary>
    private static string DeclinedDeletionDetail(MvoDeletionPolicySnapshot snapshot)
    {
        if (snapshot.DeletionRule == MetaverseObjectDeletionRule.Manual)
            return "This object type's Deletion Rule is Manual, so a disconnection never deletes the Metaverse Object.";

        if (snapshot.DeletionRule == MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected
            && snapshot.TriggerMode == AuthoritativeSourceTriggerMode.AllSourcesDisconnect
            && snapshot.RemainingConnectedSourceSystemNames.Count > 0)
        {
            var remaining = string.Join(", ", snapshot.RemainingConnectedSourceSystemNames);
            return $"An authoritative source is still connected ({remaining}), and this object type deletes only when all of them disconnect.";
        }

        return "The Deletion Rule in force at the time was evaluated and did not delete the Metaverse Object.";
    }

    private static CausalityEvent BuildEvent(
        ActivityRunProfileExecutionItemSyncOutcome outcome,
        Dictionary<Guid, List<ActivityRunProfileExecutionItemSyncOutcome>> childrenByParentId,
        CausalityPageContext context,
        IReadOnlyList<CausalityAttributeRow> recordAttributeRows,
        IReadOnlyList<CausalityAttributeRow> identityAttributeRows,
        IReadOnlySet<Guid>? livePendingExportIds,
        CausalChain? chain)
    {
        // Resolved once and shared by the outcome's own title (decision-aware for Exported, #1495) and
        // its operation chip (#1495 follow-up), rather than each re-walking the chain independently.
        var exportReasonCode = outcome.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported
            ? FindQueueingReason(chain, outcome.Id)
            : null;
        var display = GetEventDisplay(outcome, exportReasonCode, isSpeculative: false);
        var parsedDetail = OutcomeDetailMessageParser.Parse(outcome.DetailMessage);
        var usesIdChannel = UsesDetailMessageIdChannel(outcome.OutcomeType);
        var lane = GetLane(outcome.OutcomeType);

        var childOutcomes = childrenByParentId.TryGetValue(outcome.Id, out var children)
            ? children
            : [];

        var links = BuildLinks(outcome, childOutcomes, parsedDetail, context, livePendingExportIds);
        var (systemId, systemName) = GetOwningSystem(outcome, lane, usesIdChannel, parsedDetail, context);

        return new CausalityEvent
        {
            OutcomeType = outcome.OutcomeType,
            Label = display.Label,
            Tone = display.Tone,
            Icon = display.Icon,
            Lane = lane,
            SystemId = systemId,
            SystemName = systemName,
            Badge = GetBadge(outcome.OutcomeType),
            DetailCount = outcome.DetailCount,
            DetailMessage = usesIdChannel ? parsedDetail.PlainMessage : outcome.DetailMessage,
            SyncRuleId = outcome.SyncRuleId,
            SyncRuleName = outcome.SyncRuleName,
            Links = links,
            AttributeRows = GetAttributeRows(outcome, recordAttributeRows, identityAttributeRows),
            Operation = OutcomeDisplayMap.GetEventOperation(outcome.OutcomeType, exportReasonCode, outcome.StagedChangeType),
            Children = childOutcomes
                .Select(c => BuildEvent(c, childrenByParentId, context, recordAttributeRows, identityAttributeRows,
                    livePendingExportIds, chain))
                .ToList()
        };
    }

    /// <summary>
    /// The display mapping for an outcome, decision-aware for export executions (#1495): an Exported
    /// outcome states what the export did (record created, changes applied, record deleted) when the
    /// chain's queueing edge recorded the staged change's kind, and stays the bare "Exported" when it
    /// did not (pre-edge history, or no chain resolved).
    /// </summary>
    private static OutcomeDisplay GetEventDisplay(
        ActivityRunProfileExecutionItemSyncOutcome outcome,
        CausalReasonCode? exportReasonCode,
        bool isSpeculative)
    {
        var display = outcome.OutcomeType != ActivityRunProfileExecutionItemSyncOutcomeType.Exported
            ? OutcomeDisplayMap.Get(outcome.OutcomeType)
            : (exportReasonCode is { } reasonCode
                ? OutcomeDisplayMap.GetExportDecision(reasonCode)
                : OutcomeDisplayMap.Get(outcome.OutcomeType));

        return ApplySpeculativeLabel(display, outcome.OutcomeType, isSpeculative);
    }

    /// <summary>
    /// Substitutes the conditional-mood label (D-S1/D-S9) for a speculative event's label, leaving the
    /// tone and icon unchanged. A no-op for the recorded path (<paramref name="isSpeculative"/> false)
    /// and for any outcome type the preview engine cannot emit
    /// (<see cref="OutcomeDisplayMap.GetSpeculativeLabel"/> returns null), so an unanticipated type still
    /// renders its ordinary past-tense label rather than nothing.
    /// </summary>
    private static OutcomeDisplay ApplySpeculativeLabel(
        OutcomeDisplay display,
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType,
        bool isSpeculative)
    {
        if (!isSpeculative)
            return display;

        var speculativeLabel = OutcomeDisplayMap.GetSpeculativeLabel(outcomeType);
        return speculativeLabel != null ? display with { Label = speculativeLabel } : display;
    }

    /// <summary>
    /// The queueing edge's staged-change reason for an export outcome, or null where the chain does
    /// not carry one. An exact <see cref="CausalChainCohort.EffectSyncOutcomeId"/> match wins; a
    /// cohort attached to the item as a whole covers the rest, which is the ordinary single-export
    /// item.
    /// </summary>
    private static CausalReasonCode? FindQueueingReason(CausalChain? chain, Guid outcomeId)
    {
        if (chain == null)
            return null;

        var queueingCohorts = chain.Cohorts
            .Where(c => c.SourceImportChangeType == null
                && c.EdgeType == CausalEdgeType.PendingExportQueueingCausedExportExecution
                && c.ReasonCode is CausalReasonCode.ExportCreateStaged
                    or CausalReasonCode.ExportUpdateStaged
                    or CausalReasonCode.ExportDeleteStaged)
            .ToList();

        var match = queueingCohorts.FirstOrDefault(c => c.EffectSyncOutcomeId == outcomeId)
            ?? queueingCohorts.FirstOrDefault(c => c.EffectSyncOutcomeId == null);
        return match?.ReasonCode;
    }

    /// <summary>
    /// Whether this outcome type stores the "csId|csoTypeName" link channel in DetailMessage
    /// rather than plain contextual text.
    /// </summary>
    private static bool UsesDetailMessageIdChannel(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return outcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned
            || SyncOutcomeTypes.IsPendingExport(outcomeType);
    }

    private static CausalityLane GetLane(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return outcomeType switch
        {
            // Import-side record events: what happened
            ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded
                or ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated
                or ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted
                or ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected
                // Preview-only (#1475): whether an object or an attribute is imported at all is a statement about
                // what comes in, which is this lane, even though the harm it describes lands on the Metaverse.
                or ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported
                or ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported
                => CausalityLane.Source,

            // Provisioning and export-side events: what it caused. WouldStageDeleteExport is preview-only but
            // describes the same export-side event as DeprovisionQueued, so it lives in the same lane.
            ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned
                or ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated
                or ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued
                or ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport
                or ActivityRunProfileExecutionItemSyncOutcomeType.Exported
                or ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed
                or ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed
                or ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned
                // Preview-only (#1462): a Connected System Object that would not be created, and an object that would be left to
                // diverge, are both statements about the target system rather than about the Metaverse.
                or ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning
                or ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift
                // Preview-only: an export rule's scope decides what reaches the target system, so its scope pair
                // sits here rather than beside the import-side pair in the Identity lane.
                or ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope
                or ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope
                => CausalityLane.Downstream,

            // Metaverse-side events: what JIM did
            _ => CausalityLane.Identity
        };
    }

    private static string? GetBadge(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return outcomeType switch
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted => "Destructive",
            ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed => "Needs attention",
            _ => null
        };
    }

    private static (int? SystemId, string? SystemName) GetOwningSystem(
        ActivityRunProfileExecutionItemSyncOutcome outcome,
        CausalityLane lane,
        bool usesIdChannel,
        OutcomeDetailMessage parsedDetail,
        CausalityPageContext context)
    {
        if (lane == CausalityLane.Identity)
            return (null, null);

        if (usesIdChannel)
        {
            // Provisioned/PendingExportCreated carry their target system id in DetailMessage and its
            // name in TargetEntityDescription; that target is a third system in the general case
            // (neither the run's system nor the record's own), so TargetEntityDescription is always
            // preferred. The name-only fallback below is for legacy rows that predate that field
            // being captured: neither context identity is more correct than the other for an unknown
            // third system, and no href is built from this name (the id already came from
            // parsedDetail.ConnectedSystemId), so this is left as the run's name deliberately rather
            // than guessed at.
            return (parsedDetail.ConnectedSystemId, outcome.TargetEntityDescription ?? context.ConnectedSystemName);
        }

        // Source events and export execution events belong to the record's own Connected System,
        // not necessarily the system the run executed against (they diverge for cross-system
        // cascades, e.g. a Full Sync on system A provisioning or exporting to a CSO on system B)
        return (context.CsoConnectedSystemId, context.CsoConnectedSystemName);
    }

    private static List<CausalityEntityLink> BuildLinks(
        ActivityRunProfileExecutionItemSyncOutcome outcome,
        IReadOnlyList<ActivityRunProfileExecutionItemSyncOutcome> childOutcomes,
        OutcomeDetailMessage parsedDetail,
        CausalityPageContext context,
        IReadOnlySet<Guid>? livePendingExportIds)
    {
        var links = new List<CausalityEntityLink>();

        switch (outcome.OutcomeType)
        {
            case ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned:
                // TargetEntityId is the new CSO's id and DetailMessage carries "csId|csoTypeName";
                // same semantics as the legacy outcome tree's provisioned CSO link
                if (parsedDetail.ConnectedSystemId.HasValue)
                {
                    var provisioningSystemId = parsedDetail.ConnectedSystemId.Value;
                    links.Add(new CausalityEntityLink(
                        outcome.TargetEntityDescription ?? "Connected System",
                        JimUtilities.GetConnectedSystemHref(provisioningSystemId),
                        CausalityEntityKind.ConnectedSystem));

                    if (outcome.TargetEntityId is { } provisionedCsoId && provisionedCsoId != Guid.Empty)
                    {
                        var recordLabel = parsedDetail.CsoTypeName != null
                            ? $"{parsedDetail.CsoTypeName}: {provisionedCsoId}"
                            : provisionedCsoId.ToString();
                        links.Add(new CausalityEntityLink(
                            recordLabel,
                            JimUtilities.GetConnectedSystemObjectHref(provisioningSystemId, provisionedCsoId),
                            CausalityEntityKind.Record));
                    }
                }
                else if (!string.IsNullOrEmpty(outcome.TargetEntityDescription))
                {
                    links.Add(new CausalityEntityLink(outcome.TargetEntityDescription, null, CausalityEntityKind.ConnectedSystem));
                }
                break;

            case ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated:
            case ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued:
                // TargetEntityId is the Pending Export's own id, so link straight to it rather than to the
                // target system's whole queue: on a deprovisioning cascade that queue can hold thousands of
                // rows, and "which of these did this event create?" is the one question the link should not
                // leave the reader to answer. Falls back to the queue when the id was not captured.
                if (parsedDetail.ConnectedSystemId.HasValue)
                {
                    var targetSystemId = parsedDetail.ConnectedSystemId.Value;
                    links.Add(new CausalityEntityLink(
                        outcome.TargetEntityDescription ?? "Connected System",
                        JimUtilities.GetConnectedSystemHref(targetSystemId),
                        CausalityEntityKind.ConnectedSystem));

                    var queueHref = $"/admin/connected-systems/{targetSystemId}/pending-exports";
                    // Link the individual row only while it still exists. A Pending Export is
                    // hard-deleted once exported, so on an item older than the next export run the row
                    // is gone and a link to it 404s; the queue always exists. A null live set means the
                    // caller did not resolve it, not that nothing is live, so the precise link stands.
                    var isLinkable = outcome.TargetEntityId is { } id && id != Guid.Empty
                                     && livePendingExportIds?.Contains(id) != false;
                    links.Add(new CausalityEntityLink(
                        isLinkable ? "Pending Export" : "Pending Exports",
                        isLinkable ? $"{queueHref}/{outcome.TargetEntityId}" : queueHref,
                        CausalityEntityKind.PendingExport));
                }
                else if (!string.IsNullOrEmpty(outcome.TargetEntityDescription))
                {
                    links.Add(new CausalityEntityLink(outcome.TargetEntityDescription, null, CausalityEntityKind.ConnectedSystem));
                }
                break;

            case ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted:
                // The record no longer exists, so name it and link its durable deletion record rather than
                // a detail page that would 404. Mirrors MvoDeleted directly below.
                if (!string.IsNullOrEmpty(outcome.TargetEntityDescription))
                    links.Add(new CausalityEntityLink(outcome.TargetEntityDescription, null, CausalityEntityKind.Record));
                links.Add(new CausalityEntityLink(
                    "View deletion record",
                    GetDeletedCsoHref(outcome.TargetEntityId),
                    CausalityEntityKind.DeletionRecord));
                break;

            case ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted:
                // The Metaverse Object no longer exists: name it, but link the durable deletion
                // record browser instead of the (deleted) Identity's detail page
                if (!string.IsNullOrEmpty(outcome.TargetEntityDescription))
                    links.Add(new CausalityEntityLink(outcome.TargetEntityDescription, null, CausalityEntityKind.Identity));
                links.Add(new CausalityEntityLink(
                    "View deletion record",
                    GetDeletedMvoHref(outcome.TargetEntityId),
                    CausalityEntityKind.DeletionRecord));
                break;

            case ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed:
                // The failed changes remain queued on the record's own Connected System, not
                // necessarily the system the run executed against
                if (context.CsoConnectedSystemId.HasValue)
                {
                    links.Add(new CausalityEntityLink(
                        "Pending Exports",
                        $"/admin/connected-systems/{context.CsoConnectedSystemId.Value}/pending-exports",
                        CausalityEntityKind.PendingExport));
                }
                break;

            default:
                // Parity with the legacy outcome tree: no Identity link when the Identity no longer exists,
                // which is the case for parents with an MvoDeleted child in their causality tree
                if (outcome.TargetEntityId is { } mvoId && mvoId != Guid.Empty
                    && childOutcomes.All(c => c.OutcomeType != ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted))
                {
                    links.Add(new CausalityEntityLink(
                        outcome.TargetEntityDescription ?? "Metaverse Object",
                        GetMetaverseObjectHref(mvoId, context),
                        CausalityEntityKind.Identity));
                }
                else if (!string.IsNullOrEmpty(outcome.TargetEntityDescription))
                {
                    links.Add(new CausalityEntityLink(outcome.TargetEntityDescription, null, CausalityEntityKind.Identity));
                }
                break;
        }

        // Synchronisation Rule attribution (#1085) applies across outcome types. Fall back to an
        // unlinked name snapshot for pre-#1085 rows that recorded the name without the id.
        AppendSyncRuleLink(links, outcome.SyncRuleId, outcome.SyncRuleName);

        return links;
    }

    /// <summary>
    /// Appends the Synchronisation Rule mention shared by the recorded and speculative link builders
    /// (#1519 D-S1): linked when the id is known, an unlinked name snapshot otherwise, nothing when
    /// neither is present.
    /// </summary>
    private static void AppendSyncRuleLink(List<CausalityEntityLink> links, int? syncRuleId, string? syncRuleName)
    {
        if (syncRuleId.HasValue)
        {
            links.Add(new CausalityEntityLink(
                syncRuleName ?? "Synchronisation Rule",
                $"/admin/sync-rules/{syncRuleId.Value}",
                CausalityEntityKind.SynchronisationRule));
        }
        else if (!string.IsNullOrEmpty(syncRuleName))
        {
            links.Add(new CausalityEntityLink(syncRuleName, null, CausalityEntityKind.SynchronisationRule));
        }
    }

    /// <summary>
    /// The deletion record browser, deep-linked to the deleted Metaverse Object where its id was captured.
    /// The tab slug travels with the link because the browser opens on Deleted CSOs by default; without it
    /// the dialog would open over the wrong tab. Falls back to the unfiltered browser for outcomes written
    /// before the id was recorded, which is still where the record lives.
    /// </summary>
    internal static string GetDeletedMvoHref(Guid? deletedMvoId)
    {
        return deletedMvoId is { } id && id != Guid.Empty
            ? $"/admin/deleted-objects?t=deleted-mvos&mvo={id}"
            : "/admin/deleted-objects";
    }

    /// <summary>
    /// The Connected System Object counterpart of <see cref="GetDeletedMvoHref"/>. No tab slug: Deleted CSOs
    /// is the browser's first tab, and NavigableMudTabs keeps the first tab's URL clean by omitting the
    /// parameter, so naming it here would produce a link that does not match the one the page settles on.
    /// </summary>
    internal static string GetDeletedCsoHref(Guid? deletedCsoId)
    {
        return deletedCsoId is { } id && id != Guid.Empty
            ? $"/admin/deleted-objects?cso={id}"
            : "/admin/deleted-objects";
    }

    /// <summary>
    /// The Metaverse Object's own page, or null where the object's type plural name is unknown and the
    /// route therefore cannot be built.
    /// </summary>
    /// <remarks>
    /// Null, never a guess. The route is keyed on the plural name (<c>/t/{plural}/v/{id}</c>), and the
    /// fallback here used to invent <c>/identity/search/{id}</c>, which is not a route in this
    /// application: on any item whose type the page could not resolve (a synchronisation whose record
    /// has since been deleted is the common one, since the resolution chain starts at the record's
    /// object type) every Identity on the panel pointed at a page that does not exist. The caller
    /// renders an unlinked name for a null, which the deleted-Identity branch beside it already does.
    /// </remarks>
    private static string? GetMetaverseObjectHref(Guid mvoId, CausalityPageContext context)
    {
        return !string.IsNullOrEmpty(context.MvoTypePluralName)
            ? JimUtilities.GetMetaverseObjectHref(mvoId, context.MvoTypePluralName)
            : null;
    }

    /// <summary>
    /// Selects the attribute rows for an event by the change set it owns: the Pending Export staging
    /// outcomes (PendingExportCreated and DeprovisionQueued) use
    /// its persisted CSO change snapshot, record-side events (import changes and export executions)
    /// use the item's CSO change rows, and Attribute Flow uses the item's MVO change rows. Events
    /// never share the combined item-level list, so each event's row count agrees with its own
    /// outcome's DetailCount.
    /// </summary>
    private static IReadOnlyList<CausalityAttributeRow> GetAttributeRows(
        ActivityRunProfileExecutionItemSyncOutcome outcome,
        IReadOnlyList<CausalityAttributeRow> recordAttributeRows,
        IReadOnlyList<CausalityAttributeRow> identityAttributeRows)
    {
        if (SyncOutcomeTypes.IsPendingExport(outcome.OutcomeType))
            return NormaliseAttributeRows(outcome.ConnectedSystemObjectChange?.AttributeChanges, null);

        return outcome.OutcomeType switch
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow
                => identityAttributeRows,
            ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded
                or ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated
                or ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted
                or ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected
                or ActivityRunProfileExecutionItemSyncOutcomeType.Exported
                or ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned
                => recordAttributeRows,
            _ => []
        };
    }

    /// <summary>
    /// Normalises CSO and MVO attribute changes into display rows, collapsing single-valued
    /// Add and Remove pairs into one Set row with the previous value, so a value replacement
    /// reads as a single change rather than a separate removal and addition.
    /// </summary>
    private static IReadOnlyList<CausalityAttributeRow> NormaliseAttributeRows(
        IEnumerable<ConnectedSystemObjectChangeAttribute>? csoAttributeChanges,
        IEnumerable<MetaverseObjectChangeAttribute>? mvoAttributeChanges)
    {
        var flatChanges = new List<FlatAttributeChange>();

        if (csoAttributeChanges != null)
        {
            flatChanges.AddRange(csoAttributeChanges.SelectMany(ac => ac.ValueChanges.Select(vc => new FlatAttributeChange(
                ac.AttributeName,
                ac.AttributeType,
                ac.Attribute?.AttributePlurality == AttributePlurality.MultiValued,
                vc.ValueChangeType,
                GetCsoValueText(vc),
                vc.SyncRuleId,
                vc.SyncRuleName))));
        }

        if (mvoAttributeChanges != null)
        {
            flatChanges.AddRange(mvoAttributeChanges.SelectMany(ac => ac.ValueChanges.Select(vc => new FlatAttributeChange(
                ac.AttributeName,
                ac.AttributeType,
                ac.Attribute?.AttributePlurality == AttributePlurality.MultiValued,
                vc.ValueChangeType,
                GetMvoValueText(vc),
                vc.ContributedBySyncRuleId,
                vc.ContributedBySyncRuleName))));
        }

        if (flatChanges.Count == 0)
            return [];

        var rows = new List<CausalityAttributeRow>();
        foreach (var group in flatChanges.GroupBy(c => c.AttributeName).OrderBy(g => g.Key))
        {
            var changes = group.ToList();
            var isMultiValued = changes[0].IsMultiValued;
            var typeAndPlurality = GetTypeAndPlurality(changes[0].AttributeType, isMultiValued);

            if (!isMultiValued)
            {
                var addChange = changes.FirstOrDefault(c => c.ChangeType == ValueChangeType.Add);
                var removeChange = changes.FirstOrDefault(c => c.ChangeType == ValueChangeType.Remove);

                if (addChange != null && removeChange != null)
                {
                    // Single-valued update: collapse the Add and Remove pair into one Set row with
                    // the previous value. Attribution follows the new (Add) value's contributor;
                    // the removed value's own rule is not shown once collapsed.
                    rows.Add(new CausalityAttributeRow(CausalityAttributeOperation.Set, group.Key, typeAndPlurality,
                        addChange.ValueText, removeChange.ValueText, addChange.SyncRuleId, addChange.SyncRuleName));
                }
                else
                {
                    rows.AddRange(changes.Select(change => new CausalityAttributeRow(
                        change.ChangeType == ValueChangeType.Add ? CausalityAttributeOperation.Set : CausalityAttributeOperation.Remove,
                        group.Key, typeAndPlurality, change.ValueText, null, change.SyncRuleId, change.SyncRuleName)));
                }
            }
            else
            {
                rows.AddRange(changes.Select(change => new CausalityAttributeRow(
                    change.ChangeType == ValueChangeType.Add ? CausalityAttributeOperation.Add : CausalityAttributeOperation.Remove,
                    group.Key, typeAndPlurality, change.ValueText, null, change.SyncRuleId, change.SyncRuleName)));
            }
        }

        return rows;
    }

    private static string? GetCsoValueText(ConnectedSystemObjectChangeAttributeValue valueChange)
    {
        if (valueChange.ReferenceValue == null)
            return valueChange.ToString();

        // Pending Export stubs carry the resolved identifier (e.g. the DN) in StringValue, which is
        // the value the operator recognises; the stub CSO has no post-export display attributes yet
        if (valueChange.IsPendingExportStub && !string.IsNullOrEmpty(valueChange.StringValue))
            return valueChange.StringValue;

        return valueChange.ReferenceValue.NameOrId ?? valueChange.ReferenceValue.Id.ToString();
    }

    private static string? GetMvoValueText(MetaverseObjectChangeAttributeValue valueChange)
    {
        if (valueChange.ReferenceValue == null)
            return valueChange.ToString();

        return valueChange.ReferenceValue.NameOrId ?? valueChange.ReferenceValue.Id.ToString();
    }

    private static string GetTypeAndPlurality(AttributeDataType attributeType, bool isMultiValued)
    {
        var typeName = attributeType switch
        {
            AttributeDataType.NotSet => "Unknown",
            AttributeDataType.Text => "Text",
            AttributeDataType.Number => "Number",
            AttributeDataType.DateTime => "Date and Time",
            AttributeDataType.Binary => "Binary",
            AttributeDataType.Reference => "Reference",
            AttributeDataType.Guid => "GUID",
            AttributeDataType.Boolean => "Boolean",
            AttributeDataType.LongNumber => "Long Number",
            _ => attributeType.ToString()
        };

        return $"{typeName} · {(isMultiValued ? "Multi-valued" : "Single-valued")}";
    }

    /// <summary>
    /// A flattened attribute value change, unified across CSO and MVO change records.
    /// </summary>
    private sealed record FlatAttributeChange(
        string AttributeName,
        AttributeDataType AttributeType,
        bool IsMultiValued,
        ValueChangeType ChangeType,
        string? ValueText,
        int? SyncRuleId = null,
        string? SyncRuleName = null);
}
