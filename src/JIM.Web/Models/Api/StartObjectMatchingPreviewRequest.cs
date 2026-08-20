// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// The Object Matching configuration to preview (#1457): which Metaverse Object each of a Connected System's
/// unjoined objects would join to, before anything is saved.
/// </summary>
public class StartObjectMatchingPreviewRequest
{
    /// <summary>
    /// The proposed matching mode. Omitted or null previews the Connected System's stored mode, so a caller
    /// changing only the rules does not have to restate it. Sending a different mode is a real proposal: it
    /// changes which rules apply without editing a single rule.
    /// </summary>
    public ObjectMatchingRuleMode? Mode { get; set; }

    /// <summary>
    /// The proposed rules, across both modes.
    ///
    /// Omitted or null previews the Connected System's stored rules, matching the update endpoints' semantics: a
    /// caller proposing nothing is proposing no change, and the preview says so rather than inventing one. An
    /// explicitly EMPTY array is a real proposal and a very different one: it removes every rule, so nothing would
    /// ever join and every unjoined object would project a new identity instead.
    /// </summary>
    public List<ObjectMatchingRuleRequest>? Rules { get; set; }

    /// <summary>
    /// Whether drill-down rows are kept in full or capped per summary group. Counts are exact either way; capping
    /// bounds only what is retained for drill-down. Defaults to Capped, the recommended choice for large
    /// populations.
    /// </summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; } =
        ConfigurationChangePreviewDeltaPersistence.Capped;

    /// <summary>
    /// The proposal this request describes, falling back to the Connected System's stored configuration for
    /// whatever the caller left out.
    /// </summary>
    /// <param name="connectedSystem">The Connected System being previewed.</param>
    /// <param name="objectTypes">Its object types, carrying the Simple mode rules.</param>
    /// <param name="syncRules">Its Synchronisation Rules, carrying the Advanced mode rules.</param>
    public ObjectMatchingProposal ToProposal(
        ConnectedSystem connectedSystem,
        IReadOnlyCollection<ConnectedSystemObjectType> objectTypes,
        IReadOnlyCollection<SyncRule> syncRules)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        var stored = ObjectMatchingProposal.FromCurrentConfiguration(connectedSystem, objectTypes, syncRules);

        return new ObjectMatchingProposal(
            Mode ?? stored.Mode,
            Rules == null ? stored.Rules : [.. Rules.Select(rule => rule.ToProposal())]);
    }
}

/// <summary>
/// One proposed Object Matching Rule: where it lives, what it searches, and what it compares.
/// </summary>
/// <remarks>
/// Exactly one of the two owner ids is set, and which one depends on the mode being proposed: a Simple mode rule
/// belongs to a Connected System Object Type, an Advanced mode rule to a Synchronisation Rule. A rule owned by the
/// side the proposed mode does not use is simply never evaluated, which the preview reports rather than working
/// around.
/// </remarks>
public class ObjectMatchingRuleRequest
{
    /// <summary>
    /// Where this rule sits in the cascade. Load-bearing rather than presentational: rules are evaluated in
    /// ascending order until one matches, so moving one above another changes which identity an account joins to.
    /// </summary>
    public int Order { get; set; }

    /// <summary>The Connected System Object Type owning this rule in Simple mode.</summary>
    public int? ConnectedSystemObjectTypeId { get; set; }

    /// <summary>The Synchronisation Rule owning this rule in Advanced mode.</summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The Metaverse Object Type searched. Required in Simple mode; in Advanced mode the owning Synchronisation
    /// Rule's own type is used.
    /// </summary>
    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>The Metaverse Attribute the source values are compared against.</summary>
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>Whether text comparison respects case.</summary>
    public bool CaseSensitive { get; set; }

    /// <summary>What supplies the value to match on, in evaluation order.</summary>
    public List<ObjectMatchingRuleSourceRequest> Sources { get; set; } = [];

    internal ObjectMatchingRuleProposal ToProposal() =>
        new(Order,
            ConnectedSystemObjectTypeId,
            SyncRuleId,
            MetaverseObjectTypeId,
            TargetMetaverseAttributeId,
            CaseSensitive,
            [.. Sources.Select(source => source.ToProposal())]);
}

/// <summary>
/// One proposed source of the value a rule matches on.
/// </summary>
public class ObjectMatchingRuleSourceRequest
{
    /// <summary>Where this source sits; sources are evaluated in ascending order.</summary>
    public int Order { get; set; }

    /// <summary>The Connected System attribute read.</summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// An Expression producing the value instead. Accepted because the model allows it and refused by the
    /// preview's validation: Object Matching compares attribute values only.
    /// </summary>
    public string? Expression { get; set; }

    internal ObjectMatchingRuleSourceProposal ToProposal() =>
        new(Order, ConnectedSystemAttributeId, Expression);
}
