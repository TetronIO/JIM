// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Messages;

/// <summary>
/// A service provider's answer to a bulk request (RFC 7644 section 3.7.3).
/// <para>
/// Nothing in the specification promises the operations come back in the order they were sent, or that
/// every operation sent is reported on at all: a provider that stops early simply says less. A client
/// therefore has to correlate rather than count, and treat silence about an operation as an unknown
/// outcome rather than a successful one.
/// </para>
/// </summary>
public class ScimBulkResponse
{
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; set; } = [ScimUrns.BulkResponse];

    [JsonPropertyName("Operations")]
    public List<ScimBulkOperationResult> Operations { get; set; } = [];
}
