// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The evaluated impact of deleting a Metaverse Attribute: whether stored values block it (the only hard block), the
/// per-Object-Type breakdown of those values, and the configuration references that would be cascade-removed when it
/// proceeds. Returned by both the preview (read-only) and the execute method, so REST and PowerShell callers get the
/// same block/allow decision and the same list to render the type-the-name confirmation dialog.
/// </summary>
public class AttributeDeletionImpact
{
    /// <summary>
    /// The unique identifier of the attribute.
    /// </summary>
    public int AttributeId { get; set; }

    /// <summary>
    /// The attribute's name.
    /// </summary>
    public string AttributeName { get; set; } = string.Empty;

    /// <summary>
    /// True if the attribute is built-in. Built-in attributes can never be deleted.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// The total number of Metaverse Objects (across all types) that hold a stored value for the attribute.
    /// </summary>
    public int TotalObjectsWithValues { get; set; }

    /// <summary>
    /// The per-Object-Type breakdown of Metaverse Objects holding a stored value for the attribute.
    /// </summary>
    public List<AttributeObjectTypeValueCount> ObjectTypeValueCounts { get; set; } = [];

    /// <summary>
    /// The configuration references (bindings, and Attribute Flows and Object Matching Rules that map the attribute
    /// directly) that would be cascade-removed, in dependency order, when the deletion proceeds. Empty when the
    /// attribute is unreferenced.
    /// </summary>
    public List<AttributeReference> References { get; set; } = [];

    /// <summary>
    /// The configuration that reads the attribute: Scoping Criteria comparing it or reading it in an Expression, and
    /// Expressions in Attribute Flows, Object Matching Rules and Example Data Templates. Any reader refuses the
    /// deletion; change or remove each one first. These are never removed for you, because a Scoping Criterion
    /// removed from under a Synchronisation Rule changes which objects it manages.
    /// </summary>
    public List<AttributeReader> Readers { get; set; } = [];

    /// <summary>
    /// Whether the attribute was deleted: always false in a preview, and false when the deletion was refused.
    /// </summary>
    public bool Deleted { get; set; }

    /// <summary>
    /// Whether stored values block the deletion. Clear them before deleting the attribute.
    /// </summary>
    public bool BlockedByValues => TotalObjectsWithValues > 0;

    /// <summary>
    /// Whether configuration reads the attribute (see <see cref="Readers"/>), which refuses the deletion.
    /// </summary>
    public bool BlockedByReaders => Readers.Count > 0;

    /// <summary>
    /// Whether deleting would also remove configuration that uses the attribute, so JIM asks for the attribute's
    /// name to confirm it.
    /// </summary>
    public bool RequiresConfirmation => !BuiltIn && !BlockedByValues && !BlockedByReaders && References.Count > 0;
}
