// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// One line of a run's follow-up summary (#2021, Phase 6): one kind of work, its states, and how many are still
/// running or stuck.
/// </summary>
/// <param name="Kind">The kind of work: Initial Passwords or Exchange recipient changes.</param>
/// <param name="Title">What the line is called.</param>
/// <param name="States">Its states, settled first, then running, then stuck.</param>
public sealed record FollowUpSummaryLine(ActivityTargetType Kind, string Title, IReadOnlyList<FollowUpSummaryState> States)
{
    /// <summary>How many follow-ups of this kind the run has.</summary>
    public int Total => States.Sum(s => s.Count);

    /// <summary>How many JIM is still working on.</summary>
    public int RunningCount => States.Where(s => s.IsRunning).Sum(s => s.Count);

    /// <summary>How many wait for a person: parked, refused, not made, expired or cancelled.</summary>
    public int StuckCount => States.Where(s => s.IsStuck).Sum(s => s.Count);
}

/// <summary>
/// How many follow-ups of one kind stand in one state, as the summary shows them.
/// </summary>
/// <param name="Label">The state as a word in a sentence, such as "delivered".</param>
/// <param name="Modifier">The <c>jim-status-pill--</c> modifier each such follow-up shows.</param>
/// <param name="Count">How many.</param>
/// <param name="IsRunning">Whether JIM is still working on them.</param>
/// <param name="IsStuck">Whether they wait for a person.</param>
public sealed record FollowUpSummaryState(string Label, string Modifier, int Count, bool IsRunning, bool IsStuck);

/// <summary>
/// Turns a run's follow-up counts into the lines its Follow-up summary and its Activity list chips show, each state
/// toned exactly as one follow-up in it would be on the item page.
/// </summary>
public static class FollowUpSummaryDisplay
{
    // Settled first, then running, then stuck: the reader's eye ends on what needs them.
    private static readonly string[] Order =
    [
        "Set", "Delivered", "Withdrawn", "Replaced", "Carried",
        "Queued", "Delivering", "Retrying", "Held",
        "NotMade", "Parked", "Refused", "Expired", "Cancelled"
    ];

    public static IReadOnlyList<FollowUpSummaryLine> From(IEnumerable<FollowUpStateCount> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        return counts
            .GroupBy(c => c.Kind)
            .OrderBy(g => g.Key == ActivityTargetType.PasswordSynchronisation ? 0 : 1)
            .ThenBy(g => g.Key)
            .Select(g => new FollowUpSummaryLine(g.Key, Title(g.Key), g
                .OrderBy(c => Array.IndexOf(Order, c.State) is var i and >= 0 ? i : Order.Length)
                .ThenBy(c => c.State, StringComparer.Ordinal)
                .Select(c => State(c.Kind, c.State, c.Count))
                .ToList()))
            .ToList();
    }

    private static string Title(ActivityTargetType kind) => kind switch
    {
        ActivityTargetType.PasswordSynchronisation => "Initial passwords",
        ActivityTargetType.ExchangeRecipient => "Exchange changes",
        _ => kind.ToString()
    };

    private static FollowUpSummaryState State(ActivityTargetType kind, string state, int count)
    {
        var (label, modifier, running) = kind == ActivityTargetType.ExchangeRecipient && Enum.TryParse<JIM.Models.Transactional.DTOs.ExchangeChangeState>(state, out var exchange)
            ? ExchangeChangeOutcomeDisplay.Pill(exchange)
            : PasswordPill(state);
        return new FollowUpSummaryState(label.ToLowerInvariant(), modifier, count, running, IsStuckState(state));
    }

    /// <summary>
    /// An Initial Password's state as its follow-up pill reads it (<see cref="ExecutionItemFollowUpDisplay.From"/>).
    /// </summary>
    private static (string Label, string Modifier, bool Running) PasswordPill(string state) => state switch
    {
        "Set" => ("Delivered", "ok", false),
        "Queued" => ("Queued", "neutral", true),
        "Delivering" => ("Delivering", "neutral", true),
        "Retrying" => ("Retrying", "warn", true),
        "Held" => ("Held", "warn", true),
        "Parked" => ("Parked", "err", false),
        "Expired" => ("Expired", "err", false),
        "Cancelled" => ("Cancelled", "err", false),
        "Carried" => ("Carried", "neutral", false),
        _ => (state, "neutral", false)
    };

    private static bool IsStuckState(string state) =>
        state is "NotMade" or "Parked" or "Refused" or "Expired" or "Cancelled";
}
