// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

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
    /// The resource lost a reference (a Group a member, for example) because the resource it referred to was deleted.
    /// No request named it, but its contents, and so its version, changed.
    /// </summary>
    ReferenceRemovedByDeletion = 5
}

/// <summary>
/// The outcome of inserting or updating a resource in a write session.
/// </summary>
public enum ScimStoreWriteOutcome
{
    /// <summary>
    /// The resource was written.
    /// </summary>
    Written = 1,

    /// <summary>
    /// Another live resource of the same type already holds one of the values that must be unique; nothing was
    /// written, and the session is still usable.
    /// </summary>
    UniquenessConflict = 2
}

/// <summary>
/// How two SCIM store filters are joined.
/// </summary>
public enum ScimStoreLogicalOperator
{
    And = 1,
    Or = 2
}

/// <summary>
/// How a stored instant compares to a filter's.
/// </summary>
public enum ScimStoreComparison
{
    Equal = 1,
    GreaterThan = 2,
    GreaterOrEqual = 3,
    LessThan = 4,
    LessOrEqual = 5
}

/// <summary>
/// How a stored string matches a filter's.
/// </summary>
public enum ScimStoreTextMatch
{
    Equal = 1,
    Contains = 2,
    StartsWith = 3
}
