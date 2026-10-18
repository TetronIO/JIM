// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scim;

namespace JIM.Data.Repositories;

/// <summary>
/// The SCIM store: the resources identity providers push to Connected Systems using the SCIM 2.0 Service Provider
/// Connector, the resource type and schema definitions each Connected System's store accepts, each store's change
/// sequence, and the journal of accepted writes.
/// </summary>
/// <remarks>
/// Every change goes through a write session (<see cref="BeginWriteAsync"/>), which serialises a Connected System's
/// writes and assigns their change sequence in commit order. See <see cref="IScimStoreWriteSession"/>.
/// </remarks>
public interface IScimStoreRepository
{
    /// <summary>
    /// Starts a write to a Connected System's store: opens a transaction, takes the store's write lock, and assigns
    /// the write its change sequence. Returns null, having written nothing, when the Connected System does not exist
    /// (including one deleted while this call waited for its lock).
    /// </summary>
    /// <remarks>
    /// The lock is held until the session commits or is disposed, so keep sessions short: do the work that needs no
    /// database (parsing, validating, applying a PATCH) before starting one. The session runs on this repository's
    /// own connection, so use nothing else on the repository while one is open.
    /// </remarks>
    Task<IScimStoreWriteSession?> BeginWriteAsync(int connectedSystemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A live resource of a Connected System's store, or null when there is none of that type with that id (or only a
    /// tombstone). Reads outside any write session, for GET.
    /// </summary>
    Task<ScimStoreResource?> GetLiveResourceAsync(int connectedSystemId, int resourceTypeId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The references a resource holds, through every attribute, with the type of each resource referred to. Reads
    /// outside any write session, for GET.
    /// </summary>
    Task<IReadOnlyList<ScimStoreReferenceTarget>> GetReferencesAsync(Guid sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The references several resources hold, keyed by the resource holding them, with the type of each resource referred
    /// to: one read for a page of a list. A resource holding none is absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ScimStoreReferenceTarget>>> GetReferencesAsync(IReadOnlyCollection<Guid> sourceIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of a Connected System's live resources of a type, in id order, with how many match in all. Reads outside
    /// any write session, for a SCIM list request.
    /// </summary>
    Task<ScimStorePage> ListLiveResourcesAsync(int connectedSystemId, ScimStoreQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The resource types a Connected System's store accepts.
    /// </summary>
    Task<IReadOnlyList<ScimStoreResourceType>> GetResourceTypesAsync(int connectedSystemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The schemas a Connected System's store uses.
    /// </summary>
    Task<IReadOnlyList<ScimStoreSchema>> GetSchemasAsync(int connectedSystemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds schema and resource type definitions to a Connected System's store, together or not at all: the core User,
    /// Group and Enterprise User definitions when the Connected System is created, and later any custom ones. Each
    /// row's Connected System is set from <paramref name="connectedSystemId"/>.
    /// </summary>
    Task AddDefinitionsAsync(int connectedSystemId, IReadOnlyCollection<ScimStoreSchema> schemas,
        IReadOnlyCollection<ScimStoreResourceType> resourceTypes, CancellationToken cancellationToken = default);

    /// <summary>
    /// A Connected System's store counters, or null before its first write.
    /// </summary>
    Task<ScimStoreState?> GetStateAsync(int connectedSystemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a Connected System's tombstones that an import has already read (change sequence at or below
    /// <paramref name="importedUpToChangeSequence"/>) and that are older than <paramref name="deletedBefore"/>, and raises
    /// the store's tombstone purge floor to the highest change sequence removed. Returns the number removed.
    /// </summary>
    Task<int> PurgeTombstonesAsync(int connectedSystemId, long importedUpToChangeSequence, DateTime deletedBefore,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes up to <paramref name="maxRecords"/> journal entries that occurred before <paramref name="cutoff"/>, oldest
    /// first and across every Connected System, for the history retention cleanup. Returns the number removed.
    /// </summary>
    Task<int> DeleteJournalEntriesOlderThanAsync(DateTime cutoff, int maxRecords, CancellationToken cancellationToken = default);
}
