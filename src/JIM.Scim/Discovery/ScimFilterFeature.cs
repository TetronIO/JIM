// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Discovery;

/// <summary>
/// The filtering capability block (RFC 7643 section 5). Filtering is what makes delta import by
/// <c>meta.lastModified</c> possible; without it the connector falls back to a full scan.
/// </summary>
public class ScimFilterFeature : ScimSupportedFeature
{
    /// <summary>
    /// The largest number of resources the provider will return for a filtered query, regardless of the
    /// requested count. Pagination must respect this or later pages are silently truncated.
    /// </summary>
    [JsonPropertyName("maxResults")]
    public int? MaxResults { get; set; }
}
