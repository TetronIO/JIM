// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;
using JIM.Scim.Resources;

namespace JIM.Scim.Discovery;

/// <summary>
/// A schema definition served from <c>/Schemas</c> (RFC 7643 section 7): the authoritative description
/// of a resource type's attributes, including any vendor extensions the provider has defined.
/// </summary>
public class ScimSchema
{
    /// <summary>
    /// The schema's URN, for example <c>urn:ietf:params:scim:schemas:core:2.0:User</c>. This is the key
    /// a <see cref="ScimResourceType"/> refers to it by.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("attributes")]
    public List<ScimSchemaAttribute> Attributes { get; set; } = [];

    [JsonPropertyName("meta")]
    public ScimMeta? Meta { get; set; }
}
