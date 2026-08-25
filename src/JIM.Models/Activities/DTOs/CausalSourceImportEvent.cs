// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// The import event that last changed a Connected System Object before a synchronisation processed it: the
/// hop that takes a causal chain back to its root, where data arrived from the source system (#1223).
/// </summary>
/// <remarks>
/// This hop is derived from the record's own timeline rather than from a stored edge, which is the PRD's
/// requirement 4 applied: per-object timelines are the free join, used wherever "what else happened to this
/// object" is the answer. Nothing here is snapshotted at capture time, because nothing is captured; a record
/// whose import has aged out of retention simply yields no event, and the chain ends at the synchronisation.
/// </remarks>
public class CausalSourceImportEvent
{
    /// <summary>
    /// The import's Run Profile Execution Item, which the hop links to and the walk continues from.
    /// </summary>
    public Guid RunProfileExecutionItemId { get; init; }

    /// <summary>
    /// What the import did to the record: added it, changed it, or deleted it.
    /// </summary>
    public ObjectChangeType ChangeType { get; init; }

    /// <summary>
    /// How the record was named on the import item.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// The Connected System the import ran against, resolved from the import's Activity.
    /// </summary>
    public int? ConnectedSystemId { get; init; }

    /// <inheritdoc cref="ConnectedSystemId"/>
    public string? ConnectedSystemName { get; init; }

    /// <summary>
    /// When the import's Activity ran (UTC), which is what the Lineage shows on the hop's card and orders the
    /// column's cards by. This is the same value the lookup already sorts on to pick the latest import, so it
    /// costs nothing to carry.
    /// </summary>
    public DateTime Occurred { get; init; }
}
