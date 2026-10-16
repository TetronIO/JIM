// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Expressions;
using JIM.Models.Core.DTOs;
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
    /// <param name="readers">What reads each attribute (#2265); null leaves <see cref="SchemaRefreshDependents.RemovedAttributeReaders"/> empty.</param>
    public static SchemaRefreshDependents Detect(
        SchemaRefreshResult result,
        IReadOnlyList<SyncRule> syncRules,
        DateTime refreshedAtUtc,
        AttributeReaderIndex? readers = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(syncRules);

        var dependents = new SchemaRefreshDependents();
        var refreshDescription = $"schema refresh of {refreshedAtUtc.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}";

        // Names resolve through the result, which follows a case-only rename back to the name the snapshot holds and
        // keeps two names differing only in case apart (#2051).
        var typeNamesById = result.PreRefreshSchema.ToDictionary(type => type.Id, type => type.Name);
        var removedTypeIds = result.RemovedObjectTypes
            .Select(result.FindPreRefreshType)
            .OfType<SchemaRefreshPreRefreshType>()
            .Select(type => type.Id)
            .ToHashSet();

        // Removed and redefined attributes, resolved per Object Type so a name shared by two types cannot
        // invalidate the wrong one's mappings.
        var removedAttributeIdsByTypeId = new Dictionary<int, HashSet<int>>();
        var removedAttributeNamesByTypeId = new Dictionary<int, HashSet<string>>();
        foreach (var (type, attributeNames) in result.RemovedAttributes
                     .Select(kvp => (Type: result.FindPreRefreshType(kvp.Key), AttributeNames: kvp.Value))
                     .Where(entry => entry.Type != null)
                     .Select(entry => (Type: entry.Type!, entry.AttributeNames)))
        {
            removedAttributeNamesByTypeId[type.Id] = new HashSet<string>(attributeNames, StringComparer.OrdinalIgnoreCase);
            // A removed attribute is reported under the name JIM held, so it is matched exactly: a case-exact
            // Connected System can hold a second attribute whose name differs only in case, and that one stays.
            removedAttributeIdsByTypeId[type.Id] = type.Attributes
                .Where(attribute => attributeNames.Contains(attribute.Name, StringComparer.Ordinal))
                .Select(attribute => attribute.Id)
                .ToHashSet();
        }

        if (readers != null)
            dependents.RemovedAttributeReaders = FindRemovedAttributeReaders(result, readers);

        var changedByTypeIdAndAttributeId = new Dictionary<int, Dictionary<int, SchemaAttributeDefinitionChange>>();
        foreach (var (type, changes) in result.ChangedAttributes
                     .Select(kvp => (Type: result.FindPreRefreshType(kvp.Key), Changes: kvp.Value))
                     .Where(entry => entry.Type != null)
                     .Select(entry => (Type: entry.Type!, entry.Changes)))
        {
            var byAttributeId = new Dictionary<int, SchemaAttributeDefinitionChange>();
            foreach (var change in changes)
            {
                // A change names the attribute as the Connector now does, which may differ only in case from the
                // snapshot's name; an exact name wins where both exist.
                var attribute = type.Attributes.FirstOrDefault(a => string.Equals(a.Name, change.AttributeName, StringComparison.Ordinal))
                    ?? type.Attributes.FirstOrDefault(a => string.Equals(a.Name, change.AttributeName, StringComparison.OrdinalIgnoreCase));
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

            if (string.IsNullOrWhiteSpace(source.Expression) || (removedAttributeNames == null && changedAttributes == null))
                continue;

            var expressionInputNames = ExpressionInputResolver.ResolveCached(source.Expression)
                .Where(input => input.Source == ExpressionInputSource.ConnectedSystem)
                .Select(input => input.AttributeName)
                .ToList();

            var removedInput = removedAttributeNames == null ? null : expressionInputNames.FirstOrDefault(removedAttributeNames.Contains);
            if (removedInput != null)
                return ($"Attribute '{removedInput}', read by this mapping's Expression, is no longer reported by the Connected System ({refreshDescription}).", true);

            // An Expression written against an attribute's old definition receives the new one (#2128: a Boolean
            // comparison handed a number), so it is as invalidated as a mapping reading the attribute directly.
            var redefinedInput = changedAttributes?.Values.FirstOrDefault(change => expressionInputNames.Contains(change.AttributeName, StringComparer.OrdinalIgnoreCase));
            if (redefinedInput != null)
                return (DescribeDefinitionChange(redefinedInput, refreshDescription, ", read by this mapping's Expression,"), true);
        }

        return null;
    }

    /// <summary>
    /// The configuration reading each attribute the refresh removes from a surviving Object Type (#2265): what
    /// refuses Apply and Remove. Readers on a removed Object Type are left out, because their Synchronisation Rule
    /// falls whole, so nothing is left reading nothing.
    /// </summary>
    /// <param name="result">The refresh preview's result.</param>
    /// <param name="readers">What reads each attribute, as the configuration stands now.</param>
    public static List<AttributeReader> FindRemovedAttributeReaders(SchemaRefreshResult result, AttributeReaderIndex readers)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(readers);

        var removedTypeIds = result.RemovedObjectTypes
            .Select(result.FindPreRefreshType)
            .OfType<SchemaRefreshPreRefreshType>()
            .Select(type => type.Id)
            .ToHashSet();

        var seen = new HashSet<(AttributeReaderKind, int)>();
        // Matched exactly, as the removal itself is: a case-exact Connected System can hold a second attribute whose
        // name differs only in case, and that one stays.
        return result.RemovedAttributes
            .Select(kvp => (Type: result.FindPreRefreshType(kvp.Key), AttributeNames: kvp.Value))
            .Where(entry => entry.Type != null && !removedTypeIds.Contains(entry.Type.Id))
            .SelectMany(entry => entry.Type!.Attributes
                .Where(attribute => entry.AttributeNames.Contains(attribute.Name, StringComparer.Ordinal))
                .SelectMany(attribute => readers.ForConnectedSystemAttribute(attribute.Id, attribute.Name, entry.Type.Id)))
            .Where(reader => seen.Add((reader.Kind, reader.ItemId)))
            .ToList();
    }

    private static string DescribeDefinitionChange(SchemaAttributeDefinitionChange change, string refreshDescription, string readBy = "")
    {
        var aspect = change.Aspect == SchemaAttributeChangeAspect.DataType ? "data type" : "plurality";
        return $"Attribute '{change.AttributeName}'{readBy} changed {aspect} from {change.OldValue} to {change.NewValue} at the Connected System ({refreshDescription}).";
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
