// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// One item of a streamed whole-system preview (#1530), in the order the synchronisation would meet it: the population
/// first, then each Connected System Object, then each Metaverse Object the export scope review would reach. What an
/// item carries depends on its <see cref="Kind"/>.
/// </summary>
public class FullSyncPreviewItem
{
    /// <summary>
    /// What this item reports.
    /// </summary>
    public FullSyncPreviewItemKind Kind { get; init; }

    /// <summary>
    /// For <see cref="FullSyncPreviewItemKind.Population"/>: how many Connected System Objects the system holds.
    /// </summary>
    public int? TotalObjectCount { get; init; }

    /// <summary>
    /// The Connected System Object this item is about; null for the population, a refusal and the export scope review.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; init; }

    /// <summary>
    /// The Metaverse Object an export scope review item is about.
    /// </summary>
    public Guid? MetaverseObjectId { get; init; }

    /// <summary>
    /// The Metaverse Object Type of an export scope review item's object.
    /// </summary>
    public int? MetaverseObjectTypeId { get; init; }

    /// <summary>
    /// The object's name as an administrator would recognise it, for a row that names it.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// The name of the object's type.
    /// </summary>
    public string? ObjectTypeName { get; init; }

    /// <summary>
    /// For <see cref="FullSyncPreviewItemKind.Evaluated"/>: the category the object falls into.
    /// </summary>
    public FullSyncPreviewCategory? Category { get; init; }

    /// <summary>
    /// For an evaluated object or an export scope review, what the synchronisation would do to it; for a refusal, the
    /// errors that refuse the whole synchronisation.
    /// </summary>
    public SyncPreviewResult? Preview { get; init; }

    /// <summary>
    /// For <see cref="FullSyncPreviewItemKind.Truncated"/>: which bound stopped the walk.
    /// </summary>
    public FullSyncPreviewTruncationReason? TruncationReason { get; init; }
}
