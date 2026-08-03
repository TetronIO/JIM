// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
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
    public static CausalityModel Build(
        ActivityRunProfileExecutionItem item,
        CausalityPageContext context,
        IReadOnlySet<Guid>? livePendingExportIds = null)
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
                livePendingExportIds))
            .ToList();

        return new CausalityModel { Context = context, Roots = roots };
    }

    private static CausalityEvent BuildEvent(
        ActivityRunProfileExecutionItemSyncOutcome outcome,
        Dictionary<Guid, List<ActivityRunProfileExecutionItemSyncOutcome>> childrenByParentId,
        CausalityPageContext context,
        IReadOnlyList<CausalityAttributeRow> recordAttributeRows,
        IReadOnlyList<CausalityAttributeRow> identityAttributeRows,
        IReadOnlySet<Guid>? livePendingExportIds)
    {
        var display = OutcomeDisplayMap.Get(outcome.OutcomeType);
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
                    livePendingExportIds))
                .ToList()
        };
    }

    /// <summary>
    /// Whether this outcome type stores the "csId|csoTypeName" link channel in DetailMessage
    /// rather than plain contextual text.
    /// </summary>
    private static bool UsesDetailMessageIdChannel(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return outcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned
            or ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated;
    }

    private static CausalityLane GetLane(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return outcomeType switch
        {
            // Import-side record events: what came in
            ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded
                or ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated
                or ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted
                or ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected
                => CausalityLane.Source,

            // Provisioning and export-side events: what it caused
            ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned
                or ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated
                or ActivityRunProfileExecutionItemSyncOutcomeType.Exported
                or ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed
                or ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed
                or ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned
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

    private static string GetMetaverseObjectHref(Guid mvoId, CausalityPageContext context)
    {
        return !string.IsNullOrEmpty(context.MvoTypePluralName)
            ? JimUtilities.GetMetaverseObjectHref(mvoId, context.MvoTypePluralName)
            : $"/identity/search/{mvoId}";
    }

    /// <summary>
    /// Selects the attribute rows for an event by the change set it owns: PendingExportCreated uses
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
        if (outcome.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated)
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
