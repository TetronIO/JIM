// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// What a destructive schema refresh invalidates (#1485): the Synchronisation Rules bound to a removed Object
/// Type, the Attribute Flow mappings reading or writing a removed or redefined attribute (directly or through
/// an Expression), and the Object Matching Rules that reference a removed attribute. The rule and mapping lists
/// double as the "Apply and Disable Dependents" plan, each entry carrying the reason it would be disabled for;
/// Object Matching Rules are reported for the administrator's attention only, since they have no disabled state.
/// </summary>
public class SchemaRefreshDependents
{
    /// <summary>
    /// Synchronisation Rules whose Connected System Object Type the source no longer reports. Disabling the
    /// rule takes its mappings with it, so those mappings are deliberately not repeated in
    /// <see cref="InvalidatedMappings"/>.
    /// </summary>
    public List<SchemaRefreshDependentRule> InvalidatedSyncRules { get; set; } = new();

    /// <summary>
    /// Attribute Flow mappings on surviving Synchronisation Rules that read or write a removed attribute
    /// (directly or as an Expression input), or one whose definition changed.
    /// </summary>
    public List<SchemaRefreshDependentMapping> InvalidatedMappings { get; set; } = new();

    /// <summary>
    /// Object Matching Rules with a source reading a removed attribute. Display only: matching has no
    /// disabled state, so the administrator resolves these themselves.
    /// </summary>
    public List<SchemaRefreshDependentMatchingRule> ReferencedObjectMatchingRules { get; set; } = new();

    /// <summary>
    /// Whether the refresh invalidates anything at all.
    /// </summary>
    public bool HasAny => InvalidatedSyncRules.Count > 0 || InvalidatedMappings.Count > 0 || ReferencedObjectMatchingRules.Count > 0;
}

/// <summary>
/// A Synchronisation Rule a destructive schema refresh invalidates, and the reason it would be disabled for.
/// </summary>
public class SchemaRefreshDependentRule
{
    public int SyncRuleId { get; set; }
    public string SyncRuleName { get; set; } = null!;

    /// <summary>The removed Object Type the rule is bound to.</summary>
    public string ObjectTypeName { get; set; } = null!;

    /// <summary>How many Attribute Flow mappings fall with the rule.</summary>
    public int MappingCount { get; set; }

    /// <summary>The reason recorded against the rule when the disable plan is applied.</summary>
    public string Reason { get; set; } = null!;
}

/// <summary>
/// An Attribute Flow mapping a destructive schema refresh invalidates, and the reason it would be disabled for.
/// </summary>
public class SchemaRefreshDependentMapping
{
    public int MappingId { get; set; }
    public int SyncRuleId { get; set; }
    public string SyncRuleName { get; set; } = null!;

    /// <summary>The flow, described for display (for example "faxNumber → Fax Number").</summary>
    public string Description { get; set; } = null!;

    /// <summary>True when the attribute is consumed inside the mapping's Expression rather than mapped directly.</summary>
    public bool ViaExpression { get; set; }

    /// <summary>The reason recorded against the mapping when the disable plan is applied.</summary>
    public string Reason { get; set; } = null!;
}

/// <summary>
/// An Object Matching Rule with a source reading a removed attribute. Reported for the administrator's
/// attention; matching has no disabled state.
/// </summary>
public class SchemaRefreshDependentMatchingRule
{
    public int ObjectMatchingRuleId { get; set; }

    /// <summary>Where the rule lives: the owning Synchronisation Rule's name, or the Object Type for Simple Mode.</summary>
    public string Context { get; set; } = null!;

    /// <summary>Why the rule needs attention.</summary>
    public string Reason { get; set; } = null!;
}
