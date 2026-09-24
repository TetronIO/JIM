// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using MudBlazor;

namespace JIM.Web.Models;

/// <summary>
/// How a Schedule's failure handling reads on screen (#1787): the options of the Schedule's "When a step fails"
/// setting and of each step's "When this step fails" setting, and the indicator on every step row saying what the
/// step does when it fails and where that comes from.
/// <para>
/// The effective behaviour is never worked out here; it comes from <see cref="ScheduleFailureHandling"/>, the one
/// resolver the Scheduler also uses, so the editor cannot describe a step differently from how it will be run.
/// </para>
/// </summary>
public static class ScheduleFailureBehaviourDisplay
{
    /// <summary>
    /// An option of the Schedule's "When a step fails" setting.
    /// </summary>
    public static string ScheduleOption(ScheduleFailureBehaviour behaviour) => behaviour == ScheduleFailureBehaviour.Continue
        ? "Continue the Schedule"
        : "Stop the Schedule";

    /// <summary>
    /// An option of a step's "When this step fails" setting. Following the Schedule states what the Schedule is
    /// currently set to, so the choice can be made without leaving the step editor.
    /// </summary>
    public static string StepOption(ScheduleStepFailureBehaviour option, ScheduleFailureBehaviour scheduleBehaviour) => option switch
    {
        ScheduleStepFailureBehaviour.Stop => "Stop the Schedule",
        ScheduleStepFailureBehaviour.Continue => "Continue the Schedule",
        _ => $"Follow the Schedule (currently: {(scheduleBehaviour == ScheduleFailureBehaviour.Continue ? "continue" : "stop")})"
    };

    /// <summary>
    /// What a step does to its Schedule when it fails.
    /// </summary>
    public static string Effective(bool continuesOnFailure) => continuesOnFailure ? "Continues on failure" : "Stops on failure";

    /// <summary>
    /// The chip colour for <see cref="Effective"/>. Continuing is the exception worth noticing, because it lets later
    /// steps run after a failure; stopping is the default and stays quiet.
    /// </summary>
    public static Color EffectiveColour(bool continuesOnFailure) => continuesOnFailure ? Color.Warning : Color.Default;

    /// <summary>
    /// Where a step's effective behaviour comes from.
    /// </summary>
    public static string Source(ScheduleFailureBehaviourSource source) => source == ScheduleFailureBehaviourSource.Schedule
        ? "From the Schedule"
        : "Set on this step";

    /// <summary>
    /// The step row indicator for a step of the given Schedule: what it does when it fails, the chip colour for that,
    /// and where it comes from. Resolved from the in-memory Schedule, so an unsaved change to the Schedule's setting is
    /// reflected on every step that follows it.
    /// </summary>
    public static (string Effective, Color Colour, string Source) ForStep(ScheduleStep step, Schedule? schedule)
    {
        var continues = ScheduleFailureHandling.ContinuesOnFailure(step, schedule);
        return (Effective(continues), EffectiveColour(continues), Source(ScheduleFailureHandling.Source(step)));
    }

    /// <summary>
    /// Why a Schedule Execution carried on past a step that failed, for the marker beside that step's status.
    /// </summary>
    public static string ContinuedAfterFailure(ScheduleFailureBehaviourSource source) => source == ScheduleFailureBehaviourSource.Schedule
        ? "This step follows the Schedule, which is set to continue when a step fails."
        : "This step is set to let the Schedule continue when it fails.";
}
