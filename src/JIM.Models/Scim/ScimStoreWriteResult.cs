// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// The outcome of inserting or updating a resource in a SCIM store write session.
/// </summary>
public sealed class ScimStoreWriteResult
{
    public ScimStoreWriteOutcome Outcome { get; init; }

    /// <summary>
    /// The resource's version after the write; 0 when nothing was written.
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// The attribute whose value another live resource already holds, when <see cref="Outcome"/> is
    /// <see cref="ScimStoreWriteOutcome.UniquenessConflict"/>: what the SCIM error names.
    /// </summary>
    public string? ConflictingAttribute { get; init; }

    public static ScimStoreWriteResult Written(long version) => new() { Outcome = ScimStoreWriteOutcome.Written, Version = version };

    public static ScimStoreWriteResult Conflict(string attribute) =>
        new() { Outcome = ScimStoreWriteOutcome.UniquenessConflict, ConflictingAttribute = attribute };
}
