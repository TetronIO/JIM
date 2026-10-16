// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// How a Connected System Object's unresolved references stand now against what one import left (#959). Only counts
/// are compared: an item records how many references its run left unresolved, not which, so the page can say that
/// fewer remain, never which ones resolved.
/// </summary>
public enum UnresolvedReferenceSinceRun
{
    /// <summary>The item predates the count, so there is nothing to compare with.</summary>
    RunCountUnknown = 0,

    /// <summary>As many remain as the run left.</summary>
    Unchanged = 1,

    /// <summary>Fewer remain: some have resolved, or been removed from the source, since.</summary>
    Fewer = 2,

    /// <summary>More remain: a later import left further references unresolved.</summary>
    More = 3,

    /// <summary>None remain.</summary>
    NoneNow = 4
}
