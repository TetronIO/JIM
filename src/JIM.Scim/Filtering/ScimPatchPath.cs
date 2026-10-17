// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Filtering;

/// <summary>
/// The target of a PATCH operation (RFC 7644 section 3.5.2): an attribute path, optionally narrowed to the entries a
/// value filter selects, and optionally then to one sub-attribute of those entries, as in
/// <c>emails[type eq "work"].value</c>.
/// </summary>
/// <param name="Attribute">The attribute, with its sub-attribute when no value filter is given (<c>name.givenName</c>).</param>
/// <param name="ValueFilter">The filter selecting entries of a multi-valued attribute, if any.</param>
/// <param name="SubAttributeName">The sub-attribute of the selected entries, after a value filter.</param>
public sealed record ScimPatchPath(ScimAttributePath Attribute, ScimFilter? ValueFilter, string? SubAttributeName);
