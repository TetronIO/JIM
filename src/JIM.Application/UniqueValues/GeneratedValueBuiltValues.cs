// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Transactional;

namespace JIM.Application.UniqueValues;

/// <summary>
/// The values built from generated values (Generated Value Forms, #2023): which Derived Attribute Flows (#1750) and
/// export expressions build a value from a generated value, through which steps, on which Synchronisation Rules. The one
/// place JIM decides what "a value built from a generated value" is, shared by Collision Remediation's attribution, its
/// anchoring check, the "Checked for availability in" panel and the REST API, so they can never disagree. Pure: it reads
/// only the rules and derived flow graph it is handed.
/// <para>
/// A value is followed back through the Metaverse attributes its expression reads, and through the derived flows that
/// build those, until it reaches generated attributes. A value whose inputs reach exactly one generated value is built
/// from it; a value reaching two or more belongs to none of them. Inputs that are neither generated nor derived (a UPN
/// suffix from HR) are allowed. Only enabled rules and mappings build anything, and a value is only ever followed through
/// flows for its own Metaverse Object Type.
/// </para>
/// </summary>
public sealed class GeneratedValueBuiltValues
{
    private readonly DerivedFlowGraph? _graph;
    private readonly List<SyncRule> _rules;
    private readonly List<(SyncRule Rule, SyncRuleMapping Mapping)> _importMappings;
    private readonly Dictionary<int, HashSet<int>> _generatedAttributeIdsByType;
    private readonly Dictionary<string, int> _metaverseAttributeIdsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _metaverseAttributeNamesById = [];

    private GeneratedValueBuiltValues(IEnumerable<SyncRule> rules, DerivedFlowGraph? graph, IEnumerable<MetaverseObjectType> metaverseObjectTypes)
    {
        _graph = graph;
        _rules = rules.ToList();
        _importMappings = _rules
            .Where(rule => rule.Enabled && rule.Direction == SyncRuleDirection.Import)
            .SelectMany(rule => rule.AttributeFlowRules, (rule, mapping) => (Rule: rule, Mapping: mapping))
            .Where(x => x.Mapping.Enabled && x.Mapping.ResolveTargetMetaverseAttributeId().HasValue)
            .ToList();
        _generatedAttributeIdsByType = _importMappings
            .Where(x => x.Mapping.Generation != null)
            .GroupBy(x => x.Rule.MetaverseObjectTypeId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Mapping.ResolveTargetMetaverseAttributeId()!.Value).ToHashSet());

        // Names an expression may read with mv["..."], from every Metaverse Object Type the rules know.
        foreach (var attribute in metaverseObjectTypes.SelectMany(type => type.Attributes)
                     .Concat(_rules.SelectMany(rule => rule.MetaverseObjectType?.Attributes ?? []))
                     .Concat(_importMappings.Select(x => x.Mapping.TargetMetaverseAttribute).OfType<MetaverseAttribute>()))
        {
            _metaverseAttributeIdsByName.TryAdd(attribute.Name, attribute.Id);
            _metaverseAttributeNamesById.TryAdd(attribute.Id, attribute.Name);
        }
    }

    /// <summary>
    /// Indexes <paramref name="rules"/>: every Synchronisation Rule the caller knows about, import and export, enabled or
    /// not (disabled ones build nothing, but a change queued by one can still be attributed to it).
    /// </summary>
    /// <param name="rules">The Synchronisation Rules.</param>
    /// <param name="graph">The derived flow graph of the enabled import rules; null when it could not be built (a
    /// dependency cycle), in which case nothing is followed through a derivation.</param>
    /// <param name="metaverseObjectTypes">The Metaverse Object Types the rules flow to, with their attributes, for
    /// resolving the <c>mv["..."]</c> names export expressions read, when the rules' own type navigation is not loaded.</param>
    public static GeneratedValueBuiltValues Create(IEnumerable<SyncRule> rules, DerivedFlowGraph? graph, IEnumerable<MetaverseObjectType>? metaverseObjectTypes = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new GeneratedValueBuiltValues(rules, graph, metaverseObjectTypes ?? []);
    }

    /// <summary>
    /// Every value built from <paramref name="generatedMapping"/>'s value: the Derived Attribute Flows and export
    /// expressions whose inputs lead back to it, each classified as a <see cref="GeneratedValueBuiltValueKind.Form"/>, a
    /// value <see cref="GeneratedValueBuiltValueKind.BuiltOnAnotherRule"/>, or one
    /// <see cref="GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues"/>. Empty for anything but an import-mode
    /// generated mapping.
    /// </summary>
    public IReadOnlyList<GeneratedValueBuiltValue> BuiltFrom(SyncRuleMapping generatedMapping)
    {
        ArgumentNullException.ThrowIfNull(generatedMapping);
        var generatedAttributeId = generatedMapping.ResolveTargetMetaverseAttributeId();
        var ownRule = RuleOf(generatedMapping);
        if (generatedMapping.Generation == null || !generatedAttributeId.HasValue || ownRule == null)
            return [];

        var generatedId = generatedAttributeId.Value;
        return CandidateMappings(ownRule.MetaverseObjectTypeId)
            .Select(candidate => (candidate.Rule, candidate.Mapping, Paths: PathsFrom(candidate.Rule, candidate.Mapping, applySwitch: false)))
            .Where(candidate => candidate.Paths.Any(p => p.GeneratedAttributeId == generatedId))
            .Select(candidate => Describe(candidate.Rule, candidate.Mapping, candidate.Paths, generatedMapping, generatedId, ownRule))
            .ToList();
    }

    /// <summary>
    /// What <paramref name="mapping"/>'s value is built from, for its own editor and its REST representation: the value
    /// described relative to the generated value it is built from, or, built from several, relative to the first of them.
    /// Null when it is built from no generated value (a direct flow, a generated mapping itself, an expression reading
    /// none). The mapping need not be saved or enabled: the portal describes the flow being edited.
    /// </summary>
    public GeneratedValueBuiltValue? BuiltValueOf(SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var rule = RuleOf(mapping);
        if (rule == null || mapping.Generation != null || !IsExpressionMapping(mapping))
            return null;

        var paths = PathsFrom(rule, mapping, applySwitch: false);
        var generatedMapping = paths
            .Select(p => p.GeneratedAttributeId)
            .Distinct()
            .Select(id => GeneratedMappingOf(id, rule.MetaverseObjectTypeId))
            .OfType<SyncRuleMapping>()
            .OrderBy(m => m.Id)
            .FirstOrDefault();
        var ownRule = generatedMapping == null ? null : RuleOf(generatedMapping);
        if (generatedMapping == null || ownRule == null)
            return null;

        return Describe(rule, mapping, paths, generatedMapping, generatedMapping.ResolveTargetMetaverseAttributeId()!.Value, ownRule);
    }

    private GeneratedValueBuiltValue Describe(SyncRule rule, SyncRuleMapping mapping, List<SourcePath> paths, SyncRuleMapping generatedMapping,
        int generatedAttributeId, SyncRule ownRule)
    {
        var generatedAttributeIds = paths.Select(p => p.GeneratedAttributeId).Distinct().ToList();
        var kind = generatedAttributeIds.Count > 1
            ? GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues
            : paths.All(p => p.StepRuleIds.All(id => id == ownRule.Id))
                ? GeneratedValueBuiltValueKind.Form
                : GeneratedValueBuiltValueKind.BuiltOnAnotherRule;

        var via = paths
            .Where(p => p.GeneratedAttributeId == generatedAttributeId)
            .OrderBy(p => p.Via.Count)
            .First().Via;

        var metaverseAttributeId = rule.Direction == SyncRuleDirection.Import ? mapping.ResolveTargetMetaverseAttributeId() : null;
        return new GeneratedValueBuiltValue
        {
            Mapping = mapping,
            SyncRule = rule,
            GeneratedMapping = generatedMapping,
            Kind = kind,
            MetaverseAttributeId = metaverseAttributeId,
            MetaverseAttributeName = metaverseAttributeId.HasValue ? NameOf(metaverseAttributeId.Value) : null,
            Via = via.Select(NameOf).ToList(),
            BuiltFromAttributeNames = generatedAttributeIds
                .OrderBy(id => id == generatedAttributeId ? 0 : 1)
                .ThenBy(NameOf, StringComparer.OrdinalIgnoreCase)
                .Select(NameOf)
                .ToList(),
            ExportedTo = metaverseAttributeId.HasValue
                ? DirectExportTargets(metaverseAttributeId.Value, rule.MetaverseObjectTypeId)
                : [(rule.ConnectedSystemId, mapping.ResolveTargetConnectedSystemAttributeId()!.Value)]
        };
    }

    private SyncRuleMapping? GeneratedMappingOf(int generatedAttributeId, int metaverseObjectTypeId) =>
        _importMappings
            .Where(x => x.Mapping.Generation != null
                && x.Rule.MetaverseObjectTypeId == metaverseObjectTypeId
                && x.Mapping.ResolveTargetMetaverseAttributeId() == generatedAttributeId)
            .Select(x => x.Mapping)
            .OrderBy(m => m.Id)
            .FirstOrDefault();

    /// <summary>
    /// The generated import mappings whose values <paramref name="mapping"/>'s value is built from: one for a value built
    /// from a single generated value, several for one built from more than one, none for anything else (a mapping whose
    /// expression reads no generated value, a direct flow, a generated mapping itself). The mapping need not be enabled.
    /// </summary>
    public IReadOnlyList<SyncRuleMapping> GeneratedValuesBuilding(SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var rule = RuleOf(mapping);
        if (rule == null || mapping.Generation != null || !IsExpressionMapping(mapping))
            return [];

        var generatedAttributeIds = PathsFrom(rule, mapping, applySwitch: false).Select(p => p.GeneratedAttributeId).ToHashSet();
        return _importMappings
            .Where(x => x.Mapping.Generation != null
                && x.Rule.MetaverseObjectTypeId == rule.MetaverseObjectTypeId
                && generatedAttributeIds.Contains(x.Mapping.ResolveTargetMetaverseAttributeId()!.Value))
            .Select(x => x.Mapping)
            .ToList();
    }

    /// <summary>
    /// The generated values an exported attribute change carries, for attribution: from each export mapping that
    /// produces it (the change's own rule, even if since disabled), followed back to generated attributes. A value whose
    /// <see cref="SyncRuleMapping.KeepUnique"/> switch is off carries none: its refusal is an ordinary export error. The
    /// switch is per value, so only the refused value's own switch counts, never one further back.
    /// </summary>
    /// <param name="change">The exported attribute change.</param>
    /// <param name="honourSwitch">False to ignore the switch, to tell a switched-off value from one built from no
    /// generated value at all.</param>
    internal List<GeneratedValueSource> SourcesOf(PendingExportAttributeValueChange change, bool honourSwitch = true) =>
        ExportMappingsProducing(change)
            .SelectMany(producing => SourcesThrough(producing.Rule, producing.Mapping, honourSwitch))
            .Distinct()
            .ToList();

    /// <summary>
    /// The generated values one export mapping producing a refused value carries: itself when it is generated, nothing when
    /// it is an expression with its switch off (and the switch is honoured), otherwise every generated value it is built from.
    /// </summary>
    private IEnumerable<GeneratedValueSource> SourcesThrough(SyncRule rule, SyncRuleMapping mapping, bool honourSwitch)
    {
        if (mapping.Generation != null)
            return [new GeneratedValueSource(null, mapping, ThroughDerivation: false)];

        if (honourSwitch && IsExpressionMapping(mapping) && !mapping.KeepUnique)
            return [];

        return PathsFrom(rule, mapping, applySwitch: honourSwitch)
            .Select(p => new GeneratedValueSource(p.GeneratedAttributeId, null, p.ThroughDerivation));
    }

    /// <summary>
    /// The Metaverse attribute an exported attribute change carries unchanged (a direct export flow), for messages; null
    /// when every mapping producing it builds a value with an expression.
    /// </summary>
    internal string? CarriedMetaverseAttributeNameOf(PendingExportAttributeValueChange change)
    {
        var direct = ExportMappingsProducing(change)
            .Select(x => x.Mapping)
            .FirstOrDefault(m => m.Sources.Count == 1 && m.Sources[0].Expression == null);
        var id = direct == null ? null : direct.Sources[0].MetaverseAttribute?.Id ?? direct.Sources[0].MetaverseAttributeId;
        return id.HasValue ? NameOf(id.Value) : null;
    }

    /// <summary>
    /// A display name for a Metaverse attribute the rules know, for messages.
    /// </summary>
    internal string NameOf(int metaverseAttributeId) =>
        _metaverseAttributeNamesById.TryGetValue(metaverseAttributeId, out var name) ? name : $"Metaverse attribute {metaverseAttributeId}";

    private IEnumerable<(SyncRule Rule, SyncRuleMapping Mapping)> ExportMappingsProducing(PendingExportAttributeValueChange change) =>
        _rules
            .Where(rule => rule.Direction == SyncRuleDirection.Export)
            .Where(rule => rule.Enabled || rule.Id == change.SyncRuleId)
            .Where(rule => !change.SyncRuleId.HasValue || rule.Id == change.SyncRuleId)
            .SelectMany(rule => rule.AttributeFlowRules, (rule, mapping) => (Rule: rule, Mapping: mapping))
            .Where(x => x.Mapping.ResolveTargetConnectedSystemAttributeId() == change.AttributeId);

    /// <summary>
    /// The mappings that can build a value from a generated value, for one Metaverse Object Type: enabled Derived
    /// Attribute Flows, and enabled export expressions on enabled export rules.
    /// </summary>
    private IEnumerable<(SyncRule Rule, SyncRuleMapping Mapping)> CandidateMappings(int metaverseObjectTypeId)
    {
        foreach (var (rule, mapping) in _importMappings.Where(x => x.Rule.MetaverseObjectTypeId == metaverseObjectTypeId
                     && x.Mapping.Generation == null && _graph?.IsDerived(x.Mapping) == true))
        {
            yield return (rule, mapping);
        }

        foreach (var rule in _rules.Where(r => r.Enabled && r.Direction == SyncRuleDirection.Export && r.MetaverseObjectTypeId == metaverseObjectTypeId))
        {
            foreach (var mapping in rule.AttributeFlowRules.Where(m => m.Enabled && m.Generation == null
                         && m.ResolveTargetConnectedSystemAttributeId().HasValue && IsExpressionMapping(m)))
            {
                yield return (rule, mapping);
            }
        }
    }

    /// <summary>
    /// Every path from <paramref name="mapping"/>'s inputs back to a generated attribute: the generated attribute
    /// reached, the Metaverse attributes passed through (nearest the generated value first), and the rules of the derived
    /// steps taken, the mapping's own included when it is a Derived Attribute Flow.
    /// </summary>
    private List<SourcePath> PathsFrom(SyncRule rule, SyncRuleMapping mapping, bool applySwitch)
    {
        var paths = new List<SourcePath>();
        var typeId = rule.MetaverseObjectTypeId;
        var stepRules = rule.Direction == SyncRuleDirection.Import ? new List<int> { rule.Id } : [];

        // A single direct source carries the Metaverse value as it is; an expression builds a value from what it reads.
        var directSource = mapping.Sources.Count == 1 && mapping.Sources[0].Expression == null
            ? mapping.Sources[0].MetaverseAttribute?.Id ?? mapping.Sources[0].MetaverseAttributeId
            : null;
        if (directSource.HasValue)
        {
            Expand(directSource.Value, typeId, throughDerivation: false, [], [], applySwitch, paths, []);
            return paths;
        }

        foreach (var input in InputIdsOf(mapping))
            Expand(input, typeId, throughDerivation: true, stepRules, [], applySwitch: false, paths, []);

        return paths;
    }

    private void Expand(int attributeId, int typeId, bool throughDerivation, List<int> stepRules, List<int> via, bool applySwitch,
        List<SourcePath> paths, HashSet<int> onPath)
    {
        if (onPath.Contains(attributeId))
            return;

        if (_generatedAttributeIdsByType.TryGetValue(typeId, out var generated) && generated.Contains(attributeId))
        {
            paths.Add(new SourcePath(attributeId, throughDerivation, stepRules, via));
            return;
        }

        if (_graph == null)
            return;

        var nextOnPath = new HashSet<int>(onPath) { attributeId };
        foreach (var (rule, mapping) in _importMappings.Where(x => x.Rule.MetaverseObjectTypeId == typeId
                     && x.Mapping.ResolveTargetMetaverseAttributeId() == attributeId
                     && x.Mapping.Generation == null
                     && _graph.IsDerived(x.Mapping)))
        {
            // The switch is per value: only the refused value's own Derived Attribute Flows are governed by it.
            if (applySwitch && !mapping.KeepUnique)
                continue;

            List<int> nextSteps = [.. stepRules, rule.Id];
            List<int> nextVia = [attributeId, .. via];
            foreach (var input in _graph.GetMetaverseInputIds(mapping))
                Expand(input, typeId, throughDerivation: true, nextSteps, nextVia, applySwitch: false, paths, nextOnPath);
        }
    }

    private IEnumerable<int> InputIdsOf(SyncRuleMapping mapping) =>
        DerivedFlowGraph.GetMetaverseInputNames(mapping)
            .Select(name => _metaverseAttributeIdsByName.TryGetValue(name, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value);

    /// <summary>
    /// The enabled, single-source, direct export flows of <paramref name="metaverseAttributeId"/>: where the value is
    /// written unchanged.
    /// </summary>
    private List<(int ConnectedSystemId, int AttributeId)> DirectExportTargets(int metaverseAttributeId, int metaverseObjectTypeId) =>
        _rules
            .Where(rule => rule.Enabled && rule.Direction == SyncRuleDirection.Export && rule.MetaverseObjectTypeId == metaverseObjectTypeId)
            .SelectMany(rule => rule.AttributeFlowRules
                .Where(m => m.Enabled
                    && m.ResolveTargetConnectedSystemAttributeId().HasValue
                    && m.Sources.Count == 1
                    && m.Sources[0].Expression == null
                    && (m.Sources[0].MetaverseAttribute?.Id ?? m.Sources[0].MetaverseAttributeId) == metaverseAttributeId)
                .Select(m => (rule.ConnectedSystemId, m.ResolveTargetConnectedSystemAttributeId()!.Value)))
            .Distinct()
            .ToList();

    private SyncRule? RuleOf(SyncRuleMapping mapping) =>
        mapping.SyncRule ?? _rules.FirstOrDefault(rule => rule.Id == mapping.SyncRuleId);

    private static bool IsExpressionMapping(SyncRuleMapping mapping) =>
        mapping.Sources.Any(source => !string.IsNullOrWhiteSpace(source.Expression));

    private sealed record SourcePath(int GeneratedAttributeId, bool ThroughDerivation, List<int> StepRuleIds, List<int> Via);

    /// <summary>
    /// One generated value a carried attribute is built from: a generated Metaverse attribute (import mode) or an
    /// export-mode generated mapping. Equality ignores <see cref="ThroughDerivation"/>, so the same generated value
    /// reached directly and through a derivation counts once.
    /// </summary>
    internal sealed record GeneratedValueSource(int? MetaverseAttributeId, SyncRuleMapping? ExportMapping, bool ThroughDerivation)
    {
        public bool Equals(GeneratedValueSource? other) =>
            other != null && MetaverseAttributeId == other.MetaverseAttributeId && ReferenceEquals(ExportMapping, other.ExportMapping);

        public override int GetHashCode() => HashCode.Combine(MetaverseAttributeId, ExportMapping);
    }
}
