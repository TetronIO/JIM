// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Scim.Discovery;

/// <summary>
/// The simple "is this optional feature available" block used by several
/// <see cref="ScimServiceProviderConfig"/> members (RFC 7643 section 5).
/// </summary>
public class ScimSupportedFeature
{
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }
}
