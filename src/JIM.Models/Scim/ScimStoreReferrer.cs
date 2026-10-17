// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// A resource that lost a reference because the resource it referred to was deleted: which resource, its type, and the
/// attribute that held the reference. One per resource and attribute.
/// </summary>
public sealed record ScimStoreReferrer(Guid SourceId, int SourceResourceTypeId, string Attribute);
