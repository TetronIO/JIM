// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;
using JIM.Scim.Resources;

namespace JIM.Scim.Discovery;

/// <summary>
/// A resource type a service provider exposes (RFC 7643 section 6, served from <c>/ResourceTypes</c>).
/// This is what tells the connector which endpoint to enumerate and which schemas compose the type,
/// rather than assuming the standard <c>/Users</c> and <c>/Groups</c> layout.
/// </summary>
public class ScimResourceType
{
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; set; } = [];

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// The type's name, for example <c>User</c>. Used as the Connected System Object Type name, so it
    /// lines up with JIM's Metaverse Object Type naming without administrator intervention.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// The endpoint the resources live at, relative to the base URL and specified with a leading slash
    /// (for example <c>/Users</c>).
    /// </summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    /// <summary>
    /// The URN of the type's base schema.
    /// </summary>
    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("schemaExtensions")]
    public List<ScimSchemaExtension> SchemaExtensions { get; set; } = [];

    [JsonPropertyName("meta")]
    public ScimMeta? Meta { get; set; }
}
