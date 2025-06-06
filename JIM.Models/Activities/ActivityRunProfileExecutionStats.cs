﻿using System.ComponentModel.DataAnnotations.Schema;

namespace JIM.Models.Activities;

/// <summary>
/// Not persisted.
/// </summary>
[NotMapped]
public class ActivityRunProfileExecutionStats
{
    public Guid ActivityId { get; set; }

    public int TotalObjectChangeCount { get; set; }

    public int TotalObjectCreates { get; set; }
        
    public int TotalObjectUpdates { get; set; }

    public int TotalObjectDeletes { get; set; }

    public int TotalObjectErrors { get; set; }

    public int TotalObjectTypes { get; set; }
}