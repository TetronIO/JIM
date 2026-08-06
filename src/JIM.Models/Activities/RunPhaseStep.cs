// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// One of a run's own steps, reduced to what a list view needs to draw it (#1162): where it sits,
/// what it is called, and how it turned out.
/// </summary>
/// <remarks>
/// Deliberately not an <see cref="ActivityPhase"/>. A queue row draws a rail a few pixels tall and
/// needs no timestamps, no keys and no parent, and carrying the entity would invite a list view to
/// start depending on fields the detail page owns.
/// </remarks>
public class RunPhaseStep
{
    /// <summary>
    /// Position in the run, ascending.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// The administrator-facing step label, as recorded when the run started.
    /// </summary>
    public string Name { get; set; } = null!;

    public ActivityPhaseStatus Status { get; set; }
}
