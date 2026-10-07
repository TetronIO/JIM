// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.UniqueValues;

/// <summary>
/// Attributes a "value already in use" export rejection to the generated value it is about (Unique Value Generation,
/// #242, release 4; plan decision 9 as revised at the release 4 plan review). Pure: it reads only what it is handed.
/// <para>
/// Attribution never guesses. In order:
/// <list type="number">
/// <item><description>The Connected System named the attribute: attributed when the export carries that attribute and its
/// value is a generated value (an export mapping flowing a generated Metaverse attribute, or an export-mode generated
/// mapping), or a value derived from exactly one generated value (a User Principal Name derived from Account Name, read
/// from the derived flow graph, #1750; or an export expression reading exactly one).</description></item>
/// <item><description>Nothing was named: attributed when the export carries exactly one generated value, directly or
/// derived.</description></item>
/// </list>
/// Everything else is unattributable, and stays an ordinary export error: an attribute name JIM does not recognise, one
/// the export does not carry, one with no generated value behind it, or one with two or more.
/// </para>
/// </summary>
public static class GeneratedValueRejectionAttribution
{
    /// <summary>
    /// Attributes <paramref name="export"/>'s rejection.
    /// </summary>
    /// <param name="export">The rejected Pending Export, with its attribute changes.</param>
    /// <param name="rejectedAttributeName">The attribute the Connected System named, when it named one.</param>
    /// <param name="knownAttributes">The Connected System Object Type's attributes: the names the rejecting system can
    /// mean.</param>
    /// <param name="exportRules">The rejecting Connected System's export Synchronisation Rules.</param>
    /// <param name="importRules">Every import Synchronisation Rule, for the generated import mappings and derived flows.</param>
    /// <param name="derivedFlowGraph">The derived flow graph of <paramref name="importRules"/>; null when it could not be
    /// built, in which case nothing is attributed through a derivation.</param>
    public static GeneratedValueRejectionAttributionResult Attribute(
        PendingExport export,
        string? rejectedAttributeName,
        IReadOnlyCollection<ConnectedSystemObjectTypeAttribute> knownAttributes,
        IReadOnlyCollection<SyncRule> exportRules,
        IReadOnlyCollection<SyncRule> importRules,
        DerivedFlowGraph? derivedFlowGraph)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(knownAttributes);
        ArgumentNullException.ThrowIfNull(exportRules);
        ArgumentNullException.ThrowIfNull(importRules);

        var resolver = new SourceResolver(exportRules, importRules, derivedFlowGraph);

        if (!string.IsNullOrWhiteSpace(rejectedAttributeName))
        {
            var named = knownAttributes.FirstOrDefault(a => string.Equals(a.Name, rejectedAttributeName, StringComparison.OrdinalIgnoreCase));
            if (named == null)
                return GeneratedValueRejectionAttributionResult.Unattributable(
                    $"the Connected System named an attribute, \"{rejectedAttributeName}\", that is not an attribute of the object's type");

            var carrying = export.AttributeValueChanges.FirstOrDefault(c => c.AttributeId == named.Id);
            if (carrying == null)
                return GeneratedValueRejectionAttributionResult.Unattributable($"the export does not carry {named.Name}");

            var sources = resolver.Resolve(carrying);
            return sources.Count switch
            {
                0 => GeneratedValueRejectionAttributionResult.Unattributable($"{named.Name} does not carry a generated value"),
                1 => ToResult(sources[0], carrying),
                _ => GeneratedValueRejectionAttributionResult.Unattributable($"{named.Name} is built from more than one generated value")
            };
        }

        // Nothing named: the export must carry exactly one generated value, whether directly or through derivations.
        var carriers = export.AttributeValueChanges
            .Select(change => (Change: change, Sources: resolver.Resolve(change)))
            .Where(x => x.Sources.Count > 0)
            .ToList();
        var distinctSources = carriers.SelectMany(x => x.Sources).Distinct().ToList();

        if (distinctSources.Count == 0)
            return GeneratedValueRejectionAttributionResult.Unattributable("the Connected System named no attribute and the export carries no generated value");
        if (distinctSources.Count > 1)
            return GeneratedValueRejectionAttributionResult.Unattributable("the Connected System named no attribute and the export carries more than one generated value");

        // The attribute carrying the value directly is the better witness than one carrying a value derived from it.
        var direct = carriers.FirstOrDefault(x => !x.Sources[0].ThroughDerivation);
        var chosen = direct.Change != null ? direct : carriers[0];
        return ToResult(chosen.Sources[0], chosen.Change);
    }

    private static GeneratedValueRejectionAttributionResult ToResult(GeneratedSource source, PendingExportAttributeValueChange carrying) =>
        source.ExportMapping != null
            ? GeneratedValueRejectionAttributionResult.ToExportMapping(source.ExportMapping, carrying)
            : GeneratedValueRejectionAttributionResult.ToMetaverseAttribute(source.MetaverseAttributeId!.Value, carrying, source.ThroughDerivation);

    /// <summary>
    /// One generated value a carried attribute is built from: a generated Metaverse attribute (import mode) or an
    /// export-mode generated mapping. Equality ignores <see cref="ThroughDerivation"/>, so the same generated value
    /// reached directly and through a derivation counts once.
    /// </summary>
    private sealed record GeneratedSource(int? MetaverseAttributeId, SyncRuleMapping? ExportMapping, bool ThroughDerivation)
    {
        public bool Equals(GeneratedSource? other) =>
            other != null && MetaverseAttributeId == other.MetaverseAttributeId && ReferenceEquals(ExportMapping, other.ExportMapping);

        public override int GetHashCode() => HashCode.Combine(MetaverseAttributeId, ExportMapping);
    }

    /// <summary>
    /// Works out which generated values an exported attribute carries, from the export mappings that produce it and,
    /// behind those, the generated and derived import mappings.
    /// </summary>
    private sealed class SourceResolver
    {
        private readonly IReadOnlyCollection<SyncRule> _exportRules;
        private readonly DerivedFlowGraph? _graph;
        private readonly HashSet<int> _generatedMetaverseAttributeIds;
        private readonly List<SyncRuleMapping> _importMappings;
        private readonly Dictionary<string, int> _metaverseAttributeIdsByName = new(StringComparer.OrdinalIgnoreCase);

        public SourceResolver(IReadOnlyCollection<SyncRule> exportRules, IReadOnlyCollection<SyncRule> importRules, DerivedFlowGraph? graph)
        {
            _exportRules = exportRules;
            _graph = graph;
            _importMappings = importRules
                .Where(rule => rule.Enabled && rule.Direction == SyncRuleDirection.Import)
                .SelectMany(rule => rule.AttributeFlowRules)
                .Where(mapping => mapping.Enabled && mapping.ResolveTargetMetaverseAttributeId().HasValue)
                .ToList();
            _generatedMetaverseAttributeIds = _importMappings
                .Where(mapping => mapping.Generation != null)
                .Select(mapping => mapping.ResolveTargetMetaverseAttributeId()!.Value)
                .ToHashSet();

            // Names an export expression may read with mv["..."], from every Metaverse Object Type the rules know.
            foreach (var attribute in importRules.Concat(exportRules)
                         .SelectMany(rule => rule.MetaverseObjectType?.Attributes ?? [])
                         .Concat(_importMappings.Select(mapping => mapping.TargetMetaverseAttribute).OfType<JIM.Models.Core.MetaverseAttribute>()))
            {
                _metaverseAttributeIdsByName.TryAdd(attribute.Name, attribute.Id);
            }
        }

        public List<GeneratedSource> Resolve(PendingExportAttributeValueChange change)
        {
            var sources = new List<GeneratedSource>();
            foreach (var mapping in ExportMappingsProducing(change))
            {
                if (mapping.Generation != null)
                {
                    sources.Add(new GeneratedSource(null, mapping, ThroughDerivation: false));
                    continue;
                }

                // A single direct source carries the Metaverse value as it is; anything else (an expression) builds a
                // value from it, which is a derivation for attribution's purposes.
                var directSource = mapping.Sources.Count == 1 && mapping.Sources[0].Expression == null
                    ? mapping.Sources[0].MetaverseAttribute?.Id ?? mapping.Sources[0].MetaverseAttributeId
                    : null;

                var inputs = directSource.HasValue
                    ? [directSource.Value]
                    : DerivedFlowGraph.GetMetaverseInputNames(mapping)
                        .Select(name => _metaverseAttributeIdsByName.TryGetValue(name, out var id) ? id : (int?)null)
                        .Where(id => id.HasValue)
                        .Select(id => id!.Value)
                        .ToList();

                foreach (var input in inputs)
                    Expand(input, throughDerivation: !directSource.HasValue, sources, []);
            }

            return sources.Distinct().ToList();
        }

        private IEnumerable<SyncRuleMapping> ExportMappingsProducing(PendingExportAttributeValueChange change)
        {
            var mappings = _exportRules
                .Where(rule => rule.Enabled || rule.Id == change.SyncRuleId)
                .Where(rule => !change.SyncRuleId.HasValue || rule.Id == change.SyncRuleId)
                .SelectMany(rule => rule.AttributeFlowRules)
                .Where(mapping => mapping.ResolveTargetConnectedSystemAttributeId() == change.AttributeId)
                .ToList();
            return mappings;
        }

        private void Expand(int metaverseAttributeId, bool throughDerivation, List<GeneratedSource> sources, HashSet<int> visited)
        {
            if (!visited.Add(metaverseAttributeId))
                return;

            if (_generatedMetaverseAttributeIds.Contains(metaverseAttributeId))
            {
                sources.Add(new GeneratedSource(metaverseAttributeId, null, throughDerivation));
                return;
            }

            if (_graph == null)
                return;

            foreach (var input in _importMappings
                         .Where(mapping => mapping.ResolveTargetMetaverseAttributeId() == metaverseAttributeId && _graph.IsDerived(mapping))
                         .SelectMany(mapping => _graph.GetMetaverseInputIds(mapping)))
            {
                Expand(input, throughDerivation: true, sources, visited);
            }
        }
    }
}
