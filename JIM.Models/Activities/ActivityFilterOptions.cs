namespace JIM.Models.Activities;

/// <summary>
/// Contains the available filter options for worker task activity queries.
/// Populated from distinct values in the activity history.
/// </summary>
public class ActivityFilterOptions
{
    /// <summary>
    /// Distinct connected system names (from TargetContext).
    /// </summary>
    public List<string> ConnectedSystems { get; set; } = [];

    /// <summary>
    /// Distinct run profile names (from TargetName).
    /// </summary>
    public List<string> RunProfiles { get; set; } = [];
}
