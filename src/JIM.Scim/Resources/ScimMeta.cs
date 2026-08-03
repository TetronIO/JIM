// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Resources;

/// <summary>
/// The common resource metadata every SCIM 2.0 resource carries (RFC 7643 section 3.1).
/// <para>
/// <see cref="LastModified"/> is the basis of the connector's <c>LastModifiedFilter</c> delta strategy,
/// and <see cref="Version"/> is the entity tag used by the <c>ETagConditional</c> strategy.
/// </para>
/// </summary>
public class ScimMeta
{
    /// <summary>
    /// The resource type name, for example <c>User</c>; matches a <see cref="Discovery.ScimResourceType.Name"/>.
    /// </summary>
    [JsonPropertyName("resourceType")]
    public string? ResourceType { get; set; }

    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; set; }

    /// <summary>
    /// When the resource was last changed. Providers commonly expose this at one-second precision, which
    /// is why the delta watermark deliberately overlaps rather than resuming from the exact high value.
    /// </summary>
    [JsonPropertyName("lastModified")]
    public DateTimeOffset? LastModified { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    /// <summary>
    /// The resource's entity tag, including the weak-validator prefix and quotes as sent by the provider
    /// (for example <c>W/"3694e05e9dff594"</c>), so it can be echoed verbatim in an <c>If-None-Match</c>.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
