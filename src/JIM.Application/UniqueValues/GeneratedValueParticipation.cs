// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
