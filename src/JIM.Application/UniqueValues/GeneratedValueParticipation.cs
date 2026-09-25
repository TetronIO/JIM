// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.UniqueValues;

/// <summary>
/// Adopt-before-generate's shared reads (Unique Value Generation, #242, Phase 2 work package J): which
/// export targets participate in a generated mapping's adoption check, and what value (if any) those
/// targets already hold. Extracted from the worker's own copies (<c>SyncTaskProcessorBase</c>) so the
/// worker's real synchronisation and Sync Preview's read-only evaluation share one implementation with
/// identical semantics, rather than Sync Preview reimplementing (or, as before this fix, simply omitting)
/// the adoption check.
/// </summary>
public static class GeneratedValueParticipation
{
    /// <summary>
    /// A generated mapping's participating export targets: every enabled export mapping in
    /// <paramref name="exportRules"/> whose only source is <paramref name="generatedMapping"/>'s target
    /// Metaverse attribute, minus the generation's own exclusions. An export mapping with more than one
    /// source, or one that reads the attribute via an expression (<c>mv["..."]</c>) rather than a direct
    /// single-source flow, is deliberately NOT a participating target in this release (Metaverse-Derived
    /// Attribute Flows is what would make that safe to detect).
    /// </summary>
    /// <param name="generatedMapping">The generated import mapping whose participating targets are wanted;
    /// a no-op (empty result) unless it carries both a target Metaverse attribute and a Generation row.</param>
    /// <param name="exportRules">Every export Synchronisation Rule the caller knows about, whatever
    /// Metaverse Object Type each one targets; the participation test is keyed on which single Metaverse
    /// attribute a mapping reads, not on the generated mapping's own object type, so there is no benefit to
    /// pre-filtering by type before calling this.</param>
    public static List<(int ConnectedSystemId, int AttributeId)> ComputeParticipatingTargets(
        SyncRuleMapping generatedMapping, IEnumerable<SyncRule> exportRules)
    {
        if (!generatedMapping.TargetMetaverseAttributeId.HasValue || generatedMapping.Generation == null)
            return [];

        var attributeId = generatedMapping.TargetMetaverseAttributeId.Value;
        var excludedSystemIds = generatedMapping.Generation.Exclusions.Select(e => e.ConnectedSystemId).ToHashSet();

        return exportRules
            .Where(sr => sr.Enabled)
            .SelectMany(sr => sr.AttributeFlowRules
                .Where(m => m.Enabled && m.TargetConnectedSystemAttributeId.HasValue)
                .Select(m => (Rule: sr, Mapping: m)))
            .Where(x => !excludedSystemIds.Contains(x.Rule.ConnectedSystemId)
                && x.Mapping.Sources.Count == 1
                && x.Mapping.Sources[0].MetaverseAttributeId == attributeId)
            .Select(x => (ConnectedSystemId: x.Rule.ConnectedSystemId, AttributeId: x.Mapping.TargetConnectedSystemAttributeId!.Value))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Adopt before generate's value lookup (FR 30): the first non-empty value <paramref name="metaverseObjectId"/>'s
    /// joined Connected System Objects already hold, among <paramref name="targets"/>, in ascending Connected
    /// System id order. Costs two batched queries against <paramref name="repository"/> (never one per target,
    /// and never any query when <paramref name="targets"/> is empty); read-only throughout, so Sync Preview can
    /// call it over a <see cref="ReadOnlySyncRepositoryGuard"/> exactly as the worker calls it over its real
    /// repository.
    /// </summary>
    public static async Task<string?> FindAdoptableValueAsync(
        ISyncRepository repository, Guid metaverseObjectId, IReadOnlyList<(int ConnectedSystemId, int AttributeId)> targets)
    {
        if (targets.Count == 0)
            return null;

        var targetSystemIds = targets.Select(t => t.ConnectedSystemId).Distinct().ToList();
        var csoLookup = await repository.GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync([metaverseObjectId], targetSystemIds);
        if (csoLookup.Count == 0)
            return null;

        var joined = targets
            .Where(t => csoLookup.ContainsKey((metaverseObjectId, t.ConnectedSystemId)))
            .OrderBy(t => t.ConnectedSystemId)
            .ToList();
        if (joined.Count == 0)
            return null;

        var csoIds = joined.Select(t => csoLookup[(metaverseObjectId, t.ConnectedSystemId)].Id).Distinct().ToList();
        var attributeValues = await repository.GetCsoAttributeValuesByCsoIdsAsync(csoIds);

        foreach (var (connectedSystemId, attributeId) in joined)
        {
            var cso = csoLookup[(metaverseObjectId, connectedSystemId)];
            var value = attributeValues.FirstOrDefault(v => v.ConnectedSystemObject.Id == cso.Id && v.AttributeId == attributeId);
            var rendered = value == null ? null : RenderAdoptableConnectedSystemValue(value);
            if (!string.IsNullOrEmpty(rendered))
                return rendered;
        }

        return null;
    }

    /// <summary>
    /// Renders a Connected System Object attribute value as the text an adopt-before-generate candidate
    /// needs: a Number or LongNumber participating target holds its value in
    /// <see cref="ConnectedSystemObjectAttributeValue.IntValue"/>/<see cref="ConnectedSystemObjectAttributeValue.LongValue"/>,
    /// never <see cref="ConnectedSystemObjectAttributeValue.StringValue"/>, so reading only
    /// <c>StringValue</c> would mean a numeric target never adopted. The caller's own numeric parsing
    /// renders this string back to a number, invariant, for a Number/LongNumber target when building the
    /// outcome.
    /// </summary>
    private static string? RenderAdoptableConnectedSystemValue(ConnectedSystemObjectAttributeValue value)
    {
        if (!string.IsNullOrEmpty(value.StringValue))
            return value.StringValue;
        if (value.IntValue.HasValue)
            return value.IntValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value.LongValue.HasValue)
            return value.LongValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }
}
