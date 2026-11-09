// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What an export batch's results change on the Pending Exports it records (#2851), for the export-result writer: the
/// Pending Exports themselves, the attribute changes the batch sent, and those whose value or holds changed besides.
/// Writing only these, rather than every column of every change, is what keeps recording a large group's Create from
/// costing as much as the rest of its export.
/// </summary>
public sealed class PendingExportResultWrite
{
    /// <summary>
    /// The Pending Exports whose results are recorded; each is written in full.
    /// </summary>
    public required IReadOnlyList<PendingExport> PendingExports { get; init; }

    /// <summary>
    /// The changes of <see cref="PendingExports"/> the batch sent: each is now awaiting confirmation, one attempt more than
    /// was stored, sent at <see cref="SentAt"/>. Every one must still be in the database.
    /// </summary>
    public required IReadOnlyCollection<PendingExportAttributeValueChange> SentChanges { get; init; }

    /// <summary>
    /// When the batch's changes were sent: the <see cref="PendingExportAttributeValueChange.LastExportedAt"/> every one of
    /// <see cref="SentChanges"/> holds.
    /// </summary>
    public required DateTime SentAt { get; init; }

    /// <summary>
    /// The changes of <see cref="PendingExports"/> whose value or holds changed since the batch was sent
    /// (<see cref="PendingExportAttributeValueChangeState"/>), sent or not; written as they stand.
    /// </summary>
    public required IReadOnlyCollection<PendingExportAttributeValueChange> RevisedChanges { get; init; }
}
