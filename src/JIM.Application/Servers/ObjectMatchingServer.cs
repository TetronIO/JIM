// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
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
    /// <returns>The matching MVO, or null if no match found.</returns>
    /// <exception cref="MultipleMatchesException">Thrown if multiple MVOs match the criteria.</exception>
    public async Task<MetaverseObject?> FindMatchingMetaverseObjectAsync(
        ConnectedSystemObject connectedSystemObject,
        List<ObjectMatchingRule> matchingRules)
    {
        if (matchingRules.Count == 0)
        {
            Log.Debug("FindMatchingMetaverseObjectAsync: No matching rules provided for CSO {CsoId}", connectedSystemObject.Id);
            return null;
        }

        // Evaluate rules in order until we find a match
        foreach (var matchingRule in matchingRules.OrderBy(r => r.Order))
        {
            // Rule must have a MetaverseObjectType to know where to search
            var metaverseObjectType = matchingRule.MetaverseObjectType;
            if (metaverseObjectType == null)
            {
                Log.Warning("FindMatchingMetaverseObjectAsync: Skipping matching rule {RuleId} — no MetaverseObjectType set", matchingRule.Id);
                continue;
            }

            try
            {
                var mvo = await Application.SyncRepo.FindMetaverseObjectUsingMatchingRuleAsync(
                    connectedSystemObject,
                    metaverseObjectType,
                    matchingRule);

                if (mvo != null)
                {
                    Log.Debug("FindMatchingMetaverseObjectAsync: Found MVO {MvoId} using rule {RuleId}", mvo.Id, matchingRule.Id);
                    return mvo;
                }
            }
            catch (MultipleMatchesException)
            {
                // Re-throw - caller needs to handle this
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FindMatchingMetaverseObjectAsync: Error evaluating matching rule {RuleId}", matchingRule.Id);
                // Continue to next rule
            }
        }

        return null;
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
