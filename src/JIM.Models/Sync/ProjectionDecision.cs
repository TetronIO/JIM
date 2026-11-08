// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Models.Sync;

/// <summary>
/// The result of evaluating whether a new MVO should be projected for a CSO.
/// Returned by <c>ISyncEngine.EvaluateProjection</c>.
/// </summary>
public readonly struct ProjectionDecision
{
    /// <summary>
    /// Whether a new MVO should be created.
    /// </summary>
    public bool ShouldProject { get; init; }

    /// <summary>
    /// The Metaverse Object Type to use for the new MVO, when <see cref="ShouldProject"/> is true.
    /// </summary>
    public MetaverseObjectType? MetaverseObjectType { get; init; }

    /// <summary>
    /// The Synchronisation Rule that caused the projection, when <see cref="ShouldProject"/> is true.
    /// Carried so callers can attribute the resulting Projected sync outcome to the rule (#1085).
    /// </summary>
    public SyncRule? ProjectionSyncRule { get; init; }

    /// <summary>
    /// Whether in-scope projecting rules disagree on the Metaverse Object Type (#2823): nothing is projected, and the
    /// object is reported with a Projection Type Conflict naming <see cref="ConflictingRules"/>.
    /// </summary>
    public bool IsConflict { get; init; }

    /// <summary>
    /// The projecting rules that disagree on the Metaverse Object Type, by name, when <see cref="IsConflict"/> is true;
    /// otherwise empty.
    /// </summary>
    public IReadOnlyList<SyncRule> ConflictingRules { get; init; }

    /// <summary>
    /// Creates a decision indicating a new MVO should be projected.
    /// </summary>
    /// <param name="mvoType">The Metaverse Object Type for the new MVO.</param>
    /// <param name="projectionSyncRule">The Synchronisation Rule that caused the projection, for outcome attribution.</param>
    public static ProjectionDecision Project(MetaverseObjectType mvoType, SyncRule? projectionSyncRule = null) => new()
    {
        ShouldProject = true,
        MetaverseObjectType = mvoType,
        ProjectionSyncRule = projectionSyncRule,
        ConflictingRules = []
    };

    /// <summary>
    /// Creates a decision indicating no projection should occur.
    /// </summary>
    public static ProjectionDecision NoProjection() => new() { ShouldProject = false, ConflictingRules = [] };

    /// <summary>
    /// Creates a decision indicating projecting rules disagree on the Metaverse Object Type, so nothing is projected.
    /// </summary>
    /// <param name="conflictingRules">The projecting rules, by name.</param>
    public static ProjectionDecision Conflict(IReadOnlyList<SyncRule> conflictingRules) => new()
    {
        ShouldProject = false,
        IsConflict = true,
        ConflictingRules = conflictingRules
    };
}
