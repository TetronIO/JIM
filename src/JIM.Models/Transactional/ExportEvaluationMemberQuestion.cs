// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A multi-valued reference attribute of a target Connected System Object that export evaluation loads in part (#2832):
/// only the values that could name one of <see cref="MetaverseObjectIds"/>, the members whose addition or removal the
/// page's changes stage against it, rather than every member a large group holds.
/// </summary>
/// <remarks>
/// No-net-change detection asks of each staged member change whether the target already holds that member. A change
/// staged before export resolution names the member's Metaverse Object, so a held value can match it only by naming an
/// object joined to that Metaverse Object, or, unresolved, by its text (<c>SyncEngine.ReferenceValueMatches</c>). The
/// values loaded are a superset of those; the engine's own comparison still decides each answer.
/// </remarks>
public sealed class ExportEvaluationMemberQuestion
{
    /// <summary>
    /// The target Connected System Object.
    /// </summary>
    public required Guid ConnectedSystemObjectId { get; init; }

    /// <summary>
    /// The multi-valued reference attribute loaded in part.
    /// </summary>
    public required int AttributeId { get; init; }

    /// <summary>
    /// The Metaverse Objects the page's changes stage as members added or removed; empty when the page changes none, in
    /// which case none of the attribute's values is loaded.
    /// </summary>
    public required IReadOnlySet<Guid> MetaverseObjectIds { get; init; }
}
