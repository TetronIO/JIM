// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Expressions;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;

namespace JIM.Application.Services;

/// <summary>
/// Computes what a destructive schema refresh invalidates (#1485): the Synchronisation Rules bound to a removed
/// Object Type, the Attribute Flow mappings reading or writing a removed or redefined attribute (directly or as
/// an Expression input), and the Object Matching Rules referencing a removed attribute. The output doubles as
/// the "Apply and Disable Dependents" plan, so every reason is written as the sentence that will be recorded
/// against the disabled rule or mapping.
/// </summary>
public static class SchemaRefreshDependentDetector
{
    /// <summary>
    /// Detects the refresh's dependents. Pure: nothing is disabled or persisted here. Removal names are
    /// resolved against the result's own pre-refresh schema snapshot, because the merge has already rebuilt
    /// the in-memory graph and the removed entries' ids survive nowhere else.
    /// </summary>
    /// <param name="result">The refresh preview's result, whose removals and definition changes name what fell.</param>
    /// <param name="syncRules">The Connected System's Synchronisation Rules, with mappings and matching rules loaded.</param>
    /// <param name="refreshedAtUtc">When the refresh ran; named in each reason so a disabled item says which refresh disabled it.</param>
    public static SchemaRefreshDependents Detect(
        SchemaRefreshResult result,
        IReadOnlyList<SyncRule> syncRules,
        DateTime refreshedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(syncRules);

        var dependents = new SchemaRefreshDependents();
        var refreshDescription = $"schema refresh of {refreshedAtUtc.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}";

        var typesByName = result.PreRefreshSchema.ToDictionary(type => type.Name, StringComparer.OrdinalIgnoreCase);
        var typeNamesById = result.PreRefreshSchema.ToDictionary(type => type.Id, type => type.Name);
        var removedTypeIds = result.RemovedObjectTypes
            .Where(typesByName.ContainsKey)
            .Select(name => typesByName[name].Id)
            .ToHashSet();

        // Removed and redefined attributes, resolved per Object Type so a name shared by two types cannot
        // invalidate the wrong one's mappings.
        var removedAttributeIdsByTypeId = new Dictionary<int, HashSet<int>>();
        var removedAttributeNamesByTypeId = new Dictionary<int, HashSet<string>>();
        foreach (var (typeName, attributeNames) in result.RemovedAttributes.Where(kvp => typesByName.ContainsKey(kvp.Key)))
        {
            var type = typesByName[typeName];
            removedAttributeNamesByTypeId[type.Id] = new HashSet<string>(attributeNames, StringComparer.OrdinalIgnoreCase);
            removedAttributeIdsByTypeId[type.Id] = type.Attributes
                .Where(attribute => attributeNames.Contains(attribute.Name, StringComparer.OrdinalIgnoreCase))
                .Select(attribute => attribute.Id)
                .ToHashSet();
        }

        var changedByTypeIdAndAttributeId = new Dictionary<int, Dictionary<int, SchemaAttributeDefinitionChange>>();
        foreach (var (typeName, changes) in result.ChangedAttributes.Where(kvp => typesByName.ContainsKey(kvp.Key)))
        {
            var type = typesByName[typeName];
            var byAttributeId = new Dictionary<int, SchemaAttributeDefinitionChange>();
            foreach (var change in changes)
            {
                var attribute = type.Attributes.FirstOrDefault(a => string.Equals(a.Name, change.AttributeName, StringComparison.OrdinalIgnoreCase));
                if (attribute != null)
                    byAttributeId[attribute.Id] = change;
            }
            changedByTypeIdAndAttributeId[type.Id] = byAttributeId;
        }

        foreach (var rule in syncRules)
        {
            // A rule bound to a removed Object Type falls whole; its mappings fall with it and are not
            // repeated in the mapping list, or the same disable would be counted twice.
            if (removedTypeIds.Contains(rule.ConnectedSystemObjectTypeId))
            {
                var typeName = typeNamesById.TryGetValue(rule.ConnectedSystemObjectTypeId, out var name) ? name : rule.ConnectedSystemObjectTypeId.ToString(CultureInfo.InvariantCulture);
                dependents.InvalidatedSyncRules.Add(new SchemaRefreshDependentRule
                {
                    SyncRuleId = rule.Id,
                    SyncRuleName = rule.Name,
                    ObjectTypeName = typeName,
                    MappingCount = rule.AttributeFlowRules.Count,
                    Reason = $"Object Type '{typeName}' is no longer reported by the Connected System ({refreshDescription})."
                });
                CollectMatchingRuleReferences(dependents, rule, removedAttributeIdsByTypeId, rule.ConnectedSystemObjectTypeId, refreshDescription);
                continue;
            }

            var removedIds = removedAttributeIdsByTypeId.TryGetValue(rule.ConnectedSystemObjectTypeId, out var ids) ? ids : null;
            var removedNames = removedAttributeNamesByTypeId.TryGetValue(rule.ConnectedSystemObjectTypeId, out var names) ? names : null;
            var changedIds = changedByTypeIdAndAttributeId.TryGetValue(rule.ConnectedSystemObjectTypeId, out var changed) ? changed : null;

            foreach (var mapping in rule.AttributeFlowRules)
            {
                var invalidation = FindMappingInvalidation(mapping, removedIds, removedNames, changedIds, refreshDescription);
                if (invalidation == null)
                    continue;

                dependents.InvalidatedMappings.Add(new SchemaRefreshDependentMapping
                {
                    MappingId = mapping.Id,
                    SyncRuleId = rule.Id,
                    SyncRuleName = rule.Name,
                    Description = DescribeMapping(mapping),
                    ViaExpression = invalidation.Value.ViaExpression,
                    Reason = invalidation.Value.Reason
                });
            }

            CollectMatchingRuleReferences(dependents, rule, removedAttributeIdsByTypeId, rule.ConnectedSystemObjectTypeId, refreshDescription);
        }

        return dependents;
    }

    /// <summary>
    /// Why one mapping is invalidated, or null when it is not: a removed attribute mapped directly (either
    /// direction), a removed attribute consumed inside an Expression, or a redefined attribute the mapping was
    /// validated against.
    /// </summary>
    private static (string Reason, bool ViaExpression)? FindMappingInvalidation(
        SyncRuleMapping mapping,
        HashSet<int>? removedAttributeIds,
        HashSet<string>? removedAttributeNames,
        Dictionary<int, SchemaAttributeDefinitionChange>? changedAttributes,
        string refreshDescription)
    {
        // Export target.
        var targetId = mapping.TargetConnectedSystemAttributeId ?? mapping.TargetConnectedSystemAttribute?.Id;
        if (targetId is { } exportTargetId)
        {
            if (removedAttributeIds?.Contains(exportTargetId) == true)
                return ($"Attribute '{mapping.TargetConnectedSystemAttribute?.Name}' is no longer reported by the Connected System ({refreshDescription}).", false);
            if (changedAttributes?.TryGetValue(exportTargetId, out var targetChange) == true)
                return (DescribeDefinitionChange(targetChange, refreshDescription), false);
        }

        foreach (var source in mapping.Sources)
        {
            var sourceId = source.ConnectedSystemAttributeId ?? source.ConnectedSystemAttribute?.Id;
            if (sourceId is { } directSourceId)
            {
                if (removedAttributeIds?.Contains(directSourceId) == true)
                    return ($"Attribute '{source.ConnectedSystemAttribute?.Name}' is no longer reported by the Connected System ({refreshDescription}).", false);
                if (changedAttributes?.TryGetValue(directSourceId, out var sourceChange) == true)
                    return (DescribeDefinitionChange(sourceChange, refreshDescription), false);
            }

            if (string.IsNullOrWhiteSpace(source.Expression) || removedAttributeNames == null)
                continue;

            var removedInput = ExpressionInputResolver.ResolveCached(source.Expression)
                .FirstOrDefault(input => input.Source == ExpressionInputSource.ConnectedSystem && removedAttributeNames.Contains(input.AttributeName));
            if (removedInput != null)
                return ($"Attribute '{removedInput.AttributeName}', read by this mapping's Expression, is no longer reported by the Connected System ({refreshDescription}).", true);
        }

        return null;
    }

    private static string DescribeDefinitionChange(SchemaAttributeDefinitionChange change, string refreshDescription)
    {
        var aspect = change.Aspect == SchemaAttributeChangeAspect.DataType ? "data type" : "plurality";
        return $"Attribute '{change.AttributeName}' changed {aspect} from {change.OldValue} to {change.NewValue} at the Connected System ({refreshDescription}).";
    }

    private static void CollectMatchingRuleReferences(
        SchemaRefreshDependents dependents,
        SyncRule rule,
        Dictionary<int, HashSet<int>> removedAttributeIdsByTypeId,
        int objectTypeId,
        string refreshDescription)
    {
        if (!removedAttributeIdsByTypeId.TryGetValue(objectTypeId, out var removedIds) || removedIds.Count == 0)
            return;

        foreach (var (matchingRule, removedSource) in rule.ObjectMatchingRules
                     .Select(matchingRule => (matchingRule, removedSource: matchingRule.Sources
                         .FirstOrDefault(source => (source.ConnectedSystemAttributeId ?? source.ConnectedSystemAttribute?.Id) is { } id && removedIds.Contains(id))))
                     .Where(pair => pair.removedSource != null))
        {
            dependents.ReferencedObjectMatchingRules.Add(new SchemaRefreshDependentMatchingRule
            {
                ObjectMatchingRuleId = matchingRule.Id,
                Context = rule.Name,
                Reason = $"Matches on attribute '{removedSource!.ConnectedSystemAttribute?.Name}', which is no longer reported by the Connected System ({refreshDescription})."
            });
        }
    }

    private static string DescribeMapping(SyncRuleMapping mapping)
    {
        var sources = string.Join(", ", mapping.Sources
            .OrderBy(source => source.Order)
            .Select(source => !string.IsNullOrWhiteSpace(source.Expression)
                ? "Expression"
                : source.ConnectedSystemAttribute?.Name ?? source.MetaverseAttribute?.Name ?? "?"));
        var target = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "?";
        return $"{sources} → {target}";
    }
}
