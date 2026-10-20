// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// How many live resources of one resource type a SCIM store holds.
/// </summary>
/// <param name="ResourceTypeName">The resource type, for example <c>User</c>.</param>
/// <param name="Count">Its live resources.</param>
public record ScimResourceTypeCount(string ResourceTypeName, int Count);
