// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// What a Connected System's SCIM store holds and how far the imports have read it, for an administrator.
/// </summary>
public class ScimStoreSummary
{
    /// <summary>
    /// The live resources of each resource type the store serves, including types with none.
    /// </summary>
    public List<ScimResourceTypeCount> ResourceCounts { get; set; } = [];

    /// <summary>
    /// Deleted resources kept until an import has read the deletion and the purge removes them.
    /// </summary>
    public int Tombstones { get; set; }

    /// <summary>
    /// The store's last committed change sequence; zero before its first write.
    /// </summary>
    public long LastChangeSequence { get; set; }

    /// <summary>
    /// The change sequence the last successful import read up to; null before any import has run.
    /// </summary>
    public long? ImportedUpTo { get; set; }

    /// <summary>
    /// The resources the next Delta Import will read: created, changed or deleted since <see cref="ImportedUpTo"/>, or
    /// every resource and tombstone when nothing has been imported yet.
    /// </summary>
    public int ChangesAwaitingImport { get; set; }

    /// <summary>
    /// When a SCIM client last wrote to the store; null when none has.
    /// </summary>
    public DateTime? LastWrite { get; set; }

    /// <summary>
    /// Requests the store refused, other than for authentication, across every key, in the 15-minute windows that
    /// began in the last 24 hours.
    /// </summary>
    public int RejectedLast24Hours { get; set; }
}
