// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// Thrown when persisting a synchronisation page's Metaverse Object changes to the database fails.
/// It wraps the underlying database exception with structured context (which page, which Connected System,
/// a sample of the affected Metaverse Object ids) so the failure is attributable on the Activity and in the
/// logs, rather than surfacing as an anonymous "unhandled exception whilst executing sync run".
/// <para>
/// This is a hard failure by design: a page whose write did not complete leaves the run unable to guarantee
/// integrity, so the activity is failed rather than the run continuing with unknown state (see the
/// Synchronisation Integrity requirements). The value it adds over the previous raw exception is diagnosis,
/// not recovery.
/// </para>
/// </summary>
public class SyncPersistenceException : Exception
{
    /// <summary>The 1-based page number whose persistence failed.</summary>
    public int Page { get; }

    /// <summary>The total number of pages in the run.</summary>
    public int TotalPages { get; }

    /// <summary>The name of the Connected System being synchronised.</summary>
    public string ConnectedSystemName { get; }

    public SyncPersistenceException(string message, Exception innerException, int page, int totalPages, string connectedSystemName)
        : base(message, innerException)
    {
        Page = page;
        TotalPages = totalPages;
        ConnectedSystemName = connectedSystemName;
    }

    /// <summary>
    /// Builds the structured failure message. Kept as a pure static method so the exact wording is unit-testable
    /// without having to provoke a real persistence failure through the sync engine.
    /// </summary>
    /// <param name="page">The 1-based page number whose persistence failed.</param>
    /// <param name="totalPages">The total number of pages in the run.</param>
    /// <param name="connectedSystemName">The Connected System being synchronised.</param>
    /// <param name="affectedMetaverseObjectIdSample">A capped sample of the Metaverse Object ids still pending when the failure occurred; may be empty.</param>
    public static string BuildMessage(int page, int totalPages, string connectedSystemName, IReadOnlyList<Guid> affectedMetaverseObjectIdSample)
    {
        var sample = affectedMetaverseObjectIdSample.Count > 0
            ? $" Affected Metaverse Object id(s) include: {string.Join(", ", affectedMetaverseObjectIdSample)}."
            : string.Empty;

        return $"Failed to persist synchronisation changes on page {page} of {totalPages} for Connected System " +
               $"'{connectedSystemName}'.{sample}";
    }
}
