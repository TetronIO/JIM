// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Returns caller-supplied pages one call at a time, as a Connector reading a directory by calls does. One instance
/// serves one import: it hands back each page once, in order. A <paramref name="firstPageWatermark"/> is returned with
/// the first page, as a Connector's new Delta Import watermark is. <paramref name="completeReadByPage"/> says, page by
/// page, whether the Connector read every object in the run's scope (<see cref="ConnectedSystemImportResult.CompleteRead"/>),
/// as one that falls back to a full read does; a page beyond the list, and every page when it is null, says it did not.
/// </summary>
internal sealed class PagedImportConnector(List<List<ConnectedSystemImportObject>> pages, string? firstPageWatermark = null,
    IReadOnlyList<bool>? completeReadByPage = null) : IConnector, IConnectorImportUsingCalls
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
        var pageIndex = _nextPage++;
        var page = pages[pageIndex];
        var result = new ConnectedSystemImportResult
        {
            ImportObjects = page,
            PersistedConnectorData = _nextPage == 1 ? firstPageWatermark : null,
            CompleteRead = completeReadByPage != null && pageIndex < completeReadByPage.Count && completeReadByPage[pageIndex]
        };
        if (_nextPage < pages.Count)
            result.PaginationTokens.Add(new ConnectedSystemPaginationToken("page", _nextPage.ToString()));
        return Task.FromResult(result);
    }
}
