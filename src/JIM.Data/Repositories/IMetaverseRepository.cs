// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Utility;
namespace JIM.Data.Repositories;

public interface IMetaverseRepository
{
    #region object types
    public Task<List<MetaverseObjectType>> GetMetaverseObjectTypesAsync(bool includeChildObjects);

    public Task<List<MetaverseObjectTypeHeader>> GetMetaverseObjectTypeHeadersAsync();

    public Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(int id, bool includeChildObjects);

    public Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(string name, bool includeChildObjects);

    public Task<MetaverseObjectType?> GetMetaverseObjectTypeByPluralNameAsync(string pluralName, bool includeChildObjects);

    /// <summary>
    /// Creates a new Metaverse Object Type. Caller is responsible for ensuring Name and
    /// PluralName are unique and that BuiltIn is set appropriately (false for customer-created types).
    /// </summary>
    /// <param name="metaverseObjectType">The object type to create.</param>
    public Task CreateMetaverseObjectTypeAsync(MetaverseObjectType metaverseObjectType);

    public Task UpdateMetaverseObjectTypeAsync(MetaverseObjectType metaverseObjectType);
    #endregion

    #region objects
    public Task<MetaverseObject?> GetMetaverseObjectAsync(Guid id);

    public Task<MetaverseObject?> GetMetaverseObjectWithChangeHistoryAsync(Guid id);

    /// <summary>
    /// Loads a Metaverse Object with attribute loading controlled by the specified strategy.
    /// <see cref="MvoAttributeLoadStrategy.CappedMva"/> caps MVA values and includes per-attribute total counts.
    /// Change history is NOT loaded eagerly; the change-row count is surfaced on the result so callers
    /// can render a count badge, and the change rows themselves are loaded via
    /// <see cref="GetMvoChangeHistoryAsync"/>.
    /// </summary>
    public Task<MvoDetailResult?> GetMetaverseObjectDetailAsync(Guid id, MvoAttributeLoadStrategy loadStrategy);

    /// <summary>
    /// Returns a page of change-history records for a Metaverse Object, projected into a flat DTO
    /// so the full entity graph is not materialised. Ordered by <c>ChangeTime</c> descending.
    /// </summary>
    /// <param name="metaverseObjectId">The MVO whose change history is being read.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Rows per page; clamp to a sensible upper bound at the call site.</param>
    /// <returns>The page of changes plus the total count of change records for the MVO.</returns>
    public Task<(List<MvoChangeHistoryDto> Items, int TotalCount)> GetMvoChangeHistoryAsync(Guid metaverseObjectId, int page, int pageSize);

    public Task<MetaverseObjectHeader?> GetMetaverseObjectHeaderAsync(Guid id);

    public Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject);

    public Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject);

    /// <summary>
    /// Creates multiple Metaverse Objects in a single batch operation.
    /// This is more efficient than calling CreateMetaverseObjectAsync for each object.
    /// </summary>
    /// <param name="metaverseObjects">The list of Metaverse Objects to create.</param>
    public Task CreateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects);

    /// <summary>
    /// Updates multiple Metaverse Objects in a single batch operation.
    /// This is more efficient than calling UpdateMetaverseObjectAsync for each object.
    /// </summary>
    /// <param name="metaverseObjects">The list of Metaverse Objects to update.</param>
    public Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects);

    public Task<MetaverseObject?> GetMetaverseObjectByTypeAndAttributeAsync(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue);

    public Task<int> GetMetaverseObjectCountAsync();

    public Task<int> GetMetaverseObjectOfTypeCountAsync(int metaverseObjectTypeId);

    /// <summary>
    /// Gets the count of Metaverse Objects with optional filtering by type, search query, or specific attribute value.
    /// Optimised for fast counting without loading entity data.
    /// </summary>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <param name="searchQuery">Optional search query to filter by display name (partial match, case-insensitive).</param>
    /// <param name="filterAttributeName">Optional attribute name to filter by (must be used with filterAttributeValue).</param>
    /// <param name="filterAttributeValue">Optional attribute value to filter by (exact match, case-insensitive).</param>
    /// <returns>The count of matching Metaverse Objects.</returns>
    public Task<int> GetMetaverseObjectsCountAsync(
        int? objectTypeId = null,
        string? searchQuery = null,
        string? filterAttributeName = null,
        string? filterAttributeValue = null);

    public Task<PagedResultSet<MetaverseObject>> GetMetaverseObjectsOfTypeAsync(
        int metaverseObjectTypeId,
        int page,
        int pageSize,
        QuerySortBy querySortBy = QuerySortBy.DateCreated,
        QueryRange queryRange = QueryRange.Forever);

    public Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectsOfTypeAsync(
        PredefinedSearch predefinedSearch,
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true);

    /// <summary>
    /// Gets a paginated list of lightweight Metaverse Object headers with only the attributes defined
    /// in the PredefinedSearch projected directly in SQL. No EF Include chain is used — attribute values
    /// are projected inline for optimum performance at scale (100k+ objects).
    /// </summary>
    /// <param name="predefinedSearch">The predefined search defining the object type, criteria groups, and display attributes.</param>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page (max 100).</param>
    /// <param name="searchQuery">Optional search query to filter across all string attribute values.</param>
    /// <param name="sortBy">Optional attribute name to sort by.</param>
    /// <param name="sortDescending">Whether to sort in descending order.</param>
    public Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectHeadersPagedAsync(
        PredefinedSearch predefinedSearch,
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true);

    /// <summary>
    /// Gets a paginated list of Metaverse Objects with optional filtering by type, search query, or specific attribute value.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <param name="searchQuery">Optional search query to filter by display name.</param>
    /// <param name="sortDescending">Whether to sort in descending order by created date.</param>
    /// <param name="attributes">Optional list of attribute names to include. Use "*" to include all attributes. DisplayName is always included.</param>
    /// <param name="filterAttributeName">Optional attribute name to filter by (must be used with filterAttributeValue).</param>
    /// <param name="filterAttributeValue">Optional attribute value to filter by (exact match, case-insensitive).</param>
    /// <returns>A paged result set of Metaverse Object headers.</returns>
    public Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectsAsync(
        int page,
        int pageSize,
        int? objectTypeId = null,
        string? searchQuery = null,
        bool sortDescending = true,
        IEnumerable<string>? attributes = null,
        string? filterAttributeName = null,
        string? filterAttributeValue = null);

    /// <summary>
    /// Attempts to find a single Metaverse Object using criteria from a SyncRuleMapping object and attribute values from a Connected System Object.
    /// This is to help the process of joining a CSO to an MVO.
    /// </summary>
    /// <param name="connectedSystemObject">The source object to try and find a matching Metaverse Object for.</param>
    /// <param name="metaverseObjectType">The type of Metaverse Object to search for.</param>
    /// <param name="objectMatchingRule">The Object Matching Rule contains the logic needed to construct a Metaverse Object query.</param>
    /// <returns>A Metaverse Object if a single result is found, otherwise null.</returns>
    /// <exception cref="NotImplementedException">Will be thrown if more than one source is specified (advanced matching). This is not yet supported.</exception>
    /// <exception cref="ArgumentNullException">Will be thrown if the Object Matching Rule source Connected System attribute is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Will be thrown if an unsupported attribute type is specified.</exception>
    /// <exception cref="MultipleMatchesException">Will be thrown if there's more than one Metaverse Object that matches the matching rule criteria.</exception>
    public Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, ObjectMatchingRule objectMatchingRule);

    /// <summary>
    /// Deletes a Metaverse Object from the database.
    /// </summary>
    /// <param name="metaverseObject">The Metaverse Object to delete.</param>
    public Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject);

    /// <summary>
    /// Explicitly loads the AttributeValues (and their Attribute navigation) for an MVO
    /// that was queried without them. Used to capture final attribute state before deletion.
    /// </summary>
    /// <param name="metaverseObject">The MVO to load attribute values for.</param>
    public Task LoadMetaverseObjectAttributeValuesAsync(MetaverseObject metaverseObject);

    /// <summary>
    /// Sets the DeletedMetaverseObjectId on a change record via raw SQL.
    /// Used as a safety measure after saving deletion change records, since EF Core
    /// entity tracking state after MVO deletion may not persist the value correctly.
    /// </summary>
    public Task SetDeletedMetaverseObjectIdAsync(Guid changeId, Guid metaverseObjectId);

    /// <summary>
    /// Inserts a MetaverseObjectChange and its attribute changes via raw SQL,
    /// bypassing the EF Core change tracker. Used for deletion change records to avoid
    /// SaveChangesAsync flushing other tracked entities with stale FK references.
    /// </summary>
    public Task CreateMetaverseObjectChangeDirectAsync(MetaverseObjectChange change);

    /// <summary>
    /// Gets Metaverse Objects that are eligible for automatic deletion based on deletion rules.
    /// Returns MVOs where:
    /// - Origin = Projected (not Internal - protects admin accounts)
    /// - Type.DeletionRule = WhenLastConnectorDisconnected
    /// - LastConnectorDisconnectedDate + GracePeriodDays <= now
    /// - No Connected System Objects remain
    /// </summary>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <returns>List of MVOs eligible for deletion.</returns>
    public Task<List<MetaverseObject>> GetMetaverseObjectsEligibleForDeletionAsync(int maxResults = 100);

    /// <summary>
    /// Gets MVOs that will become orphaned when the specified Connected System is deleted.
    /// An MVO is considered orphaned if it:
    /// - Has DeletionRule = WhenLastConnectorDisconnected
    /// - Has Origin = Projected (not internal)
    /// - Is ONLY connected to the specified Connected System (no other connectors)
    /// </summary>
    /// <param name="connectedSystemId">The Connected System being deleted.</param>
    /// <returns>List of MVOs that will become orphaned.</returns>
    public Task<List<MetaverseObject>> GetMvosOrphanedByConnectedSystemDeletionAsync(int connectedSystemId);

    /// <summary>
    /// Marks MVOs as disconnected by setting their LastConnectorDisconnectedDate.
    /// Used when a Connected System is deleted to prepare orphaned MVOs for housekeeping deletion.
    /// </summary>
    /// <param name="mvoIds">The IDs of the MVOs to mark as disconnected.</param>
    /// <returns>The number of MVOs updated.</returns>
    public Task<int> MarkMvosAsDisconnectedAsync(IEnumerable<Guid> mvoIds);

    /// <summary>
    /// Gets MVOs that are pending deletion (have LastConnectorDisconnectedDate set but haven't been deleted yet).
    /// These are MVOs awaiting their grace period to expire before automatic deletion.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <returns>A paged result set of MVOs pending deletion.</returns>
    public Task<PagedResultSet<MetaverseObject>> GetMetaverseObjectsPendingDeletionAsync(
        int page,
        int pageSize,
        int? objectTypeId = null);

    /// <summary>
    /// Gets the count of MVOs that are pending deletion.
    /// </summary>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <returns>The count of MVOs pending deletion.</returns>
    public Task<int> GetMetaverseObjectsPendingDeletionCountAsync(int? objectTypeId = null);

    /// <summary>
    /// Creates a MetaverseObjectChange record directly in the database.
    /// Used for DELETE operations where the change should not be linked via navigation property
    /// because the MVO is about to be deleted.
    /// </summary>
    /// <param name="change">The change record to create.</param>
    public Task CreateMetaverseObjectChangeAsync(MetaverseObjectChange change);

    /// <summary>
    /// Gets MVO changes where the MVO has been deleted (ChangeType = Deleted and MetaverseObject is null).
    /// Used for the deleted objects browser.
    /// </summary>
    /// <param name="objectTypeId">Optional filter by object type ID.</param>
    /// <param name="fromDate">Optional filter for changes on or after this date.</param>
    /// <param name="toDate">Optional filter for changes on or before this date.</param>
    /// <param name="displayNameSearch">Optional search term for display name.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <returns>Paginated list of deleted MVO changes ordered by ChangeTime descending.</returns>
    Task<(List<MetaverseObjectChange> Items, int TotalCount)> GetDeletedMvoChangesAsync(
        int? objectTypeId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? displayNameSearch = null,
        int page = 1,
        int pageSize = 50);

    /// <summary>
    /// Gets the full change history for a deleted MVO by its change ID.
    /// </summary>
    /// <param name="changeId">The ID of the MVO change record.</param>
    /// <returns>List of all changes for that MVO ordered by ChangeTime descending.</returns>
    Task<List<MetaverseObjectChange>> GetDeletedMvoChangeHistoryAsync(Guid changeId);
    /// <summary>
    /// Returns a paginated set of attribute values for a specific attribute on a Metaverse Object.
    /// Supports server-side search and pagination for large multi-valued attributes.
    /// </summary>
    public Task<PagedResultSet<MetaverseObjectAttributeValue>> GetAttributeValuesPagedAsync(
        Guid metaverseObjectId,
        string attributeName,
        int page,
        int pageSize,
        string? searchText = null);
    #endregion

    #region attributes
    public Task<IList<MetaverseAttribute>?> GetMetaverseAttributesAsync();

    public Task<IList<MetaverseAttributeHeader>?> GetMetaverseAttributeHeadersAsync();

    /// <summary>
    /// Retrieves a page of Metaverse Attribute Headers with sorting and search support.
    /// </summary>
    public Task<PagedResultSet<MetaverseAttributeHeader>> GetMetaverseAttributeHeadersAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false);

    public Task<MetaverseAttribute?> GetMetaverseAttributeAsync(int id, bool withChangeTracking = false);

    /// <summary>
    /// Gets a Metaverse Attribute by ID including its associated object types.
    /// </summary>
    /// <param name="id">The unique identifier of the attribute.</param>
    /// <param name="withChangeTracking">When true, enables EF Core change tracking for write operations.</param>
    /// <returns>The attribute with its associated object types, or null if not found.</returns>
    public Task<MetaverseAttribute?> GetMetaverseAttributeWithObjectTypesAsync(int id, bool withChangeTracking = false);

    public Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name, bool withChangeTracking = false);

    /// <summary>
    /// Creates a new Metaverse Attribute.
    /// </summary>
    /// <param name="attribute">The attribute to create.</param>
    public Task CreateMetaverseAttributeAsync(MetaverseAttribute attribute);

    /// <summary>
    /// Updates an existing Metaverse Attribute.
    /// </summary>
    /// <param name="attribute">The attribute to update.</param>
    public Task UpdateMetaverseAttributeAsync(MetaverseAttribute attribute);

    /// <summary>
    /// Deletes a Metaverse Attribute.
    /// </summary>
    /// <param name="attribute">The attribute to delete.</param>
    public Task DeleteMetaverseAttributeAsync(MetaverseAttribute attribute);

    /// <summary>
    /// Counts the number of distinct Metaverse Objects that have at least one value
    /// stored for the specified attribute.
    /// </summary>
    /// <param name="attributeId">The unique identifier of the attribute.</param>
    /// <returns>The count of distinct Metaverse Objects with values for this attribute.</returns>
    public Task<int> GetAttributeValueObjectCountAsync(int attributeId);

    /// <summary>
    /// Counts the number of distinct Metaverse Objects of a specific type that have
    /// at least one value stored for the specified attribute.
    /// </summary>
    /// <param name="attributeId">The unique identifier of the attribute.</param>
    /// <param name="metaverseObjectTypeId">The unique identifier of the object type to filter by.</param>
    /// <returns>The count of distinct Metaverse Objects of the given type with values for this attribute.</returns>
    public Task<int> GetAttributeValueObjectCountByTypeAsync(int attributeId, int metaverseObjectTypeId);

    /// <summary>
    /// Gets the Synchronisation Rules that reference the specified metaverse attribute via
    /// Synchronisation Rule mappings, mapping sources, Object Matching Rules, or scoping criteria.
    /// </summary>
    /// <param name="attributeId">The unique identifier of the attribute.</param>
    /// <returns>A list of Synchronisation Rule references (ID and Name) that use this attribute.</returns>
    public Task<List<SyncRuleReference>> GetSyncRulesReferencingAttributeAsync(int attributeId);
    #endregion
}