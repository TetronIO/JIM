namespace JIM.Models.Staging;

public enum ConnectedSystemObjectStatus
{
    Normal = 0,
    Obsolete = 1,
    /// <summary>
    /// The CSO was created as part of provisioning evaluation but the export has not yet been confirmed.
    /// Once the export succeeds or import confirms the object exists, status transitions to Normal.
    /// </summary>
    PendingProvisioning = 2
}

/// <summary>
/// Determines where object matching rules are configured for a Connected System.
/// </summary>
public enum ObjectMatchingRuleMode
{
    /// <summary>
    /// Default: Object matching rules are defined at the Connected System Object Type level.
    /// These rules are shared across all sync rules for that object type.
    /// </summary>
    ConnectedSystem = 0,
    /// <summary>
    /// Advanced: Object matching rules are defined per sync rule.
    /// Allows different matching logic for different sync rules.
    /// </summary>
    SyncRule = 1
}

public enum ConnectedSystemObjectJoinType
{
    /// <summary>
    /// Default state, the Connected System Object is not joined to a Metaverse Object.
    /// </summary>
    NotJoined = 0,
    /// <summary>
    /// The Connected System Object was projected to the Metaverse, resulting in a Metaverse Object being created.
    /// </summary>
    Projected = 1,
    /// <summary>
    /// A sync rule required a Metaverse Object to be provisioned to the Connected System, resulting in a Connected System Object being created.
    /// </summary>
    Provisioned = 2,
    /// <summary>
    /// The CSO and MVO both existed and were joined (not as the result of projection or provisioning).
    /// This is common when a Connected System is cleared down, objects re-imported and synchronised, and joins re-established. In this scenario, the original project/provision metadata is lost.
    /// </summary>
    Joined = 3
}

public enum ConnectedSystemSettingCategory
{
    Connectivity,
    General,
    Capabilities,
    Schema,
    Import,
    Export
}

public enum ConnectedSystemSettingType
{
    String,
    StringEncrypted,
    Integer,
    Heading,
    Label,
    Text,
    DropDown,
    CheckBox,
    Divider,
    File
}

public enum ConnectedSystemImportObjectError
{
    NotSet,
    /// <summary>
    /// We were unable to determine what type of object was returned from the connected system.
    /// </summary>
    CouldNotDetermineObjectType,
    /// <summary>
    /// The attribute(s) used to uniquely identify the object could not be found on the object we got from the connected system.
    /// </summary>
    ExternalIdAttributes,
    /// <summary>
    /// There's been an issue with the configuration of JIM and import cannot complete.
    /// </summary>
    ConfigurationError,
    /// <summary>
    /// An attribute value could not be parsed to the expected type (e.g., invalid number, date, or GUID format).
    /// </summary>
    AttributeValueError
}

/// <summary>
/// Defines the type of run profile operation to execute against a Connected System.
/// </summary>
public enum ConnectedSystemRunType
{
    /// <summary>
    /// Default value indicating no run type has been assigned.
    /// </summary>
    NotSet = 0,
    /// <summary>
    /// Imports all objects from the Connected System, replacing the existing connector space staging data.
    /// </summary>
    FullImport = 1,
    /// <summary>
    /// Imports only objects that have changed in the Connected System since the last import.
    /// </summary>
    DeltaImport = 2,
    /// <summary>
    /// Synchronises all Connected System Objects with the Metaverse, evaluating join/projection rules,
    /// attribute flow, and provisioning for every object in the connector space.
    /// </summary>
    FullSynchronisation = 3,
    /// <summary>
    /// Synchronises only Connected System Objects that have pending changes since the last synchronisation,
    /// evaluating join/projection rules, attribute flow, and provisioning for those objects only.
    /// </summary>
    DeltaSynchronisation = 4,
    /// <summary>
    /// Exports pending changes from the Metaverse to the Connected System, applying attribute updates,
    /// object creation, and object deletion as determined by prior synchronisation.
    /// </summary>
    Export = 5
}

/// <summary>
/// Classifies the type of export error for structured error reporting.
/// </summary>
public enum ConnectedSystemExportErrorType
{
    /// <summary>General or unclassified export error.</summary>
    General,

    /// <summary>
    /// The generated external identifier (e.g., LDAP Distinguished Name) is structurally invalid.
    /// This typically occurs when expression-based ID attributes evaluate with null or empty
    /// input values, producing malformed identifiers.
    /// </summary>
    InvalidGeneratedExternalId
}
