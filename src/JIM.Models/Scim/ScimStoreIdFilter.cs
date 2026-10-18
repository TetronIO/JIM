// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// The resource with this id (<c>id eq</c>).
/// </summary>
public sealed record ScimStoreIdFilter(Guid Id) : ScimStoreFilter;
