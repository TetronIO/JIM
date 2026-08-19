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
/// What changing a Synchronisation Rule's Attribute Flow would write (#1437, gap G2): the edit that rewrites an
/// attribute on every object the rule manages, on the next synchronisation, with no statement of what the values
/// become.
///
/// The evaluation is the synchronisation preview engine's own (#288), run TWICE per object and diffed: once
/// against the stored configuration and once with the proposal substituted for the rule. Nothing here forms an
/// opinion about what a mapping produces. That matters more on this surface than on any other, because what a
/// mapping produces is the whole question: an Expression's output, the Missing Input Behaviour that decides
/// whether it runs at all, and Attribute Priority deciding whether the result is written are three separate
/// pieces of engine behaviour, and a second implementation of any of them would be a preview that disagrees with
/// the run it predicts.
///
/// Diffing two evaluations rather than reading one is what makes the answer an old-to-new pair. Each evaluation
/// reports the changes it would make relative to the object's CURRENT Metaverse state, so an attribute the stored
/// configuration already gets right produces no change under it and a change under the proposal; comparing the two
/// derived results gives the value the administrator would see before and after, which is what the framework's
/// value-pair grouping and pattern detectors (domain, container, casing, affix) read.
///
/// The honesty that matters here is about what the preview does NOT cover, and there are two pieces of it:
///
/// - **A mapping deleted outright changes no value.** Inbound flow contributes what its mappings produce; a
///   mapping that no longer exists contributes nothing at all, so the value it last wrote is left in place rather
///   than withdrawn. That is a real consequence (the attribute stops being maintained and goes stale) and it is
///   reported as a finding, not as a value delta, because reporting a withdrawal the next synchronisation would
///   not perform is exactly the confident lie this framework exists to prevent.
/// - **Only this Connected System is evaluated.** A contributor on another system takes its turn on its own
///   synchronisation, which this preview does not run, so what it would re-elect is named in a finding rather
///   than guessed at per object.
/// </summary>
public class SyncRuleAttributeFlowPreviewAdapter : IConfigurationChangePreviewAdapter
{
    private readonly JimApplication _application;
    private readonly ISyncEngine _syncEngine;

    /// <summary>
    /// How many objects are put to the preview engine per batch. Batched because the engine builds a shared
    /// evaluation context per call, and that context is the expensive half of a single-object preview; here it is
    /// paid twice per batch rather than twice per object.
    /// </summary>
    private const int EngineBatchSize = 200;

    public SyncRuleAttributeFlowPreviewAdapter(JimApplication application, ISyncEngine syncEngine)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _syncEngine = syncEngine ?? throw new ArgumentNullException(nameof(syncEngine));
    }

    public ConfigurationChangePreviewSurface Surface => ConfigurationChangePreviewSurface.SynchronisationRuleAttributeFlow;

    public bool ProducesDeltas => true;

    public Type ProposalType => typeof(SyncRuleAttributeFlowProposal);

    public async Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<SyncRuleAttributeFlowProposal>();
        var rule = await GetRuleAsync(context);
        var findings = new List<PreviewValidationFinding>();

        if (SyncRuleAttributeFlowProposal.FromCurrentMappings(rule).DescribesSameMappingsAs(proposal))
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "The proposed Attribute Flow matches the mappings this Synchronisation Rule already has, so no " +
                "object changes and no impact is counted below.",
                nameof(SyncRule.AttributeFlowRules)));
            return findings;
        }

        foreach (var message in DescribeUnwritableMappings(rule, proposal))
            findings.Add(new PreviewValidationFinding(PreviewValidationSeverity.Blocking, message, nameof(SyncRule.AttributeFlowRules)));

        if (proposal.Mappings.Count == 0 && rule.AttributeFlowRules.Count > 0)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                "The proposal removes every mapping, so this Synchronisation Rule would flow nothing.",
                nameof(SyncRule.AttributeFlowRules)));
        }

        if (!rule.Enabled)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "This Synchronisation Rule is disabled, so no synchronisation applies its Attribute Flow today. " +
                "The values below describe what the next synchronisation would write once it is enabled.",
                nameof(SyncRule.Enabled)));
        }

        findings.AddRange(await DescribeWithdrawnMappingsAsync(rule, proposal));

        if (rule.Direction == SyncRuleDirection.Import)
            findings.AddRange(await DescribeAttributePriorityAsync(rule, proposal));

        return findings;
    }

    public async Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<SyncRuleAttributeFlowProposal>();
        var rule = await GetRuleAsync(context);

        if (SyncRuleAttributeFlowProposal.FromCurrentMappings(rule).DescribesSameMappingsAs(proposal))
            return new PreviewCostEstimate(0);

        // The rule's whole population. Scope narrows what is EVALUATED, but scope membership is a criteria tree
        // over attribute values with no SQL form, so the walk still reads every object of the type to decide.
        if (rule.Direction == SyncRuleDirection.Export)
        {
            var metaverseObjectType = rule.MetaverseObjectType
                ?? await _application.Metaverse.GetMetaverseObjectTypeAsync(rule.MetaverseObjectTypeId, false);
            return new PreviewCostEstimate(metaverseObjectType == null
                ? 0
                : await _application.Metaverse.GetMetaverseObjectOfTypeCountAsync(metaverseObjectType));
        }

        return new PreviewCostEstimate(await _application.ConnectedSystems.GetConnectedSystemObjectCountOfTypeAsync(
            rule.ConnectedSystemId, rule.ConnectedSystemObjectTypeId));
    }

    /// <summary>
    /// Counts by streaming the same evaluation the drill-down performs, rather than from set-based SQL.
    /// </summary>
    /// <remarks>
    /// A documented departure from the framework's "counts come from SQL alone" rule, and the least avoidable of
    /// the departures so far: what a mapping writes for one object is the output of an Expression evaluated over
    /// that object's values, resolved against Attribute Priority. There is no SQL form of it at all, and the only
    /// alternative to streaming would be a second implementation of Attribute Flow.
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

        var proposal = context.ProposedAs<SyncRuleAttributeFlowProposal>();
        var rule = await GetRuleAsync(context);

        // No mapping change means no value can move, so the population is not read at all.
        if (SyncRuleAttributeFlowProposal.FromCurrentMappings(rule).DescribesSameMappingsAs(proposal))
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

    #region inbound

    /// <summary>
    /// The inbound walk: every object the rule manages, put to the engine twice and diffed.
    /// </summary>
    private async IAsyncEnumerable<PreviewDelta> EvaluateInboundAsync(SyncRule rule, SyncRule standIn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // One clock for the whole evaluation, so a long stream cannot classify two identical objects differently
        // depending on when each is reached (relative date scoping criteria resolve against it).
        var asAt = DateTime.UtcNow;
        var batch = new List<ConnectedSystemObject>(EngineBatchSize);

        await foreach (var cso in _application.ConnectedSystems
            .StreamConnectedSystemObjectsOfType(rule.ConnectedSystemId, rule.ConnectedSystemObjectTypeId)
            .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A flow change cannot reach an object the rule does not manage. The engine reaches the same verdict on
            // its own, so this gate is about cost rather than correctness: every object costs two full evaluations,
            // and paying that for objects outside the rule's scope would evaluate a whole system to answer for a
            // subset of it.
            if (rule.ObjectScopingCriteriaGroups.Count > 0
                && !_application.ScopingEvaluation.IsCsoInScopeForImportRule(cso, rule, asAt))
            {
                continue;
            }

            batch.Add(cso);
            if (batch.Count < EngineBatchSize)
                continue;

            foreach (var delta in await EvaluateInboundBatchAsync(rule, standIn, batch, cancellationToken))
                yield return delta;
            batch.Clear();
        }

        foreach (var delta in await EvaluateInboundBatchAsync(rule, standIn, batch, cancellationToken))
            yield return delta;
    }

    private async Task<List<PreviewDelta>> EvaluateInboundBatchAsync(SyncRule rule, SyncRule standIn,
        List<ConnectedSystemObject> batch, CancellationToken cancellationToken)
    {
        var deltas = new List<PreviewDelta>();
        if (batch.Count == 0)
            return deltas;

        var ids = batch.Select(cso => cso.Id).ToArray();
        var baseline = await _application.SyncPreview.PreviewSyncForCsosAsync(
            rule.ConnectedSystemId, ids, null, cancellationToken);
        var proposed = await _application.SyncPreview.PreviewSyncForCsosAsync(
            rule.ConnectedSystemId, ids, standIn, cancellationToken);

        foreach (var cso in batch.Where(cso => proposed.ContainsKey(cso.Id)))
        {
            var proposedPreview = proposed[cso.Id];
            baseline.TryGetValue(cso.Id, out var baselinePreview);

            deltas.AddRange(DescribeIntroducedFailures(baselinePreview, proposedPreview)
                .Select(attributeName => InboundDelta(
                    ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow, rule, cso, attributeName, null, null)));

            deltas.AddRange(DiffFlowChanges(baselinePreview?.Inbound, proposedPreview.Inbound)
                .Select(change => InboundDelta(TransitionFor(change), rule, cso, change.AttributeName, change.OldValue, change.NewValue)));
        }

        return deltas;
    }

    #endregion

    #region outbound

    /// <summary>
    /// The outbound walk: every identity the export rule manages, put to the engine twice and diffed on the
    /// attribute changes each evaluation would stage for THIS rule.
    /// </summary>
    private async IAsyncEnumerable<PreviewDelta> EvaluateOutboundAsync(SyncRule rule, SyncRule standIn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var asAt = DateTime.UtcNow;
        var batch = new List<MetaverseObject>(EngineBatchSize);

        await foreach (var mvo in _application.Metaverse
            .StreamMetaverseObjectsOfType(rule.MetaverseObjectTypeId)
            .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (rule.ObjectScopingCriteriaGroups.Count > 0
                && !_application.ScopingEvaluation.IsMvoInScopeForExportRule(mvo, rule, asAt))
            {
                continue;
            }

            batch.Add(mvo);
            if (batch.Count < EngineBatchSize)
                continue;

            foreach (var delta in await EvaluateOutboundBatchAsync(rule, standIn, batch, cancellationToken))
                yield return delta;
            batch.Clear();
        }

        foreach (var delta in await EvaluateOutboundBatchAsync(rule, standIn, batch, cancellationToken))
            yield return delta;
    }

    private async Task<List<PreviewDelta>> EvaluateOutboundBatchAsync(SyncRule rule, SyncRule standIn,
        List<MetaverseObject> batch, CancellationToken cancellationToken)
    {
        var deltas = new List<PreviewDelta>();
        if (batch.Count == 0)
            return deltas;

        var ids = batch.Select(mvo => mvo.Id).ToArray();
        var baseline = await _application.SyncPreview.PreviewSyncForMvosAsync(ids, null, cancellationToken);
        var proposed = await _application.SyncPreview.PreviewSyncForMvosAsync(ids, standIn, cancellationToken);

        foreach (var mvo in batch.Where(mvo => proposed.ContainsKey(mvo.Id)))
        {
            var proposedPreview = proposed[mvo.Id];
            baseline.TryGetValue(mvo.Id, out var baselinePreview);

            var targetObjectId = ExportTargetObjectId(proposedPreview, rule) ?? ExportTargetObjectId(baselinePreview, rule);

            deltas.AddRange(DiffExportChanges(
                    ExportChangesFor(baselinePreview, rule), ExportChangesFor(proposedPreview, rule))
                .Select(change => OutboundDelta(TransitionFor(change), rule, mvo, targetObjectId,
                    change.AttributeName, change.OldValue, change.NewValue)));
        }

        return deltas;
    }

    /// <summary>
    /// The attribute changes one preview would stage for this rule, flattened across its decision records.
    /// </summary>
    private static List<PendingExportAttributeValueChange> ExportChangesFor(SyncPreviewResult? preview, SyncRule rule) =>
        preview == null
            ? []
            : [.. preview.OutboundDecisions.Entries
                .Where(entry => entry.SyncRuleId == rule.Id)
                .SelectMany(entry => entry.AttributeChanges)];

    private static Guid? ExportTargetObjectId(SyncPreviewResult? preview, SyncRule rule) =>
        preview?.OutboundDecisions.Entries
            .Where(entry => entry.SyncRuleId == rule.Id)
            .Select(entry => entry.ExistingTargetCsoId ?? entry.WouldJoinCsoId)
            .FirstOrDefault(id => id.HasValue);

    #endregion

    #region diffing

    /// <summary>
    /// One attribute's before-and-after under the two configurations.
    /// </summary>
    private sealed record ValueChange(string AttributeName, string? OldValue, string? NewValue);

    /// <summary>
    /// What the two evaluations would leave each attribute holding, compared.
    /// </summary>
    /// <remarks>
    /// Neither evaluation reports the object's current values, only the changes it would make to them, so the
    /// current values are reconstructed from what the two evaluations REMOVE: a removal is only ever produced for
    /// a value the object actually holds. Values neither configuration touches are identical on both sides by
    /// definition and cannot affect the comparison, so reconstructing only the touched ones is exact rather than
    /// approximate.
    /// </remarks>
    private static IEnumerable<ValueChange> DiffFlowChanges(
        SyncPreviewInboundSummary? baseline, SyncPreviewInboundSummary? proposed)
    {
        var baselineChanges = baseline?.AttributeFlowChanges ?? [];
        var proposedChanges = proposed?.AttributeFlowChanges ?? [];

        var attributeNames = baselineChanges.Select(change => change.AttributeName)
            .Concat(proposedChanges.Select(change => change.AttributeName))
            .Distinct(StringComparer.Ordinal);

        foreach (var attributeName in attributeNames)
        {
            var changes = Compare(
                Values(baselineChanges, attributeName, added: false), Values(baselineChanges, attributeName, added: true),
                Values(proposedChanges, attributeName, added: false), Values(proposedChanges, attributeName, added: true));

            foreach (var (oldValue, newValue) in changes)
                yield return new ValueChange(attributeName, oldValue, newValue);
        }

        static List<string?> Values(List<SyncPreviewAttributeFlowChange> changes, string attributeName, bool added) =>
            [.. changes.Where(change => change.IsAddition == added
                    && string.Equals(change.AttributeName, attributeName, StringComparison.Ordinal))
                .Select(change => change.Value)];
    }

    /// <summary>
    /// The outbound equivalent: what the two evaluations would stage for the target system, compared.
    /// </summary>
    /// <remarks>
    /// A staged export change is a value to WRITE rather than a pair of add and remove rows, so a value the
    /// proposal stops writing appears simply as a change that is no longer staged. That reads as a withdrawal of
    /// the flow, which is what it is: the target keeps whatever it holds and this rule stops maintaining it.
    /// </remarks>
    private static IEnumerable<ValueChange> DiffExportChanges(
        List<PendingExportAttributeValueChange> baseline, List<PendingExportAttributeValueChange> proposed)
    {
        var attributeNames = baseline.Concat(proposed)
            .Select(ExportAttributeName)
            .Distinct(StringComparer.Ordinal);

        foreach (var attributeName in attributeNames)
        {
            var changes = Compare([], Values(baseline, attributeName), [], Values(proposed, attributeName));
            foreach (var (oldValue, newValue) in changes)
                yield return new ValueChange(attributeName, oldValue, newValue);
        }

        static List<string?> Values(List<PendingExportAttributeValueChange> changes, string attributeName) =>
            [.. changes.Where(change => string.Equals(ExportAttributeName(change), attributeName, StringComparison.Ordinal))
                .Select(RenderExportValue)];
    }

    /// <summary>
    /// One attribute's two derived results, paired into old-to-new rows.
    /// </summary>
    /// <remarks>
    /// The pairing is positional over the sorted difference, which is exact for a single-valued attribute (the
    /// overwhelmingly common case: one value out, one value in) and a reasonable reading for a multi-valued one,
    /// where there is no ordering to pair by and any pairing is presentational. Leftovers on either side become
    /// one-sided rows, so nothing is lost to the pairing.
    /// </remarks>
    private static List<(string? OldValue, string? NewValue)> Compare(
        List<string?> baselineRemovals, List<string?> baselineAdditions,
        List<string?> proposedRemovals, List<string?> proposedAdditions)
    {
        var current = new HashSet<string?>(baselineRemovals);
        current.UnionWith(proposedRemovals);

        var baselineResult = new HashSet<string?>(current.Except(baselineRemovals));
        baselineResult.UnionWith(baselineAdditions);

        var proposedResult = new HashSet<string?>(current.Except(proposedRemovals));
        proposedResult.UnionWith(proposedAdditions);

        var onlyBaseline = baselineResult.Except(proposedResult).OrderBy(value => value, StringComparer.Ordinal).ToList();
        var onlyProposed = proposedResult.Except(baselineResult).OrderBy(value => value, StringComparer.Ordinal).ToList();

        return [.. Enumerable.Range(0, Math.Max(onlyBaseline.Count, onlyProposed.Count))
            .Select(index => (
                OldValue: index < onlyBaseline.Count ? onlyBaseline[index] : null,
                NewValue: index < onlyProposed.Count ? onlyProposed[index] : null))];
    }

    /// <summary>
    /// The attributes the proposal would fail to evaluate for this object and the stored configuration would not.
    /// Only what the change INTRODUCES: a failure that is already happening is not this proposal's doing.
    /// </summary>
    private static IEnumerable<string?> DescribeIntroducedFailures(SyncPreviewResult? baseline, SyncPreviewResult proposed)
    {
        var alreadyFailing = (baseline?.Errors ?? []).Select(FailureKey).ToHashSet(StringComparer.Ordinal);

        return proposed.Errors
            .Where(error => error.Code is SyncPreviewMessageCode.ExpressionEvaluationError
                    or SyncPreviewMessageCode.MultiValuedToSingleValuedFlow
                && !alreadyFailing.Contains(FailureKey(error)))
            .Select(error => error.AttributeName);

        static string FailureKey(SyncPreviewMessage message) => $"{message.Code}|{message.AttributeName}|{message.Detail}";
    }

    /// <summary>
    /// A value that would no longer be written is a withdrawal; anything else is a flow writing a different value.
    /// </summary>
    private static ActivityRunProfileExecutionItemSyncOutcomeType TransitionFor(ValueChange change) =>
        change.NewValue == null
            ? ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor
            : ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow;

    #endregion

    #region findings

    /// <summary>
    /// The attributes this rule writes today and the proposal would stop writing, with what happens to their
    /// values stated rather than left to be inferred.
    /// </summary>
    private async Task<List<PreviewValidationFinding>> DescribeWithdrawnMappingsAsync(SyncRule rule, SyncRuleAttributeFlowProposal proposal)
    {
        var findings = new List<PreviewValidationFinding>();
        if (rule.Direction != SyncRuleDirection.Import)
            return findings;

        var proposedTargets = proposal.Mappings
            .Select(mapping => mapping.TargetMetaverseAttributeId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        var withdrawn = rule.AttributeFlowRules
            .Where(mapping => mapping.TargetMetaverseAttributeId.HasValue
                && !proposedTargets.Contains(mapping.TargetMetaverseAttributeId.Value))
            .ToList();

        if (withdrawn.Count == 0)
            return findings;

        var contributors = await GetImportContributorsAsync(rule);

        foreach (var mapping in withdrawn)
        {
            var attributeName = mapping.TargetMetaverseAttribute?.Name
                ?? $"Metaverse Attribute {mapping.TargetMetaverseAttributeId}";

            // The value is NOT recalled: inbound flow contributes what its mappings produce, and a mapping that no
            // longer exists contributes nothing rather than withdrawing what it last wrote.
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                $"The proposal stops this Synchronisation Rule writing '{attributeName}'. The values it has " +
                "already written are left in place rather than cleared, so they stay as they are and stop being " +
                "maintained; no value change is counted below for them.",
                nameof(SyncRule.AttributeFlowRules)));

            var others = contributors
                .Where(contributor => contributor.RuleId != rule.Id
                    && contributor.AttributeId == mapping.TargetMetaverseAttributeId!.Value)
                .Select(contributor => contributor.RuleName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (others.Count > 0)
            {
                findings.Add(new PreviewValidationFinding(
                    PreviewValidationSeverity.Information,
                    $"'{attributeName}' is also written by {Describe(others)}. Those rules take their turn on " +
                    "their own next synchronisation, which this preview does not run, so what they would write " +
                    "in place of the withdrawn values is not counted below.",
                    nameof(SyncRule.AttributeFlowRules)));
            }
        }

        return findings;
    }

    /// <summary>
    /// Proposed mappings that could not win their attribute, which is the difference between an edit that takes
    /// effect and one that is composed carefully and writes nothing.
    /// </summary>
    private async Task<List<PreviewValidationFinding>> DescribeAttributePriorityAsync(SyncRule rule, SyncRuleAttributeFlowProposal proposal)
    {
        var findings = new List<PreviewValidationFinding>();
        var contributors = await GetImportContributorsAsync(rule);

        foreach (var mapping in proposal.Mappings.Where(mapping => mapping.TargetMetaverseAttributeId.HasValue))
        {
            var attributeId = mapping.TargetMetaverseAttributeId!.Value;

            // Priority resolves ascending (1 is highest), so a competitor holds the attribute when its priority is
            // strictly better than the proposal's.
            var winners = contributors
                .Where(contributor => contributor.AttributeId == attributeId
                    && contributor.RuleId != rule.Id
                    && contributor.Priority < mapping.Priority)
                .Select(contributor => contributor.RuleName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (winners.Count == 0)
                continue;

            var attributeName = contributors
                .FirstOrDefault(contributor => contributor.AttributeId == attributeId)?.AttributeName
                ?? $"Metaverse Attribute {attributeId}";

            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                $"The proposed mapping for '{attributeName}' sits below {Describe(winners)} in Attribute Priority, " +
                "so a synchronisation would evaluate it and then write nothing. Raise its priority to make it the " +
                "contributor that wins the attribute.",
                nameof(SyncRule.AttributeFlowRules)));
        }

        return findings;
    }

    /// <summary>
    /// Mappings the evaluation could not write, described one per message. A mapping naming an attribute of the
    /// wrong side can never be written, so it silently contributes nothing and the proposal does less than it reads.
    /// </summary>
    private static IEnumerable<string> DescribeUnwritableMappings(SyncRule rule, SyncRuleAttributeFlowProposal proposal)
    {
        var wantsMetaverseTarget = rule.Direction == SyncRuleDirection.Import;

        return proposal.Mappings
            .Select(mapping => DescribeIfUnwritable(mapping, wantsMetaverseTarget))
            .Where(message => message != null)
            .Select(message => message!);
    }

    private static string? DescribeIfUnwritable(SyncRuleMappingProposal mapping, bool wantsMetaverseTarget)
    {
        if (mapping.TargetMetaverseAttributeId == null && mapping.TargetConnectedSystemAttributeId == null)
            return "A proposed Attribute Flow mapping names no target attribute, so it has nowhere to write.";

        if (wantsMetaverseTarget && mapping.TargetConnectedSystemAttributeId != null)
        {
            return "A proposed Attribute Flow mapping writes a Connected System attribute, but an import " +
                   "Synchronisation Rule flows into the Metaverse, so the mapping would write nothing.";
        }

        if (!wantsMetaverseTarget && mapping.TargetMetaverseAttributeId != null)
        {
            return "A proposed Attribute Flow mapping writes a Metaverse Attribute, but an export " +
                   "Synchronisation Rule flows out to a Connected System, so the mapping would write nothing.";
        }

        if (mapping.Sources.Count == 0)
            return "A proposed Attribute Flow mapping has no source, so there is no value for it to write.";

        return null;
    }

    /// <summary>
    /// Every import mapping that contributes to a Metaverse Attribute of this rule's object type, across every
    /// Connected System. Ownership is a property of the whole configuration, not of one system.
    /// </summary>
    private async Task<List<ImportContributor>> GetImportContributorsAsync(SyncRule rule)
    {
        var rules = await _application.ConnectedSystems.GetSyncRulesAsync();
        var metaverseObjectType = rule.MetaverseObjectType
            ?? await _application.Metaverse.GetMetaverseObjectTypeAsync(rule.MetaverseObjectTypeId, false);
        var attributeNames = metaverseObjectType?.Attributes.ToDictionary(attribute => attribute.Id, attribute => attribute.Name)
            ?? [];

        return
        [
            .. rules
                .Where(candidate => candidate.Enabled
                    && candidate.Direction == SyncRuleDirection.Import
                    && candidate.MetaverseObjectTypeId == rule.MetaverseObjectTypeId)
                .SelectMany(candidate => candidate.AttributeFlowRules
                    .Where(mapping => mapping.TargetMetaverseAttributeId.HasValue)
                    .Select(mapping => new ImportContributor(
                        candidate.Id,
                        candidate.Name,
                        mapping.TargetMetaverseAttributeId!.Value,
                        mapping.TargetMetaverseAttribute?.Name
                            ?? attributeNames.GetValueOrDefault(mapping.TargetMetaverseAttributeId!.Value),
                        mapping.Priority)))
        ];
    }

    private sealed record ImportContributor(int RuleId, string RuleName, int AttributeId, string? AttributeName, int Priority);

    private static string Describe(List<string> ruleNames) => ruleNames.Count == 1
        ? $"Synchronisation Rule '{ruleNames[0]}'"
        : $"Synchronisation Rules {string.Join(", ", ruleNames.Select(name => $"'{name}'"))}";

    #endregion

    #region helpers

    private static PreviewDelta InboundDelta(ActivityRunProfileExecutionItemSyncOutcomeType transition, SyncRule rule,
        ConnectedSystemObject cso, string? attributeName, string? oldValue, string? newValue) =>
        new(transition,
            ObjectDisplayName: cso.NameOrId,
            ObjectTypeName: cso.Type?.Name,
            MetaverseObjectTypeId: rule.MetaverseObjectTypeId,
            MetaverseObjectId: cso.MetaverseObjectId,
            ConnectedSystemObjectId: cso.Id,
            ConnectedSystemId: rule.ConnectedSystemId,
            AttributeName: attributeName,
            OldValue: oldValue,
            NewValue: newValue);

    private static PreviewDelta OutboundDelta(ActivityRunProfileExecutionItemSyncOutcomeType transition, SyncRule rule,
        MetaverseObject mvo, Guid? targetObjectId, string? attributeName, string? oldValue, string? newValue) =>
        new(transition,
            ObjectDisplayName: mvo.NameOrId,
            ObjectTypeName: mvo.Type?.Name,
            MetaverseObjectTypeId: rule.MetaverseObjectTypeId,
            MetaverseObjectId: mvo.Id,
            ConnectedSystemObjectId: targetObjectId,
            ConnectedSystemId: rule.ConnectedSystemId,
            AttributeName: attributeName,
            OldValue: oldValue,
            NewValue: newValue);

    private static string ExportAttributeName(PendingExportAttributeValueChange change) =>
        change.Attribute?.Name ?? $"attribute {change.AttributeId}";

    /// <summary>
    /// Renders a staged export value for display, mirroring the inbound summary's own rendering so the two
    /// directions of this preview read alike.
    /// </summary>
    private static string? RenderExportValue(PendingExportAttributeValueChange change)
    {
        if (change.StringValue != null)
            return change.StringValue;
        if (change.IntValue.HasValue)
            return change.IntValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (change.LongValue.HasValue)
            return change.LongValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (change.DecimalValue.HasValue)
            return change.DecimalValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (change.DateTimeValue.HasValue)
            return change.DateTimeValue.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        if (change.BoolValue.HasValue)
            return change.BoolValue.Value.ToString();
        if (change.GuidValue.HasValue)
            return change.GuidValue.Value.ToString();
        if (change.UnresolvedReferenceValue != null)
            return change.UnresolvedReferenceValue;
        if (change.ResolvedReferenceCsoId.HasValue)
            return change.ResolvedReferenceCsoId.Value.ToString();
        if (change.ByteValue != null)
            return $"{change.ByteValue.Length} bytes";
        return null;
    }

    /// <summary>
    /// The proposal as a rule the engine can be asked about, with every target and source attribute entity attached.
    /// </summary>
    private async Task<SyncRule> MaterialiseAsync(SyncRule rule, SyncRuleAttributeFlowProposal proposal)
    {
        var connectedSystemAttributes = rule.ConnectedSystemObjectType?.Attributes
            ?? (await _application.ConnectedSystems.GetObjectTypeAsync(rule.ConnectedSystemObjectTypeId))?.Attributes
            ?? [];
        var metaverseAttributes = rule.MetaverseObjectType?.Attributes
            ?? (await _application.Metaverse.GetMetaverseObjectTypeAsync(rule.MetaverseObjectTypeId, false))?.Attributes
            ?? [];

        return SyncRuleAttributeFlowProposalMaterialiser.Materialise(rule, proposal, connectedSystemAttributes, metaverseAttributes);
    }

    private async Task<SyncRule> GetRuleAsync(PreviewContext context)
    {
        if (context.TargetId is not { } ruleId)
        {
            throw new InvalidOperationException(
                "A Synchronisation Rule Attribute Flow preview needs the rule's id in the context's TargetId.");
        }

        return await _application.ConnectedSystems.GetSyncRuleAsync(ruleId)
            ?? throw new InvalidOperationException($"Synchronisation Rule {ruleId} does not exist.");
    }

    #endregion
}
