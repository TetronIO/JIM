// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Sync;
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
    /// the synthetic "Identity not deleted" event; see <see cref="BuildDeclinedDeletionEvent"/>.
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
            PlainLabel = "Identity not deleted",
            TechnicalLabel = "Metaverse Object not deleted",
            DetailMessage = DeclinedDeletionDetail(snapshot)
        };
    }

    /// <summary>
    /// The one-line reason the rule declined, derived from the decision-time snapshot.
    /// </summary>
    private static string DeclinedDeletionDetail(MvoDeletionPolicySnapshot snapshot)
    {
        if (snapshot.DeletionRule == MetaverseObjectDeletionRule.Manual)
            return "This object type's Deletion Rule is Manual, so a disconnection never deletes the Identity.";

        if (snapshot.DeletionRule == MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected
            && snapshot.TriggerMode == AuthoritativeSourceTriggerMode.AllSourcesDisconnect
            && snapshot.RemainingConnectedSourceSystemNames.Count > 0)
        {
            var remaining = string.Join(", ", snapshot.RemainingConnectedSourceSystemNames);
            return $"An authoritative source is still connected ({remaining}), and this object type deletes only when all of them disconnect.";
        }

        return "The Deletion Rule in force at the time was evaluated and did not delete the Identity.";
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
        var display = GetEventDisplay(outcome, chain);
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
            PlainLabel = display.PlainLabel,
            TechnicalLabel = display.TechnicalLabel,
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
        CausalChain? chain)
    {
        if (outcome.OutcomeType != ActivityRunProfileExecutionItemSyncOutcomeType.Exported)
            return OutcomeDisplayMap.Get(outcome.OutcomeType);

        return FindQueueingReason(chain, outcome.Id) is { } reasonCode
            ? OutcomeDisplayMap.GetExportDecision(reasonCode)
            : OutcomeDisplayMap.Get(outcome.OutcomeType);
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
                // Preview-only (#1462): an account that would not be created, and an object that would be left to
                // diverge, are both statements about the target system rather than about the Metaverse.
                or ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning
                or ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift
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
                        outcome.TargetEntityDescription ?? "Identity",
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
        if (outcome.SyncRuleId.HasValue)
        {
            links.Add(new CausalityEntityLink(
                outcome.SyncRuleName ?? "Synchronisation Rule",
                $"/admin/sync-rules/{outcome.SyncRuleId.Value}",
                CausalityEntityKind.SynchronisationRule));
        }
        else if (!string.IsNullOrEmpty(outcome.SyncRuleName))
        {
            links.Add(new CausalityEntityLink(outcome.SyncRuleName, null, CausalityEntityKind.SynchronisationRule));
        }

        return links;
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
                GetCsoValueText(vc)))));
        }

        if (mvoAttributeChanges != null)
        {
            flatChanges.AddRange(mvoAttributeChanges.SelectMany(ac => ac.ValueChanges.Select(vc => new FlatAttributeChange(
                ac.AttributeName,
                ac.AttributeType,
                ac.Attribute?.AttributePlurality == AttributePlurality.MultiValued,
                vc.ValueChangeType,
                GetMvoValueText(vc)))));
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
                    // the previous value
                    rows.Add(new CausalityAttributeRow(CausalityAttributeOperation.Set, group.Key, typeAndPlurality,
                        addChange.ValueText, removeChange.ValueText));
                }
                else
                {
                    rows.AddRange(changes.Select(change => new CausalityAttributeRow(
                        change.ChangeType == ValueChangeType.Add ? CausalityAttributeOperation.Set : CausalityAttributeOperation.Remove,
                        group.Key, typeAndPlurality, change.ValueText, null)));
                }
            }
            else
            {
                rows.AddRange(changes.Select(change => new CausalityAttributeRow(
                    change.ChangeType == ValueChangeType.Add ? CausalityAttributeOperation.Add : CausalityAttributeOperation.Remove,
                    group.Key, typeAndPlurality, change.ValueText, null)));
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
        string? ValueText);
}
