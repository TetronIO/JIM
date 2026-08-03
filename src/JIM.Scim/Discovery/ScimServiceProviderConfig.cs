// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;
using JIM.Scim.Resources;

namespace JIM.Scim.Discovery;

/// <summary>
/// A service provider's declaration of the optional protocol features it supports
/// (RFC 7643 section 5, served from <c>/ServiceProviderConfig</c>).
/// <para>
/// Every feature block is nullable on purpose. An absent block means the provider has not asserted the
/// feature, which the connector treats as unsupported: assuming support would have it send PATCH or
/// Bulk requests the provider cannot answer, turning a discovery gap into failed exports.
/// </para>
/// </summary>
public class ScimServiceProviderConfig
{
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; set; } = [];

    [JsonPropertyName("documentationUri")]
    public string? DocumentationUri { get; set; }

    /// <summary>
    /// Whether PATCH is supported. When it is not, the connector degrades updates to whole-resource PUT.
    /// </summary>
    [JsonPropertyName("patch")]
    public ScimSupportedFeature? Patch { get; set; }

    [JsonPropertyName("bulk")]
    public ScimBulkFeature? Bulk { get; set; }

    [JsonPropertyName("filter")]
    public ScimFilterFeature? Filter { get; set; }

    [JsonPropertyName("changePassword")]
    public ScimSupportedFeature? ChangePassword { get; set; }

    [JsonPropertyName("sort")]
    public ScimSupportedFeature? Sort { get; set; }

    /// <summary>
    /// Whether the provider maintains entity tags, which the <c>ETagConditional</c> change-detection
    /// strategy requires.
    /// </summary>
    [JsonPropertyName("etag")]
    public ScimSupportedFeature? ETag { get; set; }

    [JsonPropertyName("authenticationSchemes")]
    public List<ScimAuthenticationScheme> AuthenticationSchemes { get; set; } = [];

    [JsonPropertyName("meta")]
    public ScimMeta? Meta { get; set; }
}
