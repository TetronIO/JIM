// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The evaluated impact of deleting a Metaverse Object Type: the two hard blocks (Metaverse Objects of the type, and
/// Synchronisation Rules targeting it, both of which the database would otherwise silently cascade-delete), and the
/// softer configuration references (Predefined Searches, Example Data Templates, custom attribute bindings) that are
/// cascade-removed when the deletion proceeds. Returned by both the preview (read-only) and the execute method, so
/// REST and PowerShell callers get the same block/allow decision and the same list to render the type-the-name
/// confirmation dialog. Built-in types can never be deleted.
/// </summary>
public class ObjectTypeDeletionImpact
{
    public int ObjectTypeId { get; set; }

    public string ObjectTypeName { get; set; } = string.Empty;

    /// <summary>
    /// True if the Object Type is built-in (e.g. User, Group). Built-in types can never be deleted.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// The number of Metaverse Objects of this type. A hard block: identity data must be removed before the type can be
    /// deleted, because deleting the type would otherwise cascade-delete every Metaverse Object of it.
    /// </summary>
    public int MetaverseObjectCount { get; set; }

    /// <summary>
    /// The Synchronisation Rules that target this Object Type. A hard block: each rule must be removed first, because
    /// deleting the type would otherwise cascade-delete the entire rule.
    /// </summary>
    public List<ObjectTypeReference> SynchronisationRules { get; set; } = [];

    /// <summary>
    /// The softer configuration references (Predefined Searches, Example Data Templates, custom attribute bindings) that
    /// would be cascade-removed when the deletion proceeds. The bound attributes themselves are not deleted; only the
    /// binding rows are removed. Empty when the type is unreferenced.
    /// </summary>
    public List<ObjectTypeReference> CascadeReferences { get; set; } = [];

    /// <summary>
    /// Set true by the execute method when the deletion actually happened. False on a preview, on a built-in, or when a
    /// hard block refused the deletion.
    /// </summary>
    public bool Deleted { get; set; }

    /// <summary>
    /// True when Metaverse Objects of the type exist: a hard block that must be cleared before the type is deleted.
    /// </summary>
    public bool BlockedByObjects => MetaverseObjectCount > 0;

    /// <summary>
    /// True when Synchronisation Rules target the type: a hard block that must be cleared before the type is deleted.
    /// </summary>
    public bool BlockedBySynchronisationRules => SynchronisationRules.Count > 0;

    /// <summary>
    /// True when the deletion cannot proceed for any reason (built-in, live objects, or targeting rules).
    /// </summary>
    public bool Blocked => BuiltIn || BlockedByObjects || BlockedBySynchronisationRules;

    /// <summary>
    /// True when the deletion may proceed but would cascade-remove references, so a type-the-name confirmation is
    /// required.
    /// </summary>
    public bool RequiresConfirmation => !Blocked && CascadeReferences.Count > 0;
}
