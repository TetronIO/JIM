// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Filtering;

/// <summary>
/// An attribute path in a SCIM filter or PATCH path (RFC 7644 section 3.10): an optional schema URN, an attribute and
/// an optional sub-attribute, as in <c>urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:manager.value</c>.
/// Names are kept as written; comparing them case-insensitively is for whatever evaluates the path.
/// </summary>
/// <param name="SchemaUrn">The schema the attribute belongs to, when the path names one.</param>
/// <param name="AttributeName">The attribute.</param>
/// <param name="SubAttributeName">The sub-attribute of a complex attribute, when the path names one.</param>
public sealed record ScimAttributePath(string? SchemaUrn, string AttributeName, string? SubAttributeName);
