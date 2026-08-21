// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.CompilerServices;
using JIM.Application.Interfaces;
using JIM.Models.Activities;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// What changing a Synchronisation Rule's behaviour toggles would do (#1462, the half of #827 gap G3 that #1115
/// did not cover): whether the rule runs at all, which way it runs, and what it is allowed to create or correct.
///
/// These are the settings whose consequences are hardest to picture, because none of them names a population.
/// Disabling a rule reads like pausing it and is closer to withdrawing every value it owns. Turning Provision To
/// Connected System on reads like granting a capability and is account creation at scale. Turning Enforce State
/// off reads like relaxing a constraint and is a standing decision to let a target system diverge. So the
/// preview's whole job here is to put a count and a list of objects against each.
///
/// Direction is the exception, and it is refused rather than evaluated. An import rule's mappings write Metaverse
/// Attributes and its Object Matching Rules search the Metaverse; flipped to Export, every one of them addresses
/// the side the rule is leaving. There is no coherent configuration to put to the engine, so the preview says so
/// instead of answering about one.
/// </summary>
public class SyncRuleBehaviourTogglePreviewAdapter : IConfigurationChangePreviewAdapter
{
    private readonly JimApplication _application;
    private readonly ISyncEngine _syncEngine;

    /// <summary>
    /// How a behaviour transition is written into a delta row's value columns, so a drill-down reads as the
    /// toggle the administrator just moved rather than as an internal transition name.
    /// </summary>
    private const string BehaviourAttributeName = "Rule Behaviour";
    private const string WouldHappenValue = "Would happen";
    private const string WouldNotHappenValue = "Would not happen";

    /// <summary>
    /// How many objects are put to the preview engine per call. Batched because the engine builds a shared
    /// evaluation context per call, and that context is the expensive half of a single-object preview.
    /// </summary>
    private const int EngineBatchSize = 200;

    public SyncRuleBehaviourTogglePreviewAdapter(JimApplication application, ISyncEngine syncEngine)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _syncEngine = syncEngine ?? throw new ArgumentNullException(nameof(syncEngine));
    }

    public ConfigurationChangePreviewSurface Surface => ConfigurationChangePreviewSurface.SynchronisationRuleBehaviour;

    public bool ProducesDeltas => true;

    public Type ProposalType => typeof(SyncRuleBehaviourToggleProposal);

    public async Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<SyncRuleBehaviourToggleProposal>();
        var rule = await GetRuleAsync(context);
        var stored = SyncRuleBehaviourToggleProposal.FromCurrentSettings(rule);
        var findings = new List<PreviewValidationFinding>();

        if (stored.DescribesSameSettingsAs(proposal))
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "The proposed settings match the ones this Synchronisation Rule already has, so nothing would " +
                "change and no impact is counted below.",
                nameof(SyncRule.Enabled)));
            return findings;
        }

        if (proposal.Direction != stored.Direction)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Blocking,
                $"Direction cannot be changed on a saved Synchronisation Rule. This rule's Attribute Flow " +
                $"mappings and Object Matching Rules are written for {stored.Direction}, and would all address " +
                $"the wrong side as {proposal.Direction}. Create a rule in the direction you need instead.",
                nameof(SyncRule.Direction)));
            return findings;
        }

        if (proposal.Enabled != stored.Enabled)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                proposal.Enabled
                    ? "Enabling this Synchronisation Rule starts it contributing on the next synchronisation: it " +
                      "begins flowing its Attribute Flow mappings, and whatever it projects or provisions starts " +
                      "being created."
                    : "Disabling this Synchronisation Rule stops it contributing on the next synchronisation. " +
                      "Attribute values it currently owns pass to another contributor, or are cleared where it " +
                      "was the only one, and it stops projecting and provisioning.",
                nameof(SyncRule.Enabled)));
        }

        if (proposal.ProjectToMetaverse && !stored.ProjectToMetaverse)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                "Turning Project To Metaverse on creates a new Metaverse Object for every in-scope object this " +
                "rule manages that matches none, which is how identities are created in bulk.",
                nameof(SyncRule.ProjectToMetaverse)));
        }

        if (proposal.ProvisionToConnectedSystem && !stored.ProvisionToConnectedSystem)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                "Turning Provision To Connected System on creates an account in the target system for every " +
                "in-scope Metaverse Object that has none. This is account creation at scale, and the count below " +
                "is how many.",
                nameof(SyncRule.ProvisionToConnectedSystem)));
        }

        if (!proposal.EnforceState && stored.EnforceState)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                "Turning Enforce State off stops this rule correcting drift, so the objects it manages are free " +
                "to diverge from what JIM holds from the moment it is saved. Nothing is corrected until it is " +
                "turned back on.",
                nameof(SyncRule.EnforceState)));
        }

        // Toggles that do nothing in this rule's direction are said plainly. Counted as zero they would read as
        // "nothing is affected", which is a different statement from "this setting does not apply here".
        foreach (var message in DescribeInapplicableToggles(stored, proposal))
            findings.Add(new PreviewValidationFinding(PreviewValidationSeverity.Information, message, nameof(SyncRule.Direction)));

        if (!rule.Enabled && !proposal.Enabled)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "This Synchronisation Rule is disabled, so no synchronisation applies it today. The impact below " +
                "describes what the next synchronisation would do once it is enabled.",
                nameof(SyncRule.Enabled)));
        }

        return findings;
    }

    public async Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<SyncRuleBehaviourToggleProposal>();
        var rule = await GetRuleAsync(context);
        var stored = SyncRuleBehaviourToggleProposal.FromCurrentSettings(rule);

        if (stored.DescribesSameSettingsAs(proposal) || proposal.Direction != stored.Direction)
            return new PreviewCostEstimate(0);

        // The rule's whole population: these toggles are not selective, so every object the rule stands over is a
        // candidate for moving through one of the transitions.
        var affected = rule.Direction == SyncRuleDirection.Export
            ? await MetaverseObjectCountAsync(rule)
            : await _application.ConnectedSystems.GetConnectedSystemObjectCountOfTypeAsync(
                rule.ConnectedSystemId, rule.ConnectedSystemObjectTypeId);

        return new PreviewCostEstimate(affected);
    }

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

        var proposal = context.ProposedAs<SyncRuleBehaviourToggleProposal>();
        var rule = await GetRuleAsync(context);
        var stored = SyncRuleBehaviourToggleProposal.FromCurrentSettings(rule);

        // Nothing to evaluate when nothing changes, and nothing coherent to evaluate when Direction does: a
        // blocking finding that still streamed deltas would put counts beside a message saying the change cannot
        // be applied.
        if (stored.DescribesSameSettingsAs(proposal) || proposal.Direction != stored.Direction)
            yield break;

        var ruleSet = BuildProposedRuleSet(rule, proposal);

        if (rule.Direction == SyncRuleDirection.Export)
        {
            await foreach (var delta in EvaluateOutboundAsync(rule, ruleSet, cancellationToken))
                yield return delta;
        }
        else
        {
            await foreach (var delta in EvaluateInboundAsync(rule, ruleSet, cancellationToken))
                yield return delta;
        }
    }

    #region evaluation

    /// <summary>
    /// How the proposal is expressed to the preview engine.
    /// </summary>
    /// <remarks>
    /// Enabled is the reason the engine grew a rule-set proposal at all. A rule being disabled leaves the
    /// evaluated set, and a rule being enabled joins it; neither is expressible as a substitution, because a
    /// disabled rule is not in the loaded set to be substituted for and a disabled stand-in substituted into it
    /// would go on being evaluated.
    /// </remarks>
    private static ProposedSyncRuleSet BuildProposedRuleSet(SyncRule rule, SyncRuleBehaviourToggleProposal proposal)
    {
        if (!proposal.Enabled)
            return ProposedSyncRuleSet.Removing(rule.Id);

        var standIn = SyncRuleStandIn.CloneOf(rule);
        standIn.Enabled = true;
        standIn.ProjectToMetaverse = proposal.ProjectToMetaverse;
        standIn.ProvisionToConnectedSystem = proposal.ProvisionToConnectedSystem;
        standIn.EnforceState = proposal.EnforceState;

        // A rule that is disabled today is absent from the evaluated set, so it has to JOIN it rather than replace
        // something that is not there.
        return rule.Enabled ? ProposedSyncRuleSet.Substituting(standIn) : ProposedSyncRuleSet.Adding(standIn);
    }

    /// <summary>
    /// The inbound walk: every object of the rule's type, previewed once as things stand and once under the
    /// proposal, and reported where the two answers differ on whether an identity gets created.
    /// </summary>
    private async IAsyncEnumerable<PreviewDelta> EvaluateInboundAsync(SyncRule rule, ProposedSyncRuleSet ruleSet,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var objectTypeName = rule.ConnectedSystemObjectType?.Name;

        foreach (var batch in (await PopulationAsync(rule)).Chunk(EngineBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var baseline = await _application.SyncPreview.PreviewSyncForCsosAsync(
                rule.ConnectedSystemId, batch, cancellationToken: cancellationToken);
            var proposed = await _application.SyncPreview.PreviewSyncForCsosAsync(
                rule.ConnectedSystemId, batch, cancellationToken: cancellationToken, proposedRuleSet: ruleSet);

            foreach (var id in batch)
            {
                var before = baseline.GetValueOrDefault(id);
                var after = proposed.GetValueOrDefault(id);
                if (before == null || after == null)
                    continue;

                var delta = DescribeProjection(id, before, after, rule, objectTypeName);
                if (delta != null)
                    yield return delta;
            }
        }
    }

    /// <summary>
    /// The outbound walk: every Metaverse Object of the rule's type, previewed both ways, and reported where the
    /// answers differ on whether an account gets created or drift gets corrected.
    /// </summary>
    private async IAsyncEnumerable<PreviewDelta> EvaluateOutboundAsync(SyncRule rule, ProposedSyncRuleSet ruleSet,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var batch in (await MetaverseObjectPopulationAsync(rule)).Chunk(EngineBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var baseline = await _application.SyncPreview.PreviewSyncForMvosAsync(
                batch, cancellationToken: cancellationToken);
            var proposed = await _application.SyncPreview.PreviewSyncForMvosAsync(
                batch, cancellationToken: cancellationToken, proposedRuleSet: ruleSet);

            foreach (var id in batch)
            {
                var before = baseline.GetValueOrDefault(id);
                var after = proposed.GetValueOrDefault(id);
                if (before == null || after == null)
                    continue;

                foreach (var delta in DescribeOutbound(id, before, after, rule))
                    yield return delta;
            }
        }
    }

    /// <summary>
    /// Whether this object's identity would still be created, expressed as a transition, or null where the answer
    /// is the same either way.
    /// </summary>
    private static PreviewDelta? DescribeProjection(Guid connectedSystemObjectId, SyncPreviewResult before,
        SyncPreviewResult after, SyncRule rule, string? objectTypeName)
    {
        var projectedBefore = before.Inbound?.WouldProject ?? false;
        var projectedAfter = after.Inbound?.WouldProject ?? false;
        if (projectedBefore == projectedAfter)
            return null;

        return new PreviewDelta(
            projectedBefore
                ? ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting
                : ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            ObjectTypeName: objectTypeName,
            MetaverseObjectTypeId: rule.MetaverseObjectTypeId,
            ConnectedSystemObjectId: connectedSystemObjectId,
            ConnectedSystemId: rule.ConnectedSystemId,
            AttributeName: BehaviourAttributeName,
            OldValue: projectedBefore ? WouldHappenValue : WouldNotHappenValue,
            NewValue: projectedAfter ? WouldHappenValue : WouldNotHappenValue);
    }

    /// <summary>
    /// The outbound transitions for one Metaverse Object: whether an account would still be created for it, and
    /// whether its divergence from what JIM holds would still be corrected.
    /// </summary>
    /// <remarks>
    /// Read off what the engine would stage rather than off the toggles: a Create export is a provisioning and an
    /// Update export against a joined object is a drift correction, so the two questions are answered by the same
    /// evaluation the next synchronisation would perform.
    /// </remarks>
    private static IEnumerable<PreviewDelta> DescribeOutbound(Guid metaverseObjectId, SyncPreviewResult before,
        SyncPreviewResult after, SyncRule rule)
    {
        var createdBefore = Stages(before, PendingExportChangeType.Create);
        var createdAfter = Stages(after, PendingExportChangeType.Create);
        if (createdBefore != createdAfter)
        {
            yield return OutboundDelta(
                createdBefore
                    ? ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning
                    : ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
                metaverseObjectId, rule, createdBefore, createdAfter);
        }

        var correctedBefore = Stages(before, PendingExportChangeType.Update);
        var correctedAfter = Stages(after, PendingExportChangeType.Update);
        if (correctedBefore != correctedAfter)
        {
            yield return OutboundDelta(
                correctedBefore
                    ? ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift
                    : ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection,
                metaverseObjectId, rule, correctedBefore, correctedAfter);
        }
    }

    private static bool Stages(SyncPreviewResult result, PendingExportChangeType changeType) =>
        result.Outbound.ProposedExports.Exists(export => export.ChangeType == changeType);

    private static PreviewDelta OutboundDelta(ActivityRunProfileExecutionItemSyncOutcomeType transition,
        Guid metaverseObjectId, SyncRule rule, bool before, bool after) =>
        new(transition,
            ObjectTypeName: rule.MetaverseObjectType?.Name,
            MetaverseObjectTypeId: rule.MetaverseObjectTypeId,
            MetaverseObjectId: metaverseObjectId,
            ConnectedSystemId: rule.ConnectedSystemId,
            AttributeName: BehaviourAttributeName,
            OldValue: before ? WouldHappenValue : WouldNotHappenValue,
            NewValue: after ? WouldHappenValue : WouldNotHappenValue);

    #endregion

    #region population

    private async Task<List<Guid>> PopulationAsync(SyncRule rule)
    {
        var ids = new List<Guid>();
        await foreach (var cso in _application.ConnectedSystems
                           .StreamConnectedSystemObjectsOfType(rule.ConnectedSystemId, rule.ConnectedSystemObjectTypeId))
        {
            ids.Add(cso.Id);
        }

        return ids;
    }

    private async Task<List<Guid>> MetaverseObjectPopulationAsync(SyncRule rule)
    {
        var ids = new List<Guid>();
        await foreach (var mvo in _application.Metaverse.StreamMetaverseObjectsOfType(rule.MetaverseObjectTypeId))
            ids.Add(mvo.Id);

        return ids;
    }

    private async Task<int> MetaverseObjectCountAsync(SyncRule rule)
    {
        var metaverseObjectType = rule.MetaverseObjectType
            ?? await _application.Metaverse.GetMetaverseObjectTypeAsync(rule.MetaverseObjectTypeId, false);

        return metaverseObjectType == null
            ? 0
            : await _application.Metaverse.GetMetaverseObjectOfTypeCountAsync(metaverseObjectType);
    }

    #endregion

    #region helpers

    /// <summary>
    /// The proposed toggles that do nothing in this rule's direction, so the preview says so rather than counting
    /// them as zero. "Nothing is affected" and "this setting does not apply here" are different statements, and
    /// only one of them explains an empty result.
    /// </summary>
    private static IEnumerable<string> DescribeInapplicableToggles(SyncRuleBehaviourToggleProposal stored,
        SyncRuleBehaviourToggleProposal proposal)
    {
        if (stored.Direction == SyncRuleDirection.Import)
        {
            if (proposal.EnforceState != stored.EnforceState)
                yield return "Enforce State governs drift correction on Export rules only, so changing it on this Import rule has no effect.";

            if (proposal.ProvisionToConnectedSystem != stored.ProvisionToConnectedSystem)
                yield return "Provision To Connected System applies to Export rules only, so changing it on this Import rule has no effect.";
        }
        else if (proposal.ProjectToMetaverse != stored.ProjectToMetaverse)
        {
            yield return "Project To Metaverse applies to Import rules only, so changing it on this Export rule has no effect.";
        }
    }

    private async Task<SyncRule> GetRuleAsync(PreviewContext context)
    {
        if (context.TargetId is not { } syncRuleId)
            throw new InvalidOperationException("A behaviour-toggle preview must name the Synchronisation Rule it is about.");

        return await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId)
            ?? throw new InvalidOperationException($"Synchronisation Rule {syncRuleId} was not found, so its behaviour cannot be previewed.");
    }

    #endregion
}
