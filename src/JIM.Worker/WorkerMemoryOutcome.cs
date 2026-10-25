// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Operations;

namespace JIM.Worker;

/// <summary>
/// What the Worker's memory guard says, and how a run that was short of memory says so on its Activity.
/// </summary>
internal static class WorkerMemoryOutcome
{
    private static string Remedy =>
        $"At {Percent(MemoryPressure.StopShare)} the Worker stops its running work rather than be stopped part-way through; give JIM more memory, or run less at once.";

    /// <summary>What a run's Activity says when memory was low while it ran.</summary>
    internal static string WarningText(MemoryReading reading) =>
        $"Memory was low while this ran: {MemoryPressure.Describe(reading, "the Worker")}. {Remedy}";

    /// <summary>What a run's Activity says when the guard stopped it.</summary>
    internal static string StopText(MemoryReading reading) =>
        $"The Worker stopped this run because its memory was nearly exhausted: {MemoryPressure.Describe(reading, "the Worker")}. " +
        "Had it carried on, the operating system would have stopped the Worker part-way through. Give JIM more memory, or run less at once, then run it again.";

    /// <summary>What a run's Activity says when the run did not stop when asked, and the Worker restarted itself instead.</summary>
    internal static string AbortText(MemoryReading reading) =>
        $"The Worker stopped this run and restarted itself because its memory was nearly exhausted ({MemoryPressure.Describe(reading, "the Worker")}) " +
        "and the run did not stop when asked. Had it carried on, the operating system would have stopped the Worker without a word. " +
        "Give JIM more memory, or run less at once, then run it again.";

    /// <summary>
    /// Adds a warning to an Activity that has ended, after anything it already says. A completed Activity becomes
    /// Complete with warning; any other verdict, a failure above all, is left as it is.
    /// </summary>
    internal static async Task AddWarningAsync(JimApplication jim, Activity activity, string warning)
    {
        activity.WarningMessage = string.IsNullOrEmpty(activity.WarningMessage) ? warning : $"{activity.WarningMessage}\n\n{warning}";
        if (activity.Status == ActivityStatus.Complete)
            activity.Status = ActivityStatus.CompleteWithWarning;

        await jim.Activities.UpdateActivityAsync(activity);
    }

    private static string Percent(double share) => $"{(int)Math.Round(share * 100)}%";
}
