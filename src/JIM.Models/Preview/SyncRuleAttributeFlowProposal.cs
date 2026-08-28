// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Expressions;
using JIM.Models.Logic;

namespace JIM.Models.Preview;

/// <summary>
/// The Attribute Flow mappings an administrator is proposing for a Synchronisation Rule, as the Attribute Flow
/// change preview adapter receives them (#1437): what each managed object's attributes would become.
/// </summary>
/// <remarks>
/// A mapping set of its own rather than the rule's own graph, for the reasons the sibling scope proposal states
/// and one more of its own: each <see cref="SyncRuleMappingSource"/> carries whole attribute entities and a
/// backlink to its rule, so the graph neither round trips as JSON nor arrives describing anything but itself once
/// the editor has finished with it.
/// </remarks>
/// <param name="Mappings">
/// The proposed mappings, one per target attribute. Empty means the rule would flow nothing, which is a real
/// proposal: every value it currently contributes would be withdrawn.
/// </param>
/// <param name="KeepContributedValuesAttributeIds">
/// The target Metaverse Attribute ids whose staged mapping removal chose to KEEP the contributed values
/// (#1537). The preview uses this to say what the removal will actually do: an attribute listed here has its
/// values kept (provenance severed at save, never recalled); a removed attribute not listed follows the
/// default, recall at the next Full Synchronisation of the contributing system. Advisory to the preview only;
/// the save itself carries the authoritative choices.
/// </param>
public record SyncRuleAttributeFlowProposal(
    IReadOnlyList<SyncRuleMappingProposal> Mappings,
    IReadOnlyList<int>? KeepContributedValuesAttributeIds = null)
{
    /// <summary>
    /// The mappings currently in force on <paramref name="syncRule"/>, as a proposal. What "no change" looks like,
    /// and the baseline an adapter evaluates a proposal against.
    /// </summary>
    /// <param name="syncRule">The rule whose current mappings form the proposal.</param>
    /// <param name="keepContributedValuesAttributeIds">See the record parameter of the same name.</param>
    public static SyncRuleAttributeFlowProposal FromCurrentMappings(SyncRule syncRule, IReadOnlyList<int>? keepContributedValuesAttributeIds = null)
    {
        ArgumentNullException.ThrowIfNull(syncRule);

        return new SyncRuleAttributeFlowProposal(
            [.. syncRule.AttributeFlowRules.Select(SyncRuleMappingProposal.FromMapping)],
            keepContributedValuesAttributeIds);
    }

    /// <summary>
    /// Whether <paramref name="other"/> proposes the same mappings as this one. What decides whether a preview an
    /// administrator is looking at still answers the question they are about to ask.
    /// </summary>
    /// <remarks>
    /// Not the record's own equality: the nested lists compare by reference, so an editor rebuilding its proposal
    /// on every render would mark every preview stale the moment it finished. Mappings compare as a set, because
    /// there is one per target attribute and each is evaluated for its own attribute, so the order the editor
    /// lists them in is presentation. A mapping's SOURCES are compared in order, because chained sources feed each
    /// other and swapping two changes the value produced.
    /// </remarks>
    public bool DescribesSameMappingsAs(SyncRuleAttributeFlowProposal? other) =>
        other is not null && CanonicalKey() == other.CanonicalKey();

    private string CanonicalKey()
    {
        // The keep choices are part of what the preview said, so a changed choice makes a shown preview stale
        // exactly as an edited mapping does.
        var keepIds = string.Join(",", (KeepContributedValuesAttributeIds ?? []).Order());
        return string.Join("|", Mappings.Select(mapping => mapping.CanonicalKey()).Order(StringComparer.Ordinal))
            + $"|keep=[{keepIds}]";
    }
}

/// <summary>
/// One proposed mapping: the attribute it writes, where its value comes from, and the settings that decide
/// whether and how the value lands.
/// </summary>
/// <param name="TargetMetaverseAttributeId">The Metaverse Attribute an import mapping writes.</param>
/// <param name="TargetConnectedSystemAttributeId">The Connected System attribute an export mapping writes.</param>
/// <param name="Sources">Where the value comes from, in the order the sources are applied.</param>
/// <param name="InboundValueProcessing">Inbound text processing applied to the value as it flows.</param>
/// <param name="CaseNormalisation">Inbound case normalisation applied to the value as it flows.</param>
/// <param name="Priority">
/// Where this mapping sits in the target Metaverse Attribute's priority list. Part of what the mapping does, not
/// decoration: a mapping that does not win the attribute writes nothing at all.
/// </param>
/// <param name="NullIsValue">Whether a null contribution stops resolution rather than falling through.</param>
/// <param name="InitialExportOnly">Whether an export mapping flows only on the provisioning export.</param>
/// <param name="Enabled">Whether the mapping is evaluated at all (#1485); a disabled mapping flows nothing.</param>
public record SyncRuleMappingProposal(
    int? TargetMetaverseAttributeId,
    int? TargetConnectedSystemAttributeId,
    IReadOnlyList<SyncRuleMappingSourceProposal> Sources,
    InboundValueProcessing InboundValueProcessing = InboundValueProcessing.TreatWhitespaceAsNoValue,
    InboundCaseNormalisation CaseNormalisation = InboundCaseNormalisation.None,
    int Priority = int.MaxValue,
    bool NullIsValue = false,
    bool InitialExportOnly = false,
    bool Enabled = true)
{
    /// <summary>
    /// This mapping as it currently stands on a Synchronisation Rule.
    /// </summary>
    /// <remarks>
    /// Each attribute id falls back to its navigation property's id, because the editors build an UNSAVED mapping
    /// when an administrator adds one: the navigation is set and the foreign key stays unassigned until the rule is
    /// saved. Reading the key alone made a mapping the editor plainly shows invisible to the proposal, so a preview
    /// of a just-added mapping refused it as naming no target attribute and, in the same breath, reported its
    /// attribute as no longer written (#1450). A saved mapping carries both and is unaffected.
    /// </remarks>
    public static SyncRuleMappingProposal FromMapping(SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        return new SyncRuleMappingProposal(
            mapping.TargetMetaverseAttributeId ?? mapping.TargetMetaverseAttribute?.Id,
            mapping.TargetConnectedSystemAttributeId ?? mapping.TargetConnectedSystemAttribute?.Id,
            [.. mapping.Sources.OrderBy(source => source.Order).Select(SyncRuleMappingSourceProposal.FromSource)],
            mapping.InboundValueProcessing,
            mapping.CaseNormalisation,
            mapping.Priority,
            mapping.NullIsValue,
            mapping.InitialExportOnly,
            mapping.Enabled);
    }

    internal string CanonicalKey()
    {
        var sources = string.Join(">", Sources.OrderBy(source => source.Order).Select(source => source.CanonicalKey()));
        return string.Create(CultureInfo.InvariantCulture,
            $"mv={TargetMetaverseAttributeId};cs={TargetConnectedSystemAttributeId};ivp={InboundValueProcessing};" +
            $"case={CaseNormalisation};pri={Priority};niv={NullIsValue};ieo={InitialExportOnly};en={Enabled};src=[{sources}]");
    }
}

/// <summary>
/// One proposed source for a mapping: an attribute read directly, or an Expression evaluated over attributes.
/// </summary>
/// <param name="Order">Where this source sits in the chain. Load-bearing: chained sources feed each other.</param>
/// <param name="MetaverseAttributeId">The Metaverse Attribute an export mapping's source reads.</param>
/// <param name="ConnectedSystemAttributeId">The Connected System attribute an import mapping's source reads.</param>
/// <param name="Expression">The Expression evaluated, where the source is computed rather than read.</param>
/// <param name="MissingInputBehaviour">
/// What an Expression does when an attribute it reads has no value on the object: the setting that decides
/// whether a missing surname produces a malformed value, nothing at all, or a reported failure.
/// </param>
public record SyncRuleMappingSourceProposal(
    int Order,
    int? MetaverseAttributeId,
    int? ConnectedSystemAttributeId,
    string? Expression = null,
    MissingInputBehaviour MissingInputBehaviour = MissingInputBehaviour.EvaluateAnyway)
{
    /// <summary>
    /// This source as it currently stands on a mapping.
    /// </summary>
    public static SyncRuleMappingSourceProposal FromSource(SyncRuleMappingSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new SyncRuleMappingSourceProposal(
            source.Order,
            source.MetaverseAttributeId ?? source.MetaverseAttribute?.Id,
            source.ConnectedSystemAttributeId ?? source.ConnectedSystemAttribute?.Id,
            source.Expression,
            source.MissingInputBehaviour);
    }

    internal string CanonicalKey() => string.Create(CultureInfo.InvariantCulture,
        $"o={Order};mv={MetaverseAttributeId};cs={ConnectedSystemAttributeId};e={Expression};mib={MissingInputBehaviour}");
}
