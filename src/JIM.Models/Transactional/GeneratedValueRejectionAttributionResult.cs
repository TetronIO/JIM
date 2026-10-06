// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Models.Transactional;

/// <summary>
/// Which generated value a "value already in use" export rejection is about, or why that cannot be said (Unique Value
/// Generation, #242, release 4; plan decision 9). Exactly one of <see cref="MetaverseAttributeId"/> (import mode) and
/// <see cref="ExportGeneratedMapping"/> (export mode) is set when attributed.
/// </summary>
public sealed class GeneratedValueRejectionAttributionResult
{
    /// <summary>
    /// Whether the rejection was attributed to exactly one generated value.
    /// </summary>
    public bool IsAttributed { get; private init; }

    /// <summary>
    /// Why the rejection could not be attributed, for the log; null when attributed.
    /// </summary>
    public string? UnattributableReason { get; private init; }

    /// <summary>
    /// Import mode: the generated Metaverse attribute the rejection is attributed to.
    /// </summary>
    public int? MetaverseAttributeId { get; private init; }

    /// <summary>
    /// Export mode: the generated export mapping whose value the rejection is attributed to.
    /// </summary>
    public SyncRuleMapping? ExportGeneratedMapping { get; private init; }

    /// <summary>
    /// The export's attribute change that carries the value the rejection is attributed through: the generated value
    /// itself, or (<see cref="ThroughDerivation"/>) a value derived from it.
    /// </summary>
    public PendingExportAttributeValueChange? CarryingChange { get; private init; }

    /// <summary>
    /// Whether the carrying attribute holds a value derived from the generated value (a User Principal Name built from
    /// an Account Name) rather than the generated value itself.
    /// </summary>
    public bool ThroughDerivation { get; private init; }

    public static GeneratedValueRejectionAttributionResult Unattributable(string reason) => new() { UnattributableReason = reason };

    public static GeneratedValueRejectionAttributionResult ToMetaverseAttribute(int metaverseAttributeId, PendingExportAttributeValueChange carryingChange, bool throughDerivation) => new()
    {
        IsAttributed = true,
        MetaverseAttributeId = metaverseAttributeId,
        CarryingChange = carryingChange,
        ThroughDerivation = throughDerivation
    };

    public static GeneratedValueRejectionAttributionResult ToExportMapping(SyncRuleMapping exportGeneratedMapping, PendingExportAttributeValueChange carryingChange) => new()
    {
        IsAttributed = true,
        ExportGeneratedMapping = exportGeneratedMapping,
        CarryingChange = carryingChange
    };
}
