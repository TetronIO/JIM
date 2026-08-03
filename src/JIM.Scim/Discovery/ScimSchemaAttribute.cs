// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Discovery;

/// <summary>
/// One attribute in a SCIM schema definition (RFC 7643 section 7). Sub-attributes are the same shape,
/// one level deep: SCIM does not allow a complex attribute inside a complex attribute.
/// </summary>
public class ScimSchemaAttribute
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// The SCIM data type keyword; see <see cref="ScimAttributeTypes"/>. Absent means <c>string</c>
    /// per RFC 7643 section 7, which is the default the type mapper applies.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("subAttributes")]
    public List<ScimSchemaAttribute> SubAttributes { get; set; } = [];

    [JsonPropertyName("multiValued")]
    public bool MultiValued { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>
    /// The permitted values, where the provider constrains them. On a multi-valued complex attribute's
    /// <c>type</c> sub-attribute these are the canonical types (work, home, ...) that drive flattening.
    /// </summary>
    [JsonPropertyName("canonicalValues")]
    public List<string> CanonicalValues { get; set; } = [];

    /// <summary>
    /// Whether string comparisons are case sensitive. Recorded for completeness; JIM does not model
    /// per-attribute case sensitivity, which the connector documentation states.
    /// </summary>
    [JsonPropertyName("caseExact")]
    public bool CaseExact { get; set; }

    /// <summary>
    /// One of <c>readOnly</c>, <c>readWrite</c>, <c>immutable</c> or <c>writeOnly</c>; see
    /// <see cref="ScimMutability"/>. Absent means <c>readWrite</c> per RFC 7643 section 7.
    /// </summary>
    [JsonPropertyName("mutability")]
    public string? Mutability { get; set; }

    /// <summary>
    /// When the provider returns the attribute: <c>always</c>, <c>never</c>, <c>default</c> or
    /// <c>request</c>. A <c>never</c> attribute (such as <c>password</c>) can never be imported.
    /// </summary>
    [JsonPropertyName("returned")]
    public string? Returned { get; set; }

    [JsonPropertyName("uniqueness")]
    public string? Uniqueness { get; set; }

    /// <summary>
    /// For reference attributes, the resource types the reference may point at (or <c>external</c> /
    /// <c>uri</c> for non-SCIM targets).
    /// </summary>
    [JsonPropertyName("referenceTypes")]
    public List<string> ReferenceTypes { get; set; } = [];
}
