// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Messages;

/// <summary>
/// A SCIM PATCH request body (RFC 7644 section 3.5.2).
/// </summary>
public class ScimPatchRequest
{
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; set; } = [ScimUrns.PatchOp];

    /// <summary>
    /// The operations to apply, in order. The member name is capitalised in the specification, as with
    /// a ListResponse's <c>Resources</c>.
    /// </summary>
    [JsonPropertyName("Operations")]
    public List<ScimPatchOperation> Operations { get; set; } = [];
}
