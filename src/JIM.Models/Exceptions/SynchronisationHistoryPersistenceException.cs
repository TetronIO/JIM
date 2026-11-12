// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// Thrown when writing a synchronisation flush's history (its Run Profile Execution Items, outcomes, change records,
/// causal edges, match candidates and stat counters) fails, and the failure is found away from the flush that caused
/// it (#2874): the run carries on with the next page while a page's history is written, so a failure surfaces at the
/// next page, and this names the page whose history it was. The write is one transaction, so none of that page's
/// history was written; its synchronisation changes were.
/// </summary>
/// <remarks>
/// <para>
/// A hard failure, like the <see cref="SyncPersistenceException"/> it derives from: the Activity fails with this
/// message and the underlying exception, rather than the run continuing with history missing.
/// </para>
/// <para>
/// A page's history is written while the next page loads, is processed and saves its changes (#2924), so the next page
/// may have saved its changes by the time the failure is found: the run then stops, and no page beyond the next saves
/// anything. When the failure is found as the next page hands its own history off, that history is still written, so a
/// fault in one page's history costs that page's history alone; <see cref="NextPage"/> and
/// <see cref="NextPageOutcome"/> say what became of it, and <see cref="NextPageFailure"/> carries its own failure if it
/// had one. <see cref="Exception.InnerException"/> is always the first failure's cause.
/// </para>
/// </remarks>
public class SynchronisationHistoryPersistenceException : SyncPersistenceException
{
    public SynchronisationHistoryPersistenceException(string message, Exception innerException, int page, int totalPages, string connectedSystemName)
        : base(message, innerException, page, totalPages, connectedSystemName)
    {
    }

    /// <summary>
    /// A failure found as the next page handed its history off, with what became of that page's history.
    /// </summary>
    public SynchronisationHistoryPersistenceException(string message, Exception innerException, int page, int totalPages, string connectedSystemName,
        int nextPage, SynchronisationHistoryNextPageOutcome nextPageOutcome, Exception? nextPageFailure)
        : base(message, innerException, page, totalPages, connectedSystemName)
    {
        NextPage = nextPage;
        NextPageOutcome = nextPageOutcome;
        NextPageFailure = nextPageFailure;
    }

    /// <summary>
    /// The page that had saved its changes when the failure was found, as it handed its own history off; null when the
    /// failure was found elsewhere.
    /// </summary>
    public int? NextPage { get; }

    /// <summary>What became of <see cref="NextPage"/>'s history; null alongside it.</summary>
    public SynchronisationHistoryNextPageOutcome? NextPageOutcome { get; }

    /// <summary>
    /// Why <see cref="NextPage"/>'s history could not be written, when its write failed too
    /// (<see cref="SynchronisationHistoryNextPageOutcome.WriteFailed"/>); otherwise null.
    /// </summary>
    public Exception? NextPageFailure { get; }

    /// <summary>
    /// Builds the failure message. A pure static method, so the wording is unit-testable without provoking a failure.
    /// </summary>
    /// <param name="page">The 1-based page whose history was not written, or 0 for history written outside the page
    /// loop.</param>
    /// <param name="totalPages">The pages the run expected.</param>
    /// <param name="connectedSystemName">The Connected System being synchronised.</param>
    public static string BuildMessage(int page, int totalPages, string connectedSystemName)
    {
        // A page's history is written while the next page saves its changes (#2924), so a page's failure can be found
        // once the next page has saved them; the run never goes further than that.
        return page > 0
            ? $"Failed to record the execution items and change history of page {page} of {totalPages} for Connected System " +
              $"'{connectedSystemName}'. Those synchronisation changes were saved; their history was not, and the run stopped " +
              (page < totalPages
                  ? $"without saving the changes of any page after page {page + 1}."
                  : "before saving any further changes.")
            : "Failed to record the execution items and change history of the steps after the pages for Connected System " +
              $"'{connectedSystemName}'. Those synchronisation changes were saved; their history was not, and the run stopped " +
              "before saving any further changes.";
    }

    /// <summary>
    /// Builds the sentence the failure message ends with when the failure was found as the next page handed its history
    /// off: what became of that page's history. A pure static method, so the wording is unit-testable.
    /// </summary>
    /// <param name="nextPage">The next page.</param>
    /// <param name="totalPages">The pages the run expected.</param>
    /// <param name="outcome">What became of the next page's history.</param>
    /// <param name="detail">Why the next page's write failed (<see cref="SynchronisationHistoryNextPageOutcome.WriteFailed"/>),
    /// or what it refers to of the failed history (<see cref="SynchronisationHistoryNextPageOutcome.NotWrittenDependsOnFailedPage"/>);
    /// unused when it was written.</param>
    public static string BuildNextPageMessage(int nextPage, int totalPages, SynchronisationHistoryNextPageOutcome outcome, string? detail)
    {
        var saved = $"Page {nextPage} of {totalPages} had saved its changes when the failure was found; ";
        return outcome switch
        {
            SynchronisationHistoryNextPageOutcome.Written => saved + "its history was written.",
            SynchronisationHistoryNextPageOutcome.WriteFailed => saved + $"its history could not be written either: {detail}",
            SynchronisationHistoryNextPageOutcome.NotWrittenDependsOnFailedPage =>
                saved + $"its history was not written, because it refers to the history that failed ({detail}).",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown outcome for the next page's history.")
        };
    }
}
