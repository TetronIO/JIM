// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Scim;

/// <summary>
/// One reference from a resource to another resource in the same Connected System's store, held as a row rather than in
/// the source's payload: a Group's <c>members</c> in v1, and any other multi-valued reference attribute a schema
/// declares. As rows, adding one member never rewrites a large Group, and deleting a resource finds everything that
/// referred to it.
/// </summary>
public class ScimStoreReference
{
    /// <summary>
    /// The resource holding the reference (the Group).
    /// </summary>
    public Guid SourceId { get; set; }

    /// <summary>
    /// The reference attribute's SCIM name, for example <c>members</c>.
    /// </summary>
    [MaxLength(ScimStoreLimits.AttributeMaxLength)]
    public string Attribute { get; set; } = null!;

    /// <summary>
    /// The resource referred to (the member).
    /// </summary>
    public Guid TargetId { get; set; }
}
