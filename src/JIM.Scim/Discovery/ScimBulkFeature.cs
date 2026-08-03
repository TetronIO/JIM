// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Discovery;

/// <summary>
/// The bulk-operation capability block (RFC 7643 section 5). The limits are binding: RFC 7644 section
/// 3.7 requires a client to keep within them, and providers reject an oversized batch outright.
/// </summary>
public class ScimBulkFeature : ScimSupportedFeature
{
    /// <summary>
    /// The largest number of operations a single bulk request may carry. Null when the provider
    /// advertises bulk support without stating a limit.
    /// </summary>
    [JsonPropertyName("maxOperations")]
    public int? MaxOperations { get; set; }

    /// <summary>
    /// The largest bulk payload, in bytes.
    /// </summary>
    [JsonPropertyName("maxPayloadSize")]
    public long? MaxPayloadSize { get; set; }
}
