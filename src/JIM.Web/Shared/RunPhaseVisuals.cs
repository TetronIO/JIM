// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using MudBlazor;

namespace JIM.Web.Shared;

/// <summary>
/// What a step of a Run Profile execution looks like for a given outcome (#454, #1162): the CSS
/// modifier its appearance hangs on, the icon it is drawn with, how full the track leaving it is,
/// and what an unusual outcome means.
/// </summary>
/// <remarks>
/// <para>
/// These rules began as private methods on <see cref="RunPhaseStepper"/>, when the Activity page's
/// rail was the only thing drawing a step. Three rails now draw the same steps, and a run whose
/// step reads green on one and grey on another is worse than one that draws no steps at all. This
/// is the appearance-side sibling of <see cref="RunPhaseReading"/> (JIM.Models), which does the
/// same job for "which step is running, and where does it sit in the run".
/// </para>
/// <para>
/// Presentation only, and deliberately in JIM.Web: colours and icons are a portal choice, and
/// JIM.Models has no business knowing about MudBlazor. Anything a non-portal surface also needs to
/// agree on belongs in JIM.Models instead.
/// </para>
/// </remarks>
public static class RunPhaseVisuals
{
    /// <summary>
    /// Whether the run is past this step. Completed, skipped and failed are all past tense: a
    /// skipped step is one the run decided it did not need, not one still to come, and treating it
    /// otherwise leaves a permanent gap in the rail of every Delta Import.
    /// </summary>
    public static bool HasRun(ActivityPhaseStatus status) => status is
        ActivityPhaseStatus.Completed or
        ActivityPhaseStatus.Skipped or
        ActivityPhaseStatus.Failed;

    /// <summary>
    /// The suffix a stylesheet hangs a step's appearance on, one per status.
    /// </summary>
    public static string StatusModifier(ActivityPhaseStatus status) => status switch
    {
        ActivityPhaseStatus.Active => "active",
        ActivityPhaseStatus.Completed => "completed",
        ActivityPhaseStatus.Skipped => "skipped",
        ActivityPhaseStatus.Failed => "failed",
        _ => "pending"
    };

    /// <summary>
    /// A finished step shows its outcome; every other state shows what the step is for, so a rail
    /// can be scanned by shape rather than read. A step with no record at all is drawn as unreached,
    /// which is not the same as a step that has not run yet but is the safest thing to show.
    /// </summary>
    public static string StatusIcon(ActivityPhase? phase) => phase?.Status switch
    {
        null => Icons.Material.Filled.RadioButtonUnchecked,
        ActivityPhaseStatus.Completed => Icons.Material.Filled.Check,
        ActivityPhaseStatus.Skipped => Icons.Material.Filled.Remove,
        ActivityPhaseStatus.Failed => Icons.Material.Filled.PriorityHigh,
        _ => RunPhaseIcons.ForPhase(phase.Key)
    };

    /// <summary>
    /// How full the track belonging to this step is, 0 to 100. The track belongs to the step it
    /// leaves, so it carries that step's own progress: full once the step has run, the running
    /// step's count while it is in flight, empty ahead of the run.
    /// </summary>
    /// <param name="phase">The step the track leaves, or null where the rail has no record of it.</param>
    /// <param name="stepProgress">
    /// How far through its own work the running step is, 0 to 1, or null when the step counts
    /// nothing it can report. Clamped, because the count and the total are reported separately and
    /// briefly disagree at a step boundary.
    /// </param>
    public static double FillPercent(ActivityPhase? phase, double? stepProgress)
    {
        if (phase == null)
            return 0d;

        if (phase.Status == ActivityPhaseStatus.Active)
            return Math.Clamp((stepProgress ?? 0d) * 100d, 0d, 100d);

        return HasRun(phase.Status) ? 100d : 0d;
    }

    /// <summary>
    /// What an unusual outcome means, for a marker's tooltip. Empty where the step's own state
    /// needs no explaining: a tooltip on every step would train the reader to ignore all of them,
    /// including the two that carry something worth reading.
    /// </summary>
    public static string OutcomeTooltip(ActivityPhase? phase) => phase?.Status switch
    {
        ActivityPhaseStatus.Skipped => "Not needed for this run",
        ActivityPhaseStatus.Failed => "The run failed at this step",
        _ => string.Empty
    };
}
