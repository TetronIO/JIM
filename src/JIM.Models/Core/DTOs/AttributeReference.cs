// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// A single configuration reference to a Metaverse Attribute that a destructive operation would cascade-remove.
/// Surfaced by the preview so the UI/API can show the admin exactly what will be removed, and recorded per-item as a
/// child Activity when the cascade executes. References do not block deletion (only stored values do); they are
/// removed in dependency order before the attribute/binding itself.
/// </summary>
public class AttributeReference
{
    /// <summary>
    /// The kind of reference. Determines the owning entity and the removal order.
    /// </summary>
    public AttributeReferenceKind Kind { get; set; }

    /// <summary>
    /// The database id of the referencing entity (e.g. the SyncRuleMapping, SyncRuleScopingCriteria,
    /// ObjectMatchingRule, ObjectMatchingRuleSource, or SyncRuleMappingSource row). For a
    /// <see cref="AttributeReferenceKind.Binding"/> this is the bound Metaverse Object Type id.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// A human-readable description of the reference, for audit messages and the confirmation dialog.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The owning Synchronisation Rule id, when the reference belongs to one (mappings, sources, scoping criteria,
    /// Synchronisation-Rule-scoped Object Matching Rules). Null for bindings and object-type-scoped matching rules.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The owning Synchronisation Rule name (point-in-time), when applicable.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The Metaverse Object Type id, for <see cref="AttributeReferenceKind.Binding"/> references. Null otherwise.
    /// </summary>
    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The Metaverse Object Type name, for <see cref="AttributeReferenceKind.Binding"/> references. Null otherwise.
    /// </summary>
    public string? MetaverseObjectTypeName { get; set; }
}
