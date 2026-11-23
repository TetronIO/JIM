// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using JIM.Models.Activities;
using JIM.Web.Localisation;
using JIM.Web.Resources;
using JimUtilities = JIM.Utilities.Utilities;

namespace JIM.Web.Causality;

/// <summary>
/// Builds the summary band content from a <see cref="CausalityModel"/>: one plain-English sentence
/// describing what happened and what it caused (as segments, never pre-rendered HTML), plus the
/// colour-coded outcome pill strip. Sentence templates are keyed on the dominant outcome shape
/// (projection/join, out-of-scope/deletion, export attempt/failure, no change), with a generic
/// fallback that always produces a valid sentence.
/// </summary>
public static partial class CausalitySummaryBuilder
{
    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>
    /// Fills a whole-sentence template's placeholders with segments, so an entity can sit anywhere in the sentence a
    /// language needs it. A string or number argument becomes plain text; a segment or a list of segments is inserted
    /// as it is.
    /// </summary>
    private static List<SummarySegment> Compose(string template, params object[] arguments)
    {
        var segments = new List<SummarySegment>();
        var pending = new System.Text.StringBuilder();

        void FlushText()
        {
            if (pending.Length == 0)
                return;
            segments.Add(new SummarySegment.Text(pending.ToString()));
            pending.Clear();
        }

        var position = 0;
        foreach (var match in PlaceholderPattern().Matches(template).Cast<Match>())
        {
            pending.Append(template, position, match.Index - position);
            position = match.Index + match.Length;

            switch (arguments[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)])
            {
                case SummarySegment segment:
                    FlushText();
                    segments.Add(segment);
                    break;
                case IEnumerable<SummarySegment> list:
                    FlushText();
                    segments.AddRange(list);
                    break;
                case IFormattable formattable:
                    pending.Append(formattable.ToString(null, CultureInfo.CurrentCulture));
                    break;
                case var other:
                    pending.Append(other);
                    break;
            }
        }

        pending.Append(template, position, template.Length - position);
        FlushText();
        return segments;
    }

    private static List<SummarySegment> Clause(string template, params object[] arguments) => Compose(template, arguments);

    private static List<SummarySegment> ConnectedSystemTarget(string? systemName, int? systemId) =>
        systemName != null
            ? [new SummarySegment.Entity(systemName,
                systemId.HasValue ? JimUtilities.GetConnectedSystemHref(systemId.Value) : null,
                CausalityEntityKind.ConnectedSystem)]
            : [new SummarySegment.Text(CausalityText.SummaryDownstreamSystem)];

    /// <summary>
    /// Builds the summary for a causality model. Never throws for missing or legacy data.
    /// </summary>
    public static CausalitySummary Build(CausalityModel model)
    {
        var allEvents = model.AllEvents().ToList();

        var opening = BuildOpening(model.Context, model.IsSpeculative);
        var heldForDecision = model.ItemErrorType == ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved;
        List<List<SummarySegment>> clauses = heldForDecision
            ? [[new SummarySegment.Text(CausalityText.SummaryHeldForDecision)]]
            : model.IsSpeculative ? BuildSpeculativeClauses(allEvents) : BuildClauses(allEvents);
        var segments = Compose(CausalityText.SummarySentence, opening, JoinClauseItems(clauses));

        var pills = BuildPills(allEvents);
        if (heldForDecision)
            pills.Add(new CausalityPill(CausalityText.PillNeedsDecision, CausalityTone.Error));

        return new CausalitySummary
        {
            Segments = segments,
            Pills = pills
        };
    }

    private static List<SummarySegment> BuildOpening(CausalityPageContext context, bool isSpeculative)
    {
        List<SummarySegment> subject = !string.IsNullOrWhiteSpace(context.RunProfileName)
            ? Compose(
                StartsWithVowel(context.RunProfileName) ? CausalityText.SummaryRunProfileAn : CausalityText.SummaryRunProfileA,
                new SummarySegment.Entity(context.RunProfileName, null, CausalityEntityKind.RunProfile))
            : [new SummarySegment.Text(CausalityText.SummaryRun)];

        if (!string.IsNullOrWhiteSpace(context.ConnectedSystemName))
        {
            subject = Compose(CausalityText.SummarySubjectOnSystem, subject, new SummarySegment.Entity(
                context.ConnectedSystemName,
                context.ConnectedSystemId.HasValue ? JimUtilities.GetConnectedSystemHref(context.ConnectedSystemId.Value) : null,
                CausalityEntityKind.ConnectedSystem));
        }

        var recordLabel = ObjectDescription.ChipName(context.CsoDisplayName, context.CsoExternalId);
        // Conditional mood throughout for a preview (#1519, D-S9): nothing here has happened yet.

        // An item that records a Metaverse Object (an export scope review) is about that object, not a record (#1982).
        if (context.SubjectMetaverseObjectId != null)
        {
            if (string.IsNullOrWhiteSpace(context.SubjectMetaverseObjectName))
            {
                return Compose(isSpeculative ? CausalityText.SummaryWouldProcessMetaverseObject : CausalityText.SummaryProcessedMetaverseObject, subject);
            }

            var subjectEntity = new SummarySegment.Entity(context.SubjectMetaverseObjectName, context.SubjectMetaverseObjectHref,
                CausalityEntityKind.Identity);
            return !string.IsNullOrWhiteSpace(context.MvoTypeName)
                ? Compose(isSpeculative ? CausalityText.SummaryWouldProcessTypedObject : CausalityText.SummaryProcessedTypedObject,
                    subject, context.MvoTypeName, subjectEntity)
                : Compose(isSpeculative ? CausalityText.SummaryWouldProcessObject : CausalityText.SummaryProcessedObject, subject, subjectEntity);
        }

        if (recordLabel != null)
        {
            // Named by its object type where the builder knows it ("processed person Baseline User"), so
            // the sentence states what kind of object this is without a second clause; otherwise the name
            // alone carries it ("processed Baseline User").
            // The record's own Connected System, not the run's: they diverge for cross-system
            // cascades, and linking with the wrong system id 404s (ConnectedSystemObjectDetail looks
            // the record up by {connectedSystemId}+{id}).
            var recordHref = context.CsoConnectedSystemId is { } recordSystemId && context.CsoId is { } recordId
                ? context.Links.ConnectedSystemObjectHref(recordSystemId, recordId)
                : null;
            var recordEntity = new SummarySegment.Entity(recordLabel, recordHref, CausalityEntityKind.Record);
            return !string.IsNullOrWhiteSpace(context.CsoObjectTypeName)
                ? Compose(isSpeculative ? CausalityText.SummaryWouldProcessTypedObject : CausalityText.SummaryProcessedTypedObject,
                    subject, context.CsoObjectTypeName, recordEntity)
                : Compose(isSpeculative ? CausalityText.SummaryWouldProcessObject : CausalityText.SummaryProcessedObject, subject, recordEntity);
        }

        return Compose(isSpeculative ? CausalityText.SummaryWouldProcessConnectedSystemObject : CausalityText.SummaryProcessedConnectedSystemObject, subject);
    }

    private static bool StartsWithVowel(string value)
    {
        return value.Length > 0 && "AEIOUaeiou".Contains(value[0]);
    }

    /// <summary>
    /// The sentence's list conjunction rule: items joined by ", ", the last joined by ", and ". Every clause,
    /// generated-value clauses included (<see cref="BuildGeneratedValueClauses"/>), goes through this once, so a
    /// sentence never carries a second "and".
    /// </summary>
    private static List<SummarySegment> JoinClauseItems(List<List<SummarySegment>> items)
    {
        var segments = new List<SummarySegment>();
        for (var i = 0; i < items.Count; i++)
        {
            segments = i == 0
                ? [.. items[i]]
                : Compose(i == items.Count - 1 ? CausalityText.SummaryListJoinLast : CausalityText.SummaryListJoin, segments, items[i]);
        }

        return segments;
    }

    /// <summary>
    /// Builds the result clauses for the dominant outcome shape.
    /// </summary>
    private static List<List<SummarySegment>> BuildClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        // Unique Value Generation (#242, release 4): an export Collision Remediation corrected, or held for a decision,
        // wrote nothing this run; say what became of the generated value instead.
        if (allEvents.Any(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated))
            return [[new SummarySegment.Text(CausalityText.SummaryGeneratedValueCorrected)]];

        if (allEvents.Count == 0)
            return [[new SummarySegment.Text(CausalityText.SummaryNoChangesWereNeeded)]];

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

        return BuildGenericFallbackClauses(allEvents, isSpeculative: false);
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
            return [[new SummarySegment.Text(CausalityText.SummaryNoChangesAreNeeded)]];

        if (allEvents.Any(e => e.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.Projected
                or ActivityRunProfileExecutionItemSyncOutcomeType.Joined))
            return BuildSpeculativeJoinerClauses(allEvents);

        if (allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin) is { } retainedJoin)
            return [BuildRetainedJoinClause(retainedJoin, isSpeculative: true)];

        if (allEvents.Any(e => e.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope
                or ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
                or ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled))
            return BuildSpeculativeLeaverClauses(allEvents);

        return BuildGenericFallbackClauses(allEvents, isSpeculative: true);
    }

    private static List<List<SummarySegment>> BuildSpeculativeJoinerClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        var clauses = new List<List<SummarySegment>>();

        if (allEvents.Any(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected))
        {
            clauses.Add([new SummarySegment.Text(CausalityText.SummaryWouldProject)]);
        }
        else
        {
            var joined = allEvents.First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Joined);
            var identity = joined.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
            clauses.Add(identity != null
                ? Clause(CausalityText.SummaryWouldJoinTo, new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity))
                : [new SummarySegment.Text(CausalityText.SummaryWouldJoinExisting)]);
        }

        var attributeFlows = allEvents.Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow).ToList();
        if (attributeFlows.Count > 0)
        {
            var flowedCount = attributeFlows.Sum(e => e.DetailCount ?? 0);
            clauses.Add(flowedCount > 0
                ? Clause(PluralRules.Choose(flowedCount, CausalityText.SummaryWouldFlowOne, CausalityText.SummaryWouldFlowOther), flowedCount)
                : [new SummarySegment.Text(CausalityText.SummaryWouldFlowUnknown)]);
        }

        clauses.AddRange(BuildGeneratedValueClauses(allEvents, isSpeculative: true));

        var queuedExports = allEvents.Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated).ToList();
        if (queuedExports.Count > 0)
        {
            var changeCount = queuedExports.Sum(e => e.DetailCount ?? 0);
            var systems = queuedExports.Select(e => (e.SystemId, e.SystemName)).Distinct().ToList();
            if (systems.Count == 1)
            {
                var (systemId, systemName) = systems[0];
                var target = ConnectedSystemTarget(systemName, systemId);
                clauses.Add(changeCount > 0
                    ? Clause(PluralRules.Choose(changeCount, CausalityText.SummaryWouldQueueExportOne, CausalityText.SummaryWouldQueueExportOther), changeCount, target)
                    : Clause(CausalityText.SummaryWouldQueueExportUnknown, target));
            }
            else
            {
                clauses.Add(Clause(
                    PluralRules.Choose(changeCount, CausalityText.SummaryWouldQueueExportsOne, CausalityText.SummaryWouldQueueExportsOther),
                    changeCount, systems.Count));
            }
        }

        if (BuildWithdrawnChangesClause(allEvents, isSpeculative: true) is { } withdrawnClause)
            clauses.Add(withdrawnClause);

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
        var scopeExit = rule != null
            ? Compose(isSpeculative ? CausalityText.SummaryWouldLeaveScopeOfRule : CausalityText.SummaryLeftScopeOfRule,
                new SummarySegment.Entity(rule.Label, rule.Href, CausalityEntityKind.SynchronisationRule))
            : [new SummarySegment.Text(isSpeculative ? CausalityText.SummaryWouldLeaveScopeOfItsRule : CausalityText.SummaryLeftScopeOfItsRule)];

        var identity = retainedJoin.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
        var keptJoin = identity != null
            ? Compose(isSpeculative ? CausalityText.SummaryWouldKeepJoinTo : CausalityText.SummaryKeptJoinTo,
                new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity))
            : [new SummarySegment.Text(isSpeculative ? CausalityText.SummaryWouldKeepJoin : CausalityText.SummaryKeptJoin)];

        clause.AddRange(Compose(CausalityText.SummaryRetainedJoinJoin, scopeExit, keptJoin));
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
                ? Clause(CausalityText.SummaryWouldLeaveScopeOfRule, new SummarySegment.Entity(rule.Label, rule.Href, CausalityEntityKind.SynchronisationRule))
                : [new SummarySegment.Text(CausalityText.SummaryWouldLeaveScopeOfItsRule)]);
        }

        var mvoDeleted = allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        if (mvoDeleted != null)
        {
            // Unlike the recorded leaver clause, the Identity still exists (nothing is actually
            // deleted): its mention links the live object via the event's own link, never a
            // deletion record.
            var identity = mvoDeleted.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
            clauses.Add(identity != null
                ? Clause(CausalityText.SummaryMvoWouldBeDeleted, new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity))
                : [new SummarySegment.Text(CausalityText.SummaryMvoWouldBeDeletedUnnamed)]);
        }
        else
        {
            var deletionScheduled = allEvents.FirstOrDefault(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled);
            if (deletionScheduled != null)
            {
                var identity = deletionScheduled.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
                clauses.Add(identity != null
                    ? Clause(CausalityText.SummaryMvoWouldBeScheduled, new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity))
                    : [new SummarySegment.Text(CausalityText.SummaryMvoWouldBeScheduledUnnamed)]);
            }
        }

        var deprovisionSystemCount = allEvents
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued)
            .Select(e => (e.SystemId, e.SystemName))
            .Distinct()
            .Count();
        if (deprovisionSystemCount > 0)
        {
            clauses.Add(Clause(PluralRules.Choose(deprovisionSystemCount, CausalityText.SummaryWouldQueueDeprovisionOne, CausalityText.SummaryWouldQueueDeprovisionOther), deprovisionSystemCount));
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
            clauses.Add(Clause(PluralRules.Choose(provisioningCancelledSystemCount, CausalityText.SummaryWouldCancelProvisioningOne, CausalityText.SummaryWouldCancelProvisioningOther), provisioningCancelledSystemCount));
        }

        if (clauses.Count == 0)
            return BuildGenericFallbackClauses(allEvents, isSpeculative: true);

        return clauses;
    }

    private static List<List<SummarySegment>> BuildJoinerClauses(IReadOnlyList<CausalityEvent> allEvents)
    {
        var clauses = new List<List<SummarySegment>>();

        if (allEvents.Any(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected))
        {
            clauses.Add([new SummarySegment.Text(CausalityText.SummaryProjected)]);
        }
        else
        {
            var joined = allEvents.First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Joined);
            var identity = joined.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity);
            if (identity != null)
            {
                clauses.Add(Clause(CausalityText.SummaryJoinedTo, new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity)));
            }
            else
            {
                clauses.Add([new SummarySegment.Text(CausalityText.SummaryJoinedExisting)]);
            }
        }

        var deletionCancelledClause = BuildDeletionCancelledClause(allEvents);
        if (deletionCancelledClause != null)
            clauses.Add(deletionCancelledClause);

        var attributeFlowClause = BuildAttributeFlowClause(allEvents);
        if (attributeFlowClause != null)
            clauses.Add(attributeFlowClause);

        clauses.AddRange(BuildGeneratedValueClauses(allEvents, isSpeculative: false));

        var exportClause = BuildQueuedExportClause(allEvents);
        if (exportClause != null)
            clauses.Add(exportClause);

        if (BuildWithdrawnChangesClause(allEvents, isSpeculative: false) is { } withdrawnClause)
            clauses.Add(withdrawnClause);

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
            : [new SummarySegment.Text(CausalityText.SummaryDeletionCancelled)];
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
            ? Clause(PluralRules.Choose(flowedCount, CausalityText.SummaryFlowedOne, CausalityText.SummaryFlowedOther), flowedCount)
            : [new SummarySegment.Text(CausalityText.SummaryFlowedUnknown)];
    }

    /// <summary>
    /// Unique Value Generation (#242): one clause per attribute a value was generated for on this
    /// pass ("Account Name was generated as jallen42"), for the caller to add to the sentence's clause list.
    /// Empty when the item recorded no such outcome.
    /// </summary>
    private static List<List<SummarySegment>> BuildGeneratedValueClauses(IReadOnlyList<CausalityEvent> allEvents, bool isSpeculative)
    {
        // One clause per generated attribute, returned for the caller to add to the sentence's own clause list:
        // joining them into a single clause first would give the sentence two "and"s ("..., and Account Name was
        // generated as ..., and Badge Number was generated as ...").
        return allEvents
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned)
            .Select(e => BuildGeneratedValueItem(e, isSpeculative))
            .ToList();
    }

    /// <summary>
    /// One event's clause within <see cref="BuildGeneratedValueClauses"/>. The attribute name and value come
    /// from the outcome's DetailMessage (<see cref="GeneratedValueDetailParser"/>); a missing or malformed
    /// detail (legacy data, or a caller that never populated it) falls back to a generic sentence rather than
    /// rendering a blank attribute name or value.
    /// </summary>
    private static List<SummarySegment> BuildGeneratedValueItem(CausalityEvent causalityEvent, bool isSpeculative)
    {
        var detail = GeneratedValueDetailParser.Parse(causalityEvent.DetailMessage);

        if (detail.AttributeName == null || detail.Value == null)
            return [new SummarySegment.Text(isSpeculative ? CausalityText.SummaryWouldGenerateValue : CausalityText.SummaryGeneratedValue)];

        return Compose(
            isSpeculative ? CausalityText.SummaryWouldGenerateAs : CausalityText.SummaryGeneratedAs,
            detail.AttributeName,
            new SummarySegment.LiteralValue(detail.Value));
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
            var target = ConnectedSystemTarget(systemName, systemId);
            return changeCount > 0
                ? Clause(PluralRules.Choose(changeCount, CausalityText.SummaryQueuedExportOne, CausalityText.SummaryQueuedExportOther), changeCount, target)
                : Clause(CausalityText.SummaryQueuedExportUnknown, target);
        }

        return Clause(
            PluralRules.Choose(changeCount, CausalityText.SummaryQueuedExportsOne, CausalityText.SummaryQueuedExportsOther),
            changeCount, systems.Count);
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
                clauses.Add(Clause(CausalityText.SummaryLeftScopeOfRule, new SummarySegment.Entity(rule.Label, rule.Href, CausalityEntityKind.SynchronisationRule)));
            }
            else
            {
                clauses.Add([new SummarySegment.Text(CausalityText.SummaryLeftScopeOfItsRule)]);
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
                    clauses.Add(Clause(CausalityText.SummaryDisconnectedFromMvo, new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity)));
                }
                else
                {
                    clauses.Add([new SummarySegment.Text(CausalityText.SummaryDisconnectedFromItsMvo)]);
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

                clauses.Add(Clause(CausalityText.SummaryMvoWasDeleted, new SummarySegment.Entity(identityName, deletionRecordHref, CausalityEntityKind.Identity)));
            }
            else
            {
                clauses.Add([new SummarySegment.Text(CausalityText.SummaryMvoWasDeletedUnnamed)]);
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
                    clauses.Add(Clause(CausalityText.SummaryMvoWasScheduled, new SummarySegment.Entity(identity.Label, identity.Href, CausalityEntityKind.Identity)));
                }
                else
                {
                    clauses.Add([new SummarySegment.Text(CausalityText.SummaryMvoWasScheduledUnnamed)]);
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
            clauses.Add(Clause(PluralRules.Choose(deprovisionSystemCount, CausalityText.SummaryDeprovisioningQueuedOne, CausalityText.SummaryDeprovisioningQueuedOther), deprovisionSystemCount));
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
            clauses.Add(Clause(PluralRules.Choose(provisioningCancelledSystemCount, CausalityText.SummaryProvisioningCancelledOne, CausalityText.SummaryProvisioningCancelledOther), provisioningCancelledSystemCount));
        }

        if (clauses.Count == 0)
            return BuildGenericFallbackClauses(allEvents, isSpeculative: false);

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
            ? string.Format(CultureInfo.CurrentCulture, PluralRules.Choose(attemptedCount, CausalityText.SummaryExportFailedOne, CausalityText.SummaryExportFailedOther), attemptedCount)
            : CausalityText.SummaryExportFailedUnknown;
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
                1 => string.Format(CultureInfo.CurrentCulture, CausalityText.SummaryExportedOne, exportedCount),
                > 1 => string.Format(CultureInfo.CurrentCulture, CausalityText.SummaryExportedOther, exportedCount),
                _ => CausalityText.SummaryExportedUnknown
            };
            return [[new SummarySegment.Text(clause)]];
        }

        var confirmedCount = allEvents
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed)
            .Sum(e => e.DetailCount ?? 0);
        var confirmedClause = confirmedCount switch
        {
            1 => string.Format(CultureInfo.CurrentCulture, CausalityText.SummaryConfirmedOne, confirmedCount),
            > 1 => string.Format(CultureInfo.CurrentCulture, CausalityText.SummaryConfirmedOther, confirmedCount),
            _ => CausalityText.SummaryConfirmedUnknown
        };
        return [[new SummarySegment.Text(confirmedClause)]];
    }

    /// <summary>
    /// The generic fallback: name the distinct root-level outcomes in plain language, in event order. Always
    /// yields a valid sentence for unanticipated shapes.
    /// <para>
    /// Unique Value Generation (#242): a generated-value outcome is excluded from the plain-label pass and
    /// given the same richer clause the joiner shape uses (<see cref="BuildGeneratedValueClauses"/>), inserted
    /// directly after an Attribute Flow clause where one is present (or appended, when there is none), so an
    /// Attribute-Flow-rooted item (an already-joined object whose only change this pass is a generated value)
    /// reads the same way <see cref="BuildJoinerClauses"/> does.
    /// </para>
    /// </summary>
    private static List<List<SummarySegment>> BuildGenericFallbackClauses(IReadOnlyList<CausalityEvent> allEvents, bool isSpeculative)
    {
        var clauses = new List<List<SummarySegment>>();
        var seenLabels = new HashSet<string>();
        var insertIndex = -1;

        // The filter (not a generated-value outcome, and its label not seen yet) lives in the Where clause so
        // the loop body is never guard-shaped; seenLabels.Add doubles as the predicate and the dedup record.
        // Withdrawn changes get the sentence that says where and why, below, rather than their bare label.
        foreach (var causalityEvent in allEvents.Where(e =>
            e.OutcomeType is not (ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned or ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportChangesWithdrawn)
            && seenLabels.Add(e.Label)))
        {
            clauses.Add([new SummarySegment.Text(causalityEvent.Label)]);

            if (causalityEvent.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow)
                insertIndex = clauses.Count;
        }

        clauses.InsertRange(insertIndex >= 0 ? insertIndex : clauses.Count, BuildGeneratedValueClauses(allEvents, isSpeculative));

        if (BuildWithdrawnChangesClause(allEvents, isSpeculative) is { } withdrawnClause)
            clauses.Add(withdrawnClause);

        return clauses;
    }

    /// <summary>
    /// The clause stating that changes queued for target systems were withdrawn, and why (#2001): the systems already
    /// hold the values the Metaverse now wants, so the changes never go out. Names the system where there is one, as
    /// the queued export clause does, so an administrator expecting those changes to be exported can see where they
    /// went. Null when nothing was withdrawn.
    /// </summary>
    private static List<SummarySegment>? BuildWithdrawnChangesClause(IReadOnlyList<CausalityEvent> allEvents, bool isSpeculative)
    {
        var withdrawals = allEvents.Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportChangesWithdrawn).ToList();
        if (withdrawals.Count == 0)
            return null;

        var changeCount = withdrawals.Sum(e => e.DetailCount ?? 0);
        var single = changeCount == 1;
        var systems = withdrawals.Select(e => (e.SystemId, e.SystemName)).Distinct().ToList();

        if (systems.Count == 1)
        {
            var (systemId, systemName) = systems[0];
            var target = ConnectedSystemTarget(systemName, systemId);
            var template = isSpeculative
                ? single ? CausalityText.SummaryWithdrawnOneSpec : CausalityText.SummaryWithdrawnOtherSpec
                : single ? CausalityText.SummaryWithdrawnOne : CausalityText.SummaryWithdrawnOther;
            return Clause(template, changeCount, target);
        }

        return Clause(
            isSpeculative
                ? single ? CausalityText.SummaryWithdrawnSystemsOneSpec : CausalityText.SummaryWithdrawnSystemsOtherSpec
                : single ? CausalityText.SummaryWithdrawnSystemsOne : CausalityText.SummaryWithdrawnSystemsOther,
            changeCount, systems.Count);
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
                string.Format(CultureInfo.CurrentCulture, PluralRules.Choose(detailSum, CausalityText.PillAttributesFlowedOne, CausalityText.PillAttributesFlowedOther), detailSum),
            ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned =>
                string.Format(CultureInfo.CurrentCulture, PluralRules.Choose(systemCount, CausalityText.PillProvisionedSystemsOne, CausalityText.PillProvisionedSystemsOther), systemCount),
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated when detailSum > 0 =>
                string.Format(CultureInfo.CurrentCulture, PluralRules.Choose(detailSum, CausalityText.PillExportQueuedChangesOne, CausalityText.PillExportQueuedChangesOther), detailSum),
            ActivityRunProfileExecutionItemSyncOutcomeType.Exported when detailSum > 0 =>
                string.Format(CultureInfo.CurrentCulture, PluralRules.Choose(detailSum, CausalityText.PillExportedChangesOne, CausalityText.PillExportedChangesOther), detailSum),
            // Both deprovisioning pills count systems, never changes: a delete Pending Export's only attribute
            // row is the target's own identifier, so "1 change" would be a meaningless number to show.
            ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued =>
                string.Format(CultureInfo.CurrentCulture, PluralRules.Choose(systemCount, CausalityText.PillDeprovisionQueuedSystemsOne, CausalityText.PillDeprovisionQueuedSystemsOther), systemCount),
            ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned =>
                string.Format(CultureInfo.CurrentCulture, PluralRules.Choose(systemCount, CausalityText.PillDeprovisioningSystemsOne, CausalityText.PillDeprovisioningSystemsOther), systemCount),
            // Same reasoning as the deprovisioning pills above: counts systems, never changes, since a
            // cancellation carries no attribute changes at all (nothing was ever exported).
            ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled =>
                string.Format(CultureInfo.CurrentCulture, PluralRules.Choose(systemCount, CausalityText.PillProvisioningCancelledSystemsOne, CausalityText.PillProvisioningCancelledSystemsOther), systemCount),
            ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected =>
                string.Format(CultureInfo.CurrentCulture, PluralRules.Choose(systemCount, CausalityText.PillTargetDisconnectedSystemsOne, CausalityText.PillTargetDisconnectedSystemsOther), systemCount),
            ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope => CausalityText.PillOutOfScope,
            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled => CausalityText.PillDeletionScheduled,
            _ => display.Label
        };

        return new CausalityPill(label, display.Tone);
    }
}
