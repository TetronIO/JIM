// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The SCIM store's column lists, the single source of truth for its raw-SQL writers (<see cref="ScimStoreWriteSession"/>
/// and <see cref="ScimStoreRepository"/>). Each writer interpolates its list through <c>BulkSqlHelpers.ToQuotedList</c>
/// and MUST write values in exactly list order. BulkInsertColumnCompletenessTests asserts each insert list matches the
/// EF model's mapped columns, and that every resource column has a conscious home in the update list or its documented
/// exclusion list, so a migration cannot silently leave a writer behind. The resource type and schema definitions are
/// configuration, written through EF Core, and have no list here.
/// </summary>
internal static class ScimStoreColumns
{
    /// <summary>
    /// Insert columns for ScimStoreResources.
    /// </summary>
    internal static readonly string[] Resources =
    [
        "Id", "ConnectedSystemId", "ResourceTypeId", "Version", "ChangeSequence", "Created", "LastModified", "DeletedAt",
        "Payload"
    ];

    /// <summary>
    /// Update columns for replacing a live resource: its payload, plus the stamps every write sets. <c>Version</c> is
    /// assigned in SQL rather than from a parameter, so it is listed with the exclusions.
    /// </summary>
    internal static readonly string[] ResourcesUpdate = ["ChangeSequence", "LastModified", "Payload"];

    /// <summary>
    /// Resource columns the update deliberately does not write: the identity of the row (<c>Id</c>,
    /// <c>ConnectedSystemId</c>, <c>ResourceTypeId</c>, <c>Created</c>), the version (raised in SQL), and
    /// <c>DeletedAt</c>, which only the tombstone statement sets.
    /// </summary>
    internal static readonly string[] ResourcesUpdateExclusions =
    [
        "Id", "ConnectedSystemId", "ResourceTypeId", "Created", "Version", "DeletedAt"
    ];

    /// <summary>
    /// Insert columns for ScimStoreUniqueValues.
    /// </summary>
    internal static readonly string[] UniqueValues =
    [
        "ConnectedSystemId", "ResourceTypeId", "Attribute", "Value", "ValueHash", "ResourceId"
    ];

    /// <summary>
    /// Insert columns for ScimStoreReferences.
    /// </summary>
    internal static readonly string[] References = ["SourceId", "Attribute", "TargetId"];

    /// <summary>
    /// Insert columns for ScimStoreStates: a Connected System's first write creates its row.
    /// </summary>
    internal static readonly string[] States = ["ConnectedSystemId", "LastChangeSequence", "TombstonePurgeFloor"];

    /// <summary>
    /// Insert columns for ScimStoreJournalEntries. <c>Id</c> is omitted: it is an identity column.
    /// </summary>
    internal static readonly string[] JournalEntries =
    [
        "ConnectedSystemId", "ChangeSequence", "ResourceId", "ResourceTypeName", "Operation", "ChangedAttributes",
        "IgnoredAttributes", "ApiKeyId", "ApiKeyName", "ClientAddress", "Occurred"
    ];
}
