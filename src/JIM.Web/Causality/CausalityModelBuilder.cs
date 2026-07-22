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
    public static CausalityModel Build(ActivityRunProfileExecutionItem item, CausalityPageContext context)
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
        var itemAttributeRows = NormaliseAttributeRows(
            item.ConnectedSystemObjectChange?.AttributeChanges,
            item.MetaverseObjectChange?.AttributeChanges);

        var roots = outcomes
            .Where(o => !attachedChildIds.Contains(o.Id))
            .OrderBy(o => o.Ordinal)
            .Select(o => BuildEvent(o, childrenByParentId, context, itemAttributeRows))
            .ToList();

        return new CausalityModel { Context = context, Roots = roots };
    }

    private static CausalityEvent BuildEvent(
        ActivityRunProfileExecutionItemSyncOutcome outcome,
        Dictionary<Guid, List<ActivityRunProfileExecutionItemSyncOutcome>> childrenByParentId,
        CausalityPageContext context,
        IReadOnlyList<CausalityAttributeRow> itemAttributeRows)
    {
        var display = OutcomeDisplayMap.Get(outcome.OutcomeType);
        var parsedDetail = OutcomeDetailMessageParser.Parse(outcome.DetailMessage);
        var usesIdChannel = UsesDetailMessageIdChannel(outcome.OutcomeType);
        var lane = GetLane(outcome.OutcomeType);

        var childOutcomes = childrenByParentId.TryGetValue(outcome.Id, out var children)
            ? children
            : [];

        var links = BuildLinks(outcome, childOutcomes, parsedDetail, context);
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
            SentenceSegments = BuildEventSentence(display, links),
            AttributeRows = GetAttributeRows(outcome, itemAttributeRows),
            Children = childOutcomes
                .Select(c => BuildEvent(c, childrenByParentId, context, itemAttributeRows))
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
            // name in TargetEntityDescription (which may be a different system to the page's)
            return (parsedDetail.ConnectedSystemId, outcome.TargetEntityDescription ?? context.ConnectedSystemName);
        }

        // Source events and export execution events belong to the page's Connected System
        return (context.ConnectedSystemId, context.ConnectedSystemName);
    }

    private static List<CausalityEntityLink> BuildLinks(
        ActivityRunProfileExecutionItemSyncOutcome outcome,
        IReadOnlyList<ActivityRunProfileExecutionItemSyncOutcome> childOutcomes,
        OutcomeDetailMessage parsedDetail,
        CausalityPageContext context)
    {
        var links = new List<CausalityEntityLink>();

        switch (outcome.OutcomeType)
        {
            case ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned:
                // TargetEntityId is the new CSO's id and DetailMessage carries "csId|csoTypeName";
                // same semantics as OutcomeTreeNode's provisioned CSO link
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
                // TargetEntityId is the Pending Export's id; the queued changes live on the target
                // system's Pending Exports page
                if (parsedDetail.ConnectedSystemId.HasValue)
                {
                    var targetSystemId = parsedDetail.ConnectedSystemId.Value;
                    links.Add(new CausalityEntityLink(
                        outcome.TargetEntityDescription ?? "Connected System",
                        JimUtilities.GetConnectedSystemHref(targetSystemId),
                        CausalityEntityKind.ConnectedSystem));
                    links.Add(new CausalityEntityLink(
                        "Pending Exports",
                        $"/admin/connected-systems/{targetSystemId}/pending-exports",
                        CausalityEntityKind.PendingExport));
                }
                else if (!string.IsNullOrEmpty(outcome.TargetEntityDescription))
                {
                    links.Add(new CausalityEntityLink(outcome.TargetEntityDescription, null, CausalityEntityKind.ConnectedSystem));
                }
                break;

            case ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted:
                // The Metaverse Object no longer exists: name it, but link the durable deletion
                // record browser instead of the (deleted) Identity's detail page
                if (!string.IsNullOrEmpty(outcome.TargetEntityDescription))
                    links.Add(new CausalityEntityLink(outcome.TargetEntityDescription, null, CausalityEntityKind.Identity));
                links.Add(new CausalityEntityLink("View deletion record", "/admin/deleted-objects", CausalityEntityKind.DeletionRecord));
                break;

            case ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed:
                // The failed changes remain queued on the page's Connected System
                if (context.ConnectedSystemId.HasValue)
                {
                    links.Add(new CausalityEntityLink(
                        "Pending Exports",
                        $"/admin/connected-systems/{context.ConnectedSystemId.Value}/pending-exports",
                        CausalityEntityKind.PendingExport));
                }
                break;

            default:
                // Parity with OutcomeTreeNode: no Identity link when the Identity no longer exists,
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

    private static string GetMetaverseObjectHref(Guid mvoId, CausalityPageContext context)
    {
        return !string.IsNullOrEmpty(context.MvoTypePluralName)
            ? JimUtilities.GetMetaverseObjectHref(mvoId, context.MvoTypePluralName)
            : $"/identity/search/{mvoId}";
    }

    /// <summary>
    /// Builds the per-event sentence for the Timeline view: the plain label, then the event's entity
    /// mentions with a joiner appropriate to the outcome (e.g. "Joined to Identity: Liam Allen").
    /// </summary>
    private static List<SummarySegment> BuildEventSentence(OutcomeDisplay display, IReadOnlyList<CausalityEntityLink> links)
    {
        var segments = new List<SummarySegment>();
        if (links.Count == 0)
        {
            segments.Add(new SummarySegment.Text(display.PlainLabel));
            return segments;
        }

        segments.Add(new SummarySegment.Text($"{display.PlainLabel}: "));
        for (var i = 0; i < links.Count; i++)
        {
            if (i > 0)
                segments.Add(new SummarySegment.Text(", "));
            var link = links[i];
            segments.Add(new SummarySegment.Entity(link.Label, link.Href, link.Kind));
        }

        return segments;
    }

    /// <summary>
    /// Selects the attribute rows for an event: PendingExportCreated uses its persisted CSO change
    /// snapshot; record and Attribute Flow events share the item-level change rows.
    /// </summary>
    private static IReadOnlyList<CausalityAttributeRow> GetAttributeRows(
        ActivityRunProfileExecutionItemSyncOutcome outcome,
        IReadOnlyList<CausalityAttributeRow> itemAttributeRows)
    {
        if (outcome.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated)
            return NormaliseAttributeRows(outcome.ConnectedSystemObjectChange?.AttributeChanges, null);

        return outcome.OutcomeType switch
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow
                or ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded
                or ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated
                or ActivityRunProfileExecutionItemSyncOutcomeType.Exported
                or ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned
                => itemAttributeRows,
            _ => []
        };
    }

    /// <summary>
    /// Normalises CSO and MVO attribute changes into display rows, collapsing single-valued
    /// Add and Remove pairs into one Set row with the previous value; the same collapse logic as
    /// the legacy AttributeChangeTable.
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

        return valueChange.ReferenceValue.DisplayNameOrId ?? valueChange.ReferenceValue.Id.ToString();
    }

    private static string? GetMvoValueText(MetaverseObjectChangeAttributeValue valueChange)
    {
        if (valueChange.ReferenceValue == null)
            return valueChange.ToString();

        return valueChange.ReferenceValue.DisplayName ?? valueChange.ReferenceValue.Id.ToString();
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
