// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Preview;

namespace JIM.Web.Models.Api;

/// <summary>
/// The Attribute Flow mappings to preview (#1437): what values the Synchronisation Rule would write to every
/// object it manages, before anything is saved.
/// </summary>
public class StartSyncRuleAttributeFlowPreviewRequest
{
    /// <summary>
    /// The proposed mappings, one per target attribute.
    ///
    /// Omitted or null previews the rule's stored mappings, matching the update endpoints' semantics: a caller
    /// proposing nothing is proposing no change, and the preview says so rather than inventing one. An explicitly
    /// EMPTY array is a real proposal and a very different one: it removes every mapping, so the rule flows nothing.
    /// </summary>
    public List<SyncRuleMappingRequest>? Mappings { get; set; }

    /// <summary>
    /// Whether drill-down rows are kept in full or capped per summary group. Counts are exact either way; capping
    /// bounds only what is retained for drill-down. Defaults to Capped, the recommended choice for large
    /// populations.
    /// </summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; } =
        ConfigurationChangePreviewDeltaPersistence.Capped;

    /// <summary>
    /// The proposal these mappings describe, or the rule's stored Attribute Flow where the caller proposed none.
    /// </summary>
    /// <param name="syncRule">The rule being previewed, read for its stored mappings.</param>
    public SyncRuleAttributeFlowProposal ToProposal(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);

        return Mappings == null
            ? SyncRuleAttributeFlowProposal.FromCurrentMappings(syncRule)
            : new SyncRuleAttributeFlowProposal([.. Mappings.Select(mapping => mapping.ToProposal())]);
    }
}

/// <summary>
/// One proposed mapping: the attribute it writes, where its value comes from, and the settings that decide whether
/// and how the value lands.
/// </summary>
/// <remarks>
/// Exactly one of the two target attribute ids is set, and which one depends on the rule's direction: an import
/// rule writes a Metaverse Attribute, an export rule a Connected System attribute. A mapping carrying the wrong
/// side's attribute could never be written, so the preview reports it as a blocking finding rather than evaluating
/// around it and answering for a proposal that does less than it reads.
/// </remarks>
public class SyncRuleMappingRequest
{
    /// <summary>The Metaverse Attribute an import mapping writes.</summary>
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>The Connected System attribute an export mapping writes.</summary>
    public int? TargetConnectedSystemAttributeId { get; set; }

    /// <summary>Where the value comes from, in the order the sources are applied.</summary>
    public List<SyncRuleMappingSourceRequest> Sources { get; set; } = [];

    /// <summary>Inbound text processing applied to the value as it flows.</summary>
    public InboundValueProcessing InboundValueProcessing { get; set; } = InboundValueProcessing.TreatWhitespaceAsNoValue;

    /// <summary>Inbound case normalisation applied to the value as it flows.</summary>
    public InboundCaseNormalisation CaseNormalisation { get; set; } = InboundCaseNormalisation.None;

    /// <summary>
    /// Where this mapping sits in the target Metaverse Attribute's priority list (1 is highest). Part of what the
    /// mapping does rather than decoration: a mapping that does not win the attribute writes nothing at all, and
    /// the preview reports that as a finding rather than as a set of values that would never be written.
    /// </summary>
    public int Priority { get; set; } = int.MaxValue;

    /// <summary>Whether a null contribution stops resolution rather than falling through.</summary>
    public bool NullIsValue { get; set; }

    /// <summary>Whether an export mapping flows only on the provisioning export.</summary>
    public bool InitialExportOnly { get; set; }

    internal SyncRuleMappingProposal ToProposal() =>
        new(TargetMetaverseAttributeId,
            TargetConnectedSystemAttributeId,
            [.. Sources.Select(source => source.ToProposal())],
            InboundValueProcessing,
            CaseNormalisation,
            Priority,
            NullIsValue,
            InitialExportOnly);
}

/// <summary>
/// One proposed source for a mapping: an attribute read directly, or an Expression evaluated over attributes.
/// </summary>
public class SyncRuleMappingSourceRequest
{
    /// <summary>
    /// Where this source sits in the chain. Load-bearing rather than presentational: chained sources feed each
    /// other, so swapping two changes the value produced.
    /// </summary>
    public int Order { get; set; }

    /// <summary>The Metaverse Attribute an export mapping's source reads.</summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>The Connected System attribute an import mapping's source reads.</summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>The Expression evaluated, where the source is computed rather than read.</summary>
    public string? Expression { get; set; }

    /// <summary>
    /// What an Expression does when an attribute it reads has no value on the object: the setting that decides
    /// whether a missing surname produces a malformed value, nothing at all, or a reported failure.
    /// </summary>
    public MissingInputBehaviour MissingInputBehaviour { get; set; } = MissingInputBehaviour.EvaluateAnyway;

    internal SyncRuleMappingSourceProposal ToProposal() =>
        new(Order, MetaverseAttributeId, ConnectedSystemAttributeId, Expression, MissingInputBehaviour);
}
