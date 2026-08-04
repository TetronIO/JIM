// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Messages;

/// <summary>
/// A SCIM bulk request body (RFC 7644 section 3.7).
/// </summary>
public class ScimBulkRequest
{
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; set; } = [ScimUrns.BulkRequest];

    /// <summary>
    /// How many errors the provider should tolerate before abandoning the rest of the request.
    /// <para>
    /// Deliberately left unset by JIM, which RFC 7644 section 3.7.1 defines as "process everything
    /// regardless". A bulk export has to behave exactly like the per-object export it replaces, where
    /// one rejected object never abandons the rest of the batch; setting a threshold would make the
    /// number of changes applied depend on where in the batch a bad object happened to sit.
    /// </para>
    /// </summary>
    [JsonPropertyName("failOnErrors")]
    public int? FailOnErrors { get; set; }

    /// <summary>
    /// The operations to apply. The member name is capitalised in the specification, as with a
    /// ListResponse's <c>Resources</c>.
    /// </summary>
    [JsonPropertyName("Operations")]
    public List<ScimBulkOperation> Operations { get; set; } = [];
}
