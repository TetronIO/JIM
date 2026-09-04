// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using System.Runtime.CompilerServices;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// What changing a Synchronisation Rule's Scoping Criteria would do (#1436, gap G1): the edit that silently
/// decides which objects a rule manages at all.
///
/// Scope is the change whose consequences are decided somewhere else, which is what makes it worth previewing.
/// Narrowing a criterion takes objects out of scope, and what that costs is read from a different setting: an
/// import rule's Out-of-Scope Action can disconnect every object that leaves, and an export rule's Deprovisioning
/// Action can delete them from the target system. Widening pulls objects in, and what that creates is decided by
/// projection and provisioning. So the preview answers per object, in three parts:
///
/// - **Leaving scope**, split by what it actually costs: a joined object whose join breaks and whose contributed
///   values are recalled, a joined object that keeps its join and merely stops receiving flow, and an unjoined
///   object that loses JIM nothing.
/// - **Entering scope**, put to the synchronisation preview engine (#288) with the proposal substituted for the
///   stored rule, so a projection, a join or a provisioning is the engine's own answer rather than this adapter's
///   reading of the configuration.
/// - **Downstream**, through the shared deletion-eligibility evaluator: which identities the departures would
///   leave eligible for deletion.
///
/// The honesty that matters most here is negative. An import rule's scope exit only bites when the object is out
/// of scope of EVERY import rule carrying criteria, because a criteria-less rule is in scope for everything. Beside
/// such a sibling, narrowing this rule disconnects nobody at all, and a preview that counted departures would be a
/// confident number about a change that does nothing.
/// </summary>
public class SyncRuleScopingPreviewAdapter : IConfigurationChangePreviewAdapter
{
    private readonly JimApplication _application;
    private readonly ISyncEngine _syncEngine;

    /// <summary>
    /// How a scope transition is written into a delta row's value columns, so the drill-down reads as the change
    /// the administrator just made rather than as an internal transition name.
    /// </summary>
    private const string ScopeAttributeName = "Scoping Criteria";
    private const string InScopeValue = "In scope";
    private const string OutOfScopeValue = "Out of scope";

    /// <summary>
    /// How many objects entering scope are put to the preview engine per batch. Batched because the engine builds
    /// a shared evaluation context per call, and that context is the expensive half of a single-object preview.
    /// </summary>
    private const int EngineBatchSize = 200;

    public SyncRuleScopingPreviewAdapter(JimApplication application, ISyncEngine syncEngine)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _syncEngine = syncEngine ?? throw new ArgumentNullException(nameof(syncEngine));
    }

    public ConfigurationChangePreviewSurface Surface => ConfigurationChangePreviewSurface.SynchronisationRuleScope;

    public bool ProducesDeltas => true;

    public Type ProposalType => typeof(SyncRuleScopingProposal);

    public async Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<SyncRuleScopingProposal>();
        var rule = await GetRuleAsync(context);
        var findings = new List<PreviewValidationFinding>();

        var storedScope = SyncRuleScopingProposal.FromCurrentScope(rule);
        if (storedScope.DescribesSameScopeAs(proposal))
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "The proposed Scoping Criteria match the ones this Synchronisation Rule already has, so no object " +
                "changes scope and no impact is counted below.",
                nameof(SyncRule.ObjectScopingCriteriaGroups)));
            return findings;
        }

        // A criterion the evaluator cannot read is worse than a wrong one: it contributes nothing, so the proposal
        // silently evaluates wider than it looks. Caught here rather than at evaluation, where it would surface as
        // a failed preview with a stack trace instead of a sentence.
        foreach (var message in DescribeUnreadableCriteria(rule, proposal))
            findings.Add(new PreviewValidationFinding(PreviewValidationSeverity.Blocking, message, nameof(SyncRule.ObjectScopingCriteriaGroups)));

        if (proposal.IsUnscoped && !storedScope.IsUnscoped)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                $"The proposal removes every Scoping Criterion, so this Synchronisation Rule would manage every " +
                $"object of type '{rule.ConnectedSystemObjectType?.Name ?? "the configured type"}' rather than a " +
                "selected population.",
                nameof(SyncRule.ObjectScopingCriteriaGroups)));
        }

        if (!rule.Enabled)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "This Synchronisation Rule is disabled, so no synchronisation applies its scope today. The counts " +
                "below describe what the next synchronisation would do once it is enabled.",
                nameof(SyncRule.Enabled)));
        }

        if (rule.Direction == SyncRuleDirection.Import)
        {
            var unscopedSibling = (await GetImportRulesForTypeAsync(rule))
                .FirstOrDefault(sibling => sibling.Id != rule.Id && sibling.ObjectScopingCriteriaGroups.Count == 0);

            if (unscopedSibling != null)
            {
                findings.Add(new PreviewValidationFinding(
                    PreviewValidationSeverity.Warning,
                    $"Import Synchronisation Rule '{unscopedSibling.Name}' covers this object type with no Scoping " +
                    "Criteria, so every object is in scope of it whatever this rule says. No object can leave scope " +
                    "while that rule stands, so narrowing this rule's criteria disconnects nothing; the counts below " +
                    "report only what would newly enter scope.",
                    nameof(SyncRule.ObjectScopingCriteriaGroups)));
            }
        }

        return findings;
    }

    public async Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<SyncRuleScopingProposal>();
        var rule = await GetRuleAsync(context);

        if (SyncRuleScopingProposal.FromCurrentScope(rule).DescribesSameScopeAs(proposal))
            return new PreviewCostEstimate(0);

        // The whole population of the rule's type, joined or not: a scope change can move any of them, and the
        // unjoined ones are exactly what a widening would newly project.
        int affected;
        if (rule.Direction == SyncRuleDirection.Export)
        {
            var metaverseObjectType = rule.MetaverseObjectType
                ?? await _application.Metaverse.GetMetaverseObjectTypeAsync(rule.MetaverseObjectTypeId, false);
            affected = metaverseObjectType == null
                ? 0
                : await _application.Metaverse.GetMetaverseObjectOfTypeCountAsync(metaverseObjectType);
        }
        else
        {
            affected = await _application.ConnectedSystems.GetConnectedSystemObjectCountOfTypeAsync(
                rule.ConnectedSystemId, rule.ConnectedSystemObjectTypeId);
        }

        return new PreviewCostEstimate(affected);
    }

    /// <summary>
    /// Counts by streaming the same evaluation the drill-down performs, rather than from set-based SQL.
    /// </summary>
    /// <remarks>
    /// A documented departure from the framework's "counts come from SQL alone" rule, for the same reason as the
    /// destructive-toggle adapter: scope membership is a criteria tree over attribute values, with relative dates
    /// resolved against the moment of evaluation, and it has no SQL form. Counting any other way would mean a
    /// second implementation of scope evaluation that could disagree with the engine's, which is precisely the
    /// failure this framework exists to prevent.
    /// </remarks>
    public async Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rule = await GetRuleAsync(context);
        var counts = new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int>();

        await foreach (var delta in EvaluateDeltasAsync(context, CancellationToken.None))
            counts[delta.TransitionType] = counts.GetValueOrDefault(delta.TransitionType) + 1;

        return
        [
            .. counts
                .OrderByDescending(count => count.Value)
                .ThenBy(count => count.Key)
                .Select(count => new PreviewImpactCount(count.Key, count.Value, ConnectedSystemId: rule.ConnectedSystemId))
        ];
    }

    public async IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<SyncRuleScopingProposal>();
        var rule = await GetRuleAsync(context);

        // No scope change means no object can move, so the population is not read at all.
        if (SyncRuleScopingProposal.FromCurrentScope(rule).DescribesSameScopeAs(proposal))
            yield break;

        var standIn = await MaterialiseAsync(rule, proposal);

        if (rule.Direction == SyncRuleDirection.Export)
        {
            await foreach (var delta in EvaluateOutboundAsync(rule, standIn, cancellationToken))
                yield return delta;
        }
        else
        {
            await foreach (var delta in EvaluateInboundAsync(rule, standIn, cancellationToken))
                yield return delta;
        }
    }

    /// <summary>
    /// The inbound walk: every object of the import rule's type, classified by which side of the rule's scope it
    /// sits on now and would sit on under the proposal, then by what that costs.
    /// </summary>
    private async IAsyncEnumerable<PreviewDelta> EvaluateInboundAsync(SyncRule rule, SyncRule standIn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var siblings = await GetImportRulesForTypeAsync(rule);
        var proposedSiblings = siblings.Select(sibling => sibling.Id == rule.Id ? standIn : sibling).ToList();

        // A criteria-less import rule is in scope for everything, so while one stands over this type no object can
        // reach the out-of-scope path at all, whatever this rule's criteria say.
        var anySiblingCoversEverything = proposedSiblings.Any(sibling => sibling.ObjectScopingCriteriaGroups.Count == 0);

        // One clock for the whole evaluation, so a long stream cannot classify two identical objects differently
        // depending on when each is reached (relative date criteria resolve against it).
        var asAt = DateTime.UtcNow;

        var disconnectionsByMetaverseObject = new Dictionary<Guid, int>();
        var enteringUnjoined = new List<ConnectedSystemObject>(EngineBatchSize);

        await foreach (var cso in _application.ConnectedSystems
            .StreamConnectedSystemObjectsOfType(rule.ConnectedSystemId, rule.ConnectedSystemObjectTypeId)
            .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inScopeNow = _application.ScopingEvaluation.IsCsoInScopeForImportRule(cso, rule, asAt);
            var inScopeProposed = _application.ScopingEvaluation.IsCsoInScopeForImportRule(cso, standIn, asAt);
            if (inScopeNow == inScopeProposed)
                continue;

            if (inScopeNow)
            {
                var delta = ClassifyInboundDeparture(rule, cso, proposedSiblings, anySiblingCoversEverything, asAt,
                    disconnectionsByMetaverseObject);
                if (delta != null)
                    yield return delta;
                continue;
            }

            if (cso.MetaverseObjectId.HasValue)
            {
                // Already joined, so nothing is created; the rule simply starts contributing to it again.
                yield return ScopeDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope, rule, cso,
                    OutOfScopeValue, InScopeValue);
                continue;
            }

            enteringUnjoined.Add(cso);
            if (enteringUnjoined.Count < EngineBatchSize)
                continue;

            foreach (var delta in await ClassifyInboundArrivalsAsync(rule, standIn, enteringUnjoined, cancellationToken))
                yield return delta;
            enteringUnjoined.Clear();
        }

        foreach (var delta in await ClassifyInboundArrivalsAsync(rule, standIn, enteringUnjoined, cancellationToken))
            yield return delta;

        await foreach (var delta in PreviewDeletionEligibilityEvaluator.EvaluateAsync(
            _application, _syncEngine, rule.ConnectedSystemId, disconnectionsByMetaverseObject, cancellationToken))
        {
            yield return delta;
        }
    }

    /// <summary>
    /// What one object leaving an import rule's scope actually costs, which is not decided by this rule alone.
    /// </summary>
    private PreviewDelta? ClassifyInboundDeparture(SyncRule rule, ConnectedSystemObject cso,
        List<SyncRule> proposedSiblings, bool anySiblingCoversEverything, DateTime asAt,
        Dictionary<Guid, int> disconnectionsByMetaverseObject)
    {
        // Out of this rule's scope is not out of scope: the engine acts only when every import rule with criteria
        // has let the object go.
        var stillCoveredElsewhere = anySiblingCoversEverything || proposedSiblings.Any(sibling =>
            sibling.Id != rule.Id
            && sibling.ObjectScopingCriteriaGroups.Count > 0
            && _application.ScopingEvaluation.IsCsoInScopeForImportRule(cso, sibling, asAt));

        if (stillCoveredElsewhere)
            return null;

        if (!cso.MetaverseObjectId.HasValue)
        {
            // Nothing is lost: an unjoined object was only ever a projection candidate.
            return ScopeDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, rule, cso,
                InScopeValue, OutOfScopeValue);
        }

        // The action is the engine's, read from the rule that governs the path rather than assumed to be this
        // rule's own; with several import rules over one type they need not be the same rule.
        var action = _syncEngine.DetermineOutOfScopeAction(cso, proposedSiblings);
        if (action != InboundOutOfScopeAction.Disconnect)
        {
            // The join survives and Attribute Flow stops, which is a real consequence and a materially different
            // one: nothing is recalled from the Metaverse and no identity becomes deletable.
            return ScopeDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, rule, cso,
                InScopeValue, OutOfScopeValue);
        }

        var metaverseObjectId = cso.MetaverseObjectId.Value;
        disconnectionsByMetaverseObject[metaverseObjectId] =
            disconnectionsByMetaverseObject.GetValueOrDefault(metaverseObjectId) + 1;

        return ScopeDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, rule, cso,
            InScopeValue, OutOfScopeValue, metaverseObjectId);
    }

    /// <summary>
    /// What the unjoined objects entering scope would become, answered by the preview engine with the proposal
    /// substituted for the stored rule, so a projection or a join is its verdict and not this adapter's guess.
    /// </summary>
    private async Task<List<PreviewDelta>> ClassifyInboundArrivalsAsync(SyncRule rule, SyncRule standIn,
        List<ConnectedSystemObject> arrivals, CancellationToken cancellationToken)
    {
        var deltas = new List<PreviewDelta>();
        if (arrivals.Count == 0)
            return deltas;

        var previews = await _application.SyncPreview.PreviewSyncForCsosAsync(
            rule.ConnectedSystemId, [.. arrivals.Select(cso => cso.Id)], standIn, cancellationToken);

        // The engine's verdict where it reached one, and plain scope entry where it did not: an object it could not
        // speak for has still entered scope, and reporting nothing for it would lose it from the count entirely.
        deltas.AddRange(arrivals.Select(cso => ScopeDelta(
            DescribeArrival(previews.GetValueOrDefault(cso.Id)), rule, cso, OutOfScopeValue, InScopeValue)));

        return deltas;
    }

    /// <summary>
    /// The outbound walk: every Metaverse Object of the export rule's type, classified by which side of the rule's
    /// scope it sits on and whether it already has an object in the target system.
    /// </summary>
    private async IAsyncEnumerable<PreviewDelta> EvaluateOutboundAsync(SyncRule rule, SyncRule standIn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var asAt = DateTime.UtcNow;
        var disconnectionsByMetaverseObject = new Dictionary<Guid, int>();

        await foreach (var mvo in _application.Metaverse
            .StreamMetaverseObjectsOfType(rule.MetaverseObjectTypeId)
            .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inScopeNow = _application.ScopingEvaluation.IsMvoInScopeForExportRule(mvo, rule, asAt);
            var inScopeProposed = _application.ScopingEvaluation.IsMvoInScopeForExportRule(mvo, standIn, asAt);
            if (inScopeNow == inScopeProposed)
                continue;

            var targetObject = mvo.ConnectedSystemObjects
                .FirstOrDefault(cso => cso.ConnectedSystemId == rule.ConnectedSystemId);

            if (inScopeNow)
            {
                if (targetObject == null)
                {
                    // Nothing to deprovision: the rule never got as far as creating anything for this identity. What
                    // the exit costs is the Connected System Object a provisioning rule would have created and now
                    // will not; under a rule that does not provision it costs nothing at all. Either way this is a Metaverse Object
                    // leaving an EXPORT rule's scope, so the import-side transition, which the panel labels "Leaves
                    // import scope", would name a direction the rule does not have.
                    var exit = rule.ProvisionToConnectedSystem == true
                        ? ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning
                        : ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope;
                    yield return MetaverseScopeDelta(exit, rule, mvo, null, InScopeValue, OutOfScopeValue);
                    continue;
                }

                if (rule.OutboundDeprovisionAction == OutboundDeprovisionAction.Delete)
                {
                    yield return MetaverseScopeDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport,
                        rule, mvo, targetObject, InScopeValue, OutOfScopeValue);
                    continue;
                }

                disconnectionsByMetaverseObject[mvo.Id] = disconnectionsByMetaverseObject.GetValueOrDefault(mvo.Id) + 1;
                yield return MetaverseScopeDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject,
                    rule, mvo, targetObject, InScopeValue, OutOfScopeValue);
                continue;
            }

            // Entering scope. A rule that provisions creates the target object; one that does not simply begins
            // flowing attributes to an object that must already exist, so reporting a provisioning would overstate
            // what the change does. The export-side entry rather than the import-side one, for the reason above.
            var transition = targetObject == null && rule.ProvisionToConnectedSystem == true
                ? ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned
                : ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope;

            yield return MetaverseScopeDelta(transition, rule, mvo, targetObject, OutOfScopeValue, InScopeValue);
        }

        await foreach (var delta in PreviewDeletionEligibilityEvaluator.EvaluateAsync(
            _application, _syncEngine, rule.ConnectedSystemId, disconnectionsByMetaverseObject, cancellationToken))
        {
            yield return delta;
        }
    }

    #region helpers

    /// <summary>
    /// What the preview engine said an object entering scope would become.
    /// </summary>
    private static ActivityRunProfileExecutionItemSyncOutcomeType DescribeArrival(SyncPreviewResult? preview) =>
        preview?.Inbound switch
        {
            { WouldProject: true } => ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            { WouldJoinMetaverseObjectId: not null } => ActivityRunProfileExecutionItemSyncOutcomeType.Joined,
            _ => ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope
        };

    private static PreviewDelta ScopeDelta(ActivityRunProfileExecutionItemSyncOutcomeType transition, SyncRule rule,
        ConnectedSystemObject cso, string oldValue, string newValue, Guid? metaverseObjectId = null) =>
        new(transition,
            ObjectDisplayName: cso.NameOrId,
            ObjectTypeName: cso.Type?.Name,
            MetaverseObjectTypeId: rule.MetaverseObjectTypeId,
            MetaverseObjectId: metaverseObjectId ?? cso.MetaverseObjectId,
            ConnectedSystemObjectId: cso.Id,
            ConnectedSystemId: rule.ConnectedSystemId,
            AttributeName: ScopeAttributeName,
            OldValue: oldValue,
            NewValue: newValue);

    private static PreviewDelta MetaverseScopeDelta(ActivityRunProfileExecutionItemSyncOutcomeType transition,
        SyncRule rule, MetaverseObject mvo, ConnectedSystemObject? targetObject, string oldValue, string newValue) =>
        new(transition,
            ObjectDisplayName: mvo.NameOrId,
            ObjectTypeName: mvo.Type?.Name,
            MetaverseObjectTypeId: rule.MetaverseObjectTypeId,
            MetaverseObjectId: mvo.Id,
            ConnectedSystemObjectId: targetObject?.Id,
            ConnectedSystemId: rule.ConnectedSystemId,
            AttributeName: ScopeAttributeName,
            OldValue: oldValue,
            NewValue: newValue);

    /// <summary>
    /// The proposal as a rule the evaluator can be asked about, with every criterion's attribute entity attached.
    /// </summary>
    private async Task<SyncRule> MaterialiseAsync(SyncRule rule, SyncRuleScopingProposal proposal)
    {
        var connectedSystemAttributes = rule.ConnectedSystemObjectType?.Attributes
            ?? (await _application.ConnectedSystems.GetObjectTypeAsync(rule.ConnectedSystemObjectTypeId))?.Attributes
            ?? [];
        var metaverseAttributes = rule.MetaverseObjectType?.Attributes
            ?? (await _application.Metaverse.GetMetaverseObjectTypeAsync(rule.MetaverseObjectTypeId, false))?.Attributes
            ?? [];

        return SyncRuleScopingProposalMaterialiser.Materialise(rule, proposal, connectedSystemAttributes, metaverseAttributes);
    }

    /// <summary>
    /// Criteria the evaluator could not read, described one per message. A criterion naming an attribute of the
    /// wrong side (a Metaverse Attribute on an import rule, or the reverse) can never be evaluated, so it silently
    /// contributes nothing and the proposal evaluates wider than it reads.
    /// </summary>
    private static IEnumerable<string> DescribeUnreadableCriteria(SyncRule rule, SyncRuleScopingProposal proposal)
    {
        var wantsConnectedSystemAttribute = rule.Direction == SyncRuleDirection.Import;

        return EnumerateCriteria(proposal.CriteriaGroups)
            .Select(criterion => DescribeIfUnreadable(criterion, wantsConnectedSystemAttribute))
            .Where(message => message != null)
            .Select(message => message!);
    }

    /// <summary>
    /// Why one criterion could not be evaluated, or null where it can be.
    /// </summary>
    private static string? DescribeIfUnreadable(SyncRuleScopingCriterionProposal criterion, bool wantsConnectedSystemAttribute)
    {
        if (criterion.MetaverseAttributeId == null && criterion.ConnectedSystemAttributeId == null)
        {
            return "A proposed Scoping Criterion names no attribute, so there is nothing for it to evaluate and it " +
                   "would narrow nothing.";
        }

        if (wantsConnectedSystemAttribute && criterion.MetaverseAttributeId != null)
        {
            return "A proposed Scoping Criterion reads a Metaverse Attribute, but an import Synchronisation Rule " +
                   "evaluates its scope against Connected System attributes, so the criterion would never match and " +
                   "the scope would be wider than it appears.";
        }

        if (!wantsConnectedSystemAttribute && criterion.ConnectedSystemAttributeId != null)
        {
            return "A proposed Scoping Criterion reads a Connected System attribute, but an export Synchronisation " +
                   "Rule evaluates its scope against Metaverse Attributes, so the criterion would never match and " +
                   "the scope would be wider than it appears.";
        }

        return null;
    }

    private static IEnumerable<SyncRuleScopingCriterionProposal> EnumerateCriteria(
        IEnumerable<SyncRuleScopingCriteriaGroupProposal> groups)
    {
        foreach (var group in groups)
        {
            foreach (var criterion in group.Criteria)
                yield return criterion;

            foreach (var criterion in EnumerateCriteria(group.ChildGroups))
                yield return criterion;
        }
    }

    /// <summary>
    /// The enabled import rules covering the same Connected System Object Type, which together decide whether an
    /// object leaving this rule's scope leaves scope at all.
    /// </summary>
    private async Task<List<SyncRule>> GetImportRulesForTypeAsync(SyncRule rule)
    {
        var rules = await _application.ConnectedSystems.GetSyncRulesAsync(rule.ConnectedSystemId, includeDisabledSyncRules: false);
        return [.. rules.Where(sibling => sibling.Direction == SyncRuleDirection.Import
            && sibling.ConnectedSystemObjectTypeId == rule.ConnectedSystemObjectTypeId)];
    }

    private async Task<SyncRule> GetRuleAsync(PreviewContext context)
    {
        if (context.TargetId is not { } ruleId)
        {
            throw new InvalidOperationException(
                "A Synchronisation Rule scope preview needs the rule's id in the context's TargetId.");
        }

        return await _application.ConnectedSystems.GetSyncRuleAsync(ruleId)
            ?? throw new InvalidOperationException($"Synchronisation Rule {ruleId} does not exist.");
    }

    #endregion
}
