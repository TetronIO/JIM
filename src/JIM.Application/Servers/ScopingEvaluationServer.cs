// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Scoping;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Staging;
using Serilog;
namespace JIM.Application.Servers;

/// <summary>
/// Evaluates scoping criteria for Synchronisation Rules, for both export (Metaverse Object) and import (Connected
/// System Object) rules, and explains the evaluation for administrators (#348).
/// </summary>
/// <remarks>
/// Both the in-scope decisions synchronisation makes and the explanations go through <see cref="ScopingEvaluator"/>,
/// the one implementation of scoping evaluation, so an explanation always reports the outcome synchronisation reaches.
/// </remarks>
public class ScopingEvaluationServer
{
    public ScopingEvaluationServer()
    {
    }

    #region Export (MVO) Scoping

    /// <summary>
    /// Checks if an MVO is in scope for an export rule based on scoping criteria.
    /// No scoping criteria means all objects of the type are in scope.
    /// </summary>
    /// <exception cref="InvalidOperationException">A criterion the evaluation reaches compares with an operator its
    /// attribute's type cannot take.</exception>
    public bool IsMvoInScopeForExportRule(MetaverseObject mvo, SyncRule exportRule, DateTime? nowUtc = null)
    {
        if (exportRule.Direction != SyncRuleDirection.Export)
        {
            Log.Warning("IsMvoInScopeForExportRule: Called with non-export rule {RuleName}", exportRule.Name);
            return false;
        }

        // No scoping criteria means all objects are in scope
        if (exportRule.ObjectScopingCriteriaGroups.Count == 0)
            return true;

        // Resolve "now" once per evaluation so all relative date criteria in this pass share a single boundary.
        return ScopingEvaluator.IsInScope(exportRule.ObjectScopingCriteriaGroups, new MvoScopingValueSource(mvo), nowUtc ?? DateTime.UtcNow);
    }

    /// <summary>
    /// Explains whether an MVO is in scope for an export rule (#348): the outcome
    /// <see cref="IsMvoInScopeForExportRule"/> reaches, with every group and criterion as it evaluated. Unlike the
    /// boolean evaluation, an invalid criterion is recorded rather than thrown, making the outcome
    /// <see cref="ScopingRuleOutcome.Undetermined"/> where synchronisation would fail.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="exportRule"/> is not an export rule.</exception>
    public ScopingExplanation ExplainMvoForExportRule(MetaverseObject mvo, SyncRule exportRule, DateTime? nowUtc = null)
    {
        if (exportRule.Direction != SyncRuleDirection.Export)
            throw new ArgumentException($"Synchronisation Rule {exportRule.Id} is not an export rule.", nameof(exportRule));

        return Explain(exportRule, new MvoScopingValueSource(mvo), nowUtc);
    }

    #endregion

    #region Import (CSO) Scoping

    /// <summary>
    /// Checks if a CSO is in scope for an import rule based on scoping criteria.
    /// No scoping criteria means all objects of the type are in scope.
    /// </summary>
    /// <exception cref="InvalidOperationException">A criterion the evaluation reaches compares with an operator its
    /// attribute's type cannot take.</exception>
    public bool IsCsoInScopeForImportRule(ConnectedSystemObject cso, SyncRule importRule, DateTime? nowUtc = null)
    {
        if (importRule.Direction != SyncRuleDirection.Import)
        {
            Log.Warning("IsCsoInScopeForImportRule: Called with non-import rule {RuleName}", importRule.Name);
            return false;
        }

        // No scoping criteria means all objects are in scope
        if (importRule.ObjectScopingCriteriaGroups.Count == 0)
            return true;

        // Resolve "now" once per evaluation so all relative date criteria in this pass share a single boundary.
        return ScopingEvaluator.IsInScope(importRule.ObjectScopingCriteriaGroups, new CsoScopingValueSource(cso), nowUtc ?? DateTime.UtcNow);
    }

    /// <summary>
    /// Explains whether a CSO is in scope for an import rule (#348); see <see cref="ExplainMvoForExportRule"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="importRule"/> is not an import rule.</exception>
    public ScopingExplanation ExplainCsoForImportRule(ConnectedSystemObject cso, SyncRule importRule, DateTime? nowUtc = null)
    {
        if (importRule.Direction != SyncRuleDirection.Import)
            throw new ArgumentException($"Synchronisation Rule {importRule.Id} is not an import rule.", nameof(importRule));

        return Explain(importRule, new CsoScopingValueSource(cso), nowUtc);
    }

    #endregion

    private static ScopingExplanation Explain<TSource>(SyncRule rule, TSource source, DateTime? nowUtc)
        where TSource : struct, IScopingValueSource
    {
        var explanation = new ScopingExplanation
        {
            SyncRuleId = rule.Id,
            SyncRuleName = rule.Name,
            Direction = rule.Direction,
            HasCriteria = rule.ObjectScopingCriteriaGroups.Count > 0,
            EvaluatedAt = nowUtc ?? DateTime.UtcNow
        };

        explanation.Outcome = explanation.HasCriteria
            ? ScopingEvaluator.Explain(rule.ObjectScopingCriteriaGroups, source, explanation.EvaluatedAt, explanation.Groups)
            : ScopingRuleOutcome.InScope;

        ScopingExplanationSummariser.Describe(explanation);
        return explanation;
    }
}
