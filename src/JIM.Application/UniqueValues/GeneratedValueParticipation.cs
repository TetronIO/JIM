// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Application.UniqueValues;

/// <summary>
/// Adopt-before-generate's shared reads (Unique Value Generation, #242, Phase 2 work package J; adoption
/// source changed by product-owner decision after the Phase 2 release). Extracted from the worker's own
/// copies (<c>SyncTaskProcessorBase</c>) so the worker's real synchronisation and Sync Preview's read-only
/// evaluation share one implementation with identical semantics, rather than Sync Preview reimplementing (or,
/// as before the original extraction, simply omitting) the adoption check.
/// <para>
/// "Adopt before generate" as originally shipped read a value from the object's joined Connected System
/// Objects even when no import Attribute Flow from that system existed: a precedence rule outside JIM's
/// Attribute Flow priority model. That connector-space read (<c>ComputeParticipatingTargets</c>'s former
/// sibling <c>FindAdoptableValueAsync</c>) has been removed; an administrator who wants an existing target
/// account's value kept adds an import Attribute Flow from that target at higher priority instead, and
/// generation (which only runs when no higher-priority contribution supplies a value) then never runs for
/// that object. <see cref="ComputeParticipatingTargets"/> itself is unaffected: its participating-target list
/// still feeds the connector-space collision GATE (avoiding a freshly generated value that collides with a
/// value an export target already holds), which is a different concern from adoption.
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
    /// Adopt before generate's value lookup (FR 30, import mode; source changed by product-owner decision):
    /// the Metaverse Object's own current effective value for <paramref name="attributeId"/>, if it holds a
    /// non-empty one contributed by a rule OTHER than <paramref name="generatingSyncRuleId"/>. "Effective"
    /// mirrors <c>SyncEngine.GetEffectiveAttributeValues</c>: a persisted row not already staged for removal
    /// this pass. This is deliberately never a joined Connected System Object's value (see the class summary);
    /// the only way the Metaverse Object holds a value here at all, contributed by a DIFFERENT rule, is a
    /// higher-priority contributor that withdrew and left it behind for the priority model's take-over to hand
    /// to the next contributor (<c>ProcessGeneratedMapping</c> never stages an addition for its own winning
    /// attribute, and <c>TakeOverProvenance</c> never stages a removal, so a value still present and not
    /// pending removal here can only be a genuine hand-over, never a value this same pass is in the process of
    /// clearing for real).
    /// <para>
    /// <paramref name="generatingSyncRuleId"/> excludes the generating mapping's OWN prior output
    /// (<c>ApplyGeneratedValue</c> stamps a generated value's provenance with the generating rule's own id): a
    /// value this same mapping generated in an earlier pass whose commit then lost the cross-run collision
    /// race (<c>UniqueValueGenerationServer.CommitAssignmentsAsync</c>'s loser handling) is left on the object
    /// with no assignment of its own, and self-healing means that object must draw a fresh candidate next
    /// time, never "adopt" its own abandoned attempt. Passing null (only ever from a caller with no persisted
    /// mapping, such as a unit test) disables this exclusion.
    /// </para>
    /// <para>
    /// Pure and synchronous: unlike the removed connector-space read, the value (if any) is already loaded on
    /// <paramref name="mvo"/>, so this costs no repository call, and Sync Preview needs no guarded repository
    /// access to call it.
    /// </para>
    /// </summary>
    public static string? FindMetaverseOwnValue(MetaverseObject mvo, int attributeId, int? generatingSyncRuleId)
    {
        var current = mvo.AttributeValues.FirstOrDefault(av =>
            av.AttributeId == attributeId
            && !mvo.PendingAttributeValueRemovals.Contains(av)
            && (generatingSyncRuleId == null || av.ContributedBySyncRuleId != generatingSyncRuleId));

        return current == null ? null : RenderMetaverseValue(current);
    }

    /// <summary>
    /// Renders a Metaverse Object attribute value as the text an adopt-before-generate candidate needs: a
    /// Number or LongNumber target holds its value in
    /// <see cref="MetaverseObjectAttributeValue.IntValue"/>/<see cref="MetaverseObjectAttributeValue.LongValue"/>,
    /// never <see cref="MetaverseObjectAttributeValue.StringValue"/>, so reading only <c>StringValue</c> would
    /// mean a numeric target never adopted. Also the correct answer for an asserted-null marker row (every
    /// value column null): there is nothing to adopt, so this returns null exactly as it would for a genuinely
    /// absent row.
    /// </summary>
    private static string? RenderMetaverseValue(MetaverseObjectAttributeValue value)
    {
        if (!string.IsNullOrEmpty(value.StringValue))
            return value.StringValue;
        if (value.IntValue.HasValue)
            return value.IntValue.Value.ToString(CultureInfo.InvariantCulture);
        if (value.LongValue.HasValue)
            return value.LongValue.Value.ToString(CultureInfo.InvariantCulture);
        return null;
    }
}
