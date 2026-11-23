// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Localisation;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Servers;

/// <summary>
/// A Connector-defined Attribute may declare the Connected System attribute it is realised through (#2756): the LDAP
/// Connector writes its Exchange Alias to the directory as <c>mailNickname</c>. An export Synchronisation Rule that flows
/// both, from different sources, leaves each export free to overwrite the other's value, so every path that saves a rule
/// or one of its Attribute Flows warns about it. Only ever a warning: two different sources can still agree (an expression
/// can keep <c>proxyAddresses</c> in step with a primary address), and flowing the underlying attribute on its own is
/// ordinary. The check reads only what the Connector declared, and knows nothing about any particular Connector.
/// </summary>
/// <remarks>
/// It looks within one rule. Two enabled export rules that both have an object in scope already export nothing for it
/// (Export Rules Overlap, #2820), so a Connector-defined Attribute in one rule and its source in another can never both
/// reach the same object.
/// </remarks>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// Adds a warning to <see cref="SyncRule.RuleSaveWarnings"/> for each Connector-defined Attribute the export rule
    /// flows alongside its declared source, from a different source. Runs whether or not the rule is enabled, so that
    /// enabling it later does not hide the problem.
    /// </summary>
    private async Task StampConnectorDefinedAttributeOverlapWarningsAsync(SyncRule syncRule)
    {
        if (syncRule.Direction != SyncRuleDirection.Export)
            return;

        foreach (var warning in await FindConnectorDefinedAttributeOverlapsAsync(syncRule, syncRule.AttributeFlowRules, involving: null))
            syncRule.AddRuleSaveWarning(warning);
    }

    /// <summary>
    /// Adds a warning to the mapping's <see cref="SyncRuleMapping.SaveWarnings"/> for each overlap it takes part in, read
    /// against the rule's other stored Attribute Flows. Used by the single-flow create, update and settings update paths.
    /// Runs after the expression check, which clears the mapping's save warnings.
    /// </summary>
    private async Task StampConnectorDefinedAttributeOverlapWarningsAsync(SyncRuleMapping mapping)
    {
        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId;
        if (syncRuleId == 0 || mapping.ResolveTargetConnectedSystemAttributeId() == null)
            return; // a rule still being composed is checked by the whole-rule save; an import mapping cannot overlap

        var syncRule = mapping.SyncRule ?? await Application.Repository.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null || syncRule.Direction != SyncRuleDirection.Export)
            return;

        var storedMappings = await Application.Repository.ConnectedSystems.GetSyncRuleMappingsAsync(syncRuleId);
        var mappings = storedMappings.Where(stored => mapping.Id == 0 || stored.Id != mapping.Id).Append(mapping).ToList();
        foreach (var warning in await FindConnectorDefinedAttributeOverlapsAsync(syncRule, mappings, involving: mapping))
            mapping.AddSaveWarning(warning);
    }

    /// <summary>
    /// The overlaps among <paramref name="mappings"/>, as warnings. When <paramref name="involving"/> is given, only those
    /// it takes part in, on either side.
    /// </summary>
    private async Task<List<JimMessage>> FindConnectorDefinedAttributeOverlapsAsync(SyncRule syncRule, IReadOnlyCollection<SyncRuleMapping> mappings,
        SyncRuleMapping? involving)
    {
        var objectTypeId = syncRule.ConnectedSystemObjectType?.Id ?? syncRule.ConnectedSystemObjectTypeId;
        if (!mappings.Any(m => m.Enabled && m.ResolveTargetConnectedSystemAttributeId() != null))
            return [];

        // The declarations are read from the stored schema, never from whatever navigation the caller happens to hold:
        // a REST save names its targets by id alone.
        var objectType = await Application.Repository.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
        if (objectType == null)
            return [];

        var overlaps = FindConnectorDefinedAttributeOverlaps(mappings, objectType.Attributes);
        if (involving != null)
            overlaps = overlaps.Where(o => ReferenceEquals(o.ConnectorDefinedFlow, involving) || ReferenceEquals(o.SourceFlow, involving)).ToList();
        if (overlaps.Count == 0)
            return [];

        var connectorName = (await Application.Repository.ConnectedSystems.GetConnectedSystemHeaderAsync(syncRule.ConnectedSystemId))?.ConnectorName
            ?? "Connector";
        return overlaps.Select(o => BuildConnectorDefinedAttributeOverlapMessage(o.ConnectorDefinedAttribute, o.SourceAttribute, connectorName)).ToList();
    }

    /// <summary>
    /// Pairs each enabled flow writing a Connector-defined Attribute with an enabled flow writing the attribute it declares
    /// as its source, where the two read different sources. Pure, so the rule is testable without a save around it.
    /// </summary>
    /// <param name="mappings">One export rule's Attribute Flows.</param>
    /// <param name="objectTypeAttributes">Every attribute of the rule's Connected System Object Type, as stored.</param>
    internal static List<ConnectorDefinedAttributeOverlap> FindConnectorDefinedAttributeOverlaps(
        IEnumerable<SyncRuleMapping> mappings, IEnumerable<ConnectedSystemObjectTypeAttribute> objectTypeAttributes)
    {
        var attributes = objectTypeAttributes.ToList();
        var attributesById = attributes.Where(a => a.Id != 0).ToDictionary(a => a.Id);
        var flowsByTarget = mappings
            .Where(m => m.Enabled)
            .Select(m => (Mapping: m, TargetId: m.ResolveTargetConnectedSystemAttributeId()))
            .Where(f => f.TargetId != null && attributesById.ContainsKey(f.TargetId.Value))
            .Select(f => (f.Mapping, Target: attributesById[f.TargetId!.Value]))
            .ToList();

        return flowsByTarget
            .Where(flow => flow.Target.IsConnectorDefined && !string.IsNullOrWhiteSpace(flow.Target.SourceAttributeName))
            .SelectMany(flow => flowsByTarget
                .Where(other => !other.Target.IsConnectorDefined &&
                                string.Equals(other.Target.Name, flow.Target.SourceAttributeName, StringComparison.OrdinalIgnoreCase) &&
                                !ReadTheSameSource(flow.Mapping, other.Mapping))
                .Select(other => new ConnectorDefinedAttributeOverlap(flow.Mapping, flow.Target, other.Mapping, other.Target)))
            .OrderBy(o => o.ConnectorDefinedAttribute.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Whether two flows read exactly the same thing, in the same order, so they always write the same value. A
    /// generated flow (Unique Value Generation) never does: its uniqueness token can make two equal bases differ.
    /// </summary>
    private static bool ReadTheSameSource(SyncRuleMapping first, SyncRuleMapping second)
    {
        if (first.Generation != null || second.Generation != null)
            return false;

        return SourceKey(first).SequenceEqual(SourceKey(second), StringComparer.Ordinal);
    }

    private static IEnumerable<string> SourceKey(SyncRuleMapping mapping) =>
        mapping.Sources
            .OrderBy(source => source.Order)
            .Select(source =>
                $"{source.Expression}|{source.MetaverseAttribute?.Id ?? source.MetaverseAttributeId}|{source.ConnectedSystemAttribute?.Id ?? source.ConnectedSystemAttributeId}");

    private static JimMessage BuildConnectorDefinedAttributeOverlapMessage(ConnectedSystemObjectTypeAttribute connectorDefined,
        ConnectedSystemObjectTypeAttribute source, string connectorName) =>
        ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.SaveWarningConnectorDefinedOverlap), connectorDefined.Name, connectorName, source.Name);
}
