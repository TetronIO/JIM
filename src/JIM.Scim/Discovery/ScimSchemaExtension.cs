// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Discovery;

/// <summary>
/// A schema extension a resource type carries in addition to its base schema (RFC 7643 section 6),
/// for example Enterprise User on User.
/// </summary>
public class ScimSchemaExtension
{
    /// <summary>
    /// The extension schema's URN, which is also the JSON member the extension's attributes sit under
    /// on a resource.
    /// </summary>
    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    /// <summary>
    /// Whether the provider requires the extension to be present on every resource of this type.
    /// </summary>
    [JsonPropertyName("required")]
    public bool Required { get; set; }
}
