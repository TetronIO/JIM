// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace JIM.Connectors.Graph;

/// <summary>
/// What the Microsoft Graph Connector persists between runs: each Object Type's delta link, the watermark the next
/// Delta Import reads changes from, when the run that took them started, and what a Delta Import must find unchanged to
/// read changes from them rather than every object.
/// </summary>
/// <remarks>
/// JIM records this on every import's Activity, so it is kept small: the set of cloud-managed objects is held as a hash
/// rather than as its ids.
/// </remarks>
internal sealed class GraphImportState
{
    [JsonPropertyName("deltaLinks")]
    public Dictionary<string, string> DeltaLinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>Each Object Type's <see cref="GraphImportTarget.Signature"/> when its link was taken.</summary>
    [JsonPropertyName("signatures")]
    public Dictionary<string, string> Signatures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each Object Type's set of synchronised objects whose Source of Authority is switched to Entra ID, as a hash, or null where Graph refused the query.
    /// Moving an object changes nothing a delta round reports, so this is how a Delta Import sees that one moved.
    /// </summary>
    [JsonPropertyName("cloudManaged")]
    public Dictionary<string, string?> CloudManagedHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
            {
                state.DeltaLinks = new Dictionary<string, string>(state.DeltaLinks ?? [], StringComparer.OrdinalIgnoreCase);
                state.Signatures = new Dictionary<string, string>(state.Signatures ?? [], StringComparer.OrdinalIgnoreCase);
                state.CloudManagedHashes = new Dictionary<string, string?>(state.CloudManagedHashes ?? [], StringComparer.OrdinalIgnoreCase);
            }
            return state;
        }
        catch (JsonException ex)
        {
            logger.Warning(ex, "The Microsoft Graph Connector's persisted import state could not be read, so it is treated as having no delta watermark.");
            return null;
        }
    }
}
