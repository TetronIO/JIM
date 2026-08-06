// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// A run's steps reduced to what a list view needs (#1162): the shape of the whole run, and the
/// sentence naming the step running now.
/// </summary>
/// <remarks>
/// <para>
/// The Operations queue, the REST worker-task reads and PowerShell all report a run they are not
/// the detail page for. Each of them needs the same two things and could derive them separately;
/// deriving them here instead is what keeps a run from being "step 3 of 8" in the portal and
/// something else in a script, which is the failure <see cref="RunPhaseReading"/> already exists to
/// prevent for the Activity detail surfaces.
/// </para>
/// <para>
/// Built from the run's recorded phases rather than from the Run Profile's declared ones, so a step
/// the run skipped reads as skipped rather than as still to come.
/// </para>
/// </remarks>
public class RunPhaseSummary
{
    /// <summary>
    /// The name of the run's own step that is running, or null when nothing is: a run momentarily
    /// has nothing running as it moves between steps, and once it has finished nothing is running
    /// at all. Holding on to the last step instead would report finished work as still going.
    /// </summary>
    public string? CurrentStepName { get; set; }

    /// <summary>
    /// Where the running step sits among the run's own steps, 1-based so it reads as "step 3 of 8".
    /// Null whenever <see cref="CurrentStepName"/> is.
    /// </summary>
    public int? CurrentStepNumber { get; set; }

    /// <summary>
    /// How many steps the run has of its own. Worth showing even once nothing is running, because
    /// the shape of a finished run is what says where it failed.
    /// </summary>
    public int TotalSteps { get; set; }

    /// <summary>
    /// The run's own steps, in run order.
    /// </summary>
    public IReadOnlyList<RunPhaseStep> Steps { get; set; } = [];

    /// <summary>
    /// Summarises a run's recorded phases, or returns null where there is no run to summarise.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty summary is deliberate. Most Worker Tasks are not Run Profile
    /// executions (clearing Connected System Objects, example data generation, factory reset) and
    /// record no phases at all; a single null check is the whole of their handling, where an empty
    /// summary would have to be unpicked at every call site instead.
    /// </remarks>
    public static RunPhaseSummary? From(IEnumerable<ActivityPhase> phases)
    {
        // TopLevel is what decides a Connector's steps are detail inside the step that called it,
        // rather than steps of the run. Reusing it here rather than filtering on ParentKey keeps
        // that decision in one place, which is the point of RunPhaseReading.
        var phaseList = phases as IReadOnlyCollection<ActivityPhase> ?? phases.ToList();
        var steps = RunPhaseReading.TopLevel(phaseList);
        if (steps.Count == 0)
            return null;

        var current = RunPhaseReading.ActiveTopLevel(phaseList);

        return new RunPhaseSummary
        {
            CurrentStepName = current?.Name,
            CurrentStepNumber = RunPhaseReading.PositionOf(phaseList, current),
            TotalSteps = steps.Count,
            Steps = steps.Select(p => new RunPhaseStep
            {
                Order = p.Order,
                Name = p.Name,
                Status = p.Status
            }).ToList()
        };
    }
}
