// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// One reference attribute change on a Pending Export that has not been written yet, with the reason
/// (issue #1398). Computed when the detail is read, against the target's current state, so it is
/// always current: a reference that was unresolvable an hour ago reads as resolvable the moment the
/// referenced object is provisioned.
/// </summary>
public class PendingExportUnresolvedReference
{
    /// <summary>
    /// The attribute value change carrying the reference.
    /// </summary>
    public Guid AttributeChangeId { get; set; }

    /// <summary>
    /// The reference attribute's name in the Connected System.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// The Metaverse Object the change refers to.
    /// </summary>
    public Guid ReferencedMetaverseObjectId { get; set; }

    /// <summary>
    /// The referenced Metaverse Object's display name, when it has one.
    /// </summary>
    public string? ReferencedMetaverseObjectDisplayName { get; set; }

    /// <summary>
    /// Why the reference has not been written yet.
    /// </summary>
    public UnresolvedReferenceReason Reason { get; set; }
}
