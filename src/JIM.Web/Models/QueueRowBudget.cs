// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// How many rows the Activity tab's Queued section shows (#2549): as many as fit the window while In progress stays
/// in view without scrolling, and never fewer than <see cref="MinimumRows"/>.
/// </summary>
/// <remarks>
/// Recalculated when the window is resized, never on a data refresh, so rows do not come and go while someone reads.
/// </remarks>
public static class QueueRowBudget
{
    /// <summary>The fewest rows shown however short the window, so what is about to run is always visible.</summary>
    public const int MinimumRows = 3;

    /// <summary>The budget used before the page has been measured (prerendering, or a measurement that failed).</summary>
    public const int ProvisionalRows = 6;

    /// <param name="viewportHeight">The window's height in CSS pixels.</param>
    /// <param name="rowsTop">How far down the window the Queued section's first row starts.</param>
    /// <param name="inProgressReserve">The height to keep free below Queued for In progress's header and first row.</param>
    /// <param name="rowHeight">The height of one Queued row; zero or less when it could not be measured.</param>
    public static int Calculate(double viewportHeight, double rowsTop, double inProgressReserve, double rowHeight)
    {
        if (rowHeight <= 0)
            return ProvisionalRows;

        var fits = (int)Math.Floor((viewportHeight - rowsTop - inProgressReserve) / rowHeight);
        return Math.Max(fits, MinimumRows);
    }
}
