using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Utility;
namespace JIM.Application.Servers;

public class MetaverseServer
{
    #region accessors
    private JimApplication Application { get; }
    #endregion

    #region constructors
    internal MetaverseServer(JimApplication application)
    {
        Application = application;
    }
    #endregion

    #region metaverse object types
    public async Task<List<MetaverseObjectType>> GetMetaverseObjectTypesAsync(bool includeChildObjects)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectTypesAsync(includeChildObjects);
    }

    public async Task<List<MetaverseObjectTypeHeader>> GetMetaverseObjectTypeHeadersAsync()
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectTypeHeadersAsync();
    }

    public async Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(int id, bool includeChildObjects)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(id, includeChildObjects);
    }

    public async Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(string objectTypeName, bool includeChildObjects)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(objectTypeName, includeChildObjects);
    }
    #endregion

    #region metaverse attributes
    public async Task<IList<MetaverseAttribute>?> GetMetaverseAttributesAsync()
    {
        return await Application.Repository.Metaverse.GetMetaverseAttributesAsync();
    }

    public async Task<IList<MetaverseAttributeHeader>?> GetMetaverseAttributeHeadersAsync()
    {
        return await Application.Repository.Metaverse.GetMetaverseAttributeHeadersAsync();
    }

    public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(int id)
    {
        return await Application.Repository.Metaverse.GetMetaverseAttributeAsync(id);
    }

    public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name)
    {
        return await Application.Repository.Metaverse.GetMetaverseAttributeAsync(name);
    }
    #endregion

    #region metaverse objects
    public async Task<MetaverseObject?> GetMetaverseObjectAsync(Guid id)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectAsync(id);
    }

    public async Task<MetaverseObjectHeader?> GetMetaverseObjectHeaderAsync(Guid id)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectHeaderAsync(id);
    }

    public async Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        await Application.Repository.Metaverse.UpdateMetaverseObjectAsync(metaverseObject);
    }

    public async Task<MetaverseObject?> GetMetaverseObjectByTypeAndAttributeAsync(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(metaverseObjectType, metaverseAttribute, attributeValue);
    }

    public async Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        await Application.Repository.Metaverse.CreateMetaverseObjectAsync(metaverseObject);
    }

    public async Task<int> GetMetaverseObjectCountAsync()
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectCountAsync();
    }

    public async Task<int> GetMetaverseObjectOfTypeCountAsync(MetaverseObjectType metaverseObjectType)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectOfTypeCountAsync(metaverseObjectType.Id);
    }

    public async Task<PagedResultSet<MetaverseObject>> GetMetaverseObjectsOfTypeAsync(MetaverseObjectType metaverseObjectType, int page = 1, int pageSize = 20)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectsOfTypeAsync(metaverseObjectType.Id, page, pageSize);
    }

    public async Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectsOfTypeAsync(PredefinedSearch predefinedSearch, int page = 1, int pageSize = 20)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectsOfTypeAsync(predefinedSearch, page, pageSize);
    }
    
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
    public async Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, SyncRuleMapping syncRuleMapping)
    {
        if (syncRuleMapping.Sources == null || syncRuleMapping.Sources.Count == 0)
            throw new ArgumentOutOfRangeException($"{nameof(syncRuleMapping)}.Sources is null or empty. Cannot continue.");
        
        return await Application.Repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(connectedSystemObject, metaverseObjectType, syncRuleMapping);
    }
    #endregion
}
