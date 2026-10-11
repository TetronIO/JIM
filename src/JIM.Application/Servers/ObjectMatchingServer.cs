// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Expressions;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
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
        var description = DescribeRule(rule);
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
        const int maxValuesShown = 10;
        var total = ambiguity.Candidates.Count > 0 ? ambiguity.Candidates[0].CandidateTotal : ambiguity.Matches.Count;
        var on = string.Empty;
        if (ambiguity.Rule != null)
        {
            var values = ambiguity.MatchValues
                .Take(maxValuesShown)
                .Select(value => $"\"{Convert.ToString(value, CultureInfo.InvariantCulture)}\"")
                .ToList();
            if (ambiguity.MatchValues.Count > maxValuesShown)
                values.Add($"and {ambiguity.MatchValues.Count - maxValuesShown} more");
            on = $" on {DescribeRule(ambiguity.Rule)} ({string.Join(", ", values)})";
        }

        var outcome = previewing ? "a synchronisation would neither join nor project it" : "it was neither joined nor projected";
        return $"This object matches {total} Metaverse Objects{on}, so {outcome}. " +
               "A Connected System Object can join only one Metaverse Object: correct the data, or the Object Matching Rule, " +
               "so that its values identify one.";
    }

    /// <summary>
    /// What a rule compares, for example "Employee ID equals employeeNumber", or the expression in place of the
    /// attribute name when the rule computes its value.
    /// </summary>
    internal static string DescribeRule(ObjectMatchingRule rule)
    {
        var source = rule.Sources.OrderBy(s => s.Order).FirstOrDefault();
        var sourceText = string.IsNullOrWhiteSpace(source?.Expression)
            ? source?.ConnectedSystemAttribute?.Name
            : source.Expression;
        var target = rule.TargetMetaverseAttribute?.Name ?? rule.TargetMetaverseAttributeId?.ToString(CultureInfo.InvariantCulture);
        return $"{target} equals {sourceText}";
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
    /// Attempts to find an existing Connected System Object in a target system that matches a Metaverse Object.
    /// This is used during export evaluation to find existing CSOs before provisioning.
    /// The caller is responsible for resolving which rules apply based on the matching mode.
    /// </summary>
    /// <param name="metaverseObject">The MVO to find a matching CSO for.</param>
    /// <param name="connectedSystem">The target Connected System (needed to scope the CSO search).</param>
    /// <param name="connectedSystemObjectType">The CSO type to search within.</param>
    /// <param name="matchingRules">The matching rules to evaluate.</param>
    /// <returns>The matching CSO, or null if no match found.</returns>
    public async Task<ConnectedSystemObject?> FindMatchingConnectedSystemObjectAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        List<ObjectMatchingRule> matchingRules)
    {
        if (matchingRules.Count == 0)
        {
            Log.Debug("FindMatchingConnectedSystemObjectAsync: No matching rules provided for export to CS {CsId}", connectedSystem.Id);
            return null;
        }

        // Evaluate rules in order until we find a match
        foreach (var matchingRule in matchingRules.OrderBy(r => r.Order))
        {
            try
            {
                var cso = await Application.SyncRepo.FindConnectedSystemObjectUsingMatchingRuleAsync(
                    metaverseObject,
                    connectedSystem,
                    connectedSystemObjectType,
                    matchingRule);

                if (cso != null)
                {
                    Log.Debug("FindMatchingConnectedSystemObjectAsync: Found CSO {CsoId} using rule {RuleId}", cso.Id, matchingRule.Id);
                    return cso;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FindMatchingConnectedSystemObjectAsync: Error evaluating matching rule {RuleId}", matchingRule.Id);
                // Continue to next rule
            }
        }

        return null;
    }

    /// <summary>
    /// Batch equivalent of <see cref="FindMatchingConnectedSystemObjectAsync"/>: finds a matching CSO for
    /// the given Metaverse Object entirely from a page's prefetched <paramref name="candidates"/>
    /// (populated by <see cref="ExportEvaluationServer.PrefetchExportMatchCandidatesForPageAsync"/>),
    /// hydrating only the specific candidate rows a rule's resolved value points at rather than issuing
    /// the per-object database query. Callers must only use this for a (Metaverse Object, export
    /// Synchronisation Rule) pair the prefetch covered (<see cref="ExportMatchCandidates.IsCovered"/>);
    /// an uncovered pair carries no information here and must fall back to
    /// <see cref="FindMatchingConnectedSystemObjectAsync"/>.
    /// </summary>
    /// <param name="metaverseObject">The MVO to find a matching CSO for.</param>
    /// <param name="connectedSystem">The target Connected System, used only for the multiple-candidates
    /// log message (matching <see cref="FindConnectedSystemObjectUsingMatchingRuleAsync"/>'s wording).</param>
    /// <param name="matchingRules">The matching rules to evaluate, tried in <see cref="ObjectMatchingRule.Order"/>.</param>
    /// <param name="candidates">The page's prefetched export-matching candidates.</param>
    /// <returns>The matching CSO, or null when no rule resolves a usable value, or none of the
    /// prefetched candidates for a resolved value are still eligible.</returns>
    public async Task<ConnectedSystemObject?> FindPrefetchedMatchingConnectedSystemObjectAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        List<ObjectMatchingRule> matchingRules,
        ExportMatchCandidates candidates)
    {
        foreach (var matchingRule in matchingRules.OrderBy(r => r.Order))
        {
            var resolved = ExportMatchingValue.Resolve(metaverseObject, matchingRule);

            switch (resolved.Outcome)
            {
                // The per-object query throws for these two (caught by FindMatchingConnectedSystemObjectAsync's
                // caller and logged as a generic "Error evaluating matching rule" warning); logged directly
                // here instead, since Resolve() reports them without needing to throw.
                case ExportMatchingOutcome.NoSources:
                    Log.Warning("FindPrefetchedMatchingConnectedSystemObjectAsync: Object Matching Rule {RuleId} has no sources; export matching cannot evaluate it.",
                        matchingRule.Id);
                    continue;

                case ExportMatchingOutcome.MultipleSources:
                    Log.Warning("FindPrefetchedMatchingConnectedSystemObjectAsync: Object Matching Rule {RuleId} has more than one source; advanced (multi-source) matching is not yet supported.",
                        matchingRule.Id);
                    continue;

                case ExportMatchingOutcome.NoConnectedSystemAttribute:
                    Log.Warning("FindPrefetchedMatchingConnectedSystemObjectAsync: Rule {RuleId} has no Connected System attribute on its source; export matching cannot query the connector space.",
                        matchingRule.Id);
                    continue;

                case ExportMatchingOutcome.NoTargetMetaverseAttribute:
                    Log.Warning("FindPrefetchedMatchingConnectedSystemObjectAsync: Rule {RuleId} has no Target Metaverse Attribute; cannot determine the MVO-side value for export matching.",
                        matchingRule.Id);
                    continue;

                case ExportMatchingOutcome.NoMetaverseValue:
                    Log.Debug("FindPrefetchedMatchingConnectedSystemObjectAsync: MVO {MvoId} does not have a usable value for attribute {AttrId}",
                        metaverseObject.Id, matchingRule.TargetMetaverseAttribute!.Id);
                    continue;

                case ExportMatchingOutcome.UnsupportedAttributeType:
                    Log.Warning("FindPrefetchedMatchingConnectedSystemObjectAsync: Attribute type {AttributeType} on Metaverse attribute {AttributeName} is not supported for export matching; Object Matching Rule {RuleId} cannot match on it.",
                        matchingRule.TargetMetaverseAttribute!.Type, matchingRule.TargetMetaverseAttribute!.Name, matchingRule.Id);
                    continue;
            }

            // Resolved: take a copy of the candidate list before hydrating, since ExportMatchCandidates.Remove
            // mutates the backing list (a claim made while trying an earlier candidate for this same rule and
            // value, or by another Metaverse Object on the page) and iterating it directly here would skip an
            // element whenever that happens mid-loop.
            var candidateIds = candidates.GetCandidates(matchingRule.Id, resolved.Value!).ToList();

            if (candidateIds.Count > 1)
            {
                Log.Warning("FindPrefetchedMatchingConnectedSystemObjectAsync: Multiple CSOs matched rule {RuleId} in Connected System {ConnectedSystemId}. Returning first by ID. This indicates duplicate CSOs that should be investigated.",
                    matchingRule.Id, connectedSystem.Id);
            }

            foreach (var candidateId in candidateIds)
            {
                var hydrated = await Application.SyncRepo.GetConnectedSystemObjectForExportMatchAsync(candidateId);
                if (hydrated != null)
                {
                    Log.Debug("FindPrefetchedMatchingConnectedSystemObjectAsync: Found CSO {CsoId} using rule {RuleId}", hydrated.Id, matchingRule.Id);
                    return hydrated;
                }

                // No longer eligible (claimed by another Metaverse Object on this page since the prefetch,
                // or no longer Normal-status): remove so no later lookup on this page is offered it again.
                candidates.Remove(candidateId);
            }
        }

        return null;
    }

    #endregion
}
