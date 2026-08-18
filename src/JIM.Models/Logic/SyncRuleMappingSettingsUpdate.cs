// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Expressions;

namespace JIM.Models.Logic;

/// <summary>
/// A change to an existing Attribute Flow's settings. Every property is optional; a null one leaves the mapping's
/// current value alone.
/// </summary>
/// <remarks>
/// Deliberately limited to settings, meaning everything about how a mapping behaves rather than what it reads and
/// writes. Retargeting a mapping, or swapping its source between an attribute and an Expression, changes what the
/// mapping is: it revalidates against attribute types and plurality, and for an import mapping it reopens its
/// place in the attribute's priority order. Those remain a delete and a create, which says plainly what is
/// happening. Attribute Priority itself is ordered through its own endpoint and is not settable here.
/// </remarks>
public class SyncRuleMappingSettingsUpdate
{
    /// <summary>
    /// Replaces the Expression on the mapping's Expression source. Expression sources only.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// What the Expression does when an attribute it reads has no value on the object being synchronised.
    /// Expression sources only.
    /// </summary>
    public MissingInputBehaviour? MissingInputBehaviour { get; set; }

    /// <summary>
    /// Whether a contribution of no value from this mapping is authoritative ("Null is a value"). Import
    /// mappings only.
    /// </summary>
    public bool? NullIsValue { get; set; }

    /// <summary>
    /// Text value-processing transforms applied as the value flows to the Metaverse. Import mappings only.
    /// </summary>
    public InboundValueProcessing? InboundValueProcessing { get; set; }

    /// <summary>
    /// Case normalisation applied as the value flows to the Metaverse. Import mappings only.
    /// </summary>
    public InboundCaseNormalisation? CaseNormalisation { get; set; }

    /// <summary>
    /// Whether the mapping flows only during the initial provisioning export. Export mappings only.
    /// </summary>
    public bool? InitialExportOnly { get; set; }

    /// <summary>
    /// True when the update names at least one setting. A request naming none changes nothing, and is rejected
    /// rather than reported as a successful update.
    /// </summary>
    public bool HasChanges =>
        Expression != null ||
        MissingInputBehaviour.HasValue ||
        NullIsValue.HasValue ||
        InboundValueProcessing.HasValue ||
        CaseNormalisation.HasValue ||
        InitialExportOnly.HasValue;
}
