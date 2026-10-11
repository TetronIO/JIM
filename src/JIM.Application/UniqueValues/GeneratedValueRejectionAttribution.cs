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

        var resolver = GeneratedValueBuiltValues.Create(importRules.Concat(exportRules), derivedFlowGraph);

        if (!string.IsNullOrWhiteSpace(rejectedAttributeName))
        {
            var named = knownAttributes.FirstOrDefault(a => string.Equals(a.Name, rejectedAttributeName, StringComparison.OrdinalIgnoreCase));
            if (named == null)
                return GeneratedValueRejectionAttributionResult.Unattributable(
                    $"the Connected System named an attribute, \"{rejectedAttributeName}\", that is not an attribute of the object's type");

            var carrying = export.AttributeValueChanges.FirstOrDefault(c => c.AttributeId == named.Id);
            if (carrying == null)
                return GeneratedValueRejectionAttributionResult.Unattributable($"the export does not carry {named.Name}");

            var sources = resolver.SourcesOf(carrying);
            return sources.Count switch
            {
                0 => GeneratedValueRejectionAttributionResult.Unattributable(SwitchedOffReason(resolver, carrying, named.Name)
                    ?? $"{named.Name} does not carry a generated value"),
                1 => ToResult(sources[0], carrying),
                _ => GeneratedValueRejectionAttributionResult.Unattributable($"{named.Name} is built from more than one generated value")
            };
        }

        // Nothing named: the export must carry exactly one generated value, whether directly or through derivations.
        var carriers = export.AttributeValueChanges
            .Select(change => (Change: change, Sources: resolver.SourcesOf(change)))
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

    private static GeneratedValueRejectionAttributionResult ToResult(GeneratedValueBuiltValues.GeneratedValueSource source, PendingExportAttributeValueChange carrying) =>
        source.ExportMapping != null
            ? GeneratedValueRejectionAttributionResult.ToExportMapping(source.ExportMapping, carrying)
            : GeneratedValueRejectionAttributionResult.ToMetaverseAttribute(source.MetaverseAttributeId!.Value, carrying, source.ThroughDerivation);

    /// <summary>
    /// When a named value carries no generated value only because its "Keep unique" switch is off, says so, so the
    /// administrator reading the export error can tell a deliberate opt-out from a value no generated value is behind.
    /// </summary>
    private static string? SwitchedOffReason(GeneratedValueBuiltValues resolver, PendingExportAttributeValueChange carrying, string attributeName)
    {
        var sources = resolver.SourcesOf(carrying, honourSwitch: false);
        var generatedAttributeId = sources.Count == 1 ? sources[0].MetaverseAttributeId : null;
        if (!generatedAttributeId.HasValue)
            return null;

        var generatedName = resolver.NameOf(generatedAttributeId.Value);
        var value = resolver.CarriedMetaverseAttributeNameOf(carrying) ?? attributeName;
        return $"{value} is built from {generatedName}, but its \"Keep unique with {generatedName}\" switch is off, so a refusal of it is not corrected";
    }
}
