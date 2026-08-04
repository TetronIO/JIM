// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// Reading a run's recorded steps: which of them are the run's own, which one is running, and
/// where it sits in the run.
/// </summary>
/// <remarks>
/// Three surfaces ask the same questions of the same rows: the progress API (to report "step 3 of
/// 7"), the portal's stepper (to draw the rail), and the portal's progress readout (to name the
/// step its figures measure). Each had begun answering them itself, and they must agree, because a
/// run whose step number differs between the portal and PowerShell is worse than one that reports
/// neither. The rules live here instead.
/// </remarks>
public static class RunPhaseReading
{
    /// <summary>
    /// The run's own steps, in run order. A Connector's steps are deliberately excluded: they are
    /// detail inside the step that called the Connector, so counting them would make the same run
    /// read differently depending on which Connector it used.
    /// </summary>
    public static IReadOnlyList<ActivityPhase> TopLevel(IEnumerable<ActivityPhase> phases) =>
        phases.Where(p => p.ParentKey == null).OrderBy(p => p.Order).ToList();

    /// <summary>
    /// The run's own step that is running, or null when nothing is. Where a Connector is reporting
    /// a step of its own, this is the step hosting it rather than the Connector's, because that is
    /// what the Activity's object counters are measuring.
    /// </summary>
    public static ActivityPhase? ActiveTopLevel(IEnumerable<ActivityPhase> phases) =>
        TopLevel(phases).FirstOrDefault(p => p.Status == ActivityPhaseStatus.Active);

    /// <summary>
    /// Where a step sits among the run's own steps, 1-based so it reads as "step 2 of 3". Null for
    /// no step, and for a Connector's step, which has no position of its own in the run.
    /// </summary>
    public static int? PositionOf(IEnumerable<ActivityPhase> phases, ActivityPhase? phase)
    {
        if (phase?.ParentKey != null)
            return null;

        var index = TopLevel(phases).ToList().FindIndex(p => p.Key == phase?.Key);
        return index >= 0 ? index + 1 : null;
    }
}
