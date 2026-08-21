// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.CompilerServices;
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

    /// <summary>
    /// The Connected System attribute types the matching query can compare. Anything else makes the query throw
    /// at run time, so a proposal naming one is refused before an administrator can save it.
    /// </summary>
    /// <summary>
    /// How many objects are fetched per round trip while evaluating. Batched because the population is read as
    /// identifiers and the objects behind them are needed in memory to be matched, and a preview over hundreds of
    /// thousands of accounts must not put all of them there at once.
    /// </summary>
    private const int EvaluationBatchSize = 200;

    private static readonly AttributeDataType[] MatchableAttributeTypes =
        [AttributeDataType.Text, AttributeDataType.Number, AttributeDataType.LongNumber, AttributeDataType.Decimal, AttributeDataType.Guid];

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
    public async Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var configuration = await LoadAsync(context);
        var counts = new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int>();

        await foreach (var delta in EvaluateDeltasAsync(context, CancellationToken.None))
            counts[delta.TransitionType] = counts.GetValueOrDefault(delta.TransitionType) + 1;

        return
        [
            .. counts
                .OrderByDescending(count => count.Value)
                .ThenBy(count => count.Key)
                .Select(count => new PreviewImpactCount(count.Key, count.Value, ConnectedSystemId: configuration.ConnectedSystem.Id))
        ];
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

                    var scopedImportRules = ScopedImportRules(cso, importRules);
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
    /// applies.
    /// </summary>
    private List<SyncRule>? ScopedImportRules(ConnectedSystemObject cso, List<SyncRule> importRules)
    {
        var inScope = importRules
            .Where(rule => _application.ScopingEvaluation.IsCsoInScopeForImportRule(cso, rule))
            .ToList();

        if (inScope.Count == 0 && importRules.Exists(rule => rule.ObjectScopingCriteriaGroups.Count > 0))
            return null;

        return inScope.Count > 0 ? inScope : importRules;
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
            var mvo = await _application.ObjectMatching.FindMatchingMetaverseObjectAsync(cso, rules);
            return mvo == null ? MatchOutcome.NoMatch : new MatchOutcome(MatchKind.Match, mvo);
        }
        catch (MultipleMatchesException)
        {
            return MatchOutcome.Ambiguous;
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
            NewValue: proposed.Describe());
    }

    private enum MatchKind
    {
        NoMatch,
        Match,
        Ambiguous
    }

    private record MatchOutcome(MatchKind Kind, MetaverseObject? MetaverseObject)
    {
        public static readonly MatchOutcome NoMatch = new(MatchKind.NoMatch, null);
        public static readonly MatchOutcome Ambiguous = new(MatchKind.Ambiguous, null);

        /// <summary>How this outcome reads in a drill-down row's value column.</summary>
        public string Describe() => Kind switch
        {
            MatchKind.Match => MetaverseObject?.NameOrId ?? NoMatchValue,
            MatchKind.Ambiguous => AmbiguousValue,
            _ => NoMatchValue
        };
    }

    #endregion

    #region validation helpers

    /// <summary>
    /// The proposed rules the matching engine could not evaluate. Every one of these throws or silently matches
    /// nothing at run time, so they are refused here rather than discovered during a synchronisation.
    /// </summary>
    private static IEnumerable<PreviewValidationFinding> DescribeUnusableRules(ObjectMatchingProposal proposal, MatchingConfiguration configuration)
    {
        foreach (var rule in proposal.Rules)
        {
            var where = DescribeRule(rule, configuration);

            if (rule.Sources.Count == 0)
            {
                yield return Blocking($"{where} has no source attribute, so it has nothing to match on.");
                continue;
            }

            if (rule.Sources.Count > 1)
                yield return Blocking($"{where} has more than one source. Object Matching Rules support a single source; multi-source (advanced) matching is not implemented.");

            foreach (var source in rule.Sources)
            {
                if (!string.IsNullOrWhiteSpace(source.Expression))
                {
                    yield return Blocking($"{where} matches on an expression. Object Matching Rules compare attribute values only; expression sources are not supported yet.");
                    continue;
                }

                if (source.ConnectedSystemAttributeId is not { } attributeId)
                {
                    yield return Blocking($"{where} has a source naming no Connected System attribute.");
                    continue;
                }

                var attribute = configuration.ConnectedSystemAttributesById.GetValueOrDefault(attributeId);
                if (attribute == null)
                {
                    yield return Blocking($"{where} matches on a Connected System attribute that is not part of this system's schema.");
                    continue;
                }

                if (!MatchableAttributeTypes.Contains(attribute.Type))
                    yield return Blocking($"{where} matches on '{attribute.Name}', which is of type {attribute.Type}. Object Matching compares Text, Number, Long Number, Decimal and Guid attributes only.");
            }

            if (rule.TargetMetaverseAttributeId is not { } targetId)
                yield return Blocking($"{where} names no target Metaverse Attribute, so there is nothing to compare its source against.");
            else if (!configuration.MetaverseAttributesById.ContainsKey(targetId))
                yield return Blocking($"{where} targets a Metaverse Attribute that no longer exists.");

            if (proposal.Mode == ObjectMatchingRuleMode.ConnectedSystem && rule.MetaverseObjectTypeId == null)
                yield return Blocking($"{where} names no Metaverse Object Type to search. Simple mode rules must name one; in Advanced mode the Synchronisation Rule supplies it.");
        }
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
