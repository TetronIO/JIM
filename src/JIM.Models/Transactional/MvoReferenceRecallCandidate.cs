// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A single reference attribute value on a Metaverse Object that points at another Metaverse
/// Object which is about to be deleted. Captured before deletion (the deletion path nulls the
/// reference FK, after which the linkage is unrecoverable) so that reference recall can stage
/// membership-removal Pending Exports for the referencing object's provisioned Connected
/// System Objects.
/// </summary>
public class MvoReferenceRecallCandidate
{
    /// <summary>
    /// The Metaverse Object holding the reference (for example a group whose Static Members
    /// includes the deleted user).
    /// </summary>
    public Guid ReferencingMetaverseObjectId { get; set; }

    /// <summary>
    /// The MetaverseObjectAttributeValue row carrying the reference.
    /// </summary>
    public Guid AttributeValueId { get; set; }

    /// <summary>
    /// The Metaverse Attribute the reference belongs to (for example Static Members).
    /// </summary>
    public int MetaverseAttributeId { get; set; }

    /// <summary>
    /// The Metaverse Object being deleted (the reference target).
    /// </summary>
    public Guid ReferencedMetaverseObjectId { get; set; }
}
