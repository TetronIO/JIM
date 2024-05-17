namespace JIM.Models.Activities;

public enum ActivityRunProfileExecutionItemErrorType
{
    NotSet,
    CouldNotMatchObjectType,
    DuplicateImportedAttributes,
    MissingExternalIdAttributeValue,
    UnexpectedAttribute,
    UnresolvedReference,
    UnsupportedExternalIdAttributeType,
    UnhandledError,
}
