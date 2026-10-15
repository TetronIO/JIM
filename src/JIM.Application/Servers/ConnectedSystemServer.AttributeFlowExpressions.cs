// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Attribute Flow expressions are checked on save in the application layer (#2055 Phase 3), on every path a mapping is
/// saved by: the portal's whole-rule save, and the REST API's (so PowerShell's) mapping create, update and settings
/// update. The rule a save follows: a mapping that is new, or whose expressions or target the save changes, is refused
/// for any problem; one the save leaves as it was is never refused, and its problems are save warnings. An
/// administrator can therefore always disable a rule, or remove one mapping, while another is broken.
/// </summary>
public partial class ConnectedSystemServer
{
    private readonly AttributeFlowExpressionValidator _attributeFlowExpressionValidator = new(new DynamicExpressoEvaluator());

    /// <summary>
    /// Checks every mapping of <paramref name="syncRule"/> for the whole-rule save, stamping warnings onto each
    /// mapping's <see cref="SyncRuleMapping.SaveWarnings"/>. Runs after the derived flow check, which clears them.
    /// </summary>
    /// <exception cref="ArgumentException">A new or changed mapping's expression has a blocking problem; every one is named.</exception>
    private Task EnsureAttributeFlowExpressionsAreValidAsync(SyncRule syncRule) =>
        EnsureAttributeFlowExpressionsAreValidAsync(syncRule, syncRule.AttributeFlowRules);

    /// <summary>
    /// Checks <paramref name="mapping"/> for a single-mapping save, against the rule it belongs to.
    /// </summary>
    /// <exception cref="ArgumentException">The mapping is new or changed and its expression has a blocking problem.</exception>
    private async Task EnsureAttributeFlowExpressionsAreValidAsync(SyncRuleMapping mapping)
    {
        if (!HasExpression(mapping))
            return;

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId;
        var syncRule = mapping.SyncRule
            ?? (syncRuleId > 0 ? await Application.Repository.ConnectedSystems.GetSyncRuleAsync(syncRuleId) : null);
        if (syncRule == null)
            return;

        await EnsureAttributeFlowExpressionsAreValidAsync(syncRule, [mapping]);
    }

    private async Task EnsureAttributeFlowExpressionsAreValidAsync(SyncRule syncRule, IReadOnlyCollection<SyncRuleMapping> mappings)
    {
        // No I/O at all unless a mapping holds an expression, so every other save behaves exactly as before.
        var withExpressions = mappings.Where(HasExpression).ToList();
        if (withExpressions.Count == 0)
            return;

        var connectedSystemObjectType = await GetObjectTypeWithAttributesAsync(syncRule.ConnectedSystemObjectType, syncRule.ConnectedSystemObjectTypeId);
        var metaverseObjectType = syncRule.MetaverseObjectType is { Attributes.Count: > 0 } loaded
            ? loaded
            : await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(syncRule.ResolveMetaverseObjectTypeId(), true);

        // Read before anything below flushes the change, untracked, so it is what is stored and not the edit.
        var storedIds = withExpressions.Where(m => m.Id > 0).Select(m => m.Id).ToList();
        var stored = storedIds.Count > 0
            ? await Application.Repository.ConnectedSystems.GetSyncRuleMappingExpressionStatesAsync(storedIds) ?? []
            : [];

        var refusals = new List<string>();
        foreach (var mapping in withExpressions)
        {
            var unchanged = mapping.Id > 0 && stored.TryGetValue(mapping.Id, out var state) && state.IsUnchangedIn(mapping);
            foreach (var problem in _attributeFlowExpressionValidator.Validate(mapping, syncRule.Direction, connectedSystemObjectType, metaverseObjectType))
            {
                if (problem.IsWarningOnly || unchanged)
                    mapping.SaveWarnings.Add(problem.Message);
                else
                    refusals.Add(problem.Message);
            }
        }

        if (refusals.Count == 0)
            return;

        var message = string.Join(" ", refusals);
        Log.Warning("EnsureAttributeFlowExpressionsAreValidAsync: rejecting Attribute Flow save on Synchronisation Rule {SyncRuleId}; {Message}",
            syncRule.Id, LogSanitiser.Sanitise(message));
        throw new ArgumentException(message);
    }

    private static bool HasExpression(SyncRuleMapping mapping) => mapping.Sources.Any(s => !string.IsNullOrWhiteSpace(s.Expression));
}
