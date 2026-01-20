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

    public Task UpdateMetaverseObjectTypeAsync(MetaverseObjectType metaverseObjectType);
    #endregion

    #region objects
    public Task<MetaverseObject?> GetMetaverseObjectAsync(Guid id);

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
    /// Gets a paginated list of metaverse objects with optional filtering by type, search query, or specific attribute value.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <param name="searchQuery">Optional search query to filter by display name.</param>
    /// <param name="sortDescending">Whether to sort in descending order by created date.</param>
    /// <param name="attributes">Optional list of attribute names to include. Use "*" to include all attributes. DisplayName is always included.</param>
    /// <param name="filterAttributeName">Optional attribute name to filter by (must be used with filterAttributeValue).</param>
    /// <param name="filterAttributeValue">Optional attribute value to filter by (exact match, case-insensitive).</param>
    /// <returns>A paged result set of metaverse object headers.</returns>
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
    /// <exception cref="NotImplementedException">Will be thrown if more than one source is specified. This is not yet supported.</exception>
    /// <exception cref="ArgumentNullException">Will be thrown if the object matching rule source connected system attribute is null.</exception>
    /// <exception cref="NotSupportedException">Will be thrown if functions or expressions are in use in the matching rule. These are not yet supported.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Will be thrown if an unsupported attribute type is specified.</exception>
    /// <exception cref="MultipleMatchesException">Will be thrown if there's more than one Metaverse Object that matches the matching rule criteria.</exception>
    public Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, ObjectMatchingRule objectMatchingRule);

    /// <summary>
    /// Deletes a Metaverse Object from the database.
    /// </summary>
    /// <param name="metaverseObject">The Metaverse Object to delete.</param>
    public Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject);

    /// <summary>
    /// Gets Metaverse Objects that are eligible for automatic deletion based on deletion rules.
    /// Returns MVOs where:
    /// - Origin = Projected (not Internal - protects admin accounts)
    /// - Type.DeletionRule = WhenLastConnectorDisconnected
    /// - LastConnectorDisconnectedDate + GracePeriodDays <= now
    /// - No connected system objects remain
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
    #endregion

    #region attributes
    public Task<IList<MetaverseAttribute>?> GetMetaverseAttributesAsync();

    public Task<IList<MetaverseAttributeHeader>?> GetMetaverseAttributeHeadersAsync();

    public Task<MetaverseAttribute?> GetMetaverseAttributeAsync(int id);

    /// <summary>
    /// Gets a Metaverse Attribute by ID including its associated object types.
    /// </summary>
    /// <param name="id">The unique identifier of the attribute.</param>
    /// <returns>The attribute with its associated object types, or null if not found.</returns>
    public Task<MetaverseAttribute?> GetMetaverseAttributeWithObjectTypesAsync(int id);

    public Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name);

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
    #endregion
}