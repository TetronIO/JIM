// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scheduling;

/// <summary>
/// A Schedule Execution reduced to what a list view needs (#1162): the shape of the whole Schedule,
/// and where in it the execution has got to.
/// </summary>
/// <remarks>
/// The sibling of <see cref="Activities.RunPhaseSummary"/> one level up: that describes the steps
/// within a single Run Profile execution, this describes the steps of the Schedule running it.
/// </remarks>
public class ScheduleExecutionProgress
{
    /// <summary>
    /// The position of the step group running now (1-based), or null where none is: a Schedule
    /// between steps has a shape worth showing, and claiming a step is running would be a lie.
    /// </summary>
    public int? CurrentStepNumber { get; set; }

    /// <summary>
    /// The number of step groups in the Schedule. Always equal to <see cref="Steps"/>' count, so that
    /// the sentence and the drawing cannot disagree.
    /// </summary>
    public int TotalSteps => Steps.Count;

    /// <summary>
    /// Every step group, in the order the Schedule runs them.
    /// </summary>
    public IReadOnlyList<ScheduleStepProgress> Steps { get; set; } = [];
}
