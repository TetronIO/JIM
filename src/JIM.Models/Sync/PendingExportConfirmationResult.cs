using JIM.Models.Transactional;

namespace JIM.Models.Sync;

/// <summary>
/// The result of evaluating whether pending exports have been confirmed by a CSO's current state.
/// Returned by <c>ISyncEngine.EvaluatePendingExportConfirmation</c>.
/// </summary>
public readonly struct PendingExportConfirmationResult
{
    /// <summary>
    /// Pending exports that should be deleted (all attribute changes confirmed).
    /// </summary>
    public IReadOnlyList<PendingExport> ToDelete { get; init; }

    /// <summary>
    /// Pending exports that should be updated (partial confirmation or complete failure).
    /// </summary>
    public IReadOnlyList<PendingExport> ToUpdate { get; init; }

    /// <summary>
    /// True if any pending exports were evaluated.
    /// </summary>
    public bool HasResults => ToDelete.Count > 0 || ToUpdate.Count > 0;

    /// <summary>
    /// Creates a result with no pending exports evaluated.
    /// </summary>
    public static PendingExportConfirmationResult None() => new()
    {
        ToDelete = [],
        ToUpdate = []
    };

    /// <summary>
    /// Creates a result with the given confirmed and unconfirmed exports.
    /// </summary>
    public static PendingExportConfirmationResult Create(
        IReadOnlyList<PendingExport> toDelete,
        IReadOnlyList<PendingExport> toUpdate) => new()
    {
        ToDelete = toDelete,
        ToUpdate = toUpdate
    };
}
