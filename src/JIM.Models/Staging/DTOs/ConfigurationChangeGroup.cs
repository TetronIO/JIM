// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// The configuration changes made to one thing (a Synchronisation Rule, or an Object Type's Object Matching Rule) behind a
/// Connected System Object Type that a synchronisation processes in full, collapsed into one entry: what was changed, a
/// summary of what was done to it, when it was last changed and by whom. Saving a rule repeatedly records a change per
/// save, so listing them one by one buries the few things that actually changed under repeated rows.
/// </summary>
public class ConfigurationChangeGroup
{
    // Every reason is recorded as "<subject>: <what was done>". A subject (a rule's name) may itself contain the
    // separator; what was done never does, so the last one divides them.
    private const string SubjectSeparator = ": ";

    /// <summary>
    /// What was changed: the Synchronisation Rule's name as of its newest change, or the Object Type's name for an Object
    /// Matching Rule change.
    /// </summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>
    /// The Synchronisation Rule changed, where the changes were to one that still exists.
    /// </summary>
    public int? SyncRuleId { get; init; }

    /// <summary>
    /// What was done, oldest first, with repeated consecutive actions counted, e.g. "Created, then Attribute Flow changed
    /// (Sync-affecting) 6 times". Empty when the reasons name no action.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// When the newest of the changes was made (UTC).
    /// </summary>
    public DateTime LastMadeAt { get; init; }

    /// <summary>
    /// Who made the changes, each once, newest first.
    /// </summary>
    public IReadOnlyList<ConfigurationChangeInitiator> Initiators { get; init; } = [];

    /// <summary>
    /// The individual changes, oldest first.
    /// </summary>
    public IReadOnlyList<ConfigurationChangeSynchronisationSummary> Changes { get; init; } = [];

    /// <summary>
    /// What was done in one change: its reason without the subject.
    /// </summary>
    public static string ActionOf(ConfigurationChangeSynchronisationSummary change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var separator = change.Reason.LastIndexOf(SubjectSeparator, StringComparison.Ordinal);
        return separator < 0 ? string.Empty : change.Reason[(separator + SubjectSeparator.Length)..];
    }

    /// <summary>
    /// Collapses changes into one group per thing changed, in the order each first appears. Changes to a Synchronisation
    /// Rule group by the rule, so a rename between saves stays one group; changes with no rule (an Object Matching Rule, or
    /// a rule since deleted) group by subject.
    /// </summary>
    public static IReadOnlyList<ConfigurationChangeGroup> Group(IReadOnlyList<ConfigurationChangeSynchronisationSummary> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return changes
            .GroupBy(change => change.SyncRuleId is { } syncRuleId ? (syncRuleId, (string?)null) : ((int?)null, SubjectOf(change)))
            .Select(group => Build(group.Key.Item1, group.OrderBy(change => change.RequestedAt).ThenBy(change => change.Id).ToList()))
            .ToList();
    }

    private static ConfigurationChangeGroup Build(int? syncRuleId, List<ConfigurationChangeSynchronisationSummary> oldestFirst) => new()
    {
        Subject = SubjectOf(oldestFirst[^1]),
        SyncRuleId = syncRuleId,
        Summary = Summarise(oldestFirst),
        LastMadeAt = oldestFirst[^1].RequestedAt,
        Initiators = oldestFirst
            .AsEnumerable()
            .Reverse()
            .Select(change => new ConfigurationChangeInitiator(change.InitiatedByType, change.InitiatedById, change.InitiatedByName))
            .Distinct()
            .ToList(),
        Changes = oldestFirst
    };

    private static string SubjectOf(ConfigurationChangeSynchronisationSummary change)
    {
        var separator = change.Reason.LastIndexOf(SubjectSeparator, StringComparison.Ordinal);
        return separator < 0 ? change.Reason : change.Reason[..separator];
    }

    // Consecutive identical actions are counted rather than repeated; the order is kept, so "then" stays true.
    private static string Summarise(List<ConfigurationChangeSynchronisationSummary> oldestFirst)
    {
        var runs = new List<(string Action, int Count)>();
        foreach (var action in oldestFirst.Select(ActionOf).Where(action => action.Length > 0))
        {
            if (runs.Count > 0 && runs[^1].Action == action)
                runs[^1] = (action, runs[^1].Count + 1);
            else
                runs.Add((action, 1));
        }

        if (runs.Count == 0)
            return string.Empty;

        var summary = string.Join(", then ", runs.Select(run => run.Count == 1 ? run.Action : $"{run.Action} {run.Count:N0} times"));
        return char.ToUpperInvariant(summary[0]) + summary[1..];
    }
}
