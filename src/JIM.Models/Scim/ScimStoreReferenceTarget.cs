// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// One reference a resource holds, as the store reads it back: the attribute holding it, the live resource it refers to,
/// and that resource's type.
/// </summary>
/// <param name="Attribute">The reference attribute's SCIM name (<c>members</c>, or an extension's URN-qualified name).</param>
/// <param name="TargetId">The resource referred to.</param>
/// <param name="TargetResourceTypeId">The resource type of the resource referred to.</param>
public sealed record ScimStoreReferenceTarget(string Attribute, Guid TargetId, int TargetResourceTypeId);
