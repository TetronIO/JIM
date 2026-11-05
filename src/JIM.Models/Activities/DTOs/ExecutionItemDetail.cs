// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.History;
using JIM.Models.Utility;

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// A Run Profile Execution Item as its portal page shows it (#2498): the item with what it names, bounded, and a summary
/// of each change record it carries in place of the change's values, which can run to one per member of a large group
/// and are read a window at a time.
/// </summary>
/// <remarks>
/// The item carries its own row and sync outcomes, and its change records (its own, and each outcome's staged snapshot)
/// without their attribute changes. Its Connected System Object is not loaded through the item: the id the item holds is
/// history and outlives the object, so it is resolved by id (#2669) into <see cref="Record"/>.
/// </remarks>
public sealed class ExecutionItemDetail
{
    public required ActivityRunProfileExecutionItem Item { get; init; }

    /// <summary>
    /// The item's Connected System Object, resolved from the id the item holds: the live object, its deletion record, or
    /// nothing but the id. Null when the item names no Connected System Object.
    /// </summary>
    public ResolvedObjectReference? Record { get; init; }

    /// <summary>
    /// The Metaverse Object Type of the object a live <see cref="Record"/> is joined to; null when the record is not
    /// live or not joined.
    /// </summary>
    public int? RecordJoinedMetaverseObjectTypeId { get; init; }

    /// <summary>
    /// A summary of each change record the item carries that has values, keyed by change id: the item's Connected System
    /// Object and Metaverse Object changes, and each outcome's staged change. A change with no values has no entry.
    /// </summary>
    public IReadOnlyDictionary<Guid, ChangeValueSummary> ChangeValueSummaries { get; init; } = new Dictionary<Guid, ChangeValueSummary>();
}
