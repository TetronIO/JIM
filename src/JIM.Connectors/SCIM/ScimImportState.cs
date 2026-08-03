// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace JIM.Connectors.SCIM;

/// <summary>
/// What the connector remembers about a Connected System between runs, held in the Connected System's
/// persisted connector data. Only the delta watermark needs remembering: everything else about a
/// service provider is re-discovered each run, so a provider that gains or loses a capability is
/// followed immediately.
/// </summary>
public class ScimImportState
{
    /// <summary>
    /// The instant the next Delta Import asks the provider for changes after. Deliberately set behind
    /// the point the last import began, so a resource changed while that import was running is read
    /// again rather than missed.
    /// </summary>
    [JsonPropertyName("watermark")]
    public DateTimeOffset? Watermark { get; set; }

    /// <summary>
    /// When JIM recorded the watermark, which is the run's own clock rather than the provider's. Kept
    /// for support: the gap between the two is what a clock-skew problem looks like.
    /// </summary>
    [JsonPropertyName("capturedAt")]
    public DateTimeOffset? CapturedAt { get; set; }

    /// <summary>
    /// Reads the state a previous run persisted.
    /// </summary>
    /// <returns>
    /// The state, or null when there is none or it cannot be read. Unreadable state is not fatal: the
    /// run falls back to a full scan, which re-establishes the watermark, where failing would leave the
    /// Connected System unable to import at all until someone intervened.
    /// </returns>
    public static ScimImportState? Read(string? persistedConnectorData, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(persistedConnectorData))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ScimImportState>(persistedConnectorData);
        }
        catch (JsonException ex)
        {
            logger.Warning(ex, "The SCIM connector's persisted data could not be read, so this run cannot use a delta watermark. A full scan will re-establish one.");
            return null;
        }
    }

    public string Serialise()
    {
        return JsonSerializer.Serialize(this);
    }
}
