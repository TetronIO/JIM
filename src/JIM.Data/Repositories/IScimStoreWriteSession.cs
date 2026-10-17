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
/// own row and its memberships), so one request is one version. Disposing a session without <see cref="CommitAsync"/> rolls everything back.
/// </para>
/// <para>
/// A write refused by a uniqueness rule leaves the transaction unusable: the method reports the conflict, and the
/// session can then only be disposed.
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
    Task<ScimStoreResource?> GetLiveResourceAsync(ScimStoreResourceType resourceType, Guid id);

    /// <summary>
    /// Which of these ids are live resources of this Connected System, and of which type. Ids that are not (unknown,
    /// another Connected System's, or tombstoned) are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ScimStoreResourceType>> GetLiveResourceTypesAsync(IReadOnlyCollection<Guid> ids);

    /// <summary>
    /// A Group's member ids.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetGroupMemberIdsAsync(Guid groupId);

    /// <summary>
    /// Inserts a new resource at version 1. The caller supplies the id, type, identifiers, payload and
    /// <see cref="ScimStoreResource.Created"/>; the session stamps the rest.
    /// </summary>
    Task<ScimStoreWriteOutcome> InsertResourceAsync(ScimStoreResource resource);

    /// <summary>
    /// Replaces a live resource's identifiers and payload, raising its version by one. Returns the version it now has
    /// on success.
    /// </summary>
    Task<(ScimStoreWriteOutcome Outcome, long Version)> UpdateResourceAsync(ScimStoreResource resource);

    /// <summary>
    /// Deletes a live resource, leaving a tombstone: identifiers and payload cleared, version raised. Its memberships
    /// go too, both as a Group and as a member; every Group that lost it as a member has its version raised and is
    /// returned, so the caller can journal the change. Returns null when the resource is not live.
    /// </summary>
    Task<IReadOnlyList<Guid>?> TombstoneResourceAsync(ScimStoreResourceType resourceType, Guid id);

    /// <summary>
    /// Adds members to a Group; ids that are already members are ignored. Returns the Group's version.
    /// </summary>
    /// <exception cref="InvalidOperationException">The Group is not live.</exception>
    Task<long> AddGroupMembersAsync(Guid groupId, IReadOnlyCollection<Guid> memberIds);

    /// <summary>
    /// Removes members from a Group; ids that are not members are ignored. Returns the Group's version.
    /// </summary>
    /// <exception cref="InvalidOperationException">The Group is not live.</exception>
    Task<long> RemoveGroupMembersAsync(Guid groupId, IReadOnlyCollection<Guid> memberIds);

    /// <summary>
    /// Makes a Group's members exactly these. Returns the Group's version.
    /// </summary>
    /// <exception cref="InvalidOperationException">The Group is not live.</exception>
    Task<long> ReplaceGroupMembersAsync(Guid groupId, IReadOnlyCollection<Guid> memberIds);

    /// <summary>
    /// Records what this write did. The session stamps each entry's Connected System, change sequence and time.
    /// </summary>
    Task AppendJournalEntriesAsync(IReadOnlyCollection<ScimStoreJournalEntry> entries);

    /// <summary>
    /// Commits the write and releases the store's write lock.
    /// </summary>
    Task CommitAsync();
}
