// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace JIM.Connectors.Graph;

/// <summary>One answer in a <c>$batch</c> response, as Graph sends it.</summary>
internal sealed class GraphBatchAnswer
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }
}
