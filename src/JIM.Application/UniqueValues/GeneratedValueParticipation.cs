// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.UniqueValues;

/// <summary>
/// Which export targets participate in a generated import mapping's uniqueness checks (Unique Value Generation,
/// #242, Phase 2 work package J). Shared by the worker's real synchronisation (<c>SyncTaskProcessorBase</c>) and
/// Sync Preview's read-only evaluation so both compute the connector-space collision gate's inputs identically.
/// <para>
/// This is a uniqueness concern only, never a precedence one: whether a generated mapping contributes at all is
/// decided by the ordinary Attribute Flow priority model, and a value already held by a target or left on the
/// Metaverse Object is never taken over as the mapping's own (product-owner decision 2026-10-01: a generated
/// value is just another way to produce a value for an Attribute Flow; it differs only in being generated once).
/// </para>
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
    /// A generated import mapping's probe targets (release 3, plan Phase 7 item 3): the participating targets of
    /// <see cref="ComputeParticipatingTargets"/> (enabled, single-source, direct flows of the generated attribute,
    /// minus the generation's exclusions) whose target attribute <see cref="IsDirectProbeTarget"/> accepts. A value
    /// reaching a target through an export expression is not a participating target at all; a Distinguished Name,
    /// an external id, a non-text attribute, or an attribute whose definition is not loaded is participating but not
    /// probed. Collision Remediation (release 4) covers what is not probed.
    /// </summary>
    public static List<UniquenessProbeTarget> ComputeProbeTargets(SyncRuleMapping generatedMapping, IEnumerable<SyncRule> exportRules)
    {
        if (!generatedMapping.TargetMetaverseAttributeId.HasValue || generatedMapping.Generation == null)
            return [];

        var attributeId = generatedMapping.TargetMetaverseAttributeId.Value;
        var excludedSystemIds = generatedMapping.Generation.Exclusions.Select(e => e.ConnectedSystemId).ToHashSet();

        return exportRules
            .Where(sr => sr.Enabled && !excludedSystemIds.Contains(sr.ConnectedSystemId))
            .SelectMany(sr => sr.AttributeFlowRules
                .Where(m => m.Enabled
                    && m.TargetConnectedSystemAttributeId.HasValue
                    && m.Sources.Count == 1
                    && m.Sources[0].MetaverseAttributeId == attributeId
                    && IsDirectProbeTarget(m.TargetConnectedSystemAttribute))
                .Select(m => new UniquenessProbeTarget(sr.ConnectedSystemId, m.TargetConnectedSystemAttributeId!.Value)))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Describes every Connected System attribute <paramref name="generatedMapping"/>'s value is exported to, and how
    /// its availability is checked there (release 3): the read model behind the "Checked for availability in" panel,
    /// the REST mapping DTO's <c>participants</c> and <c>Get-JIMSyncRuleMapping</c>. One row per (Connected System,
    /// attribute), ordered by Connected System name then attribute name.
    /// <para>
    /// The rows say what the engine does, so they are built from the same tests: an enabled, single-source, direct
    /// flow is a participating target (<see cref="ComputeParticipatingTargets"/>), checked against JIM's own records
    /// and probed where <see cref="ComputeProbeTargets"/> would probe it and the system's Connector can. A value that
    /// reaches a system through an expression, or combined with other sources, is checked neither way, because what is
    /// written there is not the generated value. A disabled flow is listed only when its system is excluded, so the
    /// exclusion stays visible and can be removed.
    /// </para>
    /// <para>
    /// An export-mode generated mapping (one on an export Synchronisation Rule, generating a Connected System
    /// attribute) is one row: its own target in <paramref name="hostConnectedSystemId"/>, which is the only place it
    /// is checked, and so cannot be excluded.
    /// </para>
    /// </summary>
    /// <param name="generatedMapping">The generated mapping, saved or being edited; no rows unless it is generated.</param>
    /// <param name="hostConnectedSystemId">The Connected System of the Synchronisation Rule holding the mapping; read
    /// only for an export-mode generated mapping.</param>
    /// <param name="exportRules">Every export Synchronisation Rule the caller knows about, disabled ones included.</param>
    /// <param name="systems">A description of every Connected System those rules (and the host rule) export to.</param>
    public static List<GeneratedValueParticipant> DescribeParticipants(
        SyncRuleMapping generatedMapping,
        int hostConnectedSystemId,
        IEnumerable<SyncRule> exportRules,
        IReadOnlyDictionary<int, GeneratedValueParticipantSystem> systems)
    {
        if (generatedMapping.Generation == null)
            return [];

        var metaverseAttributeId = generatedMapping.ResolveTargetMetaverseAttributeId();
        if (!metaverseAttributeId.HasValue)
            return DescribeExportModeParticipant(generatedMapping, hostConnectedSystemId, systems);

        var attributeName = generatedMapping.TargetMetaverseAttribute?.Name;
        var excludedSystemIds = generatedMapping.Generation.Exclusions.Select(e => e.ConnectedSystemId).ToHashSet();

        var rows = new List<GeneratedValueParticipant>();
        foreach (var (rule, mapping) in exportRules
            .Where(rule => rule.Direction == SyncRuleDirection.Export)
            .SelectMany(rule => rule.AttributeFlowRules, (rule, mapping) => (rule, mapping))
            .Where(x => x.mapping.ResolveTargetConnectedSystemAttributeId().HasValue))
        {
            var flowsEnabled = rule.Enabled && mapping.Enabled;
            var excluded = excludedSystemIds.Contains(rule.ConnectedSystemId);
            var system = DescribeSystem(rule.ConnectedSystemId, systems);
            var target = mapping.TargetConnectedSystemAttribute;
            var targetId = mapping.ResolveTargetConnectedSystemAttributeId()!.Value;

            switch (ClassifyExportFlow(mapping, metaverseAttributeId.Value, attributeName))
            {
                case ExportFlowKind.Direct when excluded:
                    rows.Add(NewRow(system, targetId, target, GeneratedValueParticipantCheck.NotChecked, GeneratedValueParticipantReason.Excluded, isExcluded: true, canBeExcluded: true));
                    break;

                case ExportFlowKind.Direct when flowsEnabled:
                {
                    var (check, reason) = DescribeDirectCheck(system, target);
                    rows.Add(NewRow(system, targetId, target, check, reason, isExcluded: false, canBeExcluded: true));
                    break;
                }

                case ExportFlowKind.ThroughExpression when flowsEnabled:
                    rows.Add(NewRow(system, targetId, target, GeneratedValueParticipantCheck.NotChecked, GeneratedValueParticipantReason.ExportedThroughExpression, isExcluded: false, canBeExcluded: false));
                    break;

                case ExportFlowKind.CombinedWithOtherSources when flowsEnabled:
                    rows.Add(NewRow(system, targetId, target, GeneratedValueParticipantCheck.NotChecked, GeneratedValueParticipantReason.CombinedWithOtherSources, isExcluded: false, canBeExcluded: false));
                    break;
            }
        }

        // One row per (system, attribute). Where two rules send the value to the same attribute in different ways, the
        // row the engine acts on wins: a direct flow (checked, or excluded) over one it does not check.
        return rows
            .GroupBy(r => (r.ConnectedSystemId, r.ConnectedSystemObjectTypeAttributeId))
            .Select(g => g.OrderBy(r => r.CanBeExcluded ? 0 : 1).First())
            .OrderBy(r => r.ConnectedSystemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.AttributeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The Connected Systems an import-mode generated mapping may exclude (release 3): every system an export
    /// Attribute Flow sends <paramref name="metaverseAttributeId"/> to unchanged (single-source and direct), whether the
    /// flow or its rule is enabled or not, so disabling a flow never invalidates an exclusion already saved.
    /// </summary>
    public static HashSet<int> ComputeExcludableSystemIds(int metaverseAttributeId, IEnumerable<SyncRule> exportRules) =>
        exportRules
            .Where(rule => rule.Direction == SyncRuleDirection.Export)
            .Where(rule => rule.AttributeFlowRules.Any(mapping =>
                mapping.ResolveTargetConnectedSystemAttributeId().HasValue
                && ClassifyExportFlow(mapping, metaverseAttributeId, metaverseAttributeName: null) == ExportFlowKind.Direct))
            .Select(rule => rule.ConnectedSystemId)
            .ToHashSet();

    private static List<GeneratedValueParticipant> DescribeExportModeParticipant(
        SyncRuleMapping generatedMapping, int hostConnectedSystemId, IReadOnlyDictionary<int, GeneratedValueParticipantSystem> systems)
    {
        var targetId = generatedMapping.ResolveTargetConnectedSystemAttributeId();
        if (!targetId.HasValue)
            return [];

        var system = DescribeSystem(hostConnectedSystemId, systems);
        var target = generatedMapping.TargetConnectedSystemAttribute;
        var (check, reason) = DescribeDirectCheck(system, target);
        return [NewRow(system, targetId.Value, target, check, reason, isExcluded: false, canBeExcluded: false)];
    }

    /// <summary>
    /// How a value flowing unchanged to <paramref name="target"/> in <paramref name="system"/> is checked: JIM's own
    /// records always, and a probe where the engine would probe it (<see cref="IsDirectProbeTarget"/>) and the
    /// Connector can.
    /// </summary>
    private static (GeneratedValueParticipantCheck Check, GeneratedValueParticipantReason Reason) DescribeDirectCheck(
        GeneratedValueParticipantSystem system, ConnectedSystemObjectTypeAttribute? target)
    {
        if (!system.ConnectorCanProbe)
            return (GeneratedValueParticipantCheck.JimRecordsOnly, GeneratedValueParticipantReason.ConnectorCannotProbe);

        if (target != null && target.Type != AttributeDataType.Text)
            return (GeneratedValueParticipantCheck.JimRecordsOnly, GeneratedValueParticipantReason.NotTextValue);

        if (!IsDirectProbeTarget(target) || !system.CanProbeAttribute(target!.Name))
            return (GeneratedValueParticipantCheck.JimRecordsOnly, GeneratedValueParticipantReason.AttributeNotProbed);

        return (GeneratedValueParticipantCheck.JimRecordsAndProbe, GeneratedValueParticipantReason.None);
    }

    private enum ExportFlowKind
    {
        Unrelated,
        Direct,
        ThroughExpression,
        CombinedWithOtherSources
    }

    /// <summary>
    /// How an export mapping carries the generated attribute: directly (its only source, which is what
    /// <see cref="ComputeParticipatingTargets"/> counts), inside an expression reading <c>mv["..."]</c>, combined with
    /// other sources, or not at all.
    /// </summary>
    private static ExportFlowKind ClassifyExportFlow(SyncRuleMapping mapping, int metaverseAttributeId, string? metaverseAttributeName)
    {
        var readsAttribute = mapping.Sources.Select(source => source.MetaverseAttribute?.Id ?? source.MetaverseAttributeId).Contains(metaverseAttributeId);
        var expressionReadsAttribute = metaverseAttributeName != null
            && DerivedFlowGraph.GetMetaverseInputNames(mapping).Contains(metaverseAttributeName, StringComparer.OrdinalIgnoreCase);

        if (!readsAttribute && !expressionReadsAttribute)
            return ExportFlowKind.Unrelated;

        if (mapping.Sources.Count > 1)
            return ExportFlowKind.CombinedWithOtherSources;

        return readsAttribute ? ExportFlowKind.Direct : ExportFlowKind.ThroughExpression;
    }

    private static GeneratedValueParticipantSystem DescribeSystem(int connectedSystemId, IReadOnlyDictionary<int, GeneratedValueParticipantSystem> systems) =>
        systems.TryGetValue(connectedSystemId, out var system)
            ? system
            : new GeneratedValueParticipantSystem(connectedSystemId, $"Connected System {connectedSystemId}", string.Empty, false, _ => false);

    private static GeneratedValueParticipant NewRow(GeneratedValueParticipantSystem system, int targetId, ConnectedSystemObjectTypeAttribute? target,
        GeneratedValueParticipantCheck check, GeneratedValueParticipantReason reason, bool isExcluded, bool canBeExcluded) => new()
    {
        ConnectedSystemId = system.Id,
        ConnectedSystemName = system.Name,
        ConnectorName = system.ConnectorName,
        ConnectedSystemObjectTypeAttributeId = targetId,
        AttributeName = target?.Name ?? $"Attribute {targetId}",
        IsExcluded = isExcluded,
        CanBeExcluded = canBeExcluded,
        Check = check,
        Reason = reason,
        ReportsCollisions = system.ReportsCollisions
    };

    /// <summary>
    /// Whether a value generated for, or flowed directly to, <paramref name="attribute"/> is probed: a loaded, text
    /// attribute that is neither the external id nor the secondary external id (for an LDAP directory, the
    /// Distinguished Name, unique only within its container, so a partition-wide search would report false
    /// collisions). Export mode asks this of the generated attribute itself.
    /// </summary>
    public static bool IsDirectProbeTarget(ConnectedSystemObjectTypeAttribute? attribute) =>
        attribute != null
        && attribute.Type == AttributeDataType.Text
        && !attribute.IsExternalId
        && !attribute.IsSecondaryExternalId;
}
