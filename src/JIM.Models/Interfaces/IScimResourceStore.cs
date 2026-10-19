// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scim;

namespace JIM.Models.Interfaces;

/// <summary>
/// One Connected System's SCIM store, as the SCIM 2.0 Service Provider Connector reaches it: its definitions, its
/// change sequence, the resources an import reads, and the purge of tombstones an import has already read. It is bound
/// to its Connected System when JIM hands it over, so the Connector never names one.
/// </summary>
/// <remarks>
/// Defined here and implemented in JIM.Application, so JIM.Connectors never references the application or data layers
/// (the same seam as <see cref="ICertificateProvider"/>).
/// </remarks>
public interface IScimResourceStore
{
    /// <summary>
    /// The resource types the store serves.
    /// </summary>
    Task<IReadOnlyList<ScimStoreResourceType>> GetResourceTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The schemas the resource types are composed of.
    /// </summary>
    Task<IReadOnlyList<ScimStoreSchema>> GetSchemasAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The store's committed change sequence and tombstone purge floor; null before its first write.
    /// </summary>
    Task<ScimStoreState?> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of what an import reads (<see cref="ScimStoreImportQuery"/>).
    /// </summary>
    Task<ScimStoreImportPage> ReadAsync(ScimStoreImportQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes tombstones an import has read (change sequence at or below <paramref name="importedUpTo"/>) and deleted
    /// before <paramref name="deletedBefore"/>, raising the purge floor. Returns how many were removed.
    /// </summary>
    Task<int> PurgeTombstonesAsync(long importedUpTo, DateTime deletedBefore, CancellationToken cancellationToken = default);
}
