using System;

namespace JIM.Models.History
{
    public enum SyncRunHistoryDetailError
    {
        NotSet,
        CouldNotConnectToConnectedSystem
    }

    public enum SyncRunHistoryDetailItemError
    {
        NotSet,
        MissingUniqueIdentifierAttributeValue,
        CouldntMatchObjectType,
        UnsupportedUniqueIdentifierAttribyteType,
        UnexpectedAttribute,
        DuplicateImportedAttribute
    }
}
