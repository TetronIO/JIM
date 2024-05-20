namespace JIM.Models.Activities;

public enum ActivityRunProfileExecutionItemErrorType
{
    NotSet,
    CouldNotMatchObjectType,
    CouldNotJoinDueToExistingJoin,
    DuplicateImportedAttributes,
    MissingExternalIdAttributeValue,
    UnexpectedAttribute,
    UnresolvedReference,
    UnsupportedExternalIdAttributeType,
    UnhandledError,
}
