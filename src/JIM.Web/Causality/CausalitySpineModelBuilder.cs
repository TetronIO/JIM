// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JimUtilities = JIM.Utilities.Utilities;

namespace JIM.Web.Causality;

/// <summary>
/// Projects a Run Profile Execution Item's causality model and causal chain onto the object spine
/// (#1495): which object columns exist and in what order, which column each event and chain hop
/// renders on, where the chain's endings close, and how adjacent columns are joined. Pure and
/// side-effect free, so every projection rule is unit-testable without rendering anything.
/// </summary>
public static class CausalitySpineModelBuilder
{
    /// <summary>
    /// Builds the spine for an item.
    /// </summary>
    /// <param name="model">The item's causality model (this run's events and the page context).</param>
    /// <param name="chain">The upward causal walk, or null where the page did not resolve one.</param>
    /// <param name="itemChangeType">
    /// What the run did to the object, which decides which side of the Identity the page's own
    /// record column sits on: an export-side item's record is a target, everything else's a source.
    /// </param>
    public static CausalitySpineModel Build(
        CausalityModel model,
        CausalChain? chain,
        ObjectChangeType itemChangeType)
    {
        var context = model.Context;
        var allEvents = model.AllEvents().ToList();
        var pageRecordIsTarget = itemChangeType is ObjectChangeType.Exported
            or ObjectChangeType.Deprovisioned
            or ObjectChangeType.PendingExport
            or ObjectChangeType.PendingExportConfirmed;

        var recordColumns = new List<ColumnState>();
        ColumnState? identityColumn = null;
        ColumnState? unassignedColumn = null;
        var creationOrder = 0;
        var hopSequence = 0;

        ColumnState GetIdentityColumn() =>
            identityColumn ??= new ColumnState { Kind = CausalitySpineColumnKind.Identity, CreationOrder = creationOrder++ };

        ColumnState GetRecordColumn(int? systemId, string? systemName, bool isSourceSide)
        {
            var existing = recordColumns.FirstOrDefault(c => c.SystemId == systemId);
            if (existing != null)
            {
                existing.SystemName ??= systemName;
                return existing;
            }

            var created = new ColumnState
            {
                Kind = CausalitySpineColumnKind.Record,
                SystemId = systemId,
                SystemName = systemName,
                IsSourceSide = isSourceSide,
                CreationOrder = creationOrder++
            };
            recordColumns.Add(created);
            return created;
        }

        // The page's own record column exists whenever the context names a record: the record is the
        // item's subject, so it anchors the graph even when no loaded event happens to land on it (a
        // synchronisation's events all land on the Identity and the staging targets).
        ColumnState? pageRecordColumn = null;
        if (context.CsoConnectedSystemId.HasValue || context.RecordName != null)
        {
            pageRecordColumn = GetRecordColumn(context.CsoConnectedSystemId, context.CsoConnectedSystemName,
                isSourceSide: !pageRecordIsTarget);
            pageRecordColumn.IsPageRecord = true;
        }

        // ─── This run's events, each on the column of the object it happened to ───
        foreach (var causalityEvent in allEvents)
        {
            if (causalityEvent.Lane == CausalityLane.Identity)
            {
                GetIdentityColumn().ThisRunEvents.Add(causalityEvent);
                continue;
            }

            var systemId = causalityEvent.SystemId ?? context.CsoConnectedSystemId;
            var matchesPageRecord = pageRecordColumn != null && systemId == pageRecordColumn.SystemId;
            var isSourceSide = matchesPageRecord
                ? !pageRecordIsTarget
                : causalityEvent.Lane != CausalityLane.Downstream;
            var column = GetRecordColumn(systemId,
                causalityEvent.SystemName ?? (matchesPageRecord ? context.CsoConnectedSystemName : null),
                isSourceSide);
            column.ThisRunEvents.Add(causalityEvent);
        }

        // ─── The chain's hops, flattened onto the columns of the objects they describe ───
        ColumnState AssignCohort(CausalChainCohort cohort)
        {
            // The derived source-import hop is the record's own timeline: data arriving at (or
            // disappearing from) the source system.
            if (cohort.SourceImportChangeType != null)
                return GetRecordColumn(cohort.ConnectedSystemId, cohort.ConnectedSystemName, isSourceSide: true);

            switch (cohort.EdgeType)
            {
                // A Metaverse Object deletion is a Metaverse-side fact wherever its consequences land.
                case CausalEdgeType.MetaverseObjectDeletionCausedDeprovision:
                case CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval:
                    return GetIdentityColumn();

                // The export this import confirms happened to the page's own record.
                case CausalEdgeType.ExportCausedImportConfirmation:
                    return GetRecordColumn(cohort.ConnectedSystemId ?? context.CsoConnectedSystemId,
                        cohort.ConnectedSystemName ?? context.CsoConnectedSystemName,
                        isSourceSide: !pageRecordIsTarget);

                // An update was staged because the Identity changed, and a delete because the Identity
                // was deleted: both are Metaverse-side causes, so they render on the Identity.
                case CausalEdgeType.PendingExportQueueingCausedExportExecution
                    when cohort.ReasonCode is CausalReasonCode.ExportUpdateStaged or CausalReasonCode.ExportDeleteStaged:
                    return GetIdentityColumn();

                // A provisioning decision (and a legacy edge with no reason) acted on the target
                // record it staged the change against.
                case CausalEdgeType.PendingExportQueueingCausedExportExecution:
                    return GetRecordColumn(cohort.ConnectedSystemId ?? context.CsoConnectedSystemId,
                        cohort.ConnectedSystemName ?? context.CsoConnectedSystemName,
                        isSourceSide: false);

                // A seam this builder does not know lands on the neutral trailing column rather than
                // being dropped: nothing in the chain is ever silently omitted.
                default:
                    return unassignedColumn ??= new ColumnState
                    {
                        Kind = CausalitySpineColumnKind.Unassigned,
                        CreationOrder = creationOrder++
                    };
            }
        }

        void Walk(IEnumerable<CausalChainCohort> cohorts, string? effectName, Guid? effectItemId)
        {
            foreach (var cohort in cohorts)
            {
                var column = AssignCohort(cohort);
                column.Hops.Add((BuildHop(cohort, effectName, effectItemId), hopSequence++));

                foreach (var member in cohort.Members)
                {
                    if (member.Causes.Count > 0)
                        Walk(member.Causes, member.DisplayName, member.RunProfileExecutionItemId);
                    else if (CausalityCauseWording.Ending(member.Resolution) != null
                             && !column.EndingResolutions.Contains(member.Resolution))
                        column.EndingResolutions.Add(member.Resolution);
                }
            }
        }

        if (chain != null)
            Walk(chain.Cohorts, context.CsoDisplayName, chain.RunProfileExecutionItemId);

        // The Identity column also exists when the story spans both sides of the Metaverse with no
        // loaded event on the Identity itself (a create export): records never join directly, so the
        // graph the canvas draws must still route through the Identity.
        var hasSourceRecord = recordColumns.Any(c => c.IsSourceSide);
        var hasTargetRecord = recordColumns.Any(c => !c.IsSourceSide);
        if (identityColumn == null && hasSourceRecord && hasTargetRecord)
            GetIdentityColumn();

        // ─── Order: source records, the Identity, target records, then the trailing column ───
        var orderedStates = recordColumns.Where(c => c.IsSourceSide)
            .OrderByDescending(c => c.IsPageRecord).ThenBy(c => c.CreationOrder)
            .ToList();
        if (identityColumn != null)
            orderedStates.Add(identityColumn);
        orderedStates.AddRange(recordColumns.Where(c => !c.IsSourceSide)
            .OrderByDescending(c => c.IsPageRecord).ThenBy(c => c.CreationOrder));
        if (unassignedColumn != null)
            orderedStates.Add(unassignedColumn);

        var hasProjected = allEvents.Any(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        var hasJoined = allEvents.Any(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Joined);

        var columns = orderedStates.Select(state => Materialise(state, context, chain)).ToList();
        var joins = new List<CausalitySpineJoin>();
        for (var i = 0; i < orderedStates.Count - 1; i++)
            joins.Add(new CausalitySpineJoin(GetJoinLabel(orderedStates[i], orderedStates[i + 1], hasProjected, hasJoined)));

        return new CausalitySpineModel
        {
            Columns = columns,
            Joins = joins,
            IsTruncatedByDepth = chain?.IsTruncatedByDepth ?? false
        };
    }

    /// <summary>
    /// Prepares one cohort for rendering: sentence, attribution, run kind, timestamp, links and the
    /// member list a plural cohort expands to.
    /// </summary>
    private static CausalitySpineChainHop BuildHop(CausalChainCohort cohort, string? effectName, Guid? effectItemId)
    {
        var soleMember = cohort.MemberCount == 1 ? cohort.Members[0] : null;

        return new CausalitySpineChainHop
        {
            Cohort = cohort,
            SentenceParts = CausalityCauseWording.Sentence(cohort, effectName),
            Reason = CausalityCauseWording.Reason(cohort),
            ShowConnectedSystemChip = CausalityCauseWording.ShowConnectedSystemChip(cohort),
            RunKind = GetRunKind(cohort),
            ActivityItemHref = soleMember != null ? GetActivityItemHref(soleMember, effectItemId) : null,
            Occurred = cohort.Members.Count > 0 ? cohort.Members.Min(m => m.Occurred) : default,
            Members = soleMember != null
                ? []
                : cohort.Members.Select(m => new CausalitySpineChainHopMember(
                    string.IsNullOrWhiteSpace(m.DisplayName) ? "Unnamed object" : m.DisplayName!,
                    GetActivityItemHref(m, effectItemId))).ToList()
        };
    }

    /// <summary>
    /// The kind of run that recorded a cause, where the hop's seam pins one: a derived import hop was
    /// an import, the queueing seam a synchronisation, a confirmed export an export run. A Metaverse
    /// Object deletion could have been decided by a synchronisation or by housekeeping, so those hops
    /// state no run kind rather than guess one.
    /// </summary>
    private static string? GetRunKind(CausalChainCohort cohort)
    {
        if (cohort.SourceImportChangeType != null)
            return "Import run";

        return cohort.EdgeType switch
        {
            CausalEdgeType.ExportCausedImportConfirmation => "Export run",
            CausalEdgeType.PendingExportQueueingCausedExportExecution => "Synchronisation run",
            _ => null
        };
    }

    /// <summary>
    /// The href of the item that recorded a cause, or null where there is nothing useful to link to:
    /// the cause named no item, or named the very item being viewed.
    /// </summary>
    private static string? GetActivityItemHref(CausalChainMember member, Guid? effectItemId)
    {
        if (member.RunProfileExecutionItemId is not { } itemId || itemId == Guid.Empty)
            return null;

        return itemId == effectItemId ? null : $"/activity/item/{itemId}";
    }

    /// <summary>
    /// Materialises a column: its head, its cards oldest-first (chain hops in time order, then this
    /// run's events, which are always the newest thing in the story) and its endings.
    /// </summary>
    private static CausalitySpineColumn Materialise(ColumnState state, CausalityPageContext context, CausalChain? chain)
    {
        var cards = state.Hops
            .OrderBy(h => h.Hop.Occurred).ThenBy(h => h.Sequence)
            .Select(h => new CausalitySpineCard { Hop = h.Hop, Occurred = h.Hop.Occurred })
            .Concat(state.ThisRunEvents.Select(e => new CausalitySpineCard { Event = e }))
            .ToList();

        var endings = state.EndingResolutions
            .Select(r => new CausalitySpineEnding(r, CausalityCauseWording.Ending(r)!))
            .ToList();

        var head = state.Kind switch
        {
            CausalitySpineColumnKind.Identity => GetIdentityHead(state, chain),
            CausalitySpineColumnKind.Record => GetRecordHead(state, context),
            _ => new ColumnHead("Earlier causes", IsRoleHead: false, Href: null, ObjectTypeName: null)
        };

        return new CausalitySpineColumn
        {
            Kind = state.Kind,
            Title = head.Title,
            IsRoleHead = head.IsRoleHead,
            SystemId = state.Kind == CausalitySpineColumnKind.Record ? state.SystemId : null,
            SystemName = state.Kind == CausalitySpineColumnKind.Record ? state.SystemName : null,
            ObjectTypeName = head.ObjectTypeName,
            Href = head.Href,
            Cards = cards,
            Endings = endings
        };
    }

    /// <summary>
    /// The Identity column's head: the single object where the story has one (this run's own Identity
    /// link first, then a sole-cause hop's snapshot, then any sole cause anywhere in the chain for a
    /// column that exists purely to complete the graph), and the plural role where it does not.
    /// </summary>
    private static ColumnHead GetIdentityHead(ColumnState state, CausalChain? chain)
    {
        foreach (var causalityEvent in state.ThisRunEvents)
        {
            var identityLink = causalityEvent.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
            if (identityLink != null && !string.IsNullOrWhiteSpace(identityLink.Label))
                return new ColumnHead(identityLink.Label, IsRoleHead: false, identityLink.Href, ObjectTypeName: null);
        }

        var soleName = state.Hops.OrderBy(h => h.Sequence)
            .Where(h => h.Hop.Cohort.MemberCount == 1)
            .Select(h => h.Hop.Cohort.Members[0].DisplayName)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        if (soleName != null)
            return new ColumnHead(soleName, IsRoleHead: false, Href: null, ObjectTypeName: null);

        var pluralCohort = state.Hops.OrderBy(h => h.Sequence)
            .Select(h => h.Hop.Cohort)
            .FirstOrDefault(c => c.MemberCount > 1);
        if (pluralCohort != null)
            return new ColumnHead(pluralCohort.ObjectNoun ?? "Identities", IsRoleHead: true, Href: null, ObjectTypeName: null);

        if (chain != null && FirstSoleMemberName(chain.Cohorts) is { } chainName)
            return new ColumnHead(chainName, IsRoleHead: false, Href: null, ObjectTypeName: null);

        return new ColumnHead("Identity", IsRoleHead: false, Href: null, ObjectTypeName: null);
    }

    /// <summary>
    /// A record column's head: the page's own record from the context; a chain-derived column from
    /// its hops' snapshots (the single name where they all concern one object, a role where they do
    /// not); a staging target after the story's subject, linked to the record it created where this
    /// run recorded one.
    /// </summary>
    private static ColumnHead GetRecordHead(ColumnState state, CausalityPageContext context)
    {
        if (state.IsPageRecord)
        {
            string? href = context.CsoId is { } csoId && context.CsoConnectedSystemId is { } systemId
                ? JimUtilities.GetConnectedSystemObjectHref(systemId, csoId)
                : null;
            return new ColumnHead(context.RecordName ?? "Record", IsRoleHead: false, href, context.CsoObjectTypeName);
        }

        var soleNames = state.Hops.OrderBy(h => h.Sequence)
            .Where(h => h.Hop.Cohort.MemberCount == 1)
            .Select(h => h.Hop.Cohort.Members[0].DisplayName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (soleNames.Count == 1)
            return new ColumnHead(soleNames[0], IsRoleHead: false, Href: null, ObjectTypeName: null);
        if (soleNames.Count > 1)
            return new ColumnHead("Records", IsRoleHead: true, Href: null, ObjectTypeName: null);

        var pluralCohort = state.Hops.OrderBy(h => h.Sequence)
            .Select(h => h.Hop.Cohort)
            .FirstOrDefault(c => c.MemberCount > 1);
        if (pluralCohort != null)
            return new ColumnHead(pluralCohort.ObjectNoun ?? "Records", IsRoleHead: true, Href: null, ObjectTypeName: null);

        // A staging target holds this run's provisioning events and nothing chain-derived: the record
        // is the story's subject arriving in the target system, linked where the run recorded it.
        var recordLink = state.ThisRunEvents.SelectMany(e => e.Links)
            .FirstOrDefault(l => l.Kind == CausalityEntityKind.Record && l.Href != null);
        return new ColumnHead(context.RecordName ?? state.SystemName ?? "Record", IsRoleHead: false,
            recordLink?.Href, ObjectTypeName: null);
    }

    /// <summary>
    /// The first sole cause named anywhere in the chain, walk order, for heading an Identity column
    /// that exists only to complete the graph (a create export names its Identity nowhere else).
    /// </summary>
    private static string? FirstSoleMemberName(IEnumerable<CausalChainCohort> cohorts)
    {
        foreach (var cohort in cohorts)
        {
            if (cohort.MemberCount == 1 && !string.IsNullOrWhiteSpace(cohort.Members[0].DisplayName))
                return cohort.Members[0].DisplayName;

            var nested = cohort.Members
                .Select(member => FirstSoleMemberName(member.Causes))
                .FirstOrDefault(name => name != null);
            if (nested != null)
                return nested;
        }

        return null;
    }

    /// <summary>
    /// The relationship label between two adjacent columns. A record feeding the Identity reads as
    /// what this run proved ("projected", "joined") or as the standing "imported" relationship; the
    /// Identity feeding a record reads "provisioned" where the record was created (this run's
    /// provisioning event, or the chain's create-staged decision) and "exported" otherwise. Pairs
    /// touching the trailing column state no relationship.
    /// </summary>
    private static string? GetJoinLabel(ColumnState left, ColumnState right, bool hasProjected, bool hasJoined)
    {
        if (left.Kind == CausalitySpineColumnKind.Unassigned || right.Kind == CausalitySpineColumnKind.Unassigned)
            return null;

        if (left.Kind == CausalitySpineColumnKind.Record && right.Kind == CausalitySpineColumnKind.Identity)
        {
            if (left.IsPageRecord && hasProjected)
                return "projected";
            if (left.IsPageRecord && hasJoined)
                return "joined";
            return "imported";
        }

        if (left.Kind == CausalitySpineColumnKind.Identity && right.Kind == CausalitySpineColumnKind.Record)
        {
            var provisioned = right.ThisRunEvents.Any(e =>
                    e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned)
                || right.Hops.Any(h => h.Hop.Cohort.ReasonCode == CausalReasonCode.ExportCreateStaged);
            return provisioned ? "provisioned" : "exported";
        }

        return null;
    }

    /// <summary>
    /// A column under construction: its identity, side, cards-in-progress and endings, before heads
    /// and ordering are resolved.
    /// </summary>
    private sealed class ColumnState
    {
        public CausalitySpineColumnKind Kind { get; init; }

        public int? SystemId { get; init; }

        public string? SystemName { get; set; }

        public bool IsSourceSide { get; init; }

        public bool IsPageRecord { get; set; }

        public int CreationOrder { get; init; }

        public List<CausalityEvent> ThisRunEvents { get; } = [];

        public List<(CausalitySpineChainHop Hop, int Sequence)> Hops { get; } = [];

        public List<CausalChainResolution> EndingResolutions { get; } = [];
    }

    /// <summary>
    /// A resolved column head: what the column is titled, whether that title is a role, and the
    /// link and object type name where the head has them.
    /// </summary>
    private sealed record ColumnHead(string Title, bool IsRoleHead, string? Href, string? ObjectTypeName);
}
