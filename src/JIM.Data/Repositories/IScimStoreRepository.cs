// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scim;

namespace JIM.Data.Repositories;

/// <summary>
/// The SCIM store: the Users and Groups identity providers push to Connected Systems using the SCIM 2.0 Service
/// Provider Connector, their Group memberships, each store's change sequence, and the journal of accepted writes.
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
