// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Which of the Activity tab's sections are shown (#2549), and how that choice is written to the URL so a filtered
/// view can be bookmarked and survives a refresh.
/// </summary>
public sealed record ActivitySectionState(bool Queued, bool InProgress, bool Completed)
{
    private const string QueuedWord = "queued";
    private const string InProgressWord = "running";
    private const string CompletedWord = "completed";

    /// <summary>Every section shown: the default.</summary>
    public static ActivitySectionState All { get; } = new(true, true, true);

    /// <summary>Whether every section has been turned off.</summary>
    public bool NothingSelected => !Queued && !InProgress && !Completed;

    /// <summary>
    /// Reads the URL value: a comma-separated list of the sections shown. Absent means every section; empty means
    /// none. Case and unknown words are tolerated, since the value is typed by hand as often as written by the page.
    /// </summary>
    public static ActivitySectionState Parse(string? value)
    {
        if (value == null)
            return All;

        var words = value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

        return new ActivitySectionState(words.Contains(QueuedWord), words.Contains(InProgressWord), words.Contains(CompletedWord));
    }

    /// <summary>The URL value for this state, or null when every section is shown so the default URL stays clean.</summary>
    public string? ToQueryValue()
    {
        if (this == All)
            return null;

        var words = new List<string>();
        if (Queued) words.Add(QueuedWord);
        if (InProgress) words.Add(InProgressWord);
        if (Completed) words.Add(CompletedWord);
        return string.Join(',', words);
    }

    /// <summary>This state with one section switched.</summary>
    public ActivitySectionState Toggle(ActivitySection section) => section switch
    {
        ActivitySection.Queued => this with { Queued = !Queued },
        ActivitySection.InProgress => this with { InProgress = !InProgress },
        ActivitySection.Completed => this with { Completed = !Completed },
        _ => this
    };
}
