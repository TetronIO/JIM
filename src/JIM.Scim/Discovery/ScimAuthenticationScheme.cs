// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Discovery;

/// <summary>
/// An authentication scheme a service provider says it accepts (RFC 7643 section 5).
/// <para>
/// Treated as advisory only. The Laravel test provider advertises OAuth Bearer while enforcing no
/// authentication at all, so JIM never infers its credential choice from this: the administrator's
/// Authentication Method setting decides, and a mismatch surfaces as a run warning.
/// </para>
/// </summary>
public class ScimAuthenticationScheme
{
    /// <summary>
    /// The scheme keyword, for example <c>oauthbearertoken</c> or <c>httpbasic</c>.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("specUri")]
    public string? SpecUri { get; set; }

    [JsonPropertyName("documentationUri")]
    public string? DocumentationUri { get; set; }

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }
}
