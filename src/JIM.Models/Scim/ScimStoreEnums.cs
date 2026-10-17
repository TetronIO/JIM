// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// The kinds of resource the SCIM store holds. Stored as an integer, so members are never renumbered.
/// </summary>
public enum ScimStoreResourceType
{
    User = 1,
    Group = 2
}

/// <summary>
/// What one accepted SCIM write did to one resource, as the store's journal records it. Stored as an integer, so
/// members are never renumbered.
/// </summary>
public enum ScimStoreOperation
{
    /// <summary>
    /// A POST created the resource.
    /// </summary>
    Created = 1,

    /// <summary>
    /// A PUT replaced the resource.
    /// </summary>
    Replaced = 2,

    /// <summary>
    /// A PATCH changed the resource.
    /// </summary>
    Patched = 3,

    /// <summary>
    /// A DELETE removed the resource, leaving a tombstone.
    /// </summary>
    Deleted = 4,

    /// <summary>
    /// The Group lost a member because that member was deleted. No request named the Group, but its membership, and
    /// so its version, changed.
    /// </summary>
    MemberRemovedByDeletion = 5
}

/// <summary>
/// The outcome of inserting or updating a resource row in a write session.
/// </summary>
public enum ScimStoreWriteOutcome
{
    /// <summary>
    /// The row was written.
    /// </summary>
    Written = 1,

    /// <summary>
    /// Another live resource of the same type in the same Connected System already has this <c>userName</c>
    /// (compared without regard to case). Nothing was written, and the session can no longer be committed.
    /// </summary>
    UserNameTaken = 2,

    /// <summary>
    /// Another live resource of the same type in the same Connected System already has this <c>externalId</c>.
    /// Nothing was written, and the session can no longer be committed.
    /// </summary>
    ExternalIdTaken = 3
}
