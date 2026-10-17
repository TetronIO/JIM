// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Expressions;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;
namespace JIM.Application.Servers;

/// <summary>
/// Pure matching engine for object matching during import (CSO→MVO join) and export (MVO→CSO lookup).
/// Callers are responsible for resolving which matching rules apply (based on mode).
/// This server evaluates the provided rules and returns matches.
/// </summary>
public class ObjectMatchingServer
{
    #region accessors
    private JimApplication Application { get; }

    /// <summary>What each rule compares for an object; shared by every matching path (#2052).</summary>
    private readonly MatchValueResolver _matchValueResolver = new(new DynamicExpressoEvaluator());
    #endregion

    #region constructors
    internal ObjectMatchingServer(JimApplication application)
    {
        Application = application;
    }
    #endregion


    #region public methods

    /// <summary>
    /// Types <paramref name="inputs"/> from <paramref name="objectType"/>'s schema and resolves <paramref name="rule"/>'s
    /// match values from them, with no lookup: the part of a test that needs no database, shared by
    /// <see cref="TestAsync"/> and the saved test case runner (#2055 Phase 4).
    /// </summary>
    /// <param name="attributeValues">The typed inputs, as the values a Connected System Object would hold.</param>
    internal static ObjectMatchingTestResult TestMatchValues(
        MatchValueResolver matchValueResolver,
        ObjectMatchingRule rule,
        ConnectedSystemObjectType objectType,
        IReadOnlyList<AttributeSample> inputs,
        out List<ConnectedSystemObjectAttributeValue> attributeValues)
    {
        var errors = new List<string>();
        attributeValues = [];
        foreach (var input in inputs.Where(input => input.Values.Count > 0))
        {
            var attribute = objectType.Attributes.FirstOrDefault(a => string.Equals(a.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase));
            if (attribute == null)
            {
                errors.Add($"cs[\"{input.AttributeName}\"] has a value, but '{objectType.Name}' has no attribute of that name, so the rule would never see one.");
                continue;
            }

            attributeValues.AddRange(TestSampleValues.Typed($"cs[\"{attribute.Name}\"]", input.Values, attribute.Type, attribute.AttributePlurality, errors)
                .Select(typed => TestSampleValues.ToConnectedSystemValue(attribute, typed)));
        }

        return errors.Count > 0
            ? new ObjectMatchingTestResult { InputErrors = errors }
            : new ObjectMatchingTestResult { Resolution = matchValueResolver.Resolve(rule, attributeValues, objectType) };
    }

    /// <summary>
    /// The attributes <paramref name="rule"/> reads from an object of <paramref name="objectType"/>, each once: its
    /// source attribute, or each attribute of the type its Expression reads. The inputs an Object Matching test takes
    /// (#2055).
    /// </summary>
    public IReadOnlyList<AttributeSampleDescriptor> DescribeTestInputs(ObjectMatchingRule rule, ConnectedSystemObjectType objectType)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(objectType);
        return ReadAttributes(rule, objectType)
            .Select(attribute => new AttributeSampleDescriptor(attribute.Name, attribute.Type, attribute.AttributePlurality))
            .ToList();
    }

    /// <summary>
    /// The values <paramref name="connectedSystemObject"/> holds for each attribute <paramref name="rule"/> reads,
    /// every value of a multi-valued attribute included, so a real object can be tested and then edited (#2055).
    /// </summary>
    public IReadOnlyList<AttributeSample> TestInputsFromConnectedSystemObject(
        ConnectedSystemObject connectedSystemObject,
        ObjectMatchingRule rule,
        ConnectedSystemObjectType objectType)
    {
        ArgumentNullException.ThrowIfNull(connectedSystemObject);
        return ReadAttributes(rule, objectType)
            .Select(attribute => new AttributeSample(attribute.Name, connectedSystemObject.AttributeValues
                .Where(value => value.AttributeId == attribute.Id)
                .Select(value => TestSampleValues.FromConnectedSystemValue(value, attribute.Type))
                .Where(value => value != null)
                .Select(value => value!)
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// The Object Matching tester (#2055): what <paramref name="rule"/> would compare for an object of
    /// <paramref name="objectType"/> holding the sample <paramref name="inputs"/>, worked out by the resolver
    /// synchronisation uses, and, with <paramref name="lookUp"/>, what those values find in the Metaverse, by the
    /// lookup an import join runs through a repository that cannot write.
    /// </summary>
    /// <param name="rule">The rule as composed, saved or not, with its Target Metaverse Attribute and source set. For a
    /// lookup it carries the Metaverse Object Type it matches against (in Synchronisation Rule mode, the rule's).</param>
    /// <param name="objectType">The Object Type the rule reads, with its attributes and Connected System.</param>
    /// <param name="inputs">The samples, by attribute name; an attribute not given holds no value.</param>
    /// <param name="lookUp">Whether to look the values up in the Metaverse.</param>
    /// <param name="testedObject">The real object the samples were loaded from, if any, so a lookup that finds the
    /// Metaverse Object it is already joined to says so.</param>
    public async Task<ObjectMatchingTestResult> TestAsync(
        ObjectMatchingRule rule,
        ConnectedSystemObjectType objectType,
        IReadOnlyList<AttributeSample> inputs,
        bool lookUp,
        ConnectedSystemObject? testedObject = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentNullException.ThrowIfNull(inputs);

        var matchValues = TestMatchValues(_matchValueResolver, rule, objectType, inputs, out var attributeValues);
        if (!lookUp || matchValues.Resolution is not { Kind: MatchValueResolutionKind.Values } resolution)
            return matchValues;

        if (rule.MetaverseObjectType == null)
            throw new ArgumentException("A lookup needs the rule's Metaverse Object Type.", nameof(rule));

        var connectedSystemObject = new ConnectedSystemObject
        {
            Id = testedObject?.Id ?? Guid.Empty,
            TypeId = objectType.Id,
            Type = objectType,
            ConnectedSystemId = objectType.ConnectedSystemId
        };
        connectedSystemObject.AttributeValues.AddRange(attributeValues);

        return new ObjectMatchingTestResult
        {
            Resolution = resolution,
            Lookup = await LookUpAsync(connectedSystemObject, rule, objectType.ConnectedSystemId, testedObject)
        };
    }

    /// <summary>
    /// What an import join would find for <paramref name="connectedSystemObject"/> with <paramref name="rule"/> alone,
    /// including the join's own refusal of a Metaverse Object already joined from this Connected System. Read only.
    /// </summary>
    private async Task<ObjectMatchingLookup> LookUpAsync(ConnectedSystemObject connectedSystemObject, ObjectMatchingRule rule,
        int connectedSystemId, ConnectedSystemObject? testedObject)
    {
        var repository = new ReadOnlySyncRepositoryGuard(Application.SyncRepo);
        ImportMatch? match;
        try
        {
            match = await FindMatchingMetaverseObjectAsync(repository, connectedSystemObject, [rule]);
        }
        catch (MultipleMatchesException ambiguity)
        {
            return new ObjectMatchingLookup
            {
                Outcome = ObjectMatchingLookupOutcome.Ambiguous,
                Candidates = ambiguity.Candidates,
                Description = DescribeAmbiguity(ambiguity, previewing: true)
            };
        }

        if (match == null)
        {
            return new ObjectMatchingLookup
            {
                Outcome = ObjectMatchingLookupOutcome.NoMatch,
                Description = "No Metaverse Object holds these values, so a synchronisation would try the next Object Matching Rule, " +
                              "and project the object if no rule matches and its Synchronisation Rule projects."
            };
        }

        var identity = match.MetaverseObject;
        var name = identity.Name ?? identity.Id.ToString();
        var outcome = testedObject?.MetaverseObjectId == identity.Id
            ? ObjectMatchingLookupOutcome.AlreadyJoined
            : await repository.GetConnectedSystemObjectCountByMvoAsync(connectedSystemId, identity.Id) > 0
                ? ObjectMatchingLookupOutcome.JoinedToAnother
                : ObjectMatchingLookupOutcome.WouldJoin;

        return new ObjectMatchingLookup
        {
            Outcome = outcome,
            MetaverseObjectId = identity.Id,
            MetaverseObjectName = identity.Name,
            MetaverseObjectTypeName = identity.Type?.Name,
            Description = outcome switch
            {
                ObjectMatchingLookupOutcome.AlreadyJoined =>
                    $"This object is already joined to {name}, the Metaverse Object it matches on {match.MatchedValue}.",
                ObjectMatchingLookupOutcome.JoinedToAnother =>
                    $"The values match {name} on {match.MatchedValue}, but another Connected System Object from this Connected System " +
                    "is already joined to it, so a synchronisation would record that this object could not join.",
                _ => $"A synchronisation would join this object to {name}, matched on {match.MatchedValue}."
            }
        };
    }

    /// <summary>The attributes of <paramref name="objectType"/> <paramref name="rule"/> reads, each once.</summary>
    private static List<ConnectedSystemObjectTypeAttribute> ReadAttributes(ObjectMatchingRule rule, ConnectedSystemObjectType objectType)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(objectType);

        var source = rule.Sources.Count == 1 ? rule.Sources[0] : null;
        if (source == null)
            return [];

        if (string.IsNullOrWhiteSpace(source.Expression))
        {
            var attribute = source.ConnectedSystemAttribute
                ?? objectType.Attributes.FirstOrDefault(a => a.Id == source.ConnectedSystemAttributeId);
            return attribute == null ? [] : [objectType.Attributes.FirstOrDefault(a => a.Id == attribute.Id) ?? attribute];
        }

        return ExpressionInputResolver.Resolve(source.Expression)
            .Where(input => input.Source == ExpressionInputSource.ConnectedSystem)
            .Select(input => objectType.Attributes.FirstOrDefault(a => string.Equals(a.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase)))
            .Where(attribute => attribute != null)
            .Select(attribute => attribute!)
            .DistinctBy(attribute => attribute.Id)
            .ToList();
    }

    /// <summary>
    /// Attempts to find a Metaverse Object that matches a Connected System Object using the provided matching rules.
    /// This is used during import to join CSOs to existing MVOs.
    /// The caller is responsible for resolving which rules apply based on the matching mode.
    /// </summary>
    /// <param name="connectedSystemObject">The CSO to find a matching MVO for.</param>
    /// <param name="matchingRules">The matching rules to evaluate. Each rule must carry its own
    /// <see cref="ObjectMatchingRule.MetaverseObjectType"/> (simple mode) or the caller must set it
    /// from the Synchronisation Rule before passing (advanced mode).</param>
    /// <returns>The match, or null if no rule found one.</returns>
    /// <exception cref="MultipleMatchesException">A rule's values find more than one Metaverse Object.</exception>
    /// <exception cref="ObjectMatchingRuleFailedException">A rule could not be worked out for this object.</exception>
    public Task<ImportMatch?> FindMatchingMetaverseObjectAsync(
        ConnectedSystemObject connectedSystemObject,
        List<ObjectMatchingRule> matchingRules)
        => FindMatchingMetaverseObjectAsync(Application.SyncRepo, connectedSystemObject, matchingRules);

    /// <summary>
    /// As <see cref="FindMatchingMetaverseObjectAsync(ConnectedSystemObject, List{ObjectMatchingRule})"/>, against the
    /// given repository: Sync Preview passes its read-only guard, so a preview evaluates rules exactly as a
    /// synchronisation would without being able to write.
    /// </summary>
    internal async Task<ImportMatch?> FindMatchingMetaverseObjectAsync(
        ISyncRepository repository,
        ConnectedSystemObject connectedSystemObject,
        List<ObjectMatchingRule> matchingRules)
    {
        if (matchingRules.Count == 0)
        {
            Log.Debug("FindMatchingMetaverseObjectAsync: No matching rules provided for CSO {CsoId}", connectedSystemObject.Id);
            return null;
        }

        // Rules are tried in order. Only a rule with nothing to look up, or one that finds nothing, lets the next rule
        // be tried: a rule that cannot be worked out, or whose values find more than one Metaverse Object, stops the
        // object (#2052). Trying the next rule instead would let the object join on a weaker rule, or project a
        // duplicate, because of a fault the administrator never sees.
        foreach (var matchingRule in matchingRules.OrderBy(r => r.Order))
        {
            // Rule must have a MetaverseObjectType to know where to search. This is a configuration state the portal
            // already flags, and skipping it is deterministic.
            var metaverseObjectType = matchingRule.MetaverseObjectType;
            if (metaverseObjectType == null)
            {
                Log.Warning("FindMatchingMetaverseObjectAsync: Skipping matching rule {RuleId} — no MetaverseObjectType set", matchingRule.Id);
                continue;
            }

            var resolution = _matchValueResolver.Resolve(matchingRule, connectedSystemObject.AttributeValues, connectedSystemObject.Type);
            if (resolution.Kind == MatchValueResolutionKind.Failed)
                throw new ObjectMatchingRuleFailedException(matchingRule, resolution);
            if (resolution.Kind == MatchValueResolutionKind.NoValue)
                continue;

            // Resolve succeeded, so the rule has a Target Metaverse Attribute.
            var ids = await repository.FindMetaverseObjectIdsByMatchValuesAsync(
                metaverseObjectType.Id,
                matchingRule.TargetMetaverseAttribute!.Id,
                resolution.ValueType,
                resolution.Values,
                matchingRule.CaseSensitive,
                take: ActivityRunProfileExecutionItemMatchCandidate.MaxRecorded + 1);

            if (ids.Count == 0)
                continue;

            if (ids.Count > 1)
            {
                var candidates = await DescribeCandidatesAsync(repository, metaverseObjectType.Id, matchingRule, resolution, ids);
                throw new MultipleMatchesException("Multiple Metaverse Objects were found to match the source attribute.", [.. ids])
                {
                    Rule = matchingRule,
                    MatchValues = resolution.Values,
                    Candidates = candidates
                };
            }

            var mvo = await repository.GetMetaverseObjectForJoinMatchAsync(ids[0]);
            if (mvo == null)
            {
                // Found by the lookup and gone by the load: deleted in between. Nothing to join to.
                Log.Warning("FindMatchingMetaverseObjectAsync: MVO {MvoId} matched by rule {RuleId} no longer exists", ids[0], matchingRule.Id);
                continue;
            }

            Log.Debug("FindMatchingMetaverseObjectAsync: Found MVO {MvoId} using rule {RuleId}", mvo.Id, matchingRule.Id);
            return new ImportMatch(mvo, matchingRule, resolution, DescribeMatchedValues(mvo, matchingRule, resolution));
        }

        return null;
    }

    /// <summary>
    /// The candidates an ambiguous match lists on its Activity item (#2052), so the administrator can open each one
    /// rather than read a count. At most <see cref="ActivityRunProfileExecutionItemMatchCandidate.MaxRecorded"/> are
    /// listed, ordered by display name; the full total is counted only when the lookup found more than that.
    /// </summary>
    private static async Task<IReadOnlyList<ActivityRunProfileExecutionItemMatchCandidate>> DescribeCandidatesAsync(
        ISyncRepository repository,
        int metaverseObjectTypeId,
        ObjectMatchingRule rule,
        MatchValueResolution resolution,
        IReadOnlyList<Guid> ids)
    {
        var total = ids.Count > ActivityRunProfileExecutionItemMatchCandidate.MaxRecorded
            ? await repository.CountMetaverseObjectsByMatchValuesAsync(
                metaverseObjectTypeId, rule.TargetMetaverseAttribute!.Id, resolution.ValueType, resolution.Values, rule.CaseSensitive)
            : ids.Count;

        var listed = await repository.GetMetaverseObjectsByIdsNoTrackingAsync(ids.Take(ActivityRunProfileExecutionItemMatchCandidate.MaxRecorded));
        var description = rule.DescribeComparison();
        return listed
            .OrderBy(mvo => mvo.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mvo => mvo.Id)
            .Select((mvo, ordinal) => new ActivityRunProfileExecutionItemMatchCandidate
            {
                CandidateKind = MatchCandidateKind.MetaverseObject,
                CandidateId = mvo.Id,
                DisplayNameSnapshot = mvo.Name,
                TypeNameSnapshot = mvo.Type?.Name,
                MatchedValue = DescribeMatchedValues(mvo, rule, resolution),
                ObjectMatchingRuleId = rule.Id == 0 ? null : rule.Id,
                ObjectMatchingRuleDescriptionSnapshot = description,
                Ordinal = ordinal,
                CandidateTotal = total
            })
            .ToList();
    }

    /// <summary>
    /// The error an ambiguous match records (#2052): how many identities the object matched, on what, and why that
    /// stops it. The candidates themselves travel separately, so the message does not list ids.
    /// </summary>
    /// <param name="ambiguity">The ambiguous match.</param>
    /// <param name="previewing">Whether a preview is describing what a synchronisation would do.</param>
    public static string DescribeAmbiguity(MultipleMatchesException ambiguity, bool previewing = false)
    {
        var outcome = previewing ? "a synchronisation would neither join nor project it" : "it was neither joined nor projected";
        return $"This object matches {CountOf(ambiguity)} Metaverse Objects{DescribeMatchedOn(ambiguity)}, so {outcome}. " +
               "A Connected System Object can join only one Metaverse Object: correct the data, or the Object Matching Rule, " +
               "so that its values identify one.";
    }

    /// <summary>
    /// The error an ambiguous match before provisioning records (#2052): how many existing Connected System Objects the
    /// Metaverse Object matched in the target, on what, and why that stops it. The candidates travel separately.
    /// </summary>
    /// <param name="ambiguity">The ambiguous match.</param>
    /// <param name="connectedSystemName">The target Connected System's name.</param>
    /// <param name="previewing">Whether a preview is describing what a synchronisation would do.</param>
    public static string DescribeProvisioningAmbiguity(MultipleMatchesException ambiguity, string connectedSystemName, bool previewing = false)
    {
        var outcome = previewing
            ? "a synchronisation would neither join it to one nor provision it"
            : "it was neither joined to one nor provisioned";
        return $"This Metaverse Object matches {CountOf(ambiguity)} Connected System Objects in '{connectedSystemName}'" +
               $"{DescribeMatchedOn(ambiguity)}, so {outcome}. A Metaverse Object can join only one Connected System Object in " +
               "each Connected System: correct the data, or the Object Matching Rule, so that its values identify one.";
    }

    private static int CountOf(MultipleMatchesException ambiguity) =>
        ambiguity.Candidates.Count > 0 ? ambiguity.Candidates[0].CandidateTotal : ambiguity.Matches.Count;

    /// <summary>" on Employee ID equals employeeNumber ("E1001")", or nothing when the rule is not known.</summary>
    private static string DescribeMatchedOn(MultipleMatchesException ambiguity)
    {
        if (ambiguity.Rule == null)
            return string.Empty;

        const int maxValuesShown = 10;
        var values = ambiguity.MatchValues
            .Take(maxValuesShown)
            .Select(value => $"\"{Convert.ToString(value, CultureInfo.InvariantCulture)}\"")
            .ToList();
        if (ambiguity.MatchValues.Count > maxValuesShown)
            values.Add($"and {ambiguity.MatchValues.Count - maxValuesShown} more");
        return $" on {ambiguity.Rule.DescribeComparison()} ({string.Join(", ", values)})";
    }

    /// <summary>
    /// The looked-up values <paramref name="mvo"/> holds for the rule's Metaverse attribute, as text: what the join
    /// matched on, recorded on the joined object (#2052). Every value is listed when, unexpectedly, none can be
    /// confirmed, so the record never claims less than was looked up.
    /// </summary>
    private static string DescribeMatchedValues(MetaverseObject mvo, ObjectMatchingRule rule, MatchValueResolution resolution)
    {
        var attributeId = rule.TargetMetaverseAttribute!.Id;
        var held = mvo.AttributeValues
            .Where(av => av.AttributeId == attributeId)
            .Select(av => resolution.ValueType switch
            {
                AttributeDataType.Text => (object?)av.StringValue,
                AttributeDataType.Number => av.IntValue,
                AttributeDataType.LongNumber => av.LongValue,
                AttributeDataType.Decimal => av.DecimalValue,
                AttributeDataType.Guid => av.GuidValue,
                _ => null
            })
            .Where(value => value != null)
            .ToList();

        var comparer = rule.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        bool Holds(object value) => value is string text
            ? held.OfType<string>().Any(heldText => comparer.Equals(heldText, text))
            : held.Contains(value);

        var matched = resolution.Values.Where(Holds).ToList();
        return string.Join(", ", (matched.Count > 0 ? matched : resolution.Values)
            .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Attempts to find an existing Connected System Object in a target system that matches a Metaverse Object about
    /// to be provisioned there, so that it is joined rather than duplicated. The caller is responsible for resolving
    /// which rules apply based on the matching mode.
    /// </summary>
    /// <param name="metaverseObject">The Metaverse Object to find a matching Connected System Object for.</param>
    /// <param name="connectedSystem">The target Connected System.</param>
    /// <param name="connectedSystemObjectType">The Connected System Object Type to search within.</param>
    /// <param name="matchingRules">The matching rules to evaluate, tried in <see cref="ObjectMatchingRule.Order"/>.</param>
    /// <param name="prefetched">The page's prefetched candidates
    /// (<see cref="ExportEvaluationServer.PrefetchExportMatchCandidatesForPageAsync"/>), when the prefetch covered this
    /// (Metaverse Object, export Synchronisation Rule) pair (<see cref="ExportMatchCandidates.IsCovered"/>). An
    /// uncovered pair carries no information, so the caller passes null and the candidates are looked up instead.</param>
    /// <returns>The match, or null if no rule found one.</returns>
    /// <exception cref="MultipleMatchesException">A rule's values find more than one Connected System Object.</exception>
    /// <exception cref="ObjectMatchingRuleFailedException">A rule cannot be worked out for this Metaverse Object.</exception>
    public Task<ExportMatch?> FindMatchingConnectedSystemObjectAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        List<ObjectMatchingRule> matchingRules,
        ExportMatchCandidates? prefetched = null,
        IDictionary<(int ObjectMatchingRuleId, int ConnectedSystemObjectTypeId), ExpressionMatchValueIndex>? expressionIndexes = null,
        IDictionary<(int ObjectMatchingRuleId, int ConnectedSystemObjectTypeId), ExportMatchHeldObjects>? heldObjects = null)
        => FindMatchingConnectedSystemObjectAsync(
            Application.SyncRepo, metaverseObject, connectedSystem, connectedSystemObjectType, matchingRules, prefetched, expressionIndexes, heldObjects);

    /// <summary>
    /// As <see cref="FindMatchingConnectedSystemObjectAsync(MetaverseObject, ConnectedSystem, ConnectedSystemObjectType, List{ObjectMatchingRule}, ExportMatchCandidates?)"/>,
    /// against the given repository: Sync Preview passes its read-only guard, so a preview evaluates rules exactly as a
    /// synchronisation would without being able to write.
    /// </summary>
    internal async Task<ExportMatch?> FindMatchingConnectedSystemObjectAsync(
        ISyncRepository repository,
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        List<ObjectMatchingRule> matchingRules,
        ExportMatchCandidates? prefetched = null,
        IDictionary<(int ObjectMatchingRuleId, int ConnectedSystemObjectTypeId), ExpressionMatchValueIndex>? expressionIndexes = null,
        IDictionary<(int ObjectMatchingRuleId, int ConnectedSystemObjectTypeId), ExportMatchHeldObjects>? heldObjects = null)
    {
        if (matchingRules.Count == 0)
        {
            Log.Debug("FindMatchingConnectedSystemObjectAsync: No matching rules provided for export to CS {CsId}", connectedSystem.Id);
            return null;
        }

        // As on import (#2052): only a rule with nothing to look up, or one that finds nothing, lets the next rule be
        // tried. A rule that cannot be worked out, or whose values find more than one Connected System Object, stops
        // the Metaverse Object, because provisioning it anyway would create the duplicate matching exists to prevent,
        // and joining whichever object came first would hand the identity an account by accident.
        foreach (var matchingRule in matchingRules.OrderBy(r => r.Order))
        {
            var resolved = ExportMatchingValue.Resolve(metaverseObject, matchingRule);
            if (resolved.Outcome == ExportMatchingOutcome.NoMetaverseValue)
                continue;
            if (resolved.Outcome != ExportMatchingOutcome.Resolved)
                throw new ObjectMatchingRuleFailedException(matchingRule, DescribeExportFailure(matchingRule, resolved, connectedSystemObjectType), provisioning: true);

            // Which of the looked-up values found each object: every value is looked up, so two values finding two
            // objects is as ambiguous as one value finding both.
            var valuesById = new Dictionary<Guid, HashSet<object>>();
            var ids = new List<Guid>();
            void Found(object value, Guid id)
            {
                if (!valuesById.TryGetValue(id, out var values))
                {
                    values = [];
                    valuesById[id] = values;
                    ids.Add(id);
                }

                values.Add(value);
            }

            // The values that found an object, in the order the Metaverse Object holds them.
            string MatchedOn(Guid id) => FormatValues(resolved.Values.Where(valuesById[id].Contains));

            // A rule computing its value with an expression is answered by an index of what the expression computes for
            // each unjoined object; an attribute rule by the page's prefetch where it covers this pair, else a query.
            ExpressionMatchValueIndex? index = null;
            if (resolved.Expression != null)
            {
                index = await GetExpressionMatchIndexAsync(repository, matchingRule, connectedSystem, connectedSystemObjectType, expressionIndexes);
                foreach (var value in resolved.Values)
                {
                    foreach (var id in index.GetCandidates(value))
                        Found(value, id);
                }
            }
            else if (prefetched != null)
            {
                foreach (var value in resolved.Values)
                {
                    foreach (var id in prefetched.GetCandidates(matchingRule.Id, value))
                        Found(value, id);
                }
            }
            else
            {
                var found = await repository.GetExportMatchCandidateIdsAsync(
                    connectedSystem.Id, connectedSystemObjectType.Id, resolved.ConnectedSystemAttributeName!, resolved.DataType,
                    resolved.CaseSensitive, resolved.Values);
                foreach (var (value, id) in found)
                    Found(value, id);
            }

            // Load the candidates while they are still unjoined. One that has since been claimed (by another Metaverse
            // Object on this page) or is no longer Normal is passed over, and taken out of the prefetch so no later
            // lookup on the page is offered it again. Loading stops once there are more than can be listed.
            var eligible = new List<ConnectedSystemObject>();
            var loaded = 0;
            foreach (var id in ids)
            {
                if (eligible.Count > ActivityRunProfileExecutionItemMatchCandidate.MaxRecorded)
                    break;

                loaded++;
                var cso = await repository.GetConnectedSystemObjectForExportMatchAsync(id);
                if (cso != null)
                {
                    eligible.Add(cso);
                }
                else
                {
                    prefetched?.Remove(id);
                    index?.Remove(id);
                }
            }

            if (eligible.Count == 0)
            {
                // An index that could not check every object cannot say that nothing matched: one it could not check may
                // be this Metaverse Object's own account, and provisioning would duplicate it.
                if (index is { IsComplete: false, FirstUncheckable: var (uncheckableId, reason) })
                {
                    var described = index.UncheckableCount == 1 ? "1 existing Connected System Object" : $"{index.UncheckableCount} existing Connected System Objects";
                    throw new ObjectMatchingRuleFailedException(matchingRule, MatchValueResolution.Failed(
                        $"The expression could not be worked out for {described} in '{connectedSystem.Name}', so it cannot be ruled out " +
                        $"that one of them is this object's. For example, Connected System Object {uncheckableId}: {reason}",
                        resolved.DataType, resolved.Expression), provisioning: true);
                }

                continue;
            }

            if (eligible.Count > 1)
            {
                // Those not loaded were eligible when looked up, so they count towards the total.
                var total = eligible.Count + ids.Count - loaded;
                throw new MultipleMatchesException(
                    "Multiple Connected System Objects were found to match the Metaverse Object.", [.. eligible.Select(cso => cso.Id)])
                {
                    Rule = matchingRule,
                    MatchValues = resolved.Values,
                    Candidates = DescribeCandidates(eligible, connectedSystem, connectedSystemObjectType, matchingRule, MatchedOn, total)
                };
            }

            var match = eligible[0];
            Log.Debug("FindMatchingConnectedSystemObjectAsync: Found CSO {CsoId} using rule {RuleId}", match.Id, matchingRule.Id);
            return new ExportMatch(match, matchingRule, MatchedOn(match.Id));
        }

        return null;
    }

    /// <summary>
    /// The index of what an expression-sourced rule computes for each unjoined object of the type (#2052): the run's,
    /// built on first use and kept in <paramref name="runIndexes"/>, or, with no run to keep it for (Sync Preview), one
    /// built for this lookup alone.
    /// </summary>
    private async Task<ExpressionMatchValueIndex> GetExpressionMatchIndexAsync(
        ISyncRepository repository,
        ObjectMatchingRule rule,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType objectType,
        IDictionary<(int ObjectMatchingRuleId, int ConnectedSystemObjectTypeId), ExpressionMatchValueIndex>? runIndexes)
    {
        var key = (rule.Id, objectType.Id);
        if (runIndexes != null && runIndexes.TryGetValue(key, out var existing))
            return existing;

        var index = await BuildExpressionMatchIndexAsync(repository, rule, connectedSystem, objectType);
        if (runIndexes != null)
            runIndexes[key] = index;
        return index;
    }

    /// <summary>
    /// Works the rule's expression out for every unjoined, Normal object of the type, reading only the attributes it
    /// reads, through the same resolver the import join uses, so the two directions agree on what a rule computes.
    /// </summary>
    private async Task<ExpressionMatchValueIndex> BuildExpressionMatchIndexAsync(
        ISyncRepository repository,
        ObjectMatchingRule rule,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType objectType)
    {
        using var span = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("BuildExpressionMatchIndex");
        var expression = rule.Sources[0].Expression;
        var attributeIds = ExpressionInputResolver.ResolveCached(expression)
            .Where(input => input.Source == ExpressionInputSource.ConnectedSystem)
            .Select(input => objectType.Attributes.FirstOrDefault(a => string.Equals(a.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase)))
            .Where(attribute => attribute != null)
            .Select(attribute => attribute!.Id)
            .Distinct()
            .ToList();

        var index = new ExpressionMatchValueIndex(rule.CaseSensitive);
        var objectCount = 0;
        await foreach (var (id, values) in repository.StreamUnjoinedConnectedSystemObjectValues(connectedSystem.Id, objectType.Id, attributeIds))
        {
            objectCount++;
            var resolution = _matchValueResolver.Resolve(rule, values, objectType);
            switch (resolution.Kind)
            {
                case MatchValueResolutionKind.Values:
                    foreach (var value in resolution.Values)
                        index.Add(value, id);
                    break;
                case MatchValueResolutionKind.Failed:
                    if (index.UncheckableCount < ActivityRunProfileExecutionItemMatchCandidate.MaxRecorded)
                        Log.Warning("BuildExpressionMatchIndexAsync: Object Matching Rule {RuleId} could not be worked out for CSO {CsoId}: {Reason}",
                            rule.Id, id, resolution.FailureMessage);
                    index.MarkUncheckable(id, resolution.FailureMessage ?? "The expression could not be worked out.");
                    break;
            }
        }

        span.SetTag("objectCount", objectCount);
        span.SetTag("uncheckableCount", index.UncheckableCount);
        span.SetSuccess();
        Log.Information("BuildExpressionMatchIndexAsync: Object Matching Rule {RuleId} worked out for {ObjectCount} unjoined object(s) of type {TypeId} in Connected System {ConnectedSystemId}; {UncheckableCount} could not be worked out",
            rule.Id, objectCount, objectType.Id, connectedSystem.Id, index.UncheckableCount);
        return index;
    }

    /// <summary>
    /// Why an export matching rule cannot be worked out for any Metaverse Object, in the words the import side uses
    /// for the same fault, since the rule is the same rule.
    /// </summary>
    private MatchValueResolution DescribeExportFailure(ObjectMatchingRule rule, ExportMatchingValue resolved, ConnectedSystemObjectType objectType)
    {
        var targetType = rule.TargetMetaverseAttribute?.Type ?? AttributeDataType.NotSet;

        // The rule's shape is wrong whatever the object holds, so resolving it against no values reports the fault.
        var described = _matchValueResolver.Resolve(rule, [], objectType);
        if (described.Kind == MatchValueResolutionKind.Failed)
            return described;

        return MatchValueResolution.Failed(resolved.Outcome switch
        {
            ExportMatchingOutcome.NoConnectedSystemAttribute => "The rule's Connected System attribute is not set.",
            _ => $"The rule cannot be compared ({resolved.Outcome})."
        }, targetType, null);
    }

    /// <summary>
    /// The candidates an ambiguous export match lists on its Activity item (#2052), so the administrator can open each
    /// one rather than read a count: up to <see cref="ActivityRunProfileExecutionItemMatchCandidate.MaxRecorded"/>,
    /// ordered by display name, each with the values it was found by.
    /// </summary>
    private static List<ActivityRunProfileExecutionItemMatchCandidate> DescribeCandidates(
        IEnumerable<ConnectedSystemObject> eligible,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType objectType,
        ObjectMatchingRule rule,
        Func<Guid, string> matchedOn,
        int total)
    {
        var description = rule.DescribeComparison();
        return eligible
            .Take(ActivityRunProfileExecutionItemMatchCandidate.MaxRecorded)
            .Select(cso => (Cso: cso, Name: cso.NameOrId ?? cso.Id.ToString()))
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Cso.Id)
            .Select((candidate, ordinal) => new ActivityRunProfileExecutionItemMatchCandidate
            {
                CandidateKind = MatchCandidateKind.ConnectedSystemObject,
                CandidateId = candidate.Cso.Id,
                DisplayNameSnapshot = candidate.Name,
                TypeNameSnapshot = candidate.Cso.Type?.Name ?? objectType.Name,
                ConnectedSystemId = connectedSystem.Id,
                ConnectedSystemNameSnapshot = connectedSystem.Name,
                MatchedValue = matchedOn(candidate.Cso.Id),
                ObjectMatchingRuleId = rule.Id == 0 ? null : rule.Id,
                ObjectMatchingRuleDescriptionSnapshot = description,
                Ordinal = ordinal,
                CandidateTotal = total
            })
            .ToList();
    }

    private static string FormatValues(IEnumerable<object> values) =>
        string.Join(", ", values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));

    #endregion
}
