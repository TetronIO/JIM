// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scheduling.DTOs;

/// <summary>
/// The enabled Schedule that will next add its steps to the queue, and when. A "Summary" tier projection, read for the
/// Operations page's Service Health Scheduler card (#2549).
/// </summary>
public class NextDueSchedule
{
    /// <summary>
    /// The unique identifier of the Schedule.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The Schedule's name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// When the Schedule is next due to run (UTC).
    /// </summary>
    public DateTime NextRunTime { get; set; }
}
