// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A Connector that says which Partition each of its objects is in (<see cref="IConnectorPartitionPlacement"/>, #2723) by
/// the LDAP Connector's own rule, the distinguished name, and returns caller-supplied pages one call at a time as
/// <see cref="PagedImportConnector"/> does. It records every identifier JIM asked it to place, so a test can show what
/// JIM asks about and what it does not.
/// </summary>
internal sealed class PartitionPlacementTestConnector(List<List<ConnectedSystemImportObject>> pages, IReadOnlyList<bool>? completeReadByPage = null)
    : IConnector, IConnectorImportUsingCalls, IConnectorPartitionPlacement
{
    private readonly PagedImportConnector _pages = new(pages, completeReadByPage: completeReadByPage);

    public string Name => "MockConnector";
    public string? Description => null;
    public string? Url => null;

    /// <summary>Every identifier JIM asked a placer to place, in order.</summary>
    public List<string?> IdentifiersPlaced { get; } = [];

    /// <summary>How many times JIM prepared a placer.</summary>
    public int PreparedCount { get; private set; }

    public IConnectorPartitionPlacer PreparePartitionPlacement(IReadOnlyCollection<ConnectedSystemPartition> partitions)
    {
        PreparedCount++;
        using var ldapConnector = new LdapConnector();
        return new RecordingPlacer(IdentifiersPlaced, ((IConnectorPartitionPlacement)ldapConnector).PreparePartitionPlacement(partitions));
    }

    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger) =>
        _pages.OpenImportConnection(settingValues, persistedConnectorData, logger);

    public string? CloseImportConnection() => _pages.CloseImportConnection();

    public Task<ConnectedSystemImportResult> ImportAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress) =>
        _pages.ImportAsync(connectedSystem, runProfile, paginationTokens, persistedConnectorData, logger, cancellationToken, progress);

    private sealed class RecordingPlacer(List<string?> identifiersPlaced, IConnectorPartitionPlacer ldapPlacer) : IConnectorPartitionPlacer
    {
        public ConnectedSystemPartition? GetPartition(string? objectIdentifier)
        {
            identifiersPlaced.Add(objectIdentifier);
            return ldapPlacer.GetPartition(objectIdentifier);
        }
    }
}
