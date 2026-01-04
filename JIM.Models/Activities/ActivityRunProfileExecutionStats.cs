using System.ComponentModel.DataAnnotations.Schema;

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

    /// <summary>
    /// Count of objects where no MVO attributes relevant to export rules changed.
    /// Only populated when verbose no-change recording is enabled.
    /// </summary>
    public int TotalMvoNoAttributeChanges { get; set; }

    /// <summary>
    /// Count of objects where the CSO already had the target value(s).
    /// Only populated when verbose no-change recording is enabled.
    /// </summary>
    public int TotalCsoAlreadyCurrent { get; set; }
}