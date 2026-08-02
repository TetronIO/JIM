// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.SCIM;
using JIM.Models.Staging;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Drives a <see cref="ScimConnector"/> the way JIM does: open the connection once, ask for pages until
/// no pagination tokens come back, then close it. Tests that walked the loop themselves would be
/// testing their own loop as much as the connector's.
/// </summary>
internal static class ScimImportRunner
{
    /// <param name="afterPage">
    /// Called with the 1-based page number after each page, so a test can change the service provider
    /// mid-walk (expiring a cursor, modifying a resource) the way the world does.
    /// </param>
    public static async Task<List<ConnectedSystemImportResult>> RunAsync(
        ScimConnector connector,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        ILogger logger,
        string? persistedConnectorData = null,
        Action<int>? afterPage = null,
        int maximumPages = 10)
    {
        connector.OpenImportConnection(connectedSystem.SettingValues!, logger);

        var results = new List<ConnectedSystemImportResult>();
        var tokens = new List<ConnectedSystemPaginationToken>();

        try
        {
            for (var page = 1; page <= maximumPages; page++)
            {
                var result = await connector.ImportAsync(connectedSystem, runProfile, tokens, persistedConnectorData, logger, CancellationToken.None, new RecordingConnectorProgress());
                results.Add(result);
                afterPage?.Invoke(page);

                if (result.PaginationTokens.Count == 0)
                    return results;

                tokens = result.PaginationTokens;
            }
        }
        finally
        {
            connector.CloseImportConnection();
        }

        Assert.Fail($"The import did not finish within {maximumPages} pages.");
        return results;
    }

    /// <summary>
    /// The watermark a completed run left behind, ready to hand to the next run.
    /// </summary>
    public static string? PersistedConnectorData(IEnumerable<ConnectedSystemImportResult> results)
    {
        return results.Select(r => r.PersistedConnectorData).FirstOrDefault(data => data != null);
    }
}
