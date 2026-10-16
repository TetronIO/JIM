// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using JIM.Application.Expressions;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// What changing a Connected System's Object Matching Rules would do (#1457, gap G1): the edit that decides which
/// Metaverse Object each account belongs to.
///
/// This is the surface whose mistakes are the hardest to detect afterwards, because none of them fail. A rule
/// matching one attribute too loosely joins an account to the wrong identity and every value it contributes goes
/// with it; a rule matching too tightly projects a second identity beside the right one. Both look like a
/// successful synchronisation. So the preview answers per object, by asking the matching engine itself twice, once
/// with the stored rules and once with the proposal, and reporting where the two answers differ.
///
/// The negative matters as much as the positive here, and it is the first thing the preview says: Object Matching
/// Rules are evaluated only for objects that have no Metaverse Object yet. An account already joined never
/// re-matches, so no matching edit can re-home it, and an administrator who has been told the population is
/// 40,000 accounts needs to know the change stands over only the unjoined few.
/// </summary>
public class ObjectMatchingPreviewAdapter : IConfigurationChangePreviewAdapter
{
    private readonly JimApplication _application;

    /// <summary>
    /// How a matching transition is written into a delta row's value columns, so the drill-down reads as the
    /// identities concerned rather than as an internal transition name.
    /// </summary>
    private const string MatchAttributeName = "Object Matching";
    private const string NoMatchValue = "No match; projects a new identity";
    private const string AmbiguousValue = "More than one match";
    private const string FailedValue = "Rule could not be evaluated; neither joins nor projects";

    /// <summary>
    /// How many objects are fetched per round trip while evaluating. Batched because the population is read as
    /// identifiers and the objects behind them are needed in memory to be matched, and a preview over hundreds of
    /// thousands of accounts must not put all of them there at once.
    /// </summary>
    private const int EvaluationBatchSize = 200;

    /// <summary>
    /// The checks the save applies (#2052), so the preview refuses exactly what saving the proposal would refuse.
    /// </summary>
    private static readonly ObjectMatchingRuleValidator Validator = new(new DynamicExpressoEvaluator());

    public ObjectMatchingPreviewAdapter(JimApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public ConfigurationChangePreviewSurface Surface => ConfigurationChangePreviewSurface.ObjectMatching;

    public bool ProducesDeltas => true;

    public Type ProposalType => typeof(ObjectMatchingProposal);

    public async Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<ObjectMatchingProposal>();
        var configuration = await LoadAsync(context);
        var findings = new List<PreviewValidationFinding>();

        if (configuration.StoredProposal.DescribesSameMatchingAs(proposal))
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "The proposed Object Matching Rules match the ones this Connected System already has, so no object " +
                "would join differently and no impact is counted below.",
                nameof(ConnectedSystem.ObjectMatchingRuleMode)));
            return findings;
        }

        // Said first, and said on every matching preview: it is the single most common misreading of this change.
        findings.Add(new PreviewValidationFinding(
            PreviewValidationSeverity.Information,
            "Object Matching Rules are evaluated only for objects that are not already joined to a Metaverse " +
            "Object. Objects already joined keep the Metaverse Object they have, whatever this change does, so the " +
            "impact below covers the unjoined population only.",
            nameof(ConnectedSystem.ObjectMatchingRuleMode)));

        if (proposal.Mode != configuration.StoredProposal.Mode)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                proposal.Mode == ObjectMatchingRuleMode.SyncRule
                    ? "The proposal switches this Connected System to Advanced matching, so the rules held against " +
                      "each Connected System Object Type stop being used and each Synchronisation Rule's own rules " +
                      "apply instead. A Synchronisation Rule carrying no rules of its own joins nothing."
                    : "The proposal switches this Connected System to Simple matching, so each Synchronisation " +
                      "Rule's own rules stop being used and the rules held against each Connected System Object " +
                      "Type apply instead. An Object Type carrying no rules of its own joins nothing.",
                nameof(ConnectedSystem.ObjectMatchingRuleMode)));
        }

        foreach (var finding in DescribeUnusableRules(proposal, configuration))
            findings.Add(finding);

        // The types the proposal still covers, gathered once: a Connected System with many Object Types would
        // otherwise re-scan the proposal for every stored type.
        var proposedObjectTypeIds = proposal.Rules
            .Select(rule => ObjectTypeOf(rule, configuration))
            .Where(objectTypeId => objectTypeId != null)
            .ToHashSet();

        foreach (var objectTypeId in configuration.StoredProposal.Rules
                     .Select(rule => ObjectTypeOf(rule, configuration))
                     .Where(objectTypeId => objectTypeId != null && !proposedObjectTypeIds.Contains(objectTypeId))
                     .Select(objectTypeId => objectTypeId!.Value)
                     .Distinct())
        {
            var objectTypeName = configuration.ObjectTypesById.GetValueOrDefault(objectTypeId)?.Name ?? "this type";
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                $"The proposal leaves '{objectTypeName}' with no Object Matching Rule, so no object of that type " +
                "would ever join an existing Metaverse Object. Every unjoined object would project a new identity " +
                "instead, which is how duplicate identities are created.",
                nameof(ConnectedSystemObjectType.ObjectMatchingRules)));
        }

        return findings;
    }

    public async Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<ObjectMatchingProposal>();
        var configuration = await LoadAsync(context);

        if (configuration.StoredProposal.DescribesSameMatchingAs(proposal))
            return new PreviewCostEstimate(0);

        // The unjoined, live population of each affected type, counted set-based. Exactly the objects the change
        // can move: counting the whole type would state a population many times larger than the one at stake, and
        // push every preview of a mature system to the worker for no reason.
        var affected = 0;
        foreach (var objectTypeId in AffectedObjectTypeIds(proposal, configuration))
            affected += await _application.ConnectedSystems.GetUnjoinedConnectedSystemObjectCountOfTypeAsync(configuration.ConnectedSystem.Id, objectTypeId);

        return new PreviewCostEstimate(affected);
    }

    /// <summary>
    /// Stage 2. Counted from the same evaluation the deltas come from rather than from set-based SQL, because
    /// matching is a query per object: whether one object matches a different Metaverse Object under the proposal
    /// is not answerable in aggregate, and a count that guessed would be the confident wrong number this preview
    /// exists to prevent.
    /// </summary>
    public async Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context) =>
        await PreviewImpactCounter.CountAsync((await CreateImpactCounterAsync(context))!, EvaluateDeltasAsync(context, CancellationToken.None));

    /// <summary>
    /// Stage 2 counted in the framework's one evaluation pass (#1530): one per delta, per transition, exactly as
    /// <see cref="CountImpactAsync"/> counts.
    /// </summary>
    public async Task<IPreviewImpactCounter?> CreateImpactCounterAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var configuration = await LoadAsync(context);
        return PreviewImpactCounter.PerDelta(connectedSystemId: configuration.ConnectedSystem.Id);
    }

    public async IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<ObjectMatchingProposal>();
        var configuration = await LoadAsync(context);

        // No matching change means no object can join differently, so the population is not read at all.
        if (configuration.StoredProposal.DescribesSameMatchingAs(proposal))
            yield break;

        foreach (var objectTypeId in AffectedObjectTypeIds(proposal, configuration))
        {
            var importRules = configuration.SyncRules
                .Where(rule => rule.Direction == SyncRuleDirection.Import && rule.ConnectedSystemObjectTypeId == objectTypeId)
                .OrderBy(rule => rule.Id)
                .ToList();

            var objectTypeName = configuration.ObjectTypesById.GetValueOrDefault(objectTypeId)?.Name;

            // The population is read as identifiers first, then fetched in batches. Enumerating a result set while
            // querying inside the loop is what Npgsql refuses outright: one command per connection, and the
            // matching engine issues a query per object. Streaming the objects instead made every match fail with
            // "a command is already in progress", which the matching engine reports as no match, so the preview
            // answered that nothing would change.
            var population = await _application.ConnectedSystems
                .GetUnjoinedConnectedSystemObjectIdsOfTypeAsync(configuration.ConnectedSystem.Id, objectTypeId);

            foreach (var batch in population.Chunk(EvaluationBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var csos = await _application.ConnectedSystems
                    .GetConnectedSystemObjectsByIdsNoTrackingAsync(configuration.ConnectedSystem.Id, batch);

                foreach (var cso in csos)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Belt and braces over the population query: matching runs at the join step, which only
                    // unjoined, live objects reach, and reporting a joined object as re-homed would be the worst
                    // thing this preview could say.
                    if (cso.MetaverseObjectId != null || cso.MetaverseObject != null)
                        continue;
                    if (cso.Status != ConnectedSystemObjectStatus.Normal)
                        continue;

                    var (scopedImportRules, undecided) = ScopedImportRules(cso, importRules);
                    if (undecided != null)
                    {
                        // The run stops an object whose scope it cannot decide before the join step, under the
                        // stored matching and the proposal alike (#2264): said, with the reason, and the walk goes on.
                        yield return new PreviewDelta(
                            ActivityRunProfileExecutionItemSyncOutcomeType.WouldFail,
                            ObjectDisplayName: cso.NameOrId,
                            ObjectTypeName: objectTypeName ?? cso.Type?.Name,
                            ConnectedSystemObjectId: cso.Id,
                            ConnectedSystemId: configuration.ConnectedSystem.Id,
                            AttributeName: PreviewScopeAnswer.AttributeName,
                            NewValue: undecided);
                        continue;
                    }

                    if (scopedImportRules == null)
                        continue;

                    var storedOutcome = await MatchAsync(cso, configuration.StoredProposal, configuration, scopedImportRules);
                    var proposedOutcome = await MatchAsync(cso, proposal, configuration, scopedImportRules);

                    var delta = Describe(cso, storedOutcome, proposedOutcome, configuration.ConnectedSystem.Id, objectTypeName);
                    if (delta != null)
                        yield return delta;
                }
            }
        }
    }

    #region evaluation

    /// <summary>
    /// The import Synchronisation Rules the object would be matched under, or null when it never reaches the join
    /// step at all. Mirrors the synchronisation processor exactly: an object out of scope of every import rule
    /// carrying criteria is handled as out-of-scope and never matched, and where no rule scopes it in, every rule
    /// applies. Every rule is decided, as the processor decides them, and one it cannot decide stops the object:
    /// <c>Undecided</c> then says why, and no rules are returned.
    /// </summary>
    private (List<SyncRule>? Rules, string? Undecided) ScopedImportRules(ConnectedSystemObject cso, List<SyncRule> importRules)
    {
        var scopes = importRules
            .Select(rule => (Rule: rule, Scope: PreviewScopeAnswer.Decide(() => _application.ScopingEvaluation.IsCsoInScopeForImportRule(cso, rule))))
            .ToList();

        var undecided = scopes.Find(entry => entry.Scope.Undecided);
        if (undecided.Rule != null)
            return (null, undecided.Scope.Describe());

        var inScope = scopes.Where(entry => entry.Scope.InScope).Select(entry => entry.Rule).ToList();

        if (inScope.Count == 0 && importRules.Exists(rule => rule.ObjectScopingCriteriaGroups.Count > 0))
            return (null, null);

        return (inScope.Count > 0 ? inScope : importRules, null);
    }

    /// <summary>
    /// What the matching engine answers for one object under one matching configuration.
    /// </summary>
    private async Task<MatchOutcome> MatchAsync(
        ConnectedSystemObject cso,
        ObjectMatchingProposal proposal,
        MatchingConfiguration configuration,
        List<SyncRule> scopedImportRules)
    {
        var rules = ResolveRules(cso, proposal, configuration, scopedImportRules);
        if (rules.Count == 0)
            return MatchOutcome.NoMatch;

        try
        {
            var match = await _application.ObjectMatching.FindMatchingMetaverseObjectAsync(cso, rules);
            return match == null
                ? MatchOutcome.NoMatch
                : new MatchOutcome(MatchKind.Match, match.MetaverseObject, match.Resolution.Values);
        }
        catch (MultipleMatchesException ex)
        {
            return new MatchOutcome(MatchKind.Ambiguous, null, ex.MatchValues, Candidates: ex.Candidates);
        }
        catch (ObjectMatchingRuleFailedException ex)
        {
            // The run would stop this object with an ObjectMatchingRuleFailed error (#2052): neither joined nor
            // projected. The preview says so, with the reason.
            return new MatchOutcome(MatchKind.Failed, null, [], ex.Resolution.FailureMessage);
        }
    }

    /// <summary>
    /// The rules that would be evaluated for this object, in evaluation order. In Simple mode that is the object
    /// type's own rules; in Advanced mode it is each in-scope import rule's rules in turn, each searching that
    /// rule's Metaverse Object Type, which is how the synchronisation processor resolves them.
    /// </summary>
    private static List<ObjectMatchingRule> ResolveRules(
        ConnectedSystemObject cso,
        ObjectMatchingProposal proposal,
        MatchingConfiguration configuration,
        List<SyncRule> scopedImportRules)
    {
        if (proposal.Mode == ObjectMatchingRuleMode.ConnectedSystem)
        {
            return
            [
                .. proposal.RulesFor(cso.TypeId, syncRuleId: null)
                    .Select(rule => Materialise(rule, configuration, fallback: null))
            ];
        }

        var resolved = new List<ObjectMatchingRule>();
        foreach (var importRule in scopedImportRules)
        {
            var fallback = importRule.MetaverseObjectType
                ?? (importRule.MetaverseObjectTypeId is { } typeId ? configuration.MetaverseObjectTypesById.GetValueOrDefault(typeId) : null);

            resolved.AddRange(proposal.RulesFor(cso.TypeId, importRule.Id)
                .Select(rule => Materialise(rule, configuration, fallback)));
        }

        return resolved;
    }

    private static ObjectMatchingRule Materialise(ObjectMatchingRuleProposal rule, MatchingConfiguration configuration, MetaverseObjectType? fallback) =>
        ObjectMatchingProposalMaterialiser.Materialise(
            rule,
            configuration.ConnectedSystemAttributesById,
            configuration.MetaverseAttributesById,
            configuration.MetaverseObjectTypesById,
            fallback);

    /// <summary>
    /// The delta for one object, or null where the two answers agree and nothing about the object changes.
    /// </summary>
    private static PreviewDelta? Describe(ConnectedSystemObject cso, MatchOutcome stored, MatchOutcome proposed, int connectedSystemId, string? objectTypeName)
    {
        if (stored.Kind == proposed.Kind && stored.MetaverseObject?.Id == proposed.MetaverseObject?.Id)
            return null;

        var transition = (stored.Kind, proposed.Kind) switch
        {
            (_, MatchKind.Failed) => ActivityRunProfileExecutionItemSyncOutcomeType.WouldFail,
            (_, MatchKind.Ambiguous) => ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously,
            (MatchKind.Match, MatchKind.Match) => ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject,
            (_, MatchKind.Match) => ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject,
            _ => ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin
        };

        return new PreviewDelta(
            transition,
            ObjectDisplayName: cso.NameOrId,
            ObjectTypeName: objectTypeName ?? cso.Type?.Name,
            MetaverseObjectTypeId: proposed.MetaverseObject?.Type?.Id ?? stored.MetaverseObject?.Type?.Id,
            // The identity the object would end up on, so a drill-down row answers "joined to what?" rather than
            // "moved away from what?". Where the proposal joins nothing, the row names the identity being lost.
            MetaverseObjectId: proposed.MetaverseObject?.Id ?? stored.MetaverseObject?.Id,
            ConnectedSystemObjectId: cso.Id,
            ConnectedSystemId: connectedSystemId,
            AttributeName: MatchAttributeName,
            OldValue: stored.Describe(),
            NewValue: proposed.Describe(),
            // The row describes the proposal, so it lists what the proposal would find when that is ambiguous.
            MatchCandidates: proposed.Kind == MatchKind.Ambiguous ? proposed.Candidates : null);
    }

    private enum MatchKind
    {
        NoMatch,
        Match,
        Ambiguous,

        /// <summary>A rule could not be worked out for the object, so the run would stop it (#2052).</summary>
        Failed
    }

    /// <param name="MatchValues">What the deciding rule computed, so a row shows the value it matched on.</param>
    /// <param name="FailureMessage">Why a rule could not be worked out, for <see cref="MatchKind.Failed"/>.</param>
    /// <param name="Candidates">The identities an <see cref="MatchKind.Ambiguous"/> match found (#2052).</param>
    private record MatchOutcome(
        MatchKind Kind,
        MetaverseObject? MetaverseObject,
        IReadOnlyList<object>? MatchValues = null,
        string? FailureMessage = null,
        IReadOnlyList<ActivityRunProfileExecutionItemMatchCandidate>? Candidates = null)
    {

        public static readonly MatchOutcome NoMatch = new(MatchKind.NoMatch, null);

        /// <summary>How this outcome reads in a drill-down row's value column.</summary>
        public string Describe() => Kind switch
        {
            MatchKind.Match => MetaverseObject == null ? NoMatchValue : $"{MetaverseObject.NameOrId}{OnValues()}",
            MatchKind.Ambiguous => $"{AmbiguousValue}{OnValues()}",
            MatchKind.Failed => $"{FailedValue}: {FailureMessage}",
            _ => NoMatchValue
        };

        private string OnValues() => MatchValues is { Count: > 0 }
            ? $" (on {string.Join(", ", MatchValues.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)))})"
            : string.Empty;
    }

    #endregion

    #region validation helpers

    /// <summary>
    /// The proposed rules the matching engine could not evaluate. Every one of these fails each object it meets at run
    /// time, or matches nothing, so they are refused here rather than discovered during a synchronisation. What a rule
    /// compares is judged by the validator the save uses, so the preview and the save cannot disagree.
    /// </summary>
    private static IEnumerable<PreviewValidationFinding> DescribeUnusableRules(ObjectMatchingProposal proposal, MatchingConfiguration configuration)
    {
        foreach (var rule in proposal.Rules)
        {
            var where = DescribeRule(rule, configuration);

            if (proposal.Mode == ObjectMatchingRuleMode.ConnectedSystem && rule.MetaverseObjectTypeId == null)
                yield return Blocking($"{where} names no Metaverse Object Type to search. Simple mode rules must name one; in Advanced mode the Synchronisation Rule supplies it.");

            var problem = DescribeComparisonProblem(rule, configuration);
            if (problem != null)
                yield return Blocking($"{where}: {problem}");
        }
    }

    /// <summary>
    /// Why what <paramref name="rule"/> compares could never be worked out, or null when it can.
    /// </summary>
    private static string? DescribeComparisonProblem(ObjectMatchingRuleProposal rule, MatchingConfiguration configuration)
    {
        var materialised = Materialise(rule, configuration, fallback: null);
        var shape = materialised.DescribeComparisonInvalidity();
        if (shape != null)
            return shape;

        if (materialised.TargetMetaverseAttribute is not { } target)
            return "It targets a Metaverse Attribute that no longer exists.";

        return ObjectTypeOf(rule, configuration) is { } objectTypeId && configuration.ObjectTypesById.TryGetValue(objectTypeId, out var objectType)
            ? Validator.DescribeProblem(materialised, objectType, target)
            : "It belongs to a Connected System Object Type that is not part of this system's schema.";
    }

    private static PreviewValidationFinding Blocking(string message) =>
        new(PreviewValidationSeverity.Blocking, message, nameof(ConnectedSystemObjectType.ObjectMatchingRules));

    /// <summary>
    /// How a rule is named in a finding, so an administrator can find the one being complained about.
    /// </summary>
    private static string DescribeRule(ObjectMatchingRuleProposal rule, MatchingConfiguration configuration)
    {
        var owner = rule.SyncRuleId is { } syncRuleId
            ? configuration.SyncRules.FirstOrDefault(candidate => candidate.Id == syncRuleId)?.Name ?? "a Synchronisation Rule"
            : configuration.ObjectTypesById.GetValueOrDefault(rule.ConnectedSystemObjectTypeId ?? 0)?.Name ?? "an Object Type";

        return $"Object Matching Rule {rule.Order + 1} on '{owner}'";
    }

    #endregion

    #region configuration

    /// <summary>
    /// Everything both evaluations read, loaded once. Held together rather than passed as six parameters because
    /// the stored configuration and the lookups a proposal is materialised against are one snapshot: reading them
    /// at different moments would let a concurrent edit put the baseline and the proposal out of step.
    /// </summary>
    private sealed record MatchingConfiguration(
        ConnectedSystem ConnectedSystem,
        ObjectMatchingProposal StoredProposal,
        IReadOnlyList<SyncRule> SyncRules,
        IReadOnlyDictionary<int, ConnectedSystemObjectType> ObjectTypesById,
        IReadOnlyDictionary<int, ConnectedSystemObjectTypeAttribute> ConnectedSystemAttributesById,
        IReadOnlyDictionary<int, MetaverseAttribute> MetaverseAttributesById,
        IReadOnlyDictionary<int, MetaverseObjectType> MetaverseObjectTypesById);

    private async Task<MatchingConfiguration> LoadAsync(PreviewContext context)
    {
        if (context.TargetId is not { } connectedSystemId)
            throw new InvalidOperationException("An Object Matching preview must name the Connected System it is about.");

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId)
            ?? throw new InvalidOperationException($"Connected System {connectedSystemId} was not found, so its Object Matching cannot be previewed.");

        var objectTypes = await _application.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);
        var syncRules = await _application.ConnectedSystems.GetSyncRulesAsync(connectedSystemId, includeDisabledSyncRules: false);
        var metaverseObjectTypes = await _application.Metaverse.GetMetaverseObjectTypesAsync(true);

        return new MatchingConfiguration(
            connectedSystem,
            ObjectMatchingProposal.FromCurrentConfiguration(connectedSystem, objectTypes, syncRules),
            syncRules,
            objectTypes.ToDictionary(objectType => objectType.Id),
            objectTypes.SelectMany(objectType => objectType.Attributes).GroupBy(attribute => attribute.Id).ToDictionary(group => group.Key, group => group.First()),
            metaverseObjectTypes.SelectMany(objectType => objectType.Attributes).GroupBy(attribute => attribute.Id).ToDictionary(group => group.Key, group => group.First()),
            metaverseObjectTypes.ToDictionary(objectType => objectType.Id));
    }

    /// <summary>
    /// The Connected System Object Types whose objects could match differently: every type carrying rules on
    /// either side of the change. A type with no rules before and none after cannot move an object.
    /// </summary>
    private static List<int> AffectedObjectTypeIds(ObjectMatchingProposal proposal, MatchingConfiguration configuration) =>
    [
        .. proposal.Rules.Concat(configuration.StoredProposal.Rules)
            .Select(rule => ObjectTypeOf(rule, configuration))
            .Where(objectTypeId => objectTypeId != null)
            .Select(objectTypeId => objectTypeId!.Value)
            .Distinct()
            .Order()
    ];

    /// <summary>
    /// The Connected System Object Type a rule concerns: its own in Simple mode, its owning Synchronisation Rule's
    /// in Advanced.
    /// </summary>
    private static int? ObjectTypeOf(ObjectMatchingRuleProposal rule, MatchingConfiguration configuration) =>
        rule.ConnectedSystemObjectTypeId
        ?? (rule.SyncRuleId is { } syncRuleId
            ? configuration.SyncRules.FirstOrDefault(candidate => candidate.Id == syncRuleId)?.ConnectedSystemObjectTypeId
            : null);

    #endregion
}
