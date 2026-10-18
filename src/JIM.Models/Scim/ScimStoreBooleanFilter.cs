// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// Resources whose payload holds this Boolean at <paramref name="Path"/> (<c>active eq true</c>).
/// </summary>
public sealed record ScimStoreBooleanFilter(IReadOnlyList<string> Path, bool Value) : ScimStoreFilter;
