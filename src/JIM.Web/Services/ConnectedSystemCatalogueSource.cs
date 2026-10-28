// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Web.Models;

namespace JIM.Web.Services;

/// <summary>
/// Reads a Connected System's catalogue through the application layer, applying the settings on screen as drafts
/// so an administrator browses the database they are configuring rather than the one last saved.
/// </summary>
/// <param name="jimFactory">Creates the <see cref="JimApplication"/> each read runs on.</param>
/// <param name="connectedSystemId">The Connected System whose Connector reads the catalogue.</param>
/// <param name="draftSettings">The settings as they stand on screen, credential included: reading a catalogue means signing in.</param>
public sealed class ConnectedSystemCatalogueSource(
    IJimApplicationFactory jimFactory,
    int connectedSystemId,
    Func<List<ConnectedSystemSettingValueDraft>> draftSettings) : IConnectedSystemCatalogueSource
{
    public Task<ConnectedSystemCatalogueRead<IReadOnlyList<ConnectorCatalogueObject>>> GetObjectsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<ConnectorCatalogueObject>>(async jim =>
            await jim.ConnectedSystems.GetCatalogueObjectsAsync(connectedSystemId, draftSettings(), cancellationToken));

    public Task<ConnectedSystemCatalogueRead<ConnectorCatalogueObjectDetail>> GetObjectAsync(string? schemaName, string objectName, CancellationToken cancellationToken = default) =>
        ReadAsync(jim => jim.ConnectedSystems.GetCatalogueObjectAsync(connectedSystemId, schemaName, objectName, draftSettings(), cancellationToken));

    public Task<ConnectedSystemCatalogueRead<ConnectorCatalogueObjectDetail>> DescribeStatementAsync(string statement, CancellationToken cancellationToken = default) =>
        ReadAsync<ConnectorCatalogueObjectDetail>(async jim =>
            await jim.ConnectedSystems.DescribeCatalogueStatementAsync(connectedSystemId, statement, draftSettings(), cancellationToken));

    private async Task<ConnectedSystemCatalogueRead<T>> ReadAsync<T>(Func<JimApplication, Task<T?>> read)
    {
        try
        {
            using var jim = jimFactory.Create();
            return new ConnectedSystemCatalogueRead<T>(await read(jim), null);
        }
        catch (InvalidSettingValuesException ex)
        {
            return new ConnectedSystemCatalogueRead<T>(default, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new ConnectedSystemCatalogueRead<T>(default, ex.Message);
        }
        // Fallback dispatcher, as the REST endpoint has: any failure reading the source (unreachable, refused
        // credentials, a malformed response) is the reason the lists are unavailable, and the editor says so and
        // offers a retry. Cancellation propagates, because nobody is waiting for the answer any more.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectedSystemCatalogueRead<T>(default, ex.Message);
        }
    }
}
