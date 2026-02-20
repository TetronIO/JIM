namespace JIM.Models.Staging;

/// <summary>
/// Represents the operational status of a Connected System.
/// </summary>
public enum ConnectedSystemStatus
{
    /// <summary>
    /// System is active and can accept sync operations.
    /// </summary>
    Active = 0,

    /// <summary>
    /// System is disabled and will not accept sync operations.
    /// </summary>
    Disabled = 1,

    /// <summary>
    /// System is being deleted - all sync operations are blocked.
    /// This is a transient state that should not persist long-term.
    /// </summary>
    Deleting = 2
}
