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
        segments.AddRange(BuildOpening(model.Context, model.IsSpeculative));
        AppendClauses(segments, model.IsSpeculative ? BuildSpeculativeClauses(allEvents) : BuildClauses(allEvents));
        segments.Add(new SummarySegment.Text("."));

        return new CausalitySummary
        {
            Segments = segments,
            Pills = BuildPills(allEvents)
        };
    }

    private static List<SummarySegment> BuildOpening(CausalityPageContext context, bool isSpeculative)
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

        var recordLabel = ObjectDescription.ChipName(context.CsoDisplayName, context.CsoExternalId);
        // Conditional mood throughout for a preview (#1519, D-S9): nothing here has happened yet.
        var verb = isSpeculative ? "would process" : "processed";

        if (recordLabel != null)
        {
            // Named by its object type where the builder knows it ("processed person Baseline User"), so
            // the sentence states what kind of object this is without a second clause; otherwise the name
            // alone carries it ("processed Baseline User").
            segments.Add(new SummarySegment.Text(!string.IsNullOrWhiteSpace(context.CsoObjectTypeName)
                ? $" {verb} {context.CsoObjectTypeName} "
                : $" {verb} "));
            // The record's own Connected System, not the run's: they diverge for cross-system
            // cascades, and linking with the wrong system id 404s (ConnectedSystemObjectDetail looks
            // the record up by {connectedSystemId}+{id}).
            var recordHref = context.CsoConnectedSystemId.HasValue && context.CsoId.HasValue
                ? JimUtilities.GetConnectedSystemObjectHref(context.CsoConnectedSystemId.Value, context.CsoId.Value)
                : null;
            segments.Add(new SummarySegment.Entity(recordLabel, recordHref, CausalityEntityKind.Record));
        }
        else
        {
            segments.Add(new SummarySegment.Text($" {verb} the Connected System Object"));
        }

        return segments;
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

        if (allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin) is { } retainedJoin)
            return [BuildRetainedJoinClause(retainedJoin, isSpeculative: false)];

        if (allEvents.Any(e => e.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope
                or ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected
                or ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
                or ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled))
            return BuildLeaverClauses(allEvents);

        return BuildGenericFallbackClauses(allEvents);
    }

    /// <summary>
    /// The conditional-mood counterpart of <see cref="BuildClauses"/> for a speculative model (#1519,
    /// D-S9): the two dominant shapes a Sync Preview's tree actually produces (joiner and the
    /// destructive out-of-scope cascade) get hand-written "would" phrasing; every other shape falls back
    /// to <see cref="BuildGenericFallbackClauses"/>, which already reads correctly for a speculative
    /// model because each event's <see cref="CausalityEvent.Label"/> is already the conditional-mood
    /// <see cref="OutcomeDisplay.SpeculativeLabel"/> (see <see cref="CausalityModelBuilder.BuildSpeculative"/>).
    /// Export success/failure shapes have no speculative counterpart: <c>SyncPreviewServer</c> never emits
    /// Exported, ExportConfirmed or ExportFailed, so those <see cref="BuildClauses"/> branches are not
    /// mirrored here.
    /// </summary>
    private static List<List<SummarySegment>> BuildSpeculativeClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        if (allEvents.Count == 0)
            return [[new SummarySegment.Text("no changes are needed")]];

        if (allEvents.Any(e => e.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.Projected
                or ActivityRunProfileExecutionItemSyncOutcomeType.Joined))
            return BuildSpeculativeJoinerClauses(allEvents);

        if (allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin) is { } retainedJoin)
            return [BuildRetainedJoinClause(retainedJoin, isSpeculative: true)];

        if (allEvents.Any(e => e.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope
                or ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
                or ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled))
            return BuildSpeculativeLeaverClauses(allEvents);

        return BuildGenericFallbackClauses(allEvents);
    }

    private static List<List<SummarySegment>> BuildSpeculativeJoinerClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        var clauses = new List<List<SummarySegment>>();

        if (allEvents.Any(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected))
        {
            clauses.Add([new SummarySegment.Text("a new Metaverse Object would be projected")]);
        }
        else
        {
            var joined = allEvents.First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Joined);
            var identity = joined.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
            clauses.Add(identity != null
                ? [
                    new SummarySegment.Text("it would be joined to the Metaverse Object "),
                    new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity)
                ]
                : [new SummarySegment.Text("it would be joined to an existing Metaverse Object")]);
        }

        var attributeFlows = allEvents.Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow).ToList();
        if (attributeFlows.Count > 0)
        {
            var flowedCount = attributeFlows.Sum(e => e.DetailCount ?? 0);
            clauses.Add([new SummarySegment.Text(flowedCount > 0
                ? $"{flowedCount} attribute{(flowedCount == 1 ? string.Empty : "s")} would flow to it"
                : "attributes would flow to it")]);
        }

        var queuedExports = allEvents.Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated).ToList();
        if (queuedExports.Count > 0)
        {
            var changeCount = queuedExports.Sum(e => e.DetailCount ?? 0);
            var systems = queuedExports.Select(e => (e.SystemId, e.SystemName)).Distinct().ToList();
            if (systems.Count == 1)
            {
                var (systemId, systemName) = systems[0];
                var countText = changeCount > 0
                    ? $"an export of {changeCount} change{(changeCount == 1 ? string.Empty : "s")} would be queued for "
                    : "an export would be queued for ";
                var target = systemName != null
                    ? new SummarySegment.Entity(systemName,
                        systemId.HasValue ? JimUtilities.GetConnectedSystemHref(systemId.Value) : null,
                        CausalityEntityKind.ConnectedSystem)
                    : (SummarySegment)new SummarySegment.Text("a downstream system");
                clauses.Add([new SummarySegment.Text(countText), target]);
            }
            else
            {
                clauses.Add([new SummarySegment.Text(
                    $"exports of {changeCount} change{(changeCount == 1 ? string.Empty : "s")} would be queued for {systems.Count} systems")]);
            }
        }

        return clauses;
    }

    /// <summary>
    /// The single clause for an object that left scope under RemainJoined and kept its join (#1649): it names the
    /// scoping rule and the Metaverse Object the join was kept to, falling back to unnamed mentions for either
    /// where the outcome carries none. One clause rather than two, because the scope exit and the kept join are
    /// one decision, and the sentence joiner's ", and" would otherwise read them as separate events.
    /// </summary>
    private static List<SummarySegment> BuildRetainedJoinClause(CausalityEvent retainedJoin, bool isSpeculative)
    {
        var clause = new List<SummarySegment>();

        var rule = retainedJoin.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.SynchronisationRule);
        if (rule != null)
        {
            clause.Add(new SummarySegment.Text(isSpeculative
                ? "it would leave the scope of Synchronisation Rule "
                : "it left the scope of Synchronisation Rule "));
            clause.Add(new SummarySegment.Entity(rule.Label, rule.Href, CausalityEntityKind.SynchronisationRule));
        }
        else
        {
            clause.Add(new SummarySegment.Text(isSpeculative
                ? "it would leave the scope of its Synchronisation Rule"
                : "it left the scope of its Synchronisation Rule"));
        }

        var keep = isSpeculative ? "keep" : "kept";
        var identity = retainedJoin.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
        if (identity != null)
        {
            clause.Add(new SummarySegment.Text($" and {keep} its join to the Metaverse Object "));
            clause.Add(new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity));
        }
        else
        {
            clause.Add(new SummarySegment.Text($" and {keep} its Metaverse Object join"));
        }

        return clause;
    }

    private static List<List<SummarySegment>> BuildSpeculativeLeaverClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        var clauses = new List<List<SummarySegment>>();

        var outOfScope = allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope);
        if (outOfScope != null)
        {
            var rule = outOfScope.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.SynchronisationRule);
            clauses.Add(rule != null
                ? [
                    new SummarySegment.Text("it would leave the scope of Synchronisation Rule "),
                    new SummarySegment.Entity(rule.Label, rule.Href, CausalityEntityKind.SynchronisationRule)
                ]
                : [new SummarySegment.Text("it would leave the scope of its Synchronisation Rule")]);
        }

        var mvoDeleted = allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        if (mvoDeleted != null)
        {
            // Unlike the recorded leaver clause, the Identity still exists (nothing is actually
            // deleted): its mention links the live object via the event's own link, never a
            // deletion record.
            var identity = mvoDeleted.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
            clauses.Add(identity != null
                ? [
                    new SummarySegment.Text("the Metaverse Object "),
                    new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity),
                    new SummarySegment.Text(" would be deleted")
                ]
                : [new SummarySegment.Text("the Metaverse Object would be deleted")]);
        }
        else
        {
            var deletionScheduled = allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled);
            if (deletionScheduled != null)
            {
                var identity = deletionScheduled.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
                clauses.Add(identity != null
                    ? [
                        new SummarySegment.Text("the Metaverse Object "),
                        new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity),
                        new SummarySegment.Text(" would be scheduled for deletion")
                    ]
                    : [new SummarySegment.Text("the Metaverse Object would be scheduled for deletion")]);
            }
        }

        var deprovisionSystemCount = allEvents
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued)
            .Select(e => (e.SystemId, e.SystemName))
            .Distinct()
            .Count();
        if (deprovisionSystemCount > 0)
        {
            clauses.Add([new SummarySegment.Text(
                $"deprovisioning would be queued for {deprovisionSystemCount} system{(deprovisionSystemCount == 1 ? string.Empty : "s")}")]);
        }

        // Provisioning cancellations are counted separately from deprovisioning: nothing
        // would be staged for these systems at all, since nothing had ever been exported to them.
        var provisioningCancelledSystemCount = allEvents
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled)
            .Select(e => (e.SystemId, e.SystemName))
            .Distinct()
            .Count();
        if (provisioningCancelledSystemCount > 0)
        {
            clauses.Add([new SummarySegment.Text(
                $"provisioning would be cancelled for {provisioningCancelledSystemCount} " +
                $"system{(provisioningCancelledSystemCount == 1 ? string.Empty : "s")}, since nothing had been exported yet")]);
        }

        if (clauses.Count == 0)
            return BuildGenericFallbackClauses(allEvents);

        return clauses;
    }

    private static List<List<SummarySegment>> BuildJoinerClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        var clauses = new List<List<SummarySegment>>();

        if (allEvents.Any(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected))
        {
            clauses.Add([new SummarySegment.Text("a new Metaverse Object was projected")]);
        }
        else
        {
            var joined = allEvents.First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Joined);
            var identity = joined.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
            if (identity != null)
            {
                clauses.Add([
                    new SummarySegment.Text("it was joined to the Metaverse Object "),
                    new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity)
                ]);
            }
            else
            {
                clauses.Add([new SummarySegment.Text("it was joined to an existing Metaverse Object")]);
            }
        }

        var deletionCancelledClause = BuildDeletionCancelledClause(allEvents);
        if (deletionCancelledClause != null)
            clauses.Add(deletionCancelledClause);

        var attributeFlowClause = BuildAttributeFlowClause(allEvents);
        if (attributeFlowClause != null)
            clauses.Add(attributeFlowClause);

        var exportClause = BuildQueuedExportClause(allEvents);
        if (exportClause != null)
            clauses.Add(exportClause);

        return clauses;
    }

    /// <summary>
    /// The clause stating a scheduled grace-period deletion was cancelled by this join (#1620), when the
    /// join carries an MvoDeletionCancelled child outcome.
    /// </summary>
    private static List<SummarySegment>? BuildDeletionCancelledClause(IReadOnlyList<CausalityEvent> allEvents)
    {
        var cancelled = allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled);
        return cancelled == null
            ? null
            : [new SummarySegment.Text("its scheduled deletion was cancelled")];
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
                        new SummarySegment.Text("it was disconnected from the Metaverse Object "),
                        new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity)
                    ]);
                }
                else
                {
                    clauses.Add([new SummarySegment.Text("it was disconnected from its Metaverse Object")]);
                }
            }
        }

        var mvoDeleted = allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        if (mvoDeleted != null)
        {
            var identityName = mvoDeleted.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity)?.Label;
            if (identityName != null)
            {
                // The Identity no longer exists, so its mention links the durable deletion record instead.
                // Reuse the event's own deletion-record href rather than rebuilding one: the two mentions
                // are the same object, and a summary that landed somewhere else than the event beneath it
                // would be its own small lie.
                var deletionRecordHref = mvoDeleted.Links
                    .FirstOrDefault(l => l.Kind == CausalityEntityKind.DeletionRecord)?.Href
                    ?? CausalityModelBuilder.GetDeletedMvoHref(null);

                clauses.Add([
                    new SummarySegment.Text("the Metaverse Object "),
                    new SummarySegment.Entity(identityName, deletionRecordHref, CausalityEntityKind.Identity),
                    new SummarySegment.Text(" was deleted")
                ]);
            }
            else
            {
                clauses.Add([new SummarySegment.Text("the Metaverse Object was deleted")]);
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
                        new SummarySegment.Text("the Metaverse Object "),
                        new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity),
                        new SummarySegment.Text(" was scheduled for deletion")
                    ]);
                }
                else
                {
                    clauses.Add([new SummarySegment.Text("the Metaverse Object was scheduled for deletion")]);
                }
            }
        }

        // DeprovisionQueued (staged) and Deprovisioned (written). Before DeprovisionQueued existed this had
        // to accept every PendingExportCreated on a deletion item, which over-counted any export that was
        // merely an attribute update caused by the same deletion (a group's membership recall, say).
        var deprovisionSystemCount = allEvents
            .Where(e => e.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued
                or ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned)
            .Select(e => (e.SystemId, e.SystemName))
            .Distinct()
            .Count();
        if (deprovisionSystemCount > 0)
        {
            clauses.Add([new SummarySegment.Text(
                $"deprovisioning is now queued for {deprovisionSystemCount} system{(deprovisionSystemCount == 1 ? string.Empty : "s")}")]);
        }

        // Provisioning cancellations are counted separately from deprovisioning: nothing was
        // staged for these systems at all, since nothing had ever been exported to them.
        var provisioningCancelledSystemCount = allEvents
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled)
            .Select(e => (e.SystemId, e.SystemName))
            .Distinct()
            .Count();
        if (provisioningCancelledSystemCount > 0)
        {
            clauses.Add([new SummarySegment.Text(
                $"provisioning was cancelled for {provisioningCancelledSystemCount} " +
                $"system{(provisioningCancelledSystemCount == 1 ? string.Empty : "s")}, since nothing had been exported yet")]);
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
        return allEvents
            .Select(e => e.Label)
            .Distinct()
            .Select(label => (List<SummarySegment>)[new SummarySegment.Text(label)])
            .ToList();
    }

    /// <summary>
    /// Builds the outcome pill strip: one pill per outcome type present, in first-seen tree order,
    /// annotated with counts where they aid comprehension.
    /// </summary>
    private static List<CausalityPill> BuildPills(IReadOnlyList<CausalityEvent> allEvents)
    {
        var eventsByType = new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, List<CausalityEvent>>();
        var typeOrder = new List<ActivityRunProfileExecutionItemSyncOutcomeType>();

        // Synthetic events are excluded: the strip counts what the run recorded, and a synthetic event stands
        // for something it decided not to do. A "1 Metaverse Object not deleted" pill would read as an outcome.
        foreach (var causalityEvent in allEvents.Where(e => e.OutcomeType.HasValue))
        {
            var outcomeType = causalityEvent.OutcomeType!.Value;
            if (!eventsByType.TryGetValue(outcomeType, out var eventsForType))
            {
                eventsForType = [];
                eventsByType[outcomeType] = eventsForType;
                typeOrder.Add(outcomeType);
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
            // Both deprovisioning pills count systems, never changes: a delete Pending Export's only attribute
            // row is the target's own identifier, so "1 change" would be a meaningless number to show.
            ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued =>
                $"Deprovision queued · {systemCount} system{(systemCount == 1 ? string.Empty : "s")}",
            ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned =>
                $"Deprovisioning · {systemCount} system{(systemCount == 1 ? string.Empty : "s")}",
            // Same reasoning as the deprovisioning pills above: counts systems, never changes, since a
            // cancellation carries no attribute changes at all (nothing was ever exported).
            ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled =>
                $"Provisioning cancelled · {systemCount} system{(systemCount == 1 ? string.Empty : "s")}",
            ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope => "Out of scope",
            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled => "Deletion scheduled",
            _ => display.Label
        };

        return new CausalityPill(label, display.Tone);
    }
}
