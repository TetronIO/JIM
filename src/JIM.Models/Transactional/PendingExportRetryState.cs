// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A Pending Export's retry state: its status and the columns that schedule and explain its next attempt (#2083). Taken
/// from a batch as it stood just before the batch was marked Executing, so that an export the run never got to can be
/// put back exactly as it was, and one that failed can be given one failed attempt counted from where it stood.
/// </summary>
/// <param name="PendingExportId">The Pending Export this state belongs to.</param>
/// <param name="Status">The status to write.</param>
/// <param name="ErrorCount">How many attempts have failed.</param>
/// <param name="LastAttemptedAt">When the export was last attempted, or null when it never has been.</param>
/// <param name="NextRetryAt">When the export may next be attempted, or null when it may be attempted at once.</param>
/// <param name="LastErrorMessage">Why the last attempt failed, or null.</param>
/// <param name="LastErrorStackTrace">The last failure's stack trace, or null.</param>
public sealed record PendingExportRetryState(
    Guid PendingExportId,
    PendingExportStatus Status,
    int ErrorCount,
    DateTime? LastAttemptedAt,
    DateTime? NextRetryAt,
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
        pendingExport.LastAttemptedAt,
        pendingExport.NextRetryAt,
        pendingExport.LastErrorMessage,
        pendingExport.LastErrorStackTrace);

    /// <summary>
    /// Writes this state onto <paramref name="pendingExport"/>, so its in-memory copy agrees with what was persisted.
    /// </summary>
    public void ApplyTo(PendingExport pendingExport)
    {
        pendingExport.Status = Status;
        pendingExport.ErrorCount = ErrorCount;
        pendingExport.LastAttemptedAt = LastAttemptedAt;
        pendingExport.NextRetryAt = NextRetryAt;
        pendingExport.LastErrorMessage = LastErrorMessage;
        pendingExport.LastErrorStackTrace = LastErrorStackTrace;
    }
}
