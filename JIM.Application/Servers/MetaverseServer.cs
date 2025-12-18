using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Utility;
using Serilog;
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

    public async Task<MetaverseObjectType?> GetMetaverseObjectTypeByPluralNameAsync(string pluralName, bool includeChildObjects)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectTypeByPluralNameAsync(pluralName, includeChildObjects);
    }

    /// <summary>
    /// Updates an existing Metaverse Object Type.
    /// </summary>
    /// <param name="objectType">The object type to update.</param>
    public async Task UpdateMetaverseObjectTypeAsync(MetaverseObjectType objectType)
    {
        if (objectType == null)
            throw new ArgumentNullException(nameof(objectType));

        Log.Debug("UpdateMetaverseObjectTypeAsync() called for {ObjectType}", objectType.Name);
        await Application.Repository.Metaverse.UpdateMetaverseObjectTypeAsync(objectType);
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

    public async Task<MetaverseAttribute?> GetMetaverseAttributeWithObjectTypesAsync(int id)
    {
        return await Application.Repository.Metaverse.GetMetaverseAttributeWithObjectTypesAsync(id);
    }

    public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name)
    {
        return await Application.Repository.Metaverse.GetMetaverseAttributeAsync(name);
    }

    /// <summary>
    /// Creates a new Metaverse Attribute.
    /// </summary>
    /// <param name="attribute">The attribute to create.</param>
    /// <param name="initiatedBy">The user who initiated the creation.</param>
    public async Task CreateMetaverseAttributeAsync(MetaverseAttribute attribute, MetaverseObject? initiatedBy)
    {
        if (attribute == null)
            throw new ArgumentNullException(nameof(attribute));

        Log.Debug("CreateMetaverseAttributeAsync() called for {Attribute}", attribute.Name);

        var activity = new Activity
        {
            TargetName = attribute.Name,
            TargetType = ActivityTargetType.MetaverseAttribute,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await Application.Repository.Metaverse.CreateMetaverseAttributeAsync(attribute);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Metaverse Attribute.
    /// </summary>
    /// <param name="attribute">The attribute to update.</param>
    /// <param name="initiatedBy">The user who initiated the update.</param>
    public async Task UpdateMetaverseAttributeAsync(MetaverseAttribute attribute, MetaverseObject? initiatedBy)
    {
        if (attribute == null)
            throw new ArgumentNullException(nameof(attribute));

        Log.Debug("UpdateMetaverseAttributeAsync() called for {Attribute}", attribute.Name);

        var activity = new Activity
        {
            TargetName = attribute.Name,
            TargetType = ActivityTargetType.MetaverseAttribute,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await Application.Repository.Metaverse.UpdateMetaverseAttributeAsync(attribute);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes a Metaverse Attribute.
    /// </summary>
    /// <param name="attribute">The attribute to delete.</param>
    /// <param name="initiatedBy">The user who initiated the deletion.</param>
    public async Task DeleteMetaverseAttributeAsync(MetaverseAttribute attribute, MetaverseObject? initiatedBy)
    {
        if (attribute == null)
            throw new ArgumentNullException(nameof(attribute));

        Log.Debug("DeleteMetaverseAttributeAsync() called for {Attribute}", attribute.Name);

        var activity = new Activity
        {
            TargetName = attribute.Name,
            TargetType = ActivityTargetType.MetaverseAttribute,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await Application.Repository.Metaverse.DeleteMetaverseAttributeAsync(attribute);

        await Application.Activities.CompleteActivityAsync(activity);
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

    public async Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        await Application.Repository.Metaverse.DeleteMetaverseObjectAsync(metaverseObject);
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

    public async Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectsOfTypeAsync(
        PredefinedSearch predefinedSearch,
        int page = 1,
        int pageSize = 20,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectsOfTypeAsync(
            predefinedSearch, page, pageSize, searchQuery, sortBy, sortDescending);
    }

    /// <summary>
    /// Gets a paginated list of metaverse objects with optional filtering by type and search query.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <param name="searchQuery">Optional search query to filter by display name.</param>
    /// <param name="sortDescending">Whether to sort in descending order by created date.</param>
    /// <param name="attributes">Optional list of attribute names to include. DisplayName is always included.</param>
    /// <returns>A paged result set of metaverse object headers.</returns>
    public async Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectsAsync(
        int page = 1,
        int pageSize = 20,
        int? objectTypeId = null,
        string? searchQuery = null,
        bool sortDescending = true,
        IEnumerable<string>? attributes = null)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectsAsync(page, pageSize, objectTypeId, searchQuery, sortDescending, attributes);
    }

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
    public async Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, ObjectMatchingRule objectMatchingRule)
    {
        if (objectMatchingRule.Sources == null || objectMatchingRule.Sources.Count == 0)
            throw new ArgumentOutOfRangeException($"{nameof(objectMatchingRule)}.Sources is null or empty. Cannot continue.");

        return await Application.Repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(connectedSystemObject, metaverseObjectType, objectMatchingRule);
    }
    #endregion
}
