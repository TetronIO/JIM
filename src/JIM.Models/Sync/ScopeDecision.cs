using JIM.Models.Logic;

namespace JIM.Models.Sync;

/// <summary>
/// The result of evaluating which import sync rules a CSO is in scope for.
/// Returned by <c>ISyncEngine.EvaluateScope</c>.
/// </summary>
public readonly struct ScopeDecision
{
    /// <summary>
    /// The import sync rules for which the CSO is in scope.
    /// Empty if the CSO is out of scope for all rules.
    /// </summary>
    public IReadOnlyList<SyncRule> InScopeRules { get; init; }

    /// <summary>
    /// True if the CSO is in scope for at least one import sync rule.
    /// </summary>
    public bool IsInScope => InScopeRules.Count > 0;

    /// <summary>
    /// Creates a decision indicating the CSO is in scope for the given rules.
    /// </summary>
    public static ScopeDecision InScope(IReadOnlyList<SyncRule> rules) => new() { InScopeRules = rules };

    /// <summary>
    /// Creates a decision indicating the CSO is out of scope for all rules.
    /// </summary>
    public static ScopeDecision OutOfScope() => new() { InScopeRules = [] };
}
