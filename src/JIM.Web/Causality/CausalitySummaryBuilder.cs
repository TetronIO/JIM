// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JimUtilities = JIM.Utilities.Utilities;

namespace JIM.Web.Causality;

/// <summary>
/// Builds the summary band content from a <see cref="CausalityModel"/>: one plain-English sentence
/// describing what happened and what it caused (as segments, never pre-rendered HTML), plus the
/// colour-coded outcome pill strip. Sentence templates are keyed on the dominant outcome shape
/// (projection/join, out-of-scope/deletion, export attempt/failure, no change), with a generic
/// fallback that always produces a valid sentence.
/// </summary>
public static class CausalitySummaryBuilder
{
    /// <summary>
    /// Builds the summary for a causality model. Never throws for missing or legacy data.
    /// </summary>
    public static CausalitySummary Build(CausalityModel model)
    {
        var allEvents = model.AllEvents().ToList();

        var segments = new List<SummarySegment>();
        segments.AddRange(BuildOpening(model.Context));
        AppendClauses(segments, BuildClauses(allEvents));
        segments.Add(new SummarySegment.Text("."));

        return new CausalitySummary
        {
            Segments = segments,
            Pills = BuildPills(allEvents)
        };
    }

    private static List<SummarySegment> BuildOpening(CausalityPageContext context)
    {
        var segments = new List<SummarySegment>();

        if (!string.IsNullOrWhiteSpace(context.RunProfileName))
        {
            segments.Add(new SummarySegment.Text(StartsWithVowel(context.RunProfileName) ? "An " : "A "));
            segments.Add(new SummarySegment.Entity(context.RunProfileName, null, CausalityEntityKind.RunProfile));
        }
        else
        {
            segments.Add(new SummarySegment.Text("A run"));
        }

        if (!string.IsNullOrWhiteSpace(context.ConnectedSystemName))
        {
            segments.Add(new SummarySegment.Text(" on "));
            segments.Add(new SummarySegment.Entity(
                context.ConnectedSystemName,
                context.ConnectedSystemId.HasValue ? JimUtilities.GetConnectedSystemHref(context.ConnectedSystemId.Value) : null,
                CausalityEntityKind.ConnectedSystem));
        }

        var recordLabel = GetRecordLabel(context);
        if (recordLabel != null)
        {
            segments.Add(new SummarySegment.Text(" processed the record for "));
            var recordHref = context.ConnectedSystemId.HasValue && context.CsoId.HasValue
                ? JimUtilities.GetConnectedSystemObjectHref(context.ConnectedSystemId.Value, context.CsoId.Value)
                : null;
            segments.Add(new SummarySegment.Entity(recordLabel, recordHref, CausalityEntityKind.Record));
        }
        else
        {
            segments.Add(new SummarySegment.Text(" processed the record"));
        }

        return segments;
    }

    private static string? GetRecordLabel(CausalityPageContext context)
    {
        var hasDisplayName = !string.IsNullOrWhiteSpace(context.CsoDisplayName);
        var hasExternalId = !string.IsNullOrWhiteSpace(context.CsoExternalId);

        if (hasDisplayName && hasExternalId)
            return $"{context.CsoDisplayName} ({context.CsoExternalId})";
        if (hasDisplayName)
            return context.CsoDisplayName;
        if (hasExternalId)
            return context.CsoExternalId;
        return null;
    }

    private static bool StartsWithVowel(string value)
    {
        return value.Length > 0 && "AEIOUaeiou".Contains(value[0]);
    }

    /// <summary>
    /// Appends the result clauses to the sentence: ": clause1, clause2, and clause3".
    /// </summary>
    private static void AppendClauses(List<SummarySegment> segments, List<List<SummarySegment>> clauses)
    {
        segments.Add(new SummarySegment.Text(": "));
        for (var i = 0; i < clauses.Count; i++)
        {
            if (i > 0)
                segments.Add(new SummarySegment.Text(i == clauses.Count - 1 ? ", and " : ", "));
            segments.AddRange(clauses[i]);
        }
    }

    /// <summary>
    /// Builds the result clauses for the dominant outcome shape.
    /// </summary>
    private static List<List<SummarySegment>> BuildClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        if (allEvents.Count == 0)
            return [[new SummarySegment.Text("no changes were needed")]];

        if (allEvents.Any(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed))
            return BuildExportFailureClauses(allEvents);

        if (allEvents.Any(e => e.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.Exported
                or ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed))
            return BuildExportSuccessClauses(allEvents);

        if (allEvents.Any(e => e.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.Projected
                or ActivityRunProfileExecutionItemSyncOutcomeType.Joined))
            return BuildJoinerClauses(allEvents);

        if (allEvents.Any(e => e.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope
                or ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected
                or ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
                or ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled))
            return BuildLeaverClauses(allEvents);

        return BuildGenericFallbackClauses(allEvents);
    }

    private static List<List<SummarySegment>> BuildJoinerClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        var clauses = new List<List<SummarySegment>>();

        if (allEvents.Any(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected))
        {
            clauses.Add([new SummarySegment.Text("a new Identity was created")]);
        }
        else
        {
            var joined = allEvents.First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Joined);
            var identity = joined.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
            if (identity != null)
            {
                clauses.Add([
                    new SummarySegment.Text("it was joined to the Identity "),
                    new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity)
                ]);
            }
            else
            {
                clauses.Add([new SummarySegment.Text("it was joined to an existing Identity")]);
            }
        }

        var attributeFlowClause = BuildAttributeFlowClause(allEvents);
        if (attributeFlowClause != null)
            clauses.Add(attributeFlowClause);

        var exportClause = BuildQueuedExportClause(allEvents);
        if (exportClause != null)
            clauses.Add(exportClause);

        return clauses;
    }

    private static List<SummarySegment>? BuildAttributeFlowClause(IReadOnlyList<CausalityEvent> allEvents)
    {
        var attributeFlows = allEvents
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow)
            .ToList();
        if (attributeFlows.Count == 0)
            return null;

        var flowedCount = attributeFlows.Sum(e => e.DetailCount ?? 0);
        return flowedCount > 0
            ? [new SummarySegment.Text($"{flowedCount} attribute{(flowedCount == 1 ? string.Empty : "s")} flowed to it")]
            : [new SummarySegment.Text("attributes flowed to it")];
    }

    private static List<SummarySegment>? BuildQueuedExportClause(IReadOnlyList<CausalityEvent> allEvents)
    {
        var queuedExports = allEvents
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated)
            .ToList();
        if (queuedExports.Count == 0)
            return null;

        var changeCount = queuedExports.Sum(e => e.DetailCount ?? 0);
        var systems = queuedExports
            .Select(e => (e.SystemId, e.SystemName))
            .Distinct()
            .ToList();

        if (systems.Count == 1)
        {
            var (systemId, systemName) = systems[0];
            var countText = changeCount > 0
                ? $"an export of {changeCount} change{(changeCount == 1 ? string.Empty : "s")} is now queued for "
                : "an export is now queued for ";
            var target = systemName != null
                ? new SummarySegment.Entity(systemName,
                    systemId.HasValue ? JimUtilities.GetConnectedSystemHref(systemId.Value) : null,
                    CausalityEntityKind.ConnectedSystem)
                : (SummarySegment)new SummarySegment.Text("a downstream system");
            return [new SummarySegment.Text(countText), target];
        }

        return [new SummarySegment.Text(
            $"exports of {changeCount} change{(changeCount == 1 ? string.Empty : "s")} are now queued for {systems.Count} systems")];
    }

    private static List<List<SummarySegment>> BuildLeaverClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        var clauses = new List<List<SummarySegment>>();

        var outOfScope = allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope);
        if (outOfScope != null)
        {
            var rule = outOfScope.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.SynchronisationRule);
            if (rule != null)
            {
                clauses.Add([
                    new SummarySegment.Text("it left the scope of Synchronisation Rule "),
                    new SummarySegment.Entity(rule.Label, rule.Href, CausalityEntityKind.SynchronisationRule)
                ]);
            }
            else
            {
                clauses.Add([new SummarySegment.Text("it left the scope of its Synchronisation Rule")]);
            }
        }
        else
        {
            var disconnected = allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected);
            if (disconnected != null)
            {
                var identity = disconnected.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
                if (identity != null)
                {
                    clauses.Add([
                        new SummarySegment.Text("it was disconnected from the Identity "),
                        new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity)
                    ]);
                }
                else
                {
                    clauses.Add([new SummarySegment.Text("it was disconnected from its Identity")]);
                }
            }
        }

        var mvoDeleted = allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        if (mvoDeleted != null)
        {
            var identityName = mvoDeleted.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity)?.Label;
            if (identityName != null)
            {
                // The Identity no longer exists, so its mention links the durable deletion record browser
                clauses.Add([
                    new SummarySegment.Text("the Identity "),
                    new SummarySegment.Entity(identityName, "/admin/deleted-objects", CausalityEntityKind.Identity),
                    new SummarySegment.Text(" was deleted")
                ]);
            }
            else
            {
                clauses.Add([new SummarySegment.Text("the Identity was deleted")]);
            }
        }
        else
        {
            var deletionScheduled = allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled);
            if (deletionScheduled != null)
            {
                var identity = deletionScheduled.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
                if (identity != null)
                {
                    clauses.Add([
                        new SummarySegment.Text("the Identity "),
                        new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity),
                        new SummarySegment.Text(" was scheduled for deletion")
                    ]);
                }
                else
                {
                    clauses.Add([new SummarySegment.Text("the Identity was scheduled for deletion")]);
                }
            }
        }

        var deprovisionSystemCount = allEvents
            .Where(e => e.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated
                or ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned)
            .Select(e => (e.SystemId, e.SystemName))
            .Distinct()
            .Count();
        if (deprovisionSystemCount > 0)
        {
            clauses.Add([new SummarySegment.Text(
                $"deprovisioning is now queued for {deprovisionSystemCount} system{(deprovisionSystemCount == 1 ? string.Empty : "s")}")]);
        }

        if (clauses.Count == 0)
            return BuildGenericFallbackClauses(allEvents);

        return clauses;
    }

    private static List<List<SummarySegment>> BuildExportFailureClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        var attemptedCount = allEvents
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported)
            .Sum(e => e.DetailCount ?? 0);
        if (attemptedCount == 0)
        {
            attemptedCount = allEvents
                .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed)
                .Sum(e => e.DetailCount ?? 0);
        }

        var clause = attemptedCount > 0
            ? $"an export of {attemptedCount} change{(attemptedCount == 1 ? string.Empty : "s")} was attempted, but it failed and needs attention"
            : "an export was attempted, but it failed and needs attention";
        return [[new SummarySegment.Text(clause)]];
    }

    private static List<List<SummarySegment>> BuildExportSuccessClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        var exportedCount = allEvents
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported)
            .Sum(e => e.DetailCount ?? 0);
        if (allEvents.Any(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported))
        {
            var clause = exportedCount switch
            {
                1 => "1 change was exported",
                > 1 => $"{exportedCount} changes were exported",
                _ => "changes were exported"
            };
            return [[new SummarySegment.Text(clause)]];
        }

        var confirmedCount = allEvents
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed)
            .Sum(e => e.DetailCount ?? 0);
        var confirmedClause = confirmedCount switch
        {
            1 => "1 exported change was confirmed",
            > 1 => $"{confirmedCount} exported changes were confirmed",
            _ => "the export was confirmed"
        };
        return [[new SummarySegment.Text(confirmedClause)]];
    }

    /// <summary>
    /// The generic fallback: name the distinct root-level outcomes in plain language. Always yields
    /// a valid sentence for unanticipated shapes.
    /// </summary>
    private static List<List<SummarySegment>> BuildGenericFallbackClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        var labels = allEvents.Select(e => e.PlainLabel).Distinct().ToList();
        return labels.Select(label => (List<SummarySegment>)[new SummarySegment.Text(label)]).ToList();
    }

    /// <summary>
    /// Builds the outcome pill strip: one pill per outcome type present, in first-seen tree order,
    /// annotated with counts where they aid comprehension.
    /// </summary>
    private static List<CausalityPill> BuildPills(IReadOnlyList<CausalityEvent> allEvents)
    {
        var eventsByType = new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, List<CausalityEvent>>();
        var typeOrder = new List<ActivityRunProfileExecutionItemSyncOutcomeType>();
        foreach (var causalityEvent in allEvents)
        {
            if (!eventsByType.TryGetValue(causalityEvent.OutcomeType, out var eventsForType))
            {
                eventsForType = [];
                eventsByType[causalityEvent.OutcomeType] = eventsForType;
                typeOrder.Add(causalityEvent.OutcomeType);
            }
            eventsForType.Add(causalityEvent);
        }

        return typeOrder.Select(outcomeType => BuildPill(outcomeType, eventsByType[outcomeType])).ToList();
    }

    private static CausalityPill BuildPill(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType,
        IReadOnlyList<CausalityEvent> eventsOfType)
    {
        var display = OutcomeDisplayMap.Get(outcomeType);
        var detailSum = eventsOfType.Sum(e => e.DetailCount ?? 0);
        var systemCount = eventsOfType.Select(e => (e.SystemId, e.SystemName)).Distinct().Count();

        var label = outcomeType switch
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow when detailSum > 0 =>
                $"{detailSum} attribute{(detailSum == 1 ? string.Empty : "s")} flowed",
            ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned =>
                $"Provisioned · {systemCount} system{(systemCount == 1 ? string.Empty : "s")}",
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated when detailSum > 0 =>
                $"Export queued · {detailSum} change{(detailSum == 1 ? string.Empty : "s")}",
            ActivityRunProfileExecutionItemSyncOutcomeType.Exported when detailSum > 0 =>
                $"Exported · {detailSum} change{(detailSum == 1 ? string.Empty : "s")}",
            ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned =>
                $"Deprovisioning · {systemCount} system{(systemCount == 1 ? string.Empty : "s")}",
            ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope => "Out of scope",
            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled => "Deletion scheduled",
            _ => display.PlainLabel
        };

        return new CausalityPill(label, display.Tone);
    }
}
