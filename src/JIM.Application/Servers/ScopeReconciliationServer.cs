// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Sync;
using JIM.Utilities;
using Serilog;
namespace JIM.Application.Servers;

/// <summary>
/// The Temporal Scope Reconciler (issue #892). Detects relative-date scope transitions that the sync and export
/// hot paths never observe, because both skip objects whose source data has not changed before scoping is
/// evaluated. A leaver whose end date passes, or a joiner whose start date arrives, has static data yet must
/// change scope as the clock crosses its boundary.
///
/// This server holds repository access (unlike the pure, dependency-free <see cref="ScopingEvaluationServer"/>,
/// which it calls) and runs out of band on a schedule. For each enabled Synchronisation Rule carrying a
/// relative-date scoping criterion it: computes the candidate value window, fetches candidates via the indexed
/// pre-filter, evaluates full scope in memory, and flags any object whose fresh scope disagrees with its stored
/// connection state. Flagging is all it does (flag-and-delegate): the existing engine, let past its
/// unchanged-skip by the flag, applies the real outcome (project, join, Attribute Flow, disconnect, delete,
/// provision, deprovision, including matching-rule joins and bidirectional cascades).
/// </summary>
public class ScopeReconciliationServer
{
    private JimApplication Application { get; }
    private ScopingEvaluationServer ScopingEvaluation { get; }

    internal ScopeReconciliationServer(JimApplication application)
    {
        Application = application;
        ScopingEvaluation = new ScopingEvaluationServer();
    }

    /// <summary>
    /// Runs one reconciliation sweep across all enabled Synchronisation Rules that carry a relative-date scoping
    /// criterion, flagging objects whose time-driven scope membership has flipped.
    /// </summary>
    /// <param name="afterUtc">The previous sweep's instant (the watermark); null for a bootstrap sweep with no
    /// lower bound, which considers every already-transitioned object once.</param>
    /// <param name="nowUtc">The current instant, in UTC. Passed in so the sweep is deterministic and testable.</param>
    public async Task<ScopeReconciliationResult> ReconcileAsync(DateTime? afterUtc, DateTime nowUtc)
    {
        var result = new ScopeReconciliationResult();
        var rules = await Application.Repository.ConnectedSystems.GetSyncRulesAsync(withChangeTracking: false);

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            var relativeCriteria = EnumerateRelativeDateCriteria(rule).ToList();
            if (relativeCriteria.Count == 0)
                continue;

            result.RulesEvaluated++;

            try
            {
                // Union the candidate IDs across every relative-date criterion on the rule. The window is
                // operator-agnostic: it identifies the objects whose boundary the clock crossed; the in-memory
                // evaluation below makes the actual in/out-of-scope decision.
                var candidateIds = new HashSet<Guid>();
                foreach (var (attributeId, count, unit, direction) in relativeCriteria)
                {
                    var (lower, upper) = RelativeDateScopeWindow.Resolve(count, unit, direction, afterUtc, nowUtc);
                    if (rule.Direction == SyncRuleDirection.Import)
                        candidateIds.UnionWith(await Application.Repository.ConnectedSystems.GetConnectedSystemObjectIdsByDateAttributeRangeAsync(attributeId, lower, upper));
                    else
                        candidateIds.UnionWith(await Application.Repository.Metaverse.GetMetaverseObjectIdsByDateAttributeRangeAsync(rule.MetaverseObjectTypeId, attributeId, lower, upper));
                }

                if (candidateIds.Count == 0)
                    continue;

                if (rule.Direction == SyncRuleDirection.Import)
                    await ReconcileInboundAsync(rule, candidateIds, nowUtc, result);
                else
                    await ReconcileOutboundAsync(rule, candidateIds, nowUtc, result);
            }
            catch (InvalidOperationException ex)
            {
                // A criterion with an operator invalid for its attribute type (which should never persist) makes
                // the evaluator throw. Fail this rule loudly but keep reconciling the others rather than aborting
                // the whole sweep.
                Log.Error(ex, "ScopeReconciliation: skipping Synchronisation Rule {RuleId} ({RuleName}) due to an invalid scoping criterion", rule.Id, LogSanitiser.Sanitise(rule.Name));
            }
        }

        Log.Information(
            "ScopeReconciliation sweep complete: {RulesEvaluated} rule(s) reconciled, {InboundEvaluated} inbound candidate(s) evaluated ({InboundFlagged} flagged), {OutboundEvaluated} outbound candidate(s) evaluated ({OutboundFlagged} flagged)",
            result.RulesEvaluated, result.InboundCandidatesEvaluated, result.InboundFlagged, result.OutboundCandidatesEvaluated, result.OutboundFlagged);

        return result;
    }

    private async Task ReconcileInboundAsync(SyncRule rule, HashSet<Guid> candidateIds, DateTime nowUtc, ScopeReconciliationResult result)
    {
        var csos = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsByIdsNoTrackingAsync(rule.ConnectedSystemId, candidateIds);
        var flagged = new List<Guid>();

        foreach (var cso in csos)
        {
            var inScope = ScopingEvaluation.IsCsoInScopeForImportRule(cso, rule, nowUtc);
            // FK scalar, not the navigation: the CSO is joined (connected) when it points at a Metaverse Object.
            var connected = cso.MetaverseObjectId.HasValue;
            if (inScope != connected)
                flagged.Add(cso.Id);
        }

        var evaluatedIds = csos.Select(c => c.Id).ToList();
        await Application.Repository.ConnectedSystems.MarkConnectedSystemObjectsScopeEvaluatedAsync(evaluatedIds, flagged, nowUtc);

        result.InboundCandidatesEvaluated += evaluatedIds.Count;
        result.FlaggedConnectedSystemObjectIds.AddRange(flagged);
    }

    private async Task ReconcileOutboundAsync(SyncRule rule, HashSet<Guid> candidateIds, DateTime nowUtc, ScopeReconciliationResult result)
    {
        var mvos = await Application.Repository.Metaverse.GetMetaverseObjectsByIdsNoTrackingAsync(candidateIds);
        var flagged = new List<Guid>();

        foreach (var mvo in mvos)
        {
            var inScope = ScopingEvaluation.IsMvoInScopeForExportRule(mvo, rule, nowUtc);
            // The same "already provisioned to the target system?" check the export evaluator uses to deprovision.
            var provisioned = await Application.SyncRepo.GetConnectedSystemObjectByMetaverseObjectIdAsync(mvo.Id, rule.ConnectedSystemId) != null;
            if (inScope != provisioned)
                flagged.Add(mvo.Id);
        }

        var evaluatedIds = mvos.Select(m => m.Id).ToList();
        await Application.Repository.Metaverse.MarkMetaverseObjectsScopeEvaluatedAsync(evaluatedIds, flagged, nowUtc);

        result.OutboundCandidatesEvaluated += evaluatedIds.Count;
        result.FlaggedMetaverseObjectIds.AddRange(flagged);
    }

    /// <summary>
    /// Yields each complete relative-date criterion in a rule's scoping group tree as
    /// <c>(AttributeId, Count, Unit, Direction)</c>, resolving the attribute ID for the rule's direction
    /// (Connected System Object Type Attribute for import, Metaverse Attribute for export). Absolute-date and
    /// incomplete relative criteria are skipped: they do not drift with the clock.
    /// </summary>
    internal static IEnumerable<(int AttributeId, int Count, RelativeDateUnit Unit, RelativeDateDirection Direction)> EnumerateRelativeDateCriteria(SyncRule rule)
    {
        return rule.ObjectScopingCriteriaGroups.SelectMany(group => EnumerateGroup(group, rule.Direction));
    }

    private static IEnumerable<(int AttributeId, int Count, RelativeDateUnit Unit, RelativeDateDirection Direction)> EnumerateGroup(SyncRuleScopingCriteriaGroup group, SyncRuleDirection direction)
    {
        var completeRelativeCriteria = group.Criteria
            .Where(c => c.ValueMode == DateCriteriaValueMode.Relative &&
                        c.RelativeCount.HasValue && c.RelativeUnit.HasValue && c.RelativeDirection.HasValue)
            .Select(c => (Criterion: c, AttributeId: direction == SyncRuleDirection.Import ? c.ConnectedSystemAttributeId : c.MetaverseAttributeId))
            .Where(pair => pair.AttributeId.HasValue)
            .Select(pair => (pair.AttributeId!.Value, pair.Criterion.RelativeCount!.Value, pair.Criterion.RelativeUnit!.Value, pair.Criterion.RelativeDirection!.Value));

        return completeRelativeCriteria.Concat(group.ChildGroups.SelectMany(child => EnumerateGroup(child, direction)));
    }
}
