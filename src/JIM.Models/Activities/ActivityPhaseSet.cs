// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Activities;

/// <summary>
/// The phases of one Run Profile execution and the rules for moving between them (#454). Held in
/// memory by the worker for the length of the run: transitions are computed here and only the rows
/// that actually changed are persisted, so narrating a run costs a handful of small writes.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of any dependency on the database or the worker, so the transition rules are
/// provable in isolation (ActivityPhaseSetTests). The rules are:
/// </para>
/// <list type="bullet">
/// <item>Entering a phase completes whatever else was running, except the phase that hosts it: a
/// Connector's phase runs <em>inside</em> the JIM phase that called the Connector, and both are
/// active at once.</item>
/// <item>Declared phases passed over without being entered are recorded as skipped, because a run
/// legitimately does less than the full journey (no deletion detection on a Delta Import, no
/// connection to open for a file-based import).</item>
/// <item>Re-entering a phase reopens it rather than duplicating it, so a paged import that loops
/// between fetching and parsing reads as two steps taking a while, not forty steps.</item>
/// <item>A phase entered but never declared is appended rather than dropped, so a Connector that
/// narrates something unexpected still shows up instead of blanking the stepper.</item>
/// </list>
/// </remarks>
public class ActivityPhaseSet
{
    private readonly List<ActivityPhase> _phases;

    private ActivityPhaseSet(Guid activityId, ConnectedSystemRunType runType, List<ActivityPhase> phases)
    {
        ActivityId = activityId;
        RunType = runType;
        _phases = phases;
    }

    /// <summary>
    /// The Activity these phases belong to.
    /// </summary>
    public Guid ActivityId { get; }

    /// <summary>
    /// The type of run these phases describe.
    /// </summary>
    public ConnectedSystemRunType RunType { get; }

    /// <summary>
    /// Every phase of the run, in the order an administrator sees them.
    /// </summary>
    public IReadOnlyList<ActivityPhase> Phases => _phases;

    /// <summary>
    /// The most specific phase currently running (a Connector's phase in preference to the JIM
    /// phase hosting it), or null when nothing is running.
    /// </summary>
    public ActivityPhase? CurrentPhase => _phases
        .Where(p => p.Status == ActivityPhaseStatus.Active)
        .OrderByDescending(p => p.ParentKey != null)
        .ThenByDescending(p => p.Order)
        .FirstOrDefault();

    /// <summary>
    /// Records the phases a run of this type can perform, with the Connector's own declared phases
    /// nested inside whichever JIM phase calls the Connector.
    /// </summary>
    /// <param name="activityId">The Run Profile execution Activity the phases belong to.</param>
    /// <param name="runType">The type of run being performed.</param>
    /// <param name="connectorPhases">
    /// The phases the Connector declared, or null when it declared none. Ignored for run types that
    /// do not call a Connector, as there would be nowhere to nest them.
    /// </param>
    public static ActivityPhaseSet Declare(Guid activityId, ConnectedSystemRunType runType, IEnumerable<ConnectorPhase>? connectorPhases)
    {
        var hostKey = RunProfilePhaseCatalogue.GetConnectorHostPhaseKey(runType);
        var nested = hostKey == null
            ? []
            : (connectorPhases ?? []).DistinctBy(p => p.Key, StringComparer.Ordinal).ToList();

        var phases = new List<ActivityPhase>();
        foreach (var declared in RunProfilePhaseCatalogue.GetPhases(runType))
        {
            phases.Add(NewPhase(activityId, declared.Key, declared.Name, parentKey: null, order: phases.Count));

            if (!declared.HostsConnectorPhases)
                continue;

            phases.AddRange(nested.Select(connectorPhase => NewPhase(
                activityId,
                ActivityPhase.QualifyConnectorKey(connectorPhase.Key),
                connectorPhase.Name,
                parentKey: declared.Key,
                order: 0)));
        }

        // Order is assigned after nesting so the numbers read straight down the stepper.
        for (var i = 0; i < phases.Count; i++)
            phases[i].Order = i;

        return new ActivityPhaseSet(activityId, runType, phases);
    }

    /// <summary>
    /// Enters a phase, applying the transition rules described on this type.
    /// </summary>
    /// <param name="key">
    /// The phase key: a <see cref="RunPhaseKeys"/> constant for a JIM phase, or a Connector phase
    /// key already qualified by <see cref="ActivityPhase.QualifyConnectorKey"/>.
    /// </param>
    /// <param name="nowUtc">The time of the transition.</param>
    /// <param name="name">
    /// The label to use if this phase was never declared. Ignored for a declared phase, whose
    /// label was captured when the run started.
    /// </param>
    /// <returns>The phases whose recorded state changed, which is what needs persisting.</returns>
    public IReadOnlyList<ActivityPhase> Enter(string key, DateTime nowUtc, string? name = null)
    {
        if (_phases.Count == 0)
            return [];

        var phase = _phases.SingleOrDefault(p => p.Key == key);
        var changed = new List<ActivityPhase>();

        if (phase == null)
        {
            phase = AppendUndeclaredPhase(key, name);
            changed.Add(phase);
        }
        else if (phase.Status == ActivityPhaseStatus.Active)
        {
            // Already running: re-entering it is the same step continuing, and nothing needs writing.
            return [];
        }
        else
        {
            // Everything declared before this phase that never ran did not happen on this run.
            changed.AddRange(_phases
                .Where(p => p.Order < phase.Order && p.Status == ActivityPhaseStatus.Pending)
                .Select(p =>
                {
                    p.Status = ActivityPhaseStatus.Skipped;
                    return p;
                }));
        }

        // A Connector phase runs inside the JIM phase that called the Connector; that phase stays active.
        var parent = phase.ParentKey == null ? null : _phases.SingleOrDefault(p => p.Key == phase.ParentKey);
        if (parent is { Status: ActivityPhaseStatus.Pending or ActivityPhaseStatus.Skipped })
        {
            parent.Status = ActivityPhaseStatus.Active;
            parent.Started ??= nowUtc;
            parent.Ended = null;
            changed.Add(parent);
        }

        changed.AddRange(_phases
            .Where(p => p.Status == ActivityPhaseStatus.Active && p != phase && p != parent)
            .Select(p => Close(p, nowUtc, ActivityPhaseStatus.Completed)));

        phase.Status = ActivityPhaseStatus.Active;
        phase.Started ??= nowUtc;
        phase.Ended = null;
        if (!changed.Contains(phase))
            changed.Add(phase);

        return changed;
    }

    /// <summary>
    /// Closes the run out: whatever was running is completed (or failed), and anything never
    /// reached is recorded as skipped.
    /// </summary>
    /// <param name="nowUtc">The time the run finished.</param>
    /// <param name="failed">
    /// True when the run failed or was cancelled, so the phase that was running is recorded as the
    /// one it failed in rather than as completed.
    /// </param>
    /// <returns>The phases whose recorded state changed, which is what needs persisting.</returns>
    public IReadOnlyList<ActivityPhase> Finish(DateTime nowUtc, bool failed)
    {
        var closingStatus = failed ? ActivityPhaseStatus.Failed : ActivityPhaseStatus.Completed;

        var changed = _phases
            .Where(p => p.Status == ActivityPhaseStatus.Active)
            .Select(p => Close(p, nowUtc, closingStatus))
            .ToList();

        changed.AddRange(_phases
            .Where(p => p.Status == ActivityPhaseStatus.Pending)
            .Select(p =>
            {
                p.Status = ActivityPhaseStatus.Skipped;
                return p;
            }));

        return changed;
    }

    private ActivityPhase AppendUndeclaredPhase(string key, string? name)
    {
        // Undeclared phases are appended rather than slotted in: their position in the run is not
        // known, and nothing before them should be written off as skipped on their account.
        var parentKey = key.StartsWith(ActivityPhase.ConnectorPhaseKeyPrefix, StringComparison.Ordinal)
            ? RunProfilePhaseCatalogue.GetConnectorHostPhaseKey(RunType)
            : null;

        var phase = NewPhase(ActivityId, key, name ?? Humanise(key), parentKey, _phases.Max(p => p.Order) + 1);
        _phases.Add(phase);
        return phase;
    }

    private static ActivityPhase Close(ActivityPhase phase, DateTime nowUtc, ActivityPhaseStatus status)
    {
        phase.Status = status;
        phase.Ended = nowUtc;
        return phase;
    }

    private static ActivityPhase NewPhase(Guid activityId, string key, string name, string? parentKey, int order) => new()
    {
        Id = Guid.NewGuid(),
        ActivityId = activityId,
        Key = key,
        Name = name,
        ParentKey = parentKey,
        Order = order,
        Status = ActivityPhaseStatus.Pending
    };

    /// <summary>
    /// Best-effort label for a phase key nobody declared a name for, so the step still reads as
    /// something rather than as an internal identifier.
    /// </summary>
    private static string Humanise(string key)
    {
        var text = key.StartsWith(ActivityPhase.ConnectorPhaseKeyPrefix, StringComparison.Ordinal)
            ? key[ActivityPhase.ConnectorPhaseKeyPrefix.Length..]
            : key;

        text = text.Replace('-', ' ').Replace('_', ' ').Replace('.', ' ').Trim();
        if (text.Length == 0)
            return "Working";

        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
