// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Filtering;

/// <summary>
/// The negation of a filter: <c>not (emails co "example.com")</c>.
/// </summary>
public sealed record ScimNotFilter(ScimFilter Inner) : ScimFilter;
