// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// #2817: a value a Connected System Object holds for a reference attribute, as far as the import's comparison reads it:
/// the string the reference was imported as, and the object it was resolved to.
/// </summary>
/// <param name="Id">The value's id.</param>
/// <param name="UnresolvedReferenceValue">The string the reference was imported as, which resolution keeps; null where none was kept.</param>
/// <param name="ReferenceValueId">The Connected System Object the reference was resolved to; null while unresolved.</param>
public sealed record HeldReferenceValue(Guid Id, string? UnresolvedReferenceValue, Guid? ReferenceValueId);
