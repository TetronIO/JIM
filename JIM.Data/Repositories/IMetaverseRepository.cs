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
    #endregion

    #region objects
    public Task<MetaverseObject?> GetMetaverseObjectAsync(Guid id);

    public Task<MetaverseObjectHeader?> GetMetaverseObjectHeaderAsync(Guid id);

    public Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject);

    public Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject);

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
        QuerySortBy querySortBy = QuerySortBy.DateCreated,
        QueryRange queryRange = QueryRange.Forever);

    /// <summary>
    /// Attempts to find a single Metaverse Object using criteria from a SyncRuleMapping object and attribute values from a Connected System Object.
    /// This is to help the process of joining a CSO to an MVO.
    /// </summary>
    /// <param name="connectedSystemObject">The source object to try and find a matching Metaverse Object for.</param>
    /// <param name="metaverseObjectType">The type of Metaverse Object to search for.</param>
    /// <param name="syncRuleMapping">The Sync Rule Mapping contains the logic needed to construct a Metaverse Object query.</param>
    /// <returns>A Metaverse Object if a single result is found, otherwise null.</returns>
    /// <exception cref="NotImplementedException">Will be thrown if more than one source is specified. This is not yet supported.</exception>
    /// <exception cref="ArgumentNullException">Will be thrown if the sync rule mapping source connected system attribute is null.</exception>
    /// <exception cref="NotSupportedException">Will be thrown if functions or expressions are in use in the sync rule mapping. These are not yet supported.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Will be thrown if an unsupported attribute type is specified.</exception>
    /// <exception cref="MultipleMatchesException">Will be thrown if there's more than one Metaverse Object that matches the sync rule mapping criteria.</exception>
    public Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, SyncRuleMapping syncRuleMapping);

    /// <summary>
    /// Deletes a Metaverse Object from the database.
    /// </summary>
    /// <param name="metaverseObject">The Metaverse Object to delete.</param>
    public Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject);
    #endregion

    #region attributes
    public Task<IList<MetaverseAttribute>?> GetMetaverseAttributesAsync();

    public Task<IList<MetaverseAttributeHeader>?> GetMetaverseAttributeHeadersAsync();

    public Task<MetaverseAttribute?> GetMetaverseAttributeAsync(int id);

    public Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name);
    #endregion
}