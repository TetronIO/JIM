// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;
using JIM.Models.Resources;

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
public class SynchronisationHistoryPersistenceException : SyncPersistenceException, IJimMessageCarrier
{
    public SynchronisationHistoryPersistenceException(string message, Exception innerException, int page, int totalPages, string connectedSystemName)
        : base(message, innerException, page, totalPages, connectedSystemName)
    {
    }

    /// <summary>The failure as a message the portal shows in the reader's language; its English is <see cref="Exception.Message"/>.</summary>
    public SynchronisationHistoryPersistenceException(JimMessage detail, Exception innerException, int page, int totalPages, string connectedSystemName)
        : base(detail.ToEnglish(), innerException, page, totalPages, connectedSystemName)
    {
        Detail = detail;
    }

    /// <summary>A failure found as the next page handed its history off, as a message.</summary>
    public SynchronisationHistoryPersistenceException(JimMessage detail, Exception innerException, int page, int totalPages, string connectedSystemName,
        int nextPage, SynchronisationHistoryNextPageOutcome nextPageOutcome, Exception? nextPageFailure)
        : this(detail, innerException, page, totalPages, connectedSystemName)
    {
        NextPage = nextPage;
        NextPageOutcome = nextPageOutcome;
        NextPageFailure = nextPageFailure;
    }

    /// <summary>The failure as a message; null for one built from English only.</summary>
    public JimMessage? Detail { get; }

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
    public static string BuildMessage(int page, int totalPages, string connectedSystemName) =>
        Describe(page, totalPages, connectedSystemName).ToEnglish();

    /// <summary><see cref="BuildMessage"/> as a message.</summary>
    public static JimMessage Describe(int page, int totalPages, string connectedSystemName)
    {
        // A page's history is written while the next page saves its changes (#2924), so a page's failure can be found
        // once the next page has saved them; the run never goes further than that. Whether there was a next page is not
        // read from totalPages: that is an estimate (#2934), and a closing sweep page can follow one it called the last.
        return page > 0
            ? JimMessage.Create(MessageCatalogues.Sync, nameof(SyncMessages.HistoryFailedPage), page, totalPages, connectedSystemName)
            : JimMessage.Create(MessageCatalogues.Sync, nameof(SyncMessages.HistoryFailedAfterPages), connectedSystemName);
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
    public static string BuildNextPageMessage(int nextPage, int totalPages, SynchronisationHistoryNextPageOutcome outcome, string? detail) =>
        DescribeNextPage(nextPage, totalPages, outcome, detail).ToEnglish();

    /// <summary><see cref="BuildNextPageMessage"/> as a message; <paramref name="detail"/> is a message where it has one.</summary>
    public static JimMessage DescribeNextPage(int nextPage, int totalPages, SynchronisationHistoryNextPageOutcome outcome, MessageArgument? detail)
    {
        var key = outcome switch
        {
            SynchronisationHistoryNextPageOutcome.Written => nameof(SyncMessages.HistoryNextWritten),
            SynchronisationHistoryNextPageOutcome.WriteFailed => nameof(SyncMessages.HistoryNextWriteFailed),
            SynchronisationHistoryNextPageOutcome.NotWrittenDependsOnFailedPage => nameof(SyncMessages.HistoryNextDepends),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown outcome for the next page's history.")
        };

        return JimMessage.Create(MessageCatalogues.Sync, key, nextPage, totalPages, detail ?? string.Empty);
    }
}
