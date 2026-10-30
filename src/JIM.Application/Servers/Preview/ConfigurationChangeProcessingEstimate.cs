// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Servers.Preview;

/// <summary>
/// About how long a Connected System's next synchronisation takes to process every object of an Object Type that a saved
/// configuration change made it process in full (#2615), stated before the save so the administrator decides with a
/// number in front of them.
/// </summary>
/// <remarks>
/// Not <see cref="FullSynchronisationDurationEstimate"/>, which estimates a preview and never promises more than a
/// reference rate. This is the real run, so it is estimated at exactly the speed the system's own last Full
/// Synchronisation went, with no floor or ceiling: the measurement is the only evidence there is, and a made-up rate
/// would be worse than none. A system that has never completed one has no speed to estimate from, so the estimate is
/// absent rather than invented.
/// </remarks>
internal static class ConfigurationChangeProcessingEstimate
{
    /// <param name="objects">How many objects the run will process: the Object Type's.</param>
    /// <param name="lastRunObjects">How many objects the last completed Full Synchronisation went through, if there was one:
    /// the system's population, since a Full Synchronisation goes through every object.</param>
    /// <param name="lastRunTime">How long that run took, if there was one.</param>
    /// <returns>The estimate; <see cref="TimeSpan.Zero"/> when there are no objects to process; null when there is no
    /// last run to take a speed from (none completed, it recorded no time, or it went through no objects).</returns>
    internal static TimeSpan? For(int objects, int? lastRunObjects, TimeSpan? lastRunTime)
    {
        if (lastRunObjects is not > 0 || lastRunTime is not { TotalSeconds: > 0 } time)
            return null;

        if (objects <= 0)
            return TimeSpan.Zero;

        // The speed is objects over seconds, and the estimate is objects over that speed; multiplying first keeps the
        // two divisions from compounding into a rounding error on a figure that is meant to read as round.
        return TimeSpan.FromSeconds((double)objects * time.TotalSeconds / lastRunObjects.Value);
    }
}
