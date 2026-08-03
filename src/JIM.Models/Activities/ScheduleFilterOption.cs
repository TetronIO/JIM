// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// A single Schedule option for the Operations History Schedule filter. The dropdown filters on the Schedule's
/// id but must show its name, so both travel together. Projected from the Activity history's own denormalised
/// attribution columns, so every option is guaranteed to return rows.
/// </summary>
public class ScheduleFilterOption
{
    /// <summary>
    /// The Schedule's id, as recorded on the Activities it produced.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The most recently recorded name for that Schedule, so a renamed Schedule reads as administrators know it now.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
