namespace JIM.Models.Activities;

public enum ActivityTargetOperationType
{
    Create = 0,
    Read = 1,
    Update = 2,
    Delete = 3,
    /// <summary>
    /// Intended for clearing all objects from a Connected System.
    /// </summary>
    Clear = 4,
    /// <summary>
    /// Intended for executing a Data Generation Template.
    /// </summary>
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
