// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Returns caller-supplied pages one call at a time, as a Connector reading a directory by calls does. One instance
/// serves one import: it hands back each page once, in order.
/// </summary>
internal sealed class PagedImportConnector(List<List<ConnectedSystemImportObject>> pages) : IConnector, IConnectorImportUsingCalls
{
    private int _nextPage;

    public string Name => "MockConnector";
    public string? Description => null;
    public string? Url => null;

    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger) { }

    public string? CloseImportConnection() => null;

    public Task<ConnectedSystemImportResult> ImportAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        var page = pages[_nextPage++];
        var result = new ConnectedSystemImportResult { ImportObjects = page };
        if (_nextPage < pages.Count)
            result.PaginationTokens.Add(new ConnectedSystemPaginationToken("page", _nextPage.ToString()));
        return Task.FromResult(result);
    }
}
