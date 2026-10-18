// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Service;

/// <summary>
/// A SCIM list request's query (RFC 7644 section 3.4.2, RFC 9865), as the HTTP surface reads it from the URL or a
/// <c>.search</c> body.
/// </summary>
/// <param name="Filter">The filter, or null for every resource.</param>
/// <param name="StartIndex">For index paging, the 1-based position of the first resource; a value below 1 is read as 1.</param>
/// <param name="Count">The most resources to return; a negative value is read as 0, which returns only the total.</param>
/// <param name="Cursor">For cursor paging, the previous page's <c>nextCursor</c>, or an empty string for the first page;
/// null for index paging. When both are given, the cursor wins.</param>
/// <param name="Selection">The request's <c>attributes</c> or <c>excludedAttributes</c>.</param>
public sealed record ScimListRequest(
    string? Filter = null,
    int? StartIndex = null,
    int? Count = null,
    string? Cursor = null,
    ScimAttributeSelection? Selection = null);
