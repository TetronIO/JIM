namespace JIM.Models.Activities;

public enum ActivityTargetOperationType
{
    Create = 0,
    Read = 1,
    Update = 2,
    Delete = 3,
    Clear = 4,
    Execute = 5,
    /// <summary>
    /// Relates to Connected Systems.
    /// </summary>
    ImportHierarchy = 6,
    /// <summary>
    /// Relates to Connected Systems.
    /// </summary>
    ImportSchema = 7
}
