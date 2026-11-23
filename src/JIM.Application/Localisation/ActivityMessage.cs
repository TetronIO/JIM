// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Resources;
using JIM.Models.Activities;
using JIM.Models.Localisation;
using JIM.Models.Staging.DTOs;

namespace JIM.Application.Localisation;

/// <summary>
/// Builds the messages an Activity or a Run Profile Execution Item stores beside its English (#3039) from the
/// <see cref="ApplicationMessageCatalogues.Activities"/> catalogue, so a failure reads
/// <c>ActivityMessage.Create(nameof(ActivityMessages.AllItemsFailed))</c>. Public because the Worker and the Scheduler write
/// Activity text too, while the portal that renders it references this assembly alone. Pass identifiers as text: a number
/// argument is shown with the reader's digit grouping, which an id must not have.
/// </summary>
public static partial class ActivityMessage
{
    /// <summary>A message with no count.</summary>
    public static JimMessage Create(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ApplicationMessageCatalogues.Activities, key, arguments);

    /// <summary>A counted message; <paramref name="oneKey"/> is the singular's key.</summary>
    public static JimMessage Counted(string oneKey, long count, params MessageArgument[] arguments) =>
        JimMessage.Counted(ApplicationMessageCatalogues.Activities, oneKey, count, arguments);

    /// <summary>
    /// A count shown as plain digits with no grouping, as English wrote it before there were messages (<c>$"{count}"</c>),
    /// where <see cref="MessageArgument"/>'s own number would group thousands for the reader.
    /// </summary>
    public static MessageArgument Plain(long count) => count.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a hierarchy refresh changed, in brief: <see cref="HierarchyRefreshResult.GetSummary"/> as a message.</summary>
    public static JimMessage HierarchyRefreshSummary(HierarchyRefreshResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var parts = new List<JimMessage>();
        var added = result.AddedPartitions.Count + result.AddedContainers.Count;
        var removed = result.RemovedPartitions.Count + result.RemovedContainers.Count;
        var updated = result.RenamedPartitions.Count + result.RenamedContainers.Count + result.MovedContainers.Count;
        var partitionsSelected = result.PartitionsSelectedForMovedContainers.Count;

        if (added > 0)
            parts.Add(Create(nameof(ActivityMessages.HierarchyAdded), Plain(added)));
        if (removed > 0)
            parts.Add(Create(nameof(ActivityMessages.HierarchyRemoved), Plain(removed)));
        if (updated > 0)
            parts.Add(Create(nameof(ActivityMessages.HierarchyUpdated), Plain(updated)));
        if (partitionsSelected > 0)
            parts.Add(Counted(nameof(ActivityMessages.HierarchyPartitionsSelectedOne), partitionsSelected, Plain(partitionsSelected)));

        return Listed(parts) ?? Create(nameof(ActivityMessages.HierarchyNoChanges));
    }

    /// <summary>
    /// What went wrong, as an argument of a longer message: the message an exception carries where it says exactly what its
    /// text says, else its text.
    /// </summary>
    public static MessageArgument CauseOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var detail = exception.DetailOf();
        return detail is not null && detail.ToEnglish() == exception.Message ? detail : exception.Message;
    }

    /// <summary>
    /// The message for the reason an Activity records when a Schedule step did not run, given the English it was stored as
    /// (<see cref="JIM.Models.Scheduling.ScheduleStepNotRunReasons"/>); null for any other text, such as a step's progress.
    /// </summary>
    public static JimMessage? StepNotRunReason(string? english) => english switch
    {
        JIM.Models.Scheduling.ScheduleStepNotRunReasons.EarlierStepStoppedSchedule => Create(nameof(ActivityMessages.StepNotRunEarlierStepStopped)),
        JIM.Models.Scheduling.ScheduleStepNotRunReasons.ScheduleCouldNotStart => Create(nameof(ActivityMessages.StepNotRunScheduleCouldNotStart)),
        JIM.Models.Scheduling.ScheduleStepNotRunReasons.ExecutionCancelled => Create(nameof(ActivityMessages.StepNotRunExecutionCancelled)),
        _ => null
    };

    /// <summary>Two messages one after the other, separated by a space.</summary>
    public static JimMessage Spaced(JimMessage first, JimMessage second) =>
        Create(nameof(ActivityMessages.JoinedBySpace), first, second);

    /// <summary>Messages one after the other, each separated from the last by a space; null when there are none.</summary>
    public static JimMessage? Spaced(IEnumerable<JimMessage> messages) =>
        messages.Aggregate((JimMessage?)null, (joined, next) => joined is null ? next : Spaced(joined, next));

    /// <summary>Messages one after the other, each separated from the last by a comma and a space; null when there are none.</summary>
    public static JimMessage? Listed(IEnumerable<JimMessage> messages) =>
        messages.Aggregate((JimMessage?)null, (joined, next) => joined is null ? next : Create(nameof(ActivityMessages.JoinedByComma), joined, next));

    /// <summary>
    /// Adds a warning to the Activity's, on a line of its own beneath any it already carries, keeping the English and the
    /// message in step. A warning the Activity holds as English only is kept as it stands, inside the message.
    /// </summary>
    public static void AddWarning(Activity activity, JimMessage warning)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(warning);

        if (string.IsNullOrEmpty(activity.WarningMessage))
        {
            activity.WarningMessageDetail = warning;
            return;
        }

        var existing = activity.WarningMessageDetail ?? Create(nameof(ActivityMessages.Verbatim), activity.WarningMessage);
        activity.WarningMessageDetail = Create(nameof(ActivityMessages.JoinedByLine), existing, warning);
    }

    /// <summary>
    /// Records the error an exception carries on an execution item: its English, as the item has always held it, and the
    /// message behind it where the exception carries one that says exactly the same.
    /// </summary>
    public static void SetError(ActivityRunProfileExecutionItem item, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(exception);

        item.ErrorMessage = exception.Message;
        var detail = exception.DetailOf();
        item.ErrorMessageDetailJson = detail is not null && detail.ToEnglish() == exception.Message
            ? MessageJson.Serialise(detail)
            : null;
    }
}
