// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace JIM.Connectors.SCIM.ServiceProvider;

/// <summary>
/// What the SCIM 2.0 Service Provider Connector remembers between runs, held in the Connected System's persisted
/// connector data: the store change sequence the last completed import read up to.
/// </summary>
public class ScimServiceProviderImportState
{
    /// <summary>
    /// The store's committed change sequence when the last completed import began. Every change at or below it has been
    /// read, so the next Delta Import reads only the changes after it.
    /// </summary>
    [JsonPropertyName("changeSequence")]
    public long ChangeSequence { get; set; }

    /// <summary>
    /// Reads the state a previous run persisted.
    /// </summary>
    /// <returns>
    /// The state, or null when there is none or it cannot be read. Unreadable state is not fatal: a Delta Import falls
    /// back to reading everything, which re-establishes the watermark.
    /// </returns>
    public static ScimServiceProviderImportState? Read(string? persistedConnectorData, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(persistedConnectorData))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ScimServiceProviderImportState>(persistedConnectorData);
        }
        catch (JsonException ex)
        {
            logger.Warning(ex, "The SCIM 2.0 Service Provider Connector's persisted data could not be read, so this run has no watermark to read changes after.");
            return null;
        }
    }

    public string Serialise()
    {
        return JsonSerializer.Serialize(this);
    }
}
