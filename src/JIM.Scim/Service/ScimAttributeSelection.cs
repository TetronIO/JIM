// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Service;

/// <summary>
/// A request's <c>attributes</c> or <c>excludedAttributes</c> (RFC 7644 section 3.9), which shape the resource a
/// response returns.
/// </summary>
public sealed record ScimAttributeSelection(
    IReadOnlyCollection<string>? Attributes = null,
    IReadOnlyCollection<string>? ExcludedAttributes = null);
