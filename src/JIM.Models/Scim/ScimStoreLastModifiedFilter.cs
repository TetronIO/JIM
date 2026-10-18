// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// Resources whose <c>meta.lastModified</c> compares to <paramref name="Value"/> (UTC) as stated: the filter the SCIM
/// client's Delta Import sends.
/// </summary>
public sealed record ScimStoreLastModifiedFilter(ScimStoreComparison Comparison, DateTime Value) : ScimStoreFilter;
