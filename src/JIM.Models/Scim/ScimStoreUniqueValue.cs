// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Scim;

/// <summary>
/// One value a live resource holds for an attribute that must be unique among the live resources of its type in its
/// Connected System: <c>userName</c>, <c>externalId</c>, or any attribute a schema marks <c>uniqueness</c>
/// <c>server</c>. The primary key over (Connected System, resource type, attribute, value) is what enforces it, so the
/// rule is data, and a new unique attribute needs no change to the store's shape.
/// </summary>
/// <remarks>
/// The SCIM server supplies <see cref="Attribute"/> and <see cref="Value"/>, already normalised the way the schema
/// compares the attribute (folded to one case unless it is <c>caseExact</c>); the write session fills in the rest.
/// The key is over <see cref="ValueHash"/> rather than the value itself, so a value of any length can be indexed.
/// </remarks>
public class ScimStoreUniqueValue
{
    public int ConnectedSystemId { get; set; }

    public int ResourceTypeId { get; set; }

    /// <summary>
    /// The attribute's SCIM name, for example <c>userName</c>, or an extension attribute's URN-qualified name.
    /// </summary>
    [MaxLength(ScimStoreLimits.AttributeMaxLength)]
    public string Attribute { get; set; } = null!;

    /// <summary>
    /// The value, normalised for comparison.
    /// </summary>
    public string Value { get; set; } = null!;

    /// <summary>
    /// The SHA-256 of <see cref="Value"/> (UTF-8), which the key is over.
    /// </summary>
    public byte[] ValueHash { get; set; } = null!;

    /// <summary>
    /// The live resource holding the value.
    /// </summary>
    public Guid ResourceId { get; set; }
}
