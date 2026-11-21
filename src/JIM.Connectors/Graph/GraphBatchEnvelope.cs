// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Connectors.Graph;

/// <summary>The body of a <c>$batch</c> response: one answer per request, in any order.</summary>
internal sealed class GraphBatchEnvelope
{
    [JsonPropertyName("responses")]
    public List<GraphBatchAnswer> Responses { get; set; } = [];
}
