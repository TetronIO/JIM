// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A Connected System Object attribute value row that references a deleted Metaverse Object,
/// returned by the reference recall existence query (#1003). Each row is a value the target
/// system currently holds that recall must stage a removal for; rows the target does not hold
/// are never returned, which replaces per-group no-net-change detection without loading the
/// group's full membership.
/// </summary>
public class CsoReferenceValueMatch
{
    /// <summary>
    /// The attribute value row id, used to deduplicate rows matched by more than one predicate arm.
    /// </summary>
    public Guid AttributeValueId { get; set; }

    public Guid ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The Connected System attribute holding the reference (for example the member attribute).
    /// </summary>
    public int AttributeId { get; set; }

    /// <summary>
    /// The referenced Connected System Object (the deleted Metaverse Object's CSO in this system),
    /// when the row was matched by resolved reference.
    /// </summary>
    public Guid? ReferenceValueId { get; set; }

    /// <summary>
    /// The raw reference string the target system holds (for example the member DN), when present.
    /// </summary>
    public string? UnresolvedReferenceValue { get; set; }
}
