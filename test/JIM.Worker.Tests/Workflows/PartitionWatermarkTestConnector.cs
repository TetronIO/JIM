// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A Connector that keeps a Delta Import watermark per Partition (<see cref="IConnectorPartitionWatermarks"/>, #2715),
/// returning caller-supplied pages one call at a time, as <see cref="PagedImportConnector"/> does. It records every
/// watermark map JIM hands it and the order of JIM's calls, and what it held when each page was asked for, so a test can
/// assert what the processor gave it. <paramref name="partitionWatermarksByPage"/> gives, page by page, the
/// <see cref="ConnectedSystemImportResult.PartitionWatermarks"/> to return; <paramref name="closeReturn"/> is what
/// <see cref="CloseImportConnection"/> returns.
/// </summary>
internal sealed class PartitionWatermarkTestConnector(List<List<ConnectedSystemImportObject>> pages,
    IReadOnlyList<Dictionary<int, string?>?>? partitionWatermarksByPage = null, IReadOnlyList<bool>? completeReadByPage = null,
    string? firstPageWatermark = null, Action<int>? beforePage = null, string? closeReturn = null)
    : IConnector, IConnectorImportUsingCalls, IConnectorPartitionWatermarks
{
    private int _nextPage;
    private IReadOnlyDictionary<int, string?>? _watermarks;

    public string Name => "MockConnector";
    public string? Description => null;
    public string? Url => null;

    /// <summary>Every map JIM handed over, in order.</summary>
    public List<IReadOnlyDictionary<int, string?>> WatermarksSet { get; } = [];

    /// <summary>What the Connector held when each page was asked for, copied, so a later change cannot rewrite it.</summary>
    public List<Dictionary<int, string?>?> WatermarksHeldByPage { get; } = [];

    /// <summary>The calls JIM made, in order: Set, Open, Import and Close.</summary>
    public List<string> Calls { get; } = [];

    public void SetPartitionWatermarks(IReadOnlyDictionary<int, string?> watermarks)
    {
        Calls.Add("Set");
        WatermarksSet.Add(watermarks);
        _watermarks = watermarks;
    }

    public ImportWatermarkDescription? DescribePartitionWatermark(string? value, ILogger logger) =>
        value == null ? null : new ImportWatermarkDescription { Kind = "test position", Points = [new ImportWatermarkPoint { Text = value }] };

    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger) =>
        Calls.Add("Open");

    public string? CloseImportConnection()
    {
        Calls.Add("Close");
        return closeReturn;
    }

    public Task<ConnectedSystemImportResult> ImportAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        Calls.Add("Import");
        WatermarksHeldByPage.Add(_watermarks?.ToDictionary(watermark => watermark.Key, watermark => watermark.Value));

        var pageIndex = _nextPage++;
        beforePage?.Invoke(pageIndex);
        var result = new ConnectedSystemImportResult
        {
            ImportObjects = pages[pageIndex],
            PersistedConnectorData = _nextPage == 1 ? firstPageWatermark : null,
            CompleteRead = completeReadByPage != null && pageIndex < completeReadByPage.Count && completeReadByPage[pageIndex],
            PartitionWatermarks = partitionWatermarksByPage != null && pageIndex < partitionWatermarksByPage.Count ? partitionWatermarksByPage[pageIndex] : null
        };
        if (_nextPage < pages.Count)
            result.PaginationTokens.Add(new ConnectedSystemPaginationToken("page", _nextPage.ToString()));
        return Task.FromResult(result);
    }
}
