// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Staging;

/// <summary>
/// Decides which multi-valued reference attributes a Delta Synchronisation may load without their values (#2818), from
/// the run's Synchronisation Rules and Object Types.
/// </summary>
/// <remarks>
/// <para>
/// A large group's member attribute brings every member into memory with the group, and in a Target system's Delta
/// Synchronisation the only thing that reads it is Drift Detection, which needs only the members the group and its
/// Metaverse Object disagree on. Drift Detection asks the database for those (<see cref="DriftHeldReferenceAnswer"/>), so
/// the load can leave the attribute out, but only where nothing else on the page reads it.
/// </para>
/// <para>
/// So an attribute is left out only where an Enforce State export rule compares it through a direct mapping from a
/// Metaverse reference attribute, and nothing else of this Connected System's configuration reads it: no import
/// mapping, directly or through an Expression (an Expression that names attributes other than by a written-out name could
/// read any), no Scoping Criterion and no Object Matching Rule, enabled or not. An Expression or generated export mapping
/// to it reads the values held, so it keeps the attribute whole too. Everything kept whole is reported with its reason.
/// </para>
/// <para>
/// The page load then decides object by object (<c>ISyncRepository.GetConnectedSystemObjectsModifiedSinceAsync</c>):
/// see <see cref="SynchronisationValueLoadPlan"/>.
/// </para>
/// </remarks>
public static partial class SynchronisationValueLoadPlanner
{
    /// <summary>
    /// The plan for a run.
    /// </summary>
    /// <param name="objectTypes">The Connected System's Object Types, with their Object Matching Rules.</param>
    /// <param name="currentSystemSyncRules">The Connected System's Synchronisation Rules, enabled or not, with their mappings,
    /// Scoping Criteria and Object Matching Rules.</param>
    /// <param name="minimumHeldValueCount">How many values an object must hold of an attribute for it to be left out.</param>
    /// <returns>The plan, and the attributes Drift Detection compares that are kept whole, with why.</returns>
    public static (SynchronisationValueLoadPlan Plan, IReadOnlyList<SynchronisationValueLoadFallback> Fallbacks) Plan(
        IReadOnlyList<ConnectedSystemObjectType> objectTypes, IReadOnlyList<SyncRule> currentSystemSyncRules, int minimumHeldValueCount)
    {
        ArgumentNullException.ThrowIfNull(objectTypes);
        ArgumentNullException.ThrowIfNull(currentSystemSyncRules);

        var attributesByObjectTypeId = new Dictionary<int, IReadOnlyList<ConnectedSystemObjectTypeAttribute>>();
        var fallbacks = new List<SynchronisationValueLoadFallback>();

        foreach (var objectType in objectTypes)
        {
            var rules = currentSystemSyncRules.Where(r => r.ConnectedSystemObjectTypeId == objectType.Id).ToList();
            var leftOut = new List<ConnectedSystemObjectTypeAttribute>();
            foreach (var attribute in (objectType.Attributes ?? []).Where(ExportValueLoadPlanner.IsTrimmable))
            {
                var (comparedByDrift, reason) = Assess(objectType, attribute, rules);
                if (reason is { } fallbackReason)
                    fallbacks.Add(new SynchronisationValueLoadFallback(objectType.Id, attribute.Id, attribute.Name, fallbackReason));
                else if (comparedByDrift)
                    leftOut.Add(attribute);
            }

            if (leftOut.Count > 0)
                attributesByObjectTypeId[objectType.Id] = leftOut;
        }

        return (new SynchronisationValueLoadPlan(attributesByObjectTypeId, minimumHeldValueCount), fallbacks);
    }

    /// <summary>
    /// Whether an Enforce State direct mapping compares the attribute, and the first other reader found, if any.
    /// </summary>
    private static (bool ComparedByDrift, SynchronisationValueLoadFallbackReason? Reason) Assess(
        ConnectedSystemObjectType objectType, ConnectedSystemObjectTypeAttribute attribute, List<SyncRule> rules)
    {
        // Drift Detection: the Enforce State mappings it evaluates (#2180, #1485, #223), as DriftDetectionService filters them.
        var driftMappings = rules
            .Where(r => r.Direction == SyncRuleDirection.Export && r.Enabled && r.EnforceState)
            .SelectMany(r => r.AttributeFlowRules)
            .Where(m => m.Enabled && m.FlowsOnUpdateExport() &&
                        (m.TargetConnectedSystemAttributeId ?? m.TargetConnectedSystemAttribute?.Id) == attribute.Id)
            .ToList();
        if (driftMappings.Count == 0)
            return (false, null);

        if (driftMappings.Exists(m => m.Generation != null))
            return (true, SynchronisationValueLoadFallbackReason.ReadByGeneratedValueDrift);
        if (driftMappings.Exists(m => m.Sources.Exists(s => !string.IsNullOrWhiteSpace(s.Expression))))
            return (true, SynchronisationValueLoadFallbackReason.ReadByDriftExpression);
        if (driftMappings.Exists(m => m.Sources.Exists(s => s.MetaverseAttribute is { Type: not AttributeDataType.Reference })))
            return (true, SynchronisationValueLoadFallbackReason.ExpectedValuesNotReferences);

        // The import side: every import rule of the type, enabled or not, and the type's own Object Matching Rules.
        foreach (var rule in rules.Where(r => r.Direction == SyncRuleDirection.Import))
        {
            foreach (var source in rule.AttributeFlowRules.SelectMany(m => m.Sources))
            {
                if (source.ConnectedSystemAttributeId == attribute.Id || source.ConnectedSystemAttribute?.Id == attribute.Id)
                    return (true, SynchronisationValueLoadFallbackReason.ReadByImportAttributeFlow);
                if (ExpressionReads(source.Expression, attribute))
                    return (true, SynchronisationValueLoadFallbackReason.ReadByImportExpression);
            }

            if (CriteriaOf(rule.ObjectScopingCriteriaGroups).Any(c =>
                    c.ConnectedSystemAttributeId == attribute.Id || c.ConnectedSystemAttribute?.Id == attribute.Id || ExpressionReads(c.Expression, attribute)))
                return (true, SynchronisationValueLoadFallbackReason.ReadByScopingCriterion);

            if (MatchingRulesRead(rule.ObjectMatchingRules, attribute))
                return (true, SynchronisationValueLoadFallbackReason.ReadByObjectMatchingRule);
        }

        if (MatchingRulesRead(objectType.ObjectMatchingRules, attribute))
            return (true, SynchronisationValueLoadFallbackReason.ReadByObjectMatchingRule);

        return (true, null);
    }

    private static bool MatchingRulesRead(IEnumerable<ObjectMatchingRule>? matchingRules, ConnectedSystemObjectTypeAttribute attribute) =>
        (matchingRules ?? []).SelectMany(r => r.Sources).Any(s =>
            s.ConnectedSystemAttributeId == attribute.Id || s.ConnectedSystemAttribute?.Id == attribute.Id || ExpressionReads(s.Expression, attribute));

    private static IEnumerable<SyncRuleScopingCriteria> CriteriaOf(IEnumerable<SyncRuleScopingCriteriaGroup>? groups) =>
        (groups ?? []).SelectMany(g => g.Criteria.Concat(CriteriaOf(g.ChildGroups)));

    /// <summary>
    /// Whether an Expression reads the attribute: names it in a <c>cs["..."]</c> accessor (ignoring case, as the evaluator
    /// does), or reaches the object's attributes in any way other than a written-out name, which could be this one.
    /// </summary>
    internal static bool ExpressionReads(string? expression, ConnectedSystemObjectTypeAttribute attribute)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        if (ExpressionInputResolver.ResolveCached(expression).Any(input =>
                input.Source == ExpressionInputSource.ConnectedSystem &&
                string.Equals(input.AttributeName, attribute.Name, StringComparison.OrdinalIgnoreCase)))
            return true;

        return ConnectedSystemIdentifierRegex().Count(expression) > WrittenOutAccessorRegex().Count(expression);
    }

    /// <summary>Every mention of the <c>cs</c> parameter.</summary>
    [GeneratedRegex(@"\bcs\b", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectedSystemIdentifierRegex();

    /// <summary>A <c>cs["..."]</c> accessor with its name written out, as <see cref="ExpressionInputResolver"/> reads one.</summary>
    [GeneratedRegex(@"\bcs\s*\[\s*""(?:[^""\\]|\\.)*""\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex WrittenOutAccessorRegex();
}
