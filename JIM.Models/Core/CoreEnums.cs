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

/// <summary>
/// Tracks how a Metaverse Object was created - determines deletion rule applicability.
/// </summary>
public enum MetaverseObjectOrigin
{
    /// <summary>
    /// MVO was projected from a Connected System Object.
    /// Subject to automatic deletion rules when configured.
    /// </summary>
    Projected = 0,

    /// <summary>
    /// MVO was created directly in JIM (e.g., admin accounts, service accounts).
    /// NOT subject to automatic deletion when connectors disconnect.
    /// </summary>
    Internal = 1
}

/// <summary>
/// Action to take when an MVO falls out of an export sync rule's scope.
/// </summary>
public enum OutboundDeprovisionAction
{
    /// <summary>
    /// Break the join, mark CSO as disconnected/unmanaged.
    /// CSO remains in the connected system but JIM no longer manages it.
    /// </summary>
    Disconnect = 0,

    /// <summary>
    /// Break the join AND delete the CSO from the connected system.
    /// </summary>
    Delete = 1

    // Post-MVP: Disable = 2, MoveToArchiveOU = 3
}

/// <summary>
/// Action to take when a CSO falls out of an import sync rule's scope.
/// </summary>
public enum InboundOutOfScopeAction
{
    /// <summary>
    /// Keep the join intact even though CSO no longer matches scope.
    /// Useful for "once managed, always managed" scenarios.
    /// </summary>
    RemainJoined = 0,

    /// <summary>
    /// Break the join (disconnect CSO from MVO).
    /// CSO marked Obsolete, MVO deletion rules may then trigger.
    /// </summary>
    Disconnect = 1
}

/// <summary>
/// Specifies how a trusted certificate was added to the certificate store.
/// </summary>
public enum CertificateSourceType
{
    /// <summary>
    /// Certificate was uploaded and stored in the database.
    /// </summary>
    Uploaded = 0,

    /// <summary>
    /// Certificate is referenced by file path in the connector-files mount.
    /// </summary>
    FilePath = 1
}

/// <summary>
/// Categories for service settings in the admin UI.
/// </summary>
public enum ServiceSettingCategory
{
    /// <summary>
    /// Single Sign-On related settings.
    /// </summary>
    SSO = 0,

    /// <summary>
    /// Synchronisation and connector settings.
    /// </summary>
    Synchronisation = 1,

    /// <summary>
    /// Maintenance and operational settings.
    /// </summary>
    Maintenance = 2,

    /// <summary>
    /// History and audit retention settings.
    /// </summary>
    History = 3
}

/// <summary>
/// Data types for service settings.
/// </summary>
public enum ServiceSettingValueType
{
    /// <summary>
    /// Plain text string value.
    /// </summary>
    String = 0,

    /// <summary>
    /// Boolean true/false value.
    /// </summary>
    Boolean = 1,

    /// <summary>
    /// Integer numeric value.
    /// </summary>
    Integer = 2,

    /// <summary>
    /// Time span duration value.
    /// </summary>
    TimeSpan = 3,

    /// <summary>
    /// Enumeration value - requires EnumTypeName to be set.
    /// </summary>
    Enum = 4
}

/// <summary>
/// Determines how JIM handles run profile execution when no partitions/containers are selected.
/// </summary>
public enum PartitionValidationMode
{
    /// <summary>
    /// Execution is blocked with an error. The run profile will not execute.
    /// This is the default and recommended setting.
    /// </summary>
    Error = 0,

    /// <summary>
    /// A warning is returned but execution proceeds (may return 0 objects).
    /// </summary>
    Warning = 1
}