namespace JIM.Models.Activities;

public enum ActivityRunProfileExecutionItemErrorType
{
    /// <summary>
    /// Default value indicating no error has been set.
    /// Excluded from error counts and statistics.
    /// </summary>
    NotSet,
    /// <summary>
    /// Object matching found multiple Metaverse Objects matching a single Connected System Object
    /// during a join operation. An MVO can only be joined to a single CSO per Connected System.
    /// The administrator should review Object Matching Rules to ensure unique matches.
    /// </summary>
    AmbiguousMatch,
    /// <summary>
    /// Could not find a valid object type in the Connected System schema for the imported object type,
    /// or the object type exists but has no external ID attribute defined.
    /// </summary>
    CouldNotMatchObjectType,
    /// <summary>
    /// A Connected System Object cannot be joined to a Metaverse Object because that MVO already
    /// has a CSO from this Connected System. This typically indicates duplicate data in the source
    /// system or Object Matching Rules that are not sufficiently unique.
    /// </summary>
    CouldNotJoinDueToExistingJoin,
    /// <summary>
    /// The imported object contains duplicate attribute names (case-insensitive).
    /// The source data must be de-duplicated before it can be imported.
    /// </summary>
    DuplicateImportedAttributes,
    /// <summary>
    /// The imported object is missing a value for the external ID attribute.
    /// Every imported object must have a non-empty external ID to be uniquely identified.
    /// </summary>
    MissingExternalIdAttributeValue,
    /// <summary>
    /// The imported object contains an attribute that is not defined in the Connected System schema.
    /// The schema should be updated to include the attribute, or the import data corrected.
    /// </summary>
    UnexpectedAttribute,
    /// <summary>
    /// An attribute contains a reference to another Connected System Object that could not be found.
    /// This may occur when the referenced object is outside the configured container scope.
    /// </summary>
    UnresolvedReference,
    /// <summary>
    /// The external ID attribute has a data type that is not supported for use as an external identifier.
    /// </summary>
    UnsupportedExternalIdAttributeType,
    /// <summary>
    /// During confirming import, one or more exported attribute values were not found on the CSO.
    /// The export will be retried.
    /// </summary>
    ExportNotConfirmed,
    /// <summary>
    /// During confirming import, one or more exported attribute values exceeded the maximum retry count.
    /// Manual intervention may be required.
    /// </summary>
    ExportConfirmationFailed,
    /// <summary>
    /// CSO creation failed during import but no specific error was recorded.
    /// This indicates a bug in the import processing logic.
    /// </summary>
    CsoCreationFailed,
    /// <summary>
    /// Multiple objects in the same import batch have the same external ID.
    /// All duplicates are marked with this error - none are processed.
    /// The data owner must fix the source data to ensure unique external IDs.
    /// </summary>
    DuplicateObject,
    /// <summary>
    /// The export generated an external identifier (e.g., LDAP Distinguished Name) that is
    /// structurally invalid. This typically occurs when expression-based ID attributes evaluate
    /// with null or empty input values, producing malformed identifiers.
    /// </summary>
    InvalidGeneratedExternalId,

    /// <summary>
    /// An unexpected exception occurred during sync processing. The full exception message and
    /// stack trace are captured. This indicates a bug in the processing logic rather than a data issue.
    /// </summary>
    UnhandledError
}
