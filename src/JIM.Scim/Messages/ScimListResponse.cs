// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Messages;

/// <summary>
/// The SCIM 2.0 list response envelope (RFC 7644 section 3.4.2), returned by every query endpoint.
/// </summary>
/// <typeparam name="T">The resource type carried in the page.</typeparam>
public class ScimListResponse<T>
{
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; set; } = [ScimUrns.ListResponse];

    /// <summary>
    /// The total number of resources matching the query across all pages, not the size of this page.
    /// </summary>
    [JsonPropertyName("totalResults")]
    public int TotalResults { get; set; }

    [JsonPropertyName("itemsPerPage")]
    public int? ItemsPerPage { get; set; }

    /// <summary>
    /// The 1-based index of the first resource in this page, for index-based pagination.
    /// </summary>
    [JsonPropertyName("startIndex")]
    public int? StartIndex { get; set; }

    /// <summary>
    /// The opaque cursor for the next page under cursor-based pagination (RFC 9865). Absent (or empty)
    /// on the final page, and on providers that only do index-based pagination.
    /// </summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }

    /// <summary>
    /// The page's resources. Defaults to empty because RFC 7644 section 3.4.2 lets a provider omit the
    /// member entirely when nothing matched, and a null here would be indistinguishable from a bug.
    /// </summary>
    [JsonPropertyName("Resources")]
    public List<T> Resources { get; set; } = [];
}
