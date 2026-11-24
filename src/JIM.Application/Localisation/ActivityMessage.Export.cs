// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Resources;
using JIM.Models.Localisation;
using JIM.Models.Transactional;

namespace JIM.Application.Localisation;

/// <summary>
/// What a finished export, synchronisation or run says on its Activity, as messages (#3039): the summary, the warnings
/// and how long the run took. The Worker's <c>ExportOutcomeMessage</c> and <c>ThroughputTracker</c> give each one's
/// British English, which is what these say.
/// </summary>
public static partial class ActivityMessage
{
    /// <summary>How long a run took and what it averaged, as the end of a summary; null where there was too little work to average.</summary>
    /// <param name="elapsedSeconds">How long the run took, in seconds.</param>
    /// <param name="processed">How many objects it processed.</param>
    public static JimMessage? ThroughputCompletion(double elapsedSeconds, int processed)
    {
        if (elapsedSeconds < 1 || processed <= 0)
            return null;

        var rate = processed / elapsedSeconds;
        return Create(nameof(ActivityMessages.ThroughputCompletion), Duration(elapsedSeconds), (long)Math.Round(rate, MidpointRounding.AwayFromZero));
    }

    private static JimMessage Duration(double totalSeconds)
    {
        if (totalSeconds < 60)
            return Create(nameof(ActivityMessages.DurationSeconds), (long)Math.Round(totalSeconds, MidpointRounding.AwayFromZero));
        if (totalSeconds < 3600)
        {
            var minutes = (int)(totalSeconds / 60);
            var seconds = (int)(totalSeconds % 60);
            return seconds > 0
                ? Create(nameof(ActivityMessages.DurationMinutesSeconds), minutes, seconds)
                : Create(nameof(ActivityMessages.DurationMinutes), minutes);
        }

        var hours = (int)(totalSeconds / 3600);
        var mins = (int)((totalSeconds % 3600) / 60);
        return mins > 0
            ? Create(nameof(ActivityMessages.DurationHoursMinutes), hours, mins)
            : Create(nameof(ActivityMessages.DurationHours), hours);
    }

    /// <summary>A synchronisation's summary.</summary>
    public static JimMessage SyncComplete(int objectsProcessed, MessageArgument throughput) =>
        Create(nameof(ActivityMessages.SyncComplete), objectsProcessed, throughput);

    /// <summary>An export's summary.</summary>
    /// <param name="writtenInPart">How many of the succeeded exports were written in part and are still waiting on a reference.</param>
    public static JimMessage ExportComplete(int succeeded, int failed, int deferred, MessageArgument throughput, int writtenInPart = 0) =>
        writtenInPart > 0
            ? Create(nameof(ActivityMessages.ExportCompleteWithPartial), succeeded, writtenInPart, failed, deferred, throughput)
            : Create(nameof(ActivityMessages.ExportComplete), succeeded, failed, deferred, throughput);

    /// <summary>A preview's summary.</summary>
    public static JimMessage ExportPreviewComplete(int pendingExports) =>
        Create(nameof(ActivityMessages.PreviewComplete), pendingExports);

    /// <summary>The warning for a change type an export withheld because it would exceed the Run Profile's limit.</summary>
    public static JimMessage ExportWithheld(PendingExportChangeType type, int limit, int pending)
    {
        var oneKey = type switch
        {
            PendingExportChangeType.Create => nameof(ActivityMessages.ExportWithheldCreatesOne),
            PendingExportChangeType.Update => nameof(ActivityMessages.ExportWithheldUpdatesOne),
            PendingExportChangeType.Delete => nameof(ActivityMessages.ExportWithheldDeletesOne),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported change type for a withheld-export warning.")
        };

        return Counted(oneKey, pending, limit, pending);
    }

    /// <summary>The warning when queued changes were withdrawn before the run because nothing authorised them any more.</summary>
    public static JimMessage ExportWithdrawn(int changes, int pendingExports)
    {
        var removal = pendingExports switch
        {
            0 => Create(nameof(ActivityMessages.ExportWithdrawnEnd)),
            _ => Counted(nameof(ActivityMessages.ExportWithdrawnRemovalOne), pendingExports, pendingExports)
        };

        return Counted(nameof(ActivityMessages.ExportWithdrawnOne), changes, changes, removal);
    }

    /// <summary>The warning when auto-confirmed exports were written but JIM could not record the values they wrote.</summary>
    public static JimMessage ExportUnrecorded(int exports) =>
        Counted(nameof(ActivityMessages.ExportUnrecordedOne), exports, exports);

    /// <summary>The warning for newly provisioned accounts that may be owed an Initial Password JIM could not record.</summary>
    public static JimMessage InitialPasswordsNotStaged(int accounts) =>
        Counted(nameof(ActivityMessages.InitialPasswordsNotStagedOne), accounts, accounts);

    /// <summary>The warning for exported objects whose declared Exchange recipient state JIM could not queue or report.</summary>
    public static JimMessage ExchangeChangesNotStaged(int objects) =>
        Counted(nameof(ActivityMessages.ExchangeChangesNotStagedOne), objects, objects);

    /// <summary>The warning for objects an export created or renamed whose Partition JIM could not record.</summary>
    public static JimMessage PartitionsNotRecorded(int objects) =>
        Counted(nameof(ActivityMessages.PartitionsNotRecordedOne), objects, objects);

    /// <summary>The warning for accounts whose enabled state an export wrote where JIM could not check for an Initial Password owed to them.</summary>
    public static JimMessage InitialPasswordMarkingFailed(int accounts) =>
        Counted(nameof(ActivityMessages.InitialPasswordMarkingFailedOne), accounts, accounts);
}
