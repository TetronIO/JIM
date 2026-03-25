using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Sync;

/// <summary>
/// The result of evaluating whether a CSO should join an existing MVO.
/// Returned by <c>ISyncEngine.EvaluateJoin</c>.
/// </summary>
public readonly struct JoinDecision
{
    /// <summary>
    /// Whether a join should be established.
    /// </summary>
    public bool ShouldJoin { get; init; }

    /// <summary>
    /// The MVO to join to, when <see cref="ShouldJoin"/> is true.
    /// </summary>
    public MetaverseObject? TargetMvo { get; init; }

    /// <summary>
    /// If non-null, the join attempt failed with this error type.
    /// The orchestrator should create an error RPEI with the corresponding message.
    /// </summary>
    public JoinError? Error { get; init; }

    /// <summary>
    /// Creates a decision indicating a successful join.
    /// </summary>
    public static JoinDecision Join(MetaverseObject mvo) => new()
    {
        ShouldJoin = true,
        TargetMvo = mvo
    };

    /// <summary>
    /// Creates a decision indicating no join candidate was found.
    /// </summary>
    public static JoinDecision NoMatch() => new() { ShouldJoin = false };

    /// <summary>
    /// Creates a decision indicating the join failed due to a constraint violation.
    /// </summary>
    public static JoinDecision Failed(JoinErrorType errorType, string message) => new()
    {
        ShouldJoin = false,
        Error = new JoinError(errorType, message)
    };
}

/// <summary>
/// Describes a join failure.
/// </summary>
public sealed record JoinError(JoinErrorType ErrorType, string Message);

/// <summary>
/// The type of join error that occurred.
/// </summary>
public enum JoinErrorType
{
    /// <summary>Multiple MVOs matched the matching rules.</summary>
    AmbiguousMatch,
    /// <summary>The target MVO already has a CSO from this connected system.</summary>
    ExistingJoin
}
