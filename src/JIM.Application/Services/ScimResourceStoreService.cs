// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Scim;

namespace JIM.Application.Services;

/// <summary>
/// A Connected System's SCIM store, for the SCIM 2.0 Service Provider Connector (<see cref="IScimResourceStore"/>),
/// over the SCIM server.
/// </summary>
public class ScimResourceStoreService(JimApplication application, int connectedSystemId) : IScimResourceStore
{
    public Task<IReadOnlyList<ScimStoreResourceType>> GetResourceTypesAsync(CancellationToken cancellationToken = default) =>
        application.Scim.GetResourceTypeRowsAsync(connectedSystemId, cancellationToken);

    public Task<IReadOnlyList<ScimStoreSchema>> GetSchemasAsync(CancellationToken cancellationToken = default) =>
        application.Scim.GetSchemaRowsAsync(connectedSystemId, cancellationToken);

    public Task<ScimStoreState?> GetStateAsync(CancellationToken cancellationToken = default) =>
        application.Scim.GetStoreStateAsync(connectedSystemId, cancellationToken);

    public Task<ScimStoreImportPage> ReadAsync(ScimStoreImportQuery query, CancellationToken cancellationToken = default) =>
        application.Scim.ReadForImportAsync(connectedSystemId, query, cancellationToken);

    public Task<int> PurgeTombstonesAsync(long importedUpTo, DateTime deletedBefore, CancellationToken cancellationToken = default) =>
        application.Scim.PurgeTombstonesAsync(connectedSystemId, importedUpTo, deletedBefore, cancellationToken);
}
