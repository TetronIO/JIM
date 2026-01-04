namespace JIM.Models.Enums;

public enum ObjectChangeType
{
    NotSet,
    Create,
    Update,
    Obsolete,
    Delete,
    /// <summary>
    /// Indicates that export evaluation detected the CSO already has the target value(s),
    /// so no pending export was created. Used for tracking/reporting purposes.
    /// </summary>
    NoChange
}