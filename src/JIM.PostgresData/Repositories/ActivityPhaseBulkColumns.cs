// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column lists used by the raw-SQL upsert that records the
/// phases of a Run Profile execution (<c>SyncRepository.SaveActivityPhasesAsync</c>). The writer
/// MUST write values in exactly this order.
///
/// The upsert is raw SQL rather than EF because it runs on the synchronisation path, where the
/// worker's DbContext holds a tracker full of entities from the run and any SaveChangesAsync would
/// drag DetectChanges across all of it (the same reason UpdateActivityMessageAsync is raw SQL).
/// BulkInsertColumnCompletenessTests asserts this list matches the EF model's mapped columns
/// exactly, so a migration that adds a phase column fails the test run instead of silently leaving
/// it null on every recorded phase.
/// </summary>
internal static class ActivityPhaseBulkColumns
{
    /// <summary>
    /// Insert columns for the ActivityPhases table.
    /// </summary>
    internal static readonly string[] ActivityPhases =
    [
        "Id", "ActivityId", "Order", "Key", "Name", "ParentKey", "Status", "Started", "Ended"
    ];

    /// <summary>
    /// Update columns for the upsert: the state a phase transition changes. A phase's identity,
    /// position, label and parentage are fixed when the run declares it.
    /// </summary>
    internal static readonly string[] ActivityPhasesUpdate =
    [
        "Status", "Started", "Ended"
    ];

    /// <summary>
    /// Columns deliberately excluded from <see cref="ActivityPhasesUpdate"/>. Id and ActivityId are
    /// the identity; Order, Key, Name and ParentKey are declared when the run starts and never
    /// change afterwards, so that a historic Activity keeps the step names it actually ran with.
    /// </summary>
    internal static readonly string[] ActivityPhasesUpdateExclusions =
    [
        "Id", "ActivityId", "Order", "Key", "Name", "ParentKey"
    ];
}
