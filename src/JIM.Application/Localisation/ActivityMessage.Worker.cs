// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Resources;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Localisation;
using JIM.Models.Operations;

namespace JIM.Application.Localisation;

/// <summary>
/// What the Worker says on an Activity or an execution item that is not about one kind of object: how a failure is
/// described, and what the memory guard says (#3039). Each message's English is the text the Worker wrote before there
/// were messages, so the REST API, logs and integration scenarios read what they always read.
/// </summary>
public static partial class ActivityMessage
{
    /// <summary>
    /// An exception's whole chain as the English <c>message --&gt; inner message</c> an Activity has always recorded.
    /// </summary>
    public static string ChainText(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.InnerException == null
            ? exception.Message
            : $"{exception.Message} --> {ChainText(exception.InnerException)}";
    }

    /// <summary>
    /// The exception's message with its causes (the chain <see cref="ChainText"/> writes), where any link in the chain
    /// carries a message of its own; null when none does, which leaves the English alone.
    /// </summary>
    public static JimMessage? DescribeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var own = exception.DetailOf();
        if (exception.InnerException is null)
            return own;

        var cause = DescribeFailure(exception.InnerException);
        if (own is null && cause is null)
            return null;

        return Create(
            nameof(ActivityMessages.FailureWithCause),
            own is not null && own.ToEnglish() == exception.Message ? own : (MessageArgument)exception.Message,
            cause ?? (MessageArgument)ChainText(exception.InnerException));
    }

    /// <summary>
    /// A failure with what the Worker was doing when it happened, as <c>"{context}: {failure}"</c>: the failure is its
    /// message where the chain carries one, else its English.
    /// </summary>
    public static JimMessage FailureInContext(JimMessage context, Exception exception) =>
        Create(nameof(ActivityMessages.FailureInContext), context, DescribeFailure(exception) ?? (MessageArgument)ChainText(exception));

    /// <summary>What the Worker was doing when a sync run raised an exception nothing handled.</summary>
    public static JimMessage ContextSyncRunUnhandled() => Create(nameof(ActivityMessages.ContextSyncRunUnhandled));

    /// <summary>What the Worker was doing when its memory guard stopped a run.</summary>
    public static JimMessage ContextMemoryGuard() => Create(nameof(ActivityMessages.ContextMemoryGuard));

    /// <summary>What the Worker was doing when completing a run's Activity failed.</summary>
    public static JimMessage ContextCompleteFailed() => Create(nameof(ActivityMessages.ContextCompleteFailed));

    /// <summary>
    /// Two messages with a blank line between them, as the memory guard adds its warning beneath what an Activity
    /// already says.
    /// </summary>
    public static JimMessage Paragraphs(JimMessage first, JimMessage second) =>
        Create(nameof(ActivityMessages.JoinedByLine), Create(nameof(ActivityMessages.JoinedByLine), first, string.Empty), second);

    /// <summary>
    /// "57 GB of 62 GB in use, 41 GB of it by the Worker": the figures a person needs to judge a memory reading; an
    /// empty argument where the total is unknown. The words are those of <see cref="MemoryPressure.Describe"/>.
    /// </summary>
    /// <param name="reading">The reading.</param>
    /// <param name="byTheWorker">Whether the process the last figure names is the Worker, rather than "this service".</param>
    public static MessageArgument MemoryFigures(MemoryReading reading, bool byTheWorker)
    {
        ArgumentNullException.ThrowIfNull(reading);

        if (reading is not { UsedBytes: { } used, TotalBytes: { } total })
            return string.Empty;

        return Create(
            byTheWorker ? nameof(ActivityMessages.MemoryFiguresByWorker) : nameof(ActivityMessages.MemoryFiguresByThisService),
            MemoryPressure.Bytes(used), MemoryPressure.Bytes(total), MemoryPressure.Bytes(reading.ProcessBytes));
    }

    /// <summary>What a run's Activity says when memory was low while it ran.</summary>
    public static JimMessage MemoryWarning(MemoryReading reading) =>
        Create(nameof(ActivityMessages.MemoryWarning), MemoryFigures(reading, true), MemoryRemedy());

    /// <summary>What a run's Activity says when the guard stopped it.</summary>
    public static JimMessage MemoryStop(MemoryReading reading) =>
        Create(nameof(ActivityMessages.MemoryStop), MemoryFigures(reading, true));

    /// <summary>What a run's Activity says when the run did not stop when asked, and the Worker restarted itself instead.</summary>
    public static JimMessage MemoryAbort(MemoryReading reading) =>
        Create(nameof(ActivityMessages.MemoryAbort), MemoryFigures(reading, true));

    /// <summary>What a run that failed for want of memory says on its Activity.</summary>
    public static JimMessage OutOfMemory(MemoryReading reading) =>
        MemoryFigures(reading, false) is { Text: { Length: 0 } }
            ? Create(nameof(ActivityMessages.OutOfMemory))
            : Create(nameof(ActivityMessages.OutOfMemoryWithFigures), MemoryFigures(reading, false));

    /// <summary>What recovery says of a task whose Worker most likely ran out of memory.</summary>
    public static JimMessage LikelyOutOfMemory(MemoryReading reading) =>
        Create(nameof(ActivityMessages.LikelyOutOfMemory), MemoryFigures(reading, true));

    private static JimMessage MemoryRemedy() =>
        Create(nameof(ActivityMessages.MemoryRemedy), MemoryPressure.Percent(MemoryPressure.StopShare));

    /// <summary>
    /// Records an error on an execution item as <paramref name="message"/> and its British English, so the item's
    /// <see cref="ActivityRunProfileExecutionItem.ErrorMessage"/> and the message cannot disagree.
    /// </summary>
    public static void SetError(ActivityRunProfileExecutionItem item, JimMessage message)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(message);
        item.ErrorMessageDetail = message;
    }
}
