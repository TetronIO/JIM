// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Servers.Preview;

/// <summary>
/// About how long a Full Synchronisation preview will take (#1530), stated before a large one starts so the
/// administrator decides with a number in front of them.
/// </summary>
/// <remarks>
/// Never faster than <see cref="ReferenceObjectsPerSecond"/>. A routine Full Synchronisation skips the objects unchanged
/// since the last one, so its own speed overstates how fast every object can be evaluated, and a preview is asked for
/// exactly when configuration has changed and nothing will be skipped. A system whose last Full Synchronisation ran
/// slower than the reference is estimated at its own speed, because then the measurement is the better evidence.
/// </remarks>
internal static class FullSynchronisationDurationEstimate
{
    /// <summary>
    /// Objects evaluated per second on a reference host. A conservative placeholder until #1520's timing at 100,000
    /// objects measures the real figure, which then replaces it.
    /// </summary>
    internal const double ReferenceObjectsPerSecond = 50;

    /// <param name="objects">How many objects the preview will evaluate.</param>
    /// <param name="lastRunObjects">How many objects the last completed Full Synchronisation processed, if there was one.</param>
    /// <param name="lastRunTime">How long that run took, if there was one.</param>
    internal static TimeSpan For(int objects, int? lastRunObjects, TimeSpan? lastRunTime)
    {
        var rate = ReferenceObjectsPerSecond;
        if (lastRunObjects is > 0 && lastRunTime is { TotalSeconds: > 0 } time)
            rate = Math.Min(rate, lastRunObjects.Value / time.TotalSeconds);

        return TimeSpan.FromSeconds(objects / rate);
    }
}
