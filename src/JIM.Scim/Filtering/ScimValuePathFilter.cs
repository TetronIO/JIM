// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Filtering;

/// <summary>
/// A filter applied to the entries of a multi-valued complex attribute, matching a resource with at least one entry
/// that matches: <c>emails[type eq "work" and value co "@example.com"]</c>. The inner filter's paths name the entry's
/// sub-attributes.
/// </summary>
public sealed record ScimValuePathFilter(ScimAttributePath Path, ScimFilter Filter) : ScimFilter;
