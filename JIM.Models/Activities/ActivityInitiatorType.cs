namespace JIM.Models.Activities;

/// <summary>
/// Identifies the type of security principal that initiated an activity.
/// Used for audit trail purposes to distinguish between user-initiated and automated actions.
/// </summary>
public enum ActivityInitiatorType
{
    /// <summary>
    /// The initiator type has not been set. This should not occur in production.
    /// </summary>
    NotSet = 0,

    /// <summary>
    /// The activity was initiated by a user (represented as a MetaverseObject).
    /// </summary>
    User = 1,

    /// <summary>
    /// The activity was initiated via an API key (automation, CI/CD, integration testing).
    /// </summary>
    ApiKey = 2
}
