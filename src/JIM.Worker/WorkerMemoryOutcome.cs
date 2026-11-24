// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Localisation;
using JIM.Models.Operations;

namespace JIM.Worker;

/// <summary>
/// What the Worker's memory guard says, and how a run that was short of memory says so on its Activity.
/// </summary>
internal static class WorkerMemoryOutcome
{
    /// <summary>What a run's Activity says when memory was low while it ran.</summary>
    internal static JimMessage Warning(MemoryReading reading) => ActivityMessage.MemoryWarning(reading);

    /// <summary>What a run's Activity says when the guard stopped it.</summary>
    internal static JimMessage Stop(MemoryReading reading) => ActivityMessage.MemoryStop(reading);

    /// <summary>What a run's Activity says when the run did not stop when asked, and the Worker restarted itself instead.</summary>
    internal static JimMessage Abort(MemoryReading reading) => ActivityMessage.MemoryAbort(reading);

    /// <summary><see cref="Warning"/> in British English, for the log.</summary>
    internal static string WarningText(MemoryReading reading) => Warning(reading).ToEnglish();

    /// <summary><see cref="Stop"/> in British English, for the log.</summary>
    internal static string StopText(MemoryReading reading) => Stop(reading).ToEnglish();

    /// <summary><see cref="Abort"/> in British English, for the log.</summary>
    internal static string AbortText(MemoryReading reading) => Abort(reading).ToEnglish();

    /// <summary>
    /// Adds a warning to an Activity that has ended, after anything it already says. A completed Activity becomes
    /// Complete with warning; any other verdict, a failure above all, is left as it is.
    /// </summary>
    internal static Task AddWarningAsync(JimApplication jim, Activity activity, string warning) =>
        AddWarningAsync(jim, activity, ActivityMessage.Create(nameof(ActivityMessages.Verbatim), warning));

    /// <summary>
    /// Adds a warning to an Activity that has ended, after anything it already says, in the reader's language where
    /// the Activity's own warning is a message. See the overload taking text.
    /// </summary>
    internal static async Task AddWarningAsync(JimApplication jim, Activity activity, JimMessage warning)
    {
        activity.WarningMessageDetail = string.IsNullOrEmpty(activity.WarningMessage)
            ? warning
            : ActivityMessage.Paragraphs(activity.WarningMessageDetail ?? ActivityMessage.Create(nameof(ActivityMessages.Verbatim), activity.WarningMessage), warning);
        if (activity.Status == ActivityStatus.Complete)
            activity.Status = ActivityStatus.CompleteWithWarning;

        await jim.Activities.UpdateActivityAsync(activity);
    }
}
