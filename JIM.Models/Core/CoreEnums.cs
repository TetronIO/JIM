namespace JIM.Models.Core;

public enum AttributeDataType
{
    NotSet = 0,
    Text = 1,
    Number = 2,
    DateTime = 3,
    Binary = 4,
    Reference = 5,
    Guid = 6,
    Boolean = 7
}

public enum AttributePlurality
{
    SingleValued = 0,
    MultiValued = 1
}

public enum MetaverseObjectStatus
{
    Normal = 0,
    Obsolete = 1
}


public enum MetaverseObjectChangeInitiatorType
{
    NotSet = 0,
    User = 1,
    WorkflowInstance = 2,
    GroupMembershipRuleEvaluation = 3,
    SynchronisationRule = 4
}

/// <summary>
/// Determines when a Metaverse Object should be deleted.
/// </summary>
public enum MetaverseObjectDeletionRule
{
    /// <summary>
    /// The MVO will never be automatically deleted. Manual deletion is required.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// The MVO will be deleted when the last Connected System Object is disconnected.
    /// If a grace period is configured on the MetaverseObjectType, deletion will be scheduled for after that period.
    /// </summary>
    WhenLastConnectorDisconnected = 1
}