// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// What became of the next page's history when a page's history write failed and the failure was found as the next page
/// handed its own history off (#2924): by then the next page had saved its changes.
/// </summary>
public enum SynchronisationHistoryNextPageOutcome
{
    /// <summary>The next page's history was written.</summary>
    Written,

    /// <summary>The next page's history was written and that write failed too.</summary>
    WriteFailed,

    /// <summary>
    /// The next page's history was not written, because it refers to history only the failed write would have written.
    /// </summary>
    NotWrittenDependsOnFailedPage
}
