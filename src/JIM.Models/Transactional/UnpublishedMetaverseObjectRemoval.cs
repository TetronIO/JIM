// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What removing unpublished Metaverse Objects did (#2848): the objects removed, and the objects kept because something
/// joined them after they were projected (another Connected System's synchronisation, running at the same time).
/// </summary>
/// <param name="RemovedIds">The unpublished objects removed: projected by a page that never committed, joined to
/// nothing.</param>
/// <param name="KeptJoinedIds">Unpublished objects kept because a Connected System Object is joined to them. Their records
/// are removed, so they are ordinary Metaverse Objects from then on.</param>
public sealed record UnpublishedMetaverseObjectRemoval(IReadOnlyList<Guid> RemovedIds, IReadOnlyList<Guid> KeptJoinedIds)
{
    /// <summary>Nothing removed and nothing kept.</summary>
    public static UnpublishedMetaverseObjectRemoval None { get; } = new([], []);
}
