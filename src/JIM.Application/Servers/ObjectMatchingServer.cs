// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
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

    #endregion
}
