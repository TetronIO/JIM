// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// Thrown when writing a synchronisation flush's history (its Run Profile Execution Items, outcomes, change records,
/// causal edges, match candidates and stat counters) fails, and the failure is found away from the flush that caused
/// it (#2874): the run carries on with the next page while a page's history is written, so a failure surfaces at the
/// next page, and this names the page whose history it was. The write is one transaction, so none of that page's
/// history was written; its synchronisation changes were, and the run stops before saving any more.
/// </summary>
/// <remarks>
/// A hard failure, like the <see cref="SyncPersistenceException"/> it derives from: the Activity fails with this
/// message and the underlying exception, rather than the run continuing with history missing.
/// </remarks>
public class SynchronisationHistoryPersistenceException : SyncPersistenceException
{
    public SynchronisationHistoryPersistenceException(string message, Exception innerException, int page, int totalPages, string connectedSystemName)
        : base(message, innerException, page, totalPages, connectedSystemName)
    {
    }

    /// <summary>
    /// Builds the failure message. A pure static method, so the wording is unit-testable without provoking a failure.
    /// </summary>
    /// <param name="page">The 1-based page whose history was not written, or 0 for history written outside the page
    /// loop.</param>
    /// <param name="totalPages">The pages the run expected.</param>
    /// <param name="connectedSystemName">The Connected System being synchronised.</param>
    public static string BuildMessage(int page, int totalPages, string connectedSystemName)
    {
        var what = page > 0
            ? $"page {page} of {totalPages}"
            : "the steps after the pages";

        return $"Failed to record the execution items and change history of {what} for Connected System '{connectedSystemName}'. " +
               "Those synchronisation changes were saved; their history was not, and the run stopped before saving any further changes.";
    }
}
