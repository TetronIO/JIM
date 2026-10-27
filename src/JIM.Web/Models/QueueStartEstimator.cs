// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Estimates when each queued task will start (#2549), for the Activity tab's Queued section.
/// </summary>
/// <remarks>
/// Each slot starts when the one before it is expected to finish, and lasts as long as its slowest task's typical
/// (median) duration. An estimate is given only while every input behind it is known: an unknown remaining time for
/// the work running now, a task that is not a Run Profile execution, or a Run Profile with no successful history
/// leaves that slot's own start intact where it can be known, and every later one blank. A blank is honest; a time
/// built on a guess reads as a forecast.
/// </remarks>
public static class QueueStartEstimator
{
    /// <param name="slots">The queue as the worker will start it, from <see cref="QueueRunSlot.Plan"/>.</param>
    /// <param name="runningRemaining">How long the work running now is expected to take to finish: zero when nothing
    /// is running, null when something is running and its remaining time is not known.</param>
    /// <param name="medianDurations">Each Run Profile's typical duration, by Run Profile id.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>The estimated start (UTC) of every task in <paramref name="slots"/>, or null where none can be given.</returns>
    public static Dictionary<Guid, DateTime?> Estimate(IReadOnlyList<QueueRunSlot> slots, TimeSpan? runningRemaining,
        IReadOnlyDictionary<int, TimeSpan> medianDurations, DateTime now)
    {
        var starts = new Dictionary<Guid, DateTime?>();
        DateTime? cursor = runningRemaining.HasValue ? now + runningRemaining.Value : null;

        foreach (var slot in slots)
        {
            foreach (var task in slot.Tasks)
                starts[task.Id] = cursor;

            cursor = cursor + SlotDuration(slot, medianDurations);
        }

        return starts;
    }

    /// <summary>
    /// How much longer a running task is expected to take: what is left of its Run Profile's typical duration, but
    /// never less than its current step still needs. Null where that cannot be said honestly.
    /// </summary>
    /// <remarks>
    /// The step estimate (from the Activity's live rate) describes the step running now and nothing after it, so it
    /// is a floor rather than an answer: without a typical duration to say how much run remains beyond the step, the
    /// result is unknown. Once a run has outlasted its typical duration, the step estimate is all there is to go on.
    /// </remarks>
    /// <param name="started">When the worker started the task (UTC), or null if it has not.</param>
    /// <param name="typicalDuration">The Run Profile's typical (median) duration, or null if it has none.</param>
    /// <param name="stepSecondsRemaining">The current step's estimated seconds remaining, or null if unknown.</param>
    /// <param name="now">The current time (UTC).</param>
    public static TimeSpan? Remaining(DateTime? started, TimeSpan? typicalDuration, double? stepSecondsRemaining, DateTime now)
    {
        if (started is not { } startedAt || typicalDuration is not { } typical)
            return null;

        var step = stepSecondsRemaining is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        var left = typical - (now - startedAt);

        if (left <= TimeSpan.Zero)
            return step;

        return step is { } stepLeft && stepLeft > left ? stepLeft : left;
    }

    /// <summary>
    /// When the work running now is expected to be done: the longest of the running tasks' remaining times, zero when
    /// nothing is running, and unknown if any of them is.
    /// </summary>
    public static TimeSpan? LongestRemaining(IEnumerable<TimeSpan?> remaining)
    {
        var longest = TimeSpan.Zero;
        foreach (var value in remaining)
        {
            if (value is not { } known)
                return null;

            if (known > longest)
                longest = known;
        }

        return longest;
    }

    /// <summary>The slot's slowest task's typical duration, or null if any of its tasks has none.</summary>
    private static TimeSpan? SlotDuration(QueueRunSlot slot, IReadOnlyDictionary<int, TimeSpan> medianDurations)
    {
        var longest = TimeSpan.Zero;
        foreach (var runProfileId in slot.Tasks.Select(t => t.ConnectedSystemRunProfileId))
        {
            if (runProfileId is not { } id || !medianDurations.TryGetValue(id, out var duration))
                return null;

            if (duration > longest)
                longest = duration;
        }

        return longest;
    }
}
