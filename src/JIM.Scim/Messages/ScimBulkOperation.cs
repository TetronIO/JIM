// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Messages;

/// <summary>
/// One operation inside a bulk request (RFC 7644 section 3.7.2): the same change that would otherwise
/// have been its own HTTP request, described rather than sent.
/// </summary>
public class ScimBulkOperation
{
    /// <summary>
    /// The HTTP method the operation stands in for: <c>POST</c>, <c>PUT</c>, <c>PATCH</c> or
    /// <c>DELETE</c>.
    /// </summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// The client's own identifier for this operation, unique within the request. Required for a POST,
    /// because a resource being created has no other name yet; sent on every operation regardless, so
    /// the provider has an unambiguous way to report each outcome back.
    /// </summary>
    [JsonPropertyName("bulkId")]
    public string? BulkId { get; set; }

    /// <summary>
    /// The resource or collection the operation addresses, rooted at the service provider's base
    /// (for example <c>/Users</c> for a create, <c>/Users/2819c223</c> for anything else).
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The body the equivalent standalone request would have carried: a resource for POST and PUT, a
    /// PATCH request for PATCH, and nothing at all for DELETE.
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }

    /// <summary>
    /// The entity tag the resource carried when JIM last read it, which is how a bulk operation asks
    /// for the guarantee <c>If-Match</c> gives a standalone request.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
