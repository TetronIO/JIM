// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A Pending Export's retry state: its status and the columns that record and explain its failed attempts (#2083).
/// Taken from a batch as it stood just before the batch was marked Executing, so that an export the run never got to
/// can be put back exactly as it was, and one that failed can be given one failed attempt counted from where it stood.
/// </summary>
/// <param name="PendingExportId">The Pending Export this state belongs to.</param>
/// <param name="Status">The status to write.</param>
/// <param name="ErrorCount">How many attempts in a row have failed.</param>
/// <param name="FailingSince">When the current run of failed attempts began, or null when the last attempt did not fail.</param>
/// <param name="LastAttemptedAt">When the export was last attempted, or null when it never has been.</param>
/// <param name="LastErrorMessage">Why the last attempt failed, or null.</param>
/// <param name="LastErrorStackTrace">The last failure's stack trace, or null.</param>
public sealed record PendingExportRetryState(
    Guid PendingExportId,
    PendingExportStatus Status,
    int ErrorCount,
    DateTime? FailingSince,
    DateTime? LastAttemptedAt,
    string? LastErrorMessage,
    string? LastErrorStackTrace)
{
    /// <summary>
    /// The retry state <paramref name="pendingExport"/> holds now.
    /// </summary>
    public static PendingExportRetryState Of(PendingExport pendingExport) => new(
        pendingExport.Id,
        pendingExport.Status,
        pendingExport.ErrorCount,
        pendingExport.FailingSince,
        pendingExport.LastAttemptedAt,
        pendingExport.LastErrorMessage,
        pendingExport.LastErrorStackTrace);

    /// <summary>
    /// The state after one more failed attempt (#2119): counted, with the error kept and the start of the run of
    /// failures kept from the first of them. Always <see cref="PendingExportStatus.Pending"/>: JIM never gives up on a
    /// Pending Export, so the next export run attempts it again, however many attempts have failed before.
    /// </summary>
    public PendingExportRetryState AfterFailedAttempt(string errorMessage, string? stackTrace, DateTime attemptedAt) => this with
    {
        Status = PendingExportStatus.Pending,
        ErrorCount = ErrorCount + 1,
        FailingSince = FailingSince ?? attemptedAt,
        LastAttemptedAt = attemptedAt,
        LastErrorMessage = errorMessage,
        LastErrorStackTrace = stackTrace
    };

    /// <summary>
    /// Writes this state onto <paramref name="pendingExport"/>, so its in-memory copy agrees with what was persisted.
    /// </summary>
    public void ApplyTo(PendingExport pendingExport)
    {
        pendingExport.Status = Status;
        pendingExport.ErrorCount = ErrorCount;
        pendingExport.FailingSince = FailingSince;
        pendingExport.LastAttemptedAt = LastAttemptedAt;
        pendingExport.LastErrorMessage = LastErrorMessage;
        pendingExport.LastErrorStackTrace = LastErrorStackTrace;
    }
}
