// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JIM.Scim.Messages;

/// <summary>
/// One operation in a SCIM PATCH request (RFC 7644 section 3.5.2). The client connector generates
/// these; JIM's own service provider applies them.
/// </summary>
public class ScimPatchOperation
{
    /// <summary>One of <see cref="ScimPatchOperations"/>.</summary>
    [JsonPropertyName("op")]
    public string Op { get; set; } = ScimPatchOperations.Replace;

    /// <summary>
    /// The attribute path, which may carry a value filter selecting one entry of a multi-valued
    /// attribute (<c>members[value eq "x"]</c>). Absent on an operation that carries a whole resource
    /// fragment as its value.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>
    /// The value to apply. Absent on a <see cref="ScimPatchOperations.Remove"/>, where the path alone
    /// says what to take away.
    /// </summary>
    [JsonPropertyName("value")]
    public JsonNode? Value { get; set; }
}
