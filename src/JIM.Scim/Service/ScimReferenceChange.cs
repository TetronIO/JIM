// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Service;

/// <summary>
/// What a membership-only PATCH does to one reference attribute: references to add and to remove, or, where the request
/// cleared or replaced the attribute, the whole set it now holds (<paramref name="ReplaceAll"/>, with that set in
/// <paramref name="Add"/>).
/// </summary>
/// <param name="Attribute">The reference attribute's store name.</param>
/// <param name="ReplaceAll">Whether <paramref name="Add"/> replaces everything the attribute holds.</param>
/// <param name="Add">References to add, or the whole set where <paramref name="ReplaceAll"/>.</param>
/// <param name="Remove">References to remove; empty where <paramref name="ReplaceAll"/>.</param>
public sealed record ScimReferenceChange(string Attribute, bool ReplaceAll, IReadOnlyCollection<Guid> Add, IReadOnlyCollection<Guid> Remove);
