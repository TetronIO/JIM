// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace JIM.Connectors.Graph;

/// <summary>
/// What the Microsoft Graph Connector persists between runs: each Object Type's delta link, the watermark the next
/// Delta Import reads changes from, and when the run that took them started.
/// </summary>
internal sealed class GraphImportState
{
    [JsonPropertyName("deltaLinks")]
    public Dictionary<string, string> DeltaLinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; set; }

    public string Serialise() => JsonSerializer.Serialize(this);

    /// <summary>The state persisted, or null where there is none or it cannot be read, which means no watermark.</summary>
    public static GraphImportState? Read(string? persistedConnectorData, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(persistedConnectorData))
            return null;

        try
        {
            var state = JsonSerializer.Deserialize<GraphImportState>(persistedConnectorData);
            if (state != null)
                state.DeltaLinks = new Dictionary<string, string>(state.DeltaLinks, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (JsonException ex)
        {
            logger.Warning(ex, "The Microsoft Graph Connector's persisted import state could not be read, so it is treated as having no delta watermark.");
            return null;
        }
    }
}
