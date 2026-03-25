using JIM.Models.Core;

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
    /// Creates a decision indicating a new MVO should be projected.
    /// </summary>
    public static ProjectionDecision Project(MetaverseObjectType mvoType) => new()
    {
        ShouldProject = true,
        MetaverseObjectType = mvoType
    };

    /// <summary>
    /// Creates a decision indicating no projection should occur.
    /// </summary>
    public static ProjectionDecision NoProjection() => new() { ShouldProject = false };
}
