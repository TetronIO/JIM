// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scim;

namespace JIM.Data.Repositories;

/// <summary>
/// One write to one Connected System's SCIM store, in one transaction. It holds the store's write lock from start to
/// commit, so the Connected System's writes commit one at a time, in the order of their <see cref="ChangeSequence"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every row the session writes is stamped with <see cref="ChangeSequence"/> and <see cref="Timestamp"/>, and every
/// resource it changes gets its <see cref="ScimStoreResource.Version"/> raised by one, by the session itself; callers
/// cannot set those. A resource's version rises once per session however many of its rows the session touches (its
/// own row, its unique values and its references), so one request is one version. Disposing a session without
/// <see cref="CommitAsync"/> rolls everything back.
/// </para>
/// <para>
/// The session knows nothing of User or Group. Resource types are the Connected System's
/// <see cref="ScimStoreResourceType"/> rows; which values must be unique, and which attributes are held as references,
/// is decided by the caller from the schemas and passed in as data.
/// </para>
/// </remarks>
public interface IScimStoreWriteSession : IAsyncDisposable
{
    /// <summary>
    /// The Connected System whose store this session writes.
    /// </summary>
    int ConnectedSystemId { get; }

    /// <summary>
    /// This write's change sequence: one more than the last committed write's.
    /// </summary>
    long ChangeSequence { get; }

    /// <summary>
    /// The instant this write happens (UTC), taken once the write lock was held: the <c>meta.lastModified</c> of
    /// every resource it changes, and the time of its journal entries.
    /// </summary>
    DateTime Timestamp { get; }

    /// <summary>
    /// A live resource of this Connected System, or null when there is none of that type with that id (or only a
    /// tombstone).
    /// </summary>
    Task<ScimStoreResource?> GetLiveResourceAsync(int resourceTypeId, Guid id);

    /// <summary>
    /// Which of these ids are live resources of this Connected System, and of which resource type. Ids that are not
    /// (unknown, another Connected System's, or tombstoned) are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetLiveResourceTypeIdsAsync(IReadOnlyCollection<Guid> ids);

    /// <summary>
    /// The resources a resource refers to through one reference attribute (a Group's <c>members</c>).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetReferenceTargetIdsAsync(Guid sourceId, string attribute);

    /// <summary>
    /// Inserts a new resource at version 1, holding <paramref name="uniqueValues"/>. The caller supplies the id, type,
    /// payload and <see cref="ScimStoreResource.Created"/>; the session stamps the rest. When another live resource of
    /// the same type already holds one of the values, nothing is written and the conflict is reported; the session
    /// stays usable.
    /// </summary>
    /// <param name="uniqueValues">Every value of the resource that must be unique, each with its attribute name and its
    /// value already normalised for comparison.</param>
    Task<ScimStoreWriteResult> InsertResourceAsync(ScimStoreResource resource, IReadOnlyCollection<ScimStoreUniqueValue> uniqueValues);

    /// <summary>
    /// Replaces a live resource's payload and its unique values, raising its version. Conflicts are reported as for
    /// <see cref="InsertResourceAsync"/>, with nothing written.
    /// </summary>
    /// <exception cref="InvalidOperationException">The resource is not live.</exception>
    Task<ScimStoreWriteResult> UpdateResourceAsync(ScimStoreResource resource, IReadOnlyCollection<ScimStoreUniqueValue> uniqueValues);

    /// <summary>
    /// Deletes a live resource, leaving a tombstone: payload cleared, unique values and its own references removed,
    /// version raised. References to it from other resources are removed too, and every resource that lost one has its
    /// version raised and is returned, so the caller can journal the change. Returns null when the resource is not
    /// live.
    /// </summary>
    Task<IReadOnlyList<Guid>?> TombstoneResourceAsync(int resourceTypeId, Guid id);

    /// <summary>
    /// Adds references from a resource; ones it already holds are ignored. Returns the source's version.
    /// </summary>
    /// <exception cref="InvalidOperationException">The source is not live.</exception>
    Task<long> AddReferencesAsync(Guid sourceId, string attribute, IReadOnlyCollection<Guid> targetIds);

    /// <summary>
    /// Removes references from a resource; ones it does not hold are ignored. Returns the source's version.
    /// </summary>
    /// <exception cref="InvalidOperationException">The source is not live.</exception>
    Task<long> RemoveReferencesAsync(Guid sourceId, string attribute, IReadOnlyCollection<Guid> targetIds);

    /// <summary>
    /// Makes a resource's references through one attribute exactly these. Returns the source's version.
    /// </summary>
    /// <exception cref="InvalidOperationException">The source is not live.</exception>
    Task<long> ReplaceReferencesAsync(Guid sourceId, string attribute, IReadOnlyCollection<Guid> targetIds);

    /// <summary>
    /// Records what this write did. The session stamps each entry's Connected System, change sequence and time.
    /// </summary>
    Task AppendJournalEntriesAsync(IReadOnlyCollection<ScimStoreJournalEntry> entries);

    /// <summary>
    /// Commits the write and releases the store's write lock.
    /// </summary>
    Task CommitAsync();
}
