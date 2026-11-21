// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Connectors.Graph;

/// <summary>A page of a Microsoft Graph collection: its values, and the links to the next page or the next delta.</summary>
internal sealed class GraphCollectionPage<T>
{
    [JsonPropertyName("value")]
    public List<T> Value { get; set; } = [];

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }

    [JsonPropertyName("@odata.deltaLink")]
    public string? DeltaLink { get; set; }
}
