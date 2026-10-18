// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// Resources holding a value of an attribute that must be unique, found through the unique-value rows rather than the
/// payload: the <c>userName eq</c> probe identity providers send. <paramref name="Value"/> is normalised exactly as the
/// write path normalises it (see <see cref="ScimStoreUniqueValue"/>).
/// </summary>
public sealed record ScimStoreUniqueValueFilter(string Attribute, string Value) : ScimStoreFilter;
