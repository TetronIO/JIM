// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using MudBlazor;

namespace JIM.Web.Models;

/// <summary>
/// How a person's recent password changes read as a timeline (#1635): a heading per day, one entry per change with
/// a dot coloured by its worst outcome, one chip per Connected System in the portal's status-pill vocabulary, and a
/// line of words only where something is wrong or still owed.
/// <para>
/// Pure, so the rules live somewhere a unit test can reach without rendering. The rule that most needs pinning is
/// how the queue is read alongside history. History is Activities: the change itself, and a child per delivery
/// attempt per system, all durable. The queue is not: its row for a person and system is deleted the moment the
/// password arrives, and coalesced when a newer change supersedes it, so at most one live row exists per system
/// and it belongs to the newest change that targeted the system. The coalescing UPSERT refreshes the row's
/// CreatedAt to the superseding change's time, and the change's Activity is always created before its rows, so a
/// row belongs to the newest change created at or before it. Where that row exists it says what the system is
/// doing with the change now, and wins; everywhere else the newest attempt's Activity speaks.
/// </para>
/// </summary>
public static class PasswordHistoryTimelineModel
{
    /// <summary>
    /// What one Connected System is doing, or did, with one change.
    /// </summary>
    public enum TargetState
    {
        /// <summary>The system took the password.</summary>
        Set,

        /// <summary>The newest attempt failed and no live queue row remains: superseded, or the row has since been removed.</summary>
        Failed,

        /// <summary>Queued and not yet attempted.</summary>
        Queued,

        /// <summary>The Password Delivery Service is writing it now.</summary>
        Delivering,

        /// <summary>An attempt failed in a way another may resolve, and the next one is booked.</summary>
        Retrying,

        /// <summary>Waiting on somebody switching Password Synchronisation on for the system.</summary>
        Held,

        /// <summary>JIM has stopped trying; waits on a person.</summary>
        Parked,

        /// <summary>Outlived its time to live before it could be delivered.</summary>
        Expired,

        /// <summary>An administrator stopped it being delivered.</summary>
        Cancelled
    }

    /// <summary>
    /// A day's worth of changes under one heading.
    /// </summary>
    /// <param name="Heading">"Today", "Yesterday", or the day.</param>
    /// <param name="Entries">The day's changes, newest first.</param>
    public sealed record Day(string Heading, IReadOnlyList<Entry> Entries);

    /// <summary>
    /// One password change as a timeline entry.
    /// </summary>
    /// <param name="ActivityId">The change's Activity, and the entry's identity in a render loop.</param>
    /// <param name="LocalTime">When the change was made, in the viewer's local time.</param>
    /// <param name="Origin">Set or Propagated, or null for an Activity from before origins were recorded (no kind chip).</param>
    /// <param name="InitiatorLead">"by" for a person, "via" for an API key.</param>
    /// <param name="InitiatorName">Who made the change, or null where the Activity did not record one.</param>
    /// <param name="InitiatorTrail">"(API key)" after an automation's name; null for a person.</param>
    /// <param name="Scope">"on 3 Connected Systems" for an administrator's explicit choice of more than one account; null otherwise.</param>
    /// <param name="DotColour">The timeline dot: the worst state among the entry's systems.</param>
    /// <param name="Targets">One per Connected System, in the order they were first reached.</param>
    public sealed record Entry(
        Guid ActivityId,
        DateTime LocalTime,
        PendingPasswordChangeOrigin? Origin,
        string InitiatorLead,
        string? InitiatorName,
        string? InitiatorTrail,
        string? Scope,
        Color DotColour,
        IReadOnlyList<Target> Targets);

    /// <summary>
    /// One Connected System's chip and, where it has one, its line of words.
    /// </summary>
    /// <param name="ConnectedSystemId">The system, for the Retry and Stop trying actions; null on an outcome recorded without one.</param>
    /// <param name="Name">The Connected System's name, which is the chip's text.</param>
    /// <param name="State">The derived state.</param>
    /// <param name="PillModifier">The status-pill class suffix (ok, warn, err, neutral) that paints the chip.</param>
    /// <param name="ChipSuffix">The word after the name on the chip ("retrying", "parked"), or null for a plain success.</param>
    /// <param name="Tooltip">The target's own words and when they were recorded, one sentence each.</param>
    /// <param name="Detail">The line beneath the chips, or null where the chip says everything.</param>
    /// <param name="CanRetry">Whether the line offers Retry (a parked change).</param>
    /// <param name="CanStopTrying">Whether the line offers Stop trying (a change JIM is still retrying).</param>
    public sealed record Target(
        int? ConnectedSystemId,
        string Name,
        TargetState State,
        string PillModifier,
        string? ChipSuffix,
        string Tooltip,
        string? Detail,
        bool CanRetry,
        bool CanStopTrying);

    /// <summary>
    /// A success that landed within this of the request carries no words; one that lagged by more says when it
    /// landed and by how much, because a password that took a quarter of an hour to arrive is a fact about the
    /// delivery worth a line.
    /// </summary>
    private static readonly TimeSpan UnremarkableLag = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Builds the timeline from a person's changes (newest first, as the read returns them) and what the queue
    /// still holds for them.
    /// </summary>
    /// <param name="events">The person's recent changes with their per-system outcomes.</param>
    /// <param name="queuedChanges">Every live queue row for the person, whichever change it belongs to.</param>
    /// <param name="nowUtc">The moment the timeline is drawn, for Today and Yesterday and for same-day times.</param>
    public static IReadOnlyList<Day> Build(
        IReadOnlyList<PasswordSynchronisationEvent> events,
        IReadOnlyList<PendingPasswordChangeHeader> queuedChanges,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(queuedChanges);

        var ordered = events.OrderByDescending(e => e.Created).ToList();
        var rowsByChange = AttributeRows(ordered, queuedChanges);
        var localToday = nowUtc.ToLocalTime().Date;

        return ordered
            .Select(change => BuildEntry(change, rowsByChange.GetValueOrDefault(change.ActivityId, []), nowUtc))
            .GroupBy(entry => entry.LocalTime.Date)
            .Select(group => new Day(group.Key.ToDayHeading(localToday), group.ToList()))
            .ToList();
    }

    /// <summary>
    /// Which change each live queue row belongs to: the newest change created at or before the row (see the class
    /// remarks). A row older than every change shown belongs to a change beyond the panel's limit, and is left to
    /// the Still to be delivered block, which lists every row regardless.
    /// </summary>
    private static Dictionary<Guid, List<PendingPasswordChangeHeader>> AttributeRows(
        IReadOnlyList<PasswordSynchronisationEvent> newestFirst,
        IReadOnlyList<PendingPasswordChangeHeader> rows)
    {
        var byChange = new Dictionary<Guid, List<PendingPasswordChangeHeader>>();
        foreach (var row in rows)
        {
            var owner = newestFirst.FirstOrDefault(change => change.Created <= row.CreatedAt);
            if (owner == null)
                continue;

            if (!byChange.TryGetValue(owner.ActivityId, out var owned))
                byChange[owner.ActivityId] = owned = [];
            owned.Add(row);
        }

        return byChange;
    }

    private static Entry BuildEntry(PasswordSynchronisationEvent change, IReadOnlyList<PendingPasswordChangeHeader> ownedRows, DateTime nowUtc)
    {
        // One chip per system, in the order the systems were first reached, then any system the queue still holds
        // for this change that no attempt has been recorded against (queued or held, typically).
        var targets = new List<Target>();
        var seen = new HashSet<int>();
        foreach (var group in change.Outcomes.GroupBy(o => o.ConnectedSystemId))
        {
            var newest = group.OrderBy(o => o.OccurredAt).Last();
            var row = group.Key is { } systemId ? ownedRows.FirstOrDefault(r => r.ConnectedSystemId == systemId) : null;
            if (group.Key is { } id)
                seen.Add(id);
            targets.Add(row != null ? FromRow(row, nowUtc) : FromOutcome(newest, change.Created));
        }

        foreach (var row in ownedRows.Where(r => !seen.Contains(r.ConnectedSystemId)))
            targets.Add(FromRow(row, nowUtc));

        var explicitChange = change.Origin == PendingPasswordChangeOrigin.Explicit;
        var initiatorKnown = !string.IsNullOrWhiteSpace(change.InitiatedByName);
        var apiKey = change.InitiatedByType == ActivityInitiatorType.ApiKey;

        return new Entry(
            change.ActivityId,
            change.Created.ToLocalTime(),
            change.Origin,
            InitiatorLead: apiKey ? "via" : "by",
            InitiatorName: initiatorKnown ? change.InitiatedByName : null,
            InitiatorTrail: initiatorKnown && apiKey ? "(API key)" : null,
            Scope: explicitChange && targets.Count > 1 ? $"on {targets.Count} Connected Systems" : null,
            DotColour: WorstColour(targets),
            Targets: targets);
    }

    /// <summary>
    /// The dot answers "is this change finished, and how did it go": red if any system is parked, expired, cancelled
    /// or failed; amber if any is retrying or held; grey while any is still queued or delivering, or when nothing
    /// was attempted at all; green only when every system took it.
    /// </summary>
    private static Color WorstColour(IReadOnlyList<Target> targets)
    {
        if (targets.Count == 0)
            return Color.Default;
        if (targets.Any(t => t.PillModifier == "err"))
            return Color.Error;
        if (targets.Any(t => t.PillModifier == "warn"))
            return Color.Warning;
        if (targets.Any(t => t.PillModifier == "neutral"))
            return Color.Default;
        return Color.Success;
    }

    private static Target FromRow(PendingPasswordChangeHeader row, DateTime nowUtc)
    {
        var state = row.Status switch
        {
            PendingPasswordChangeStatus.Delivering => TargetState.Delivering,
            PendingPasswordChangeStatus.Parked => TargetState.Parked,
            PendingPasswordChangeStatus.Expired => TargetState.Expired,
            PendingPasswordChangeStatus.Cancelled => TargetState.Cancelled,
            _ when row.IsHeld => TargetState.Held,
            _ when row.AttemptCount > 0 => TargetState.Retrying,
            _ => TargetState.Queued
        };

        var detail = state switch
        {
            TargetState.Parked => Sentence(Fault(row) ?? "parked; JIM has stopped trying until somebody retries it"),
            TargetState.Retrying => Sentence(Fault(row) ?? "retrying") + NextAttempt(row, nowUtc),
            TargetState.Held => "held until Password Synchronisation is switched on for this Connected System.",
            TargetState.Queued => "queued, not yet attempted.",
            TargetState.Delivering => "delivering now.",
            TargetState.Expired => "expired before it could be delivered.",
            TargetState.Cancelled => row.CancelledByName == null ? "cancelled by an administrator." : $"cancelled by {row.CancelledByName}.",
            _ => null
        };

        var when = row.LastAttemptedAt is { } lastAttemptedAt
            ? $"Last attempted {lastAttemptedAt.ToLocalTime().ToFriendlyDate()}."
            : $"Queued {row.CreatedAt.ToLocalTime().ToFriendlyDate()}.";
        var words = PendingPasswordChangeDisplay.Detail(row) ?? PendingPasswordChangeDisplay.Status(row);

        return new Target(
            row.ConnectedSystemId,
            row.ConnectedSystemName,
            state,
            Modifier(state),
            Suffix(state),
            $"{Sentence(words)} {when}",
            detail,
            CanRetry: state == TargetState.Parked,
            CanStopTrying: state == TargetState.Retrying);
    }

    private static Target FromOutcome(PasswordSynchronisationEventOutcome outcome, DateTime requestedAt)
    {
        var state = outcome.Succeeded switch
        {
            true => TargetState.Set,
            false => TargetState.Failed,
            null => TargetState.Delivering
        };

        string? detail = state switch
        {
            TargetState.Set when outcome.OccurredAt - requestedAt > UnremarkableLag =>
                $"delivered {outcome.OccurredAt.ToLocalTime().ToFriendlyTime()}, {(outcome.OccurredAt - requestedAt).ToAbbreviatedString(1)} after the request.",
            TargetState.Failed => Sentence(FirstNonBlank(outcome.ErrorMessage, outcome.Message) ?? "did not take the password"),
            TargetState.Delivering => "delivering now.",
            _ => null
        };

        var words = FirstNonBlank(outcome.ErrorMessage, outcome.Message) ?? (state == TargetState.Set ? "Password set" : "The attempt is still running");

        return new Target(
            outcome.ConnectedSystemId,
            outcome.ConnectedSystemName,
            state,
            Modifier(state),
            Suffix(state),
            $"{Sentence(words)} Recorded {outcome.OccurredAt.ToLocalTime().ToFriendlyDate()}.",
            detail,
            CanRetry: false,
            CanStopTrying: false);
    }

    /// <summary>
    /// JIM's classification of the last attempt in lower case, then the target's own words: "target unavailable,
    /// the LDAP server did not respond". The classification says whether another attempt could ever help; the
    /// words say where the remedy lives. Null where the row records neither.
    /// </summary>
    private static string? Fault(PendingPasswordChangeHeader row)
    {
        var reason = row.FailureReason is { } failureReason and not PasswordSetFailureReason.None
            ? PendingPasswordChangeDisplay.Reason(failureReason).ToLowerInvariant()
            : null;
        var message = string.IsNullOrWhiteSpace(row.TargetMessage) ? null : row.TargetMessage.Trim();

        return (reason, message) switch
        {
            (not null, not null) => $"{reason}, {message}",
            (not null, null) => reason,
            (null, not null) => message,
            _ => null
        };
    }

    /// <summary>
    /// " Next attempt 14:23." for a retry booked later today, with the date where it falls on another day; nothing
    /// where the row carries no booking.
    /// </summary>
    private static string NextAttempt(PendingPasswordChangeHeader row, DateTime nowUtc)
    {
        if (row.NextRetryAt is not { } nextRetryAt)
            return string.Empty;

        var local = nextRetryAt.ToLocalTime();
        var when = local.Date == nowUtc.ToLocalTime().Date ? local.ToFriendlyTime() : local.ToFriendlyDate();
        return $" Next attempt {when}.";
    }

    private static string Modifier(TargetState state) => state switch
    {
        TargetState.Set => "ok",
        TargetState.Retrying or TargetState.Held => "warn",
        TargetState.Parked or TargetState.Expired or TargetState.Cancelled or TargetState.Failed => "err",
        _ => "neutral"
    };

    private static string? Suffix(TargetState state) => state switch
    {
        TargetState.Set => null,
        TargetState.Failed => "failed",
        TargetState.Queued => "queued",
        TargetState.Delivering => "delivering",
        TargetState.Retrying => "retrying",
        TargetState.Held => "held",
        TargetState.Parked => "parked",
        TargetState.Expired => "expired",
        TargetState.Cancelled => "cancelled",
        _ => state.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Ends a fragment with one full stop, whether or not the target's own words already carried one.
    /// </summary>
    private static string Sentence(string words)
    {
        var trimmed = words.Trim();
        return trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?') ? trimmed : trimmed + ".";
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
}
