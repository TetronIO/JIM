// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The SCIM store's column lists and schema names, the single source of truth for its raw-SQL writers
/// (<see cref="ScimStoreRepository"/>). Each writer interpolates its list through <c>BulkSqlHelpers.ToQuotedList</c>
/// and MUST write values in exactly list order. BulkInsertColumnCompletenessTests asserts each insert list matches the
/// EF model's mapped columns, and that every resource column has a conscious home in the update list or its
/// documented exclusion list, so a migration cannot silently leave a writer behind.
/// </summary>
internal static class ScimStoreColumns
{
    /// <summary>
    /// The stored generated column holding <c>lower("UserName")</c>, the case-insensitive uniqueness key. A shadow
    /// property: nothing outside the database reads it.
    /// </summary>
    internal const string UserNameNormalised = "UserNameNormalised";

    /// <summary>
    /// The unique index a duplicate <c>userName</c> violates; a write naming it is reported as a uniqueness conflict.
    /// </summary>
    internal const string UserNameUniqueIndex = "IX_ScimStoreResources_UserName_Unique";

    /// <summary>
    /// The unique index a duplicate <c>externalId</c> violates.
    /// </summary>
    internal const string ExternalIdUniqueIndex = "IX_ScimStoreResources_ExternalId_Unique";

    /// <summary>
    /// Insert columns for ScimStoreResources. <c>UserNameNormalised</c> is omitted: the database generates it.
    /// </summary>
    internal static readonly string[] Resources =
    [
        "Id", "ConnectedSystemId", "ResourceType", "UserName", "ExternalId", "DisplayName", "Version", "ChangeSequence",
        "Created", "LastModified", "DeletedAt", "Payload"
    ];

    /// <summary>
    /// Update columns for replacing a live resource: what a PUT or PATCH can change, plus the stamps every write sets.
    /// <c>Version</c> is assigned in SQL rather than from a parameter, so it is listed with the exclusions.
    /// </summary>
    internal static readonly string[] ResourcesUpdate =
    [
        "UserName", "ExternalId", "DisplayName", "ChangeSequence", "LastModified", "Payload"
    ];

    /// <summary>
    /// Resource columns the update deliberately does not write: the identity of the row (<c>Id</c>,
    /// <c>ConnectedSystemId</c>, <c>ResourceType</c>, <c>Created</c>), the version (raised in SQL), and
    /// <c>DeletedAt</c>, which only the tombstone statement sets.
    /// </summary>
    internal static readonly string[] ResourcesUpdateExclusions =
    [
        "Id", "ConnectedSystemId", "ResourceType", "Created", "Version", "DeletedAt"
    ];

    /// <summary>
    /// Insert columns for ScimStoreGroupMembers.
    /// </summary>
    internal static readonly string[] GroupMembers = ["GroupId", "MemberId"];

    /// <summary>
    /// Insert columns for ScimStoreStates: a Connected System's first write creates its row.
    /// </summary>
    internal static readonly string[] States = ["ConnectedSystemId", "LastChangeSequence", "TombstonePurgeFloor"];

    /// <summary>
    /// Insert columns for ScimStoreJournalEntries. <c>Id</c> is omitted: it is an identity column.
    /// </summary>
    internal static readonly string[] JournalEntries =
    [
        "ConnectedSystemId", "ChangeSequence", "ResourceId", "ResourceType", "Operation", "ChangedAttributes",
        "ApiKeyId", "ApiKeyName", "ClientAddress", "Occurred"
    ];
}
