// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// One retained full-tree sample from a full-system preview (#288, PRD decision D2's sampled tier): the
/// category it represents, the Connected System Object it is, and its complete per-object preview.
/// </summary>
public class FullSyncPreviewSample
{
    /// <summary>
    /// The outcome category this sample represents.
    /// </summary>
    public FullSyncPreviewCategory Category { get; set; }

    /// <summary>
    /// The sampled Connected System Object.
    /// </summary>
    public Guid ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The object's full per-object preview, outcome tree included, exactly as
    /// <c>PreviewSyncForCsoAsync</c> would return it.
    /// </summary>
    public SyncPreviewResult Preview { get; set; } = new();
}
