namespace JIM.Models.Activities;

public enum ActivityRunProfileExecutionItemErrorType
{
    NotSet,
    AmbiguousMatch,
    CouldNotMatchObjectType,
    CouldNotJoinDueToExistingJoin,
    DuplicateImportedAttributes,
    MissingExternalIdAttributeValue,
    UnexpectedAttribute,
    UnresolvedReference,
    UnsupportedExternalIdAttributeType,
    UnhandledError,
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
}
