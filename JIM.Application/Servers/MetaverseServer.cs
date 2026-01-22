using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Security;
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

    /// <summary>
    /// Creates a new Metaverse Attribute (initiated by API key).
    /// </summary>
    /// <param name="attribute">The attribute to create.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the creation.</param>
    public async Task CreateMetaverseAttributeAsync(MetaverseAttribute attribute, ApiKey initiatedByApiKey)
    {
        if (attribute == null)
            throw new ArgumentNullException(nameof(attribute));

        Log.Debug("CreateMetaverseAttributeAsync() called for {Attribute} (API key initiated)", attribute.Name);

        var activity = new Activity
        {
            TargetName = attribute.Name,
            TargetType = ActivityTargetType.MetaverseAttribute,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        await Application.Repository.Metaverse.CreateMetaverseAttributeAsync(attribute);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Metaverse Attribute (initiated by API key).
    /// </summary>
    /// <param name="attribute">The attribute to update.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the update.</param>
    public async Task UpdateMetaverseAttributeAsync(MetaverseAttribute attribute, ApiKey initiatedByApiKey)
    {
        if (attribute == null)
            throw new ArgumentNullException(nameof(attribute));

        Log.Debug("UpdateMetaverseAttributeAsync() called for {Attribute} (API key initiated)", attribute.Name);

        var activity = new Activity
        {
            TargetName = attribute.Name,
            TargetType = ActivityTargetType.MetaverseAttribute,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        await Application.Repository.Metaverse.UpdateMetaverseAttributeAsync(attribute);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes a Metaverse Attribute (initiated by API key).
    /// </summary>
    /// <param name="attribute">The attribute to delete.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the deletion.</param>
    public async Task DeleteMetaverseAttributeAsync(MetaverseAttribute attribute, ApiKey initiatedByApiKey)
    {
        if (attribute == null)
            throw new ArgumentNullException(nameof(attribute));

        Log.Debug("DeleteMetaverseAttributeAsync() called for {Attribute} (API key initiated)", attribute.Name);

        var activity = new Activity
        {
            TargetName = attribute.Name,
            TargetType = ActivityTargetType.MetaverseAttribute,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

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

    /// <summary>
    /// Creates a MetaverseObjectChange record for direct MVO updates (e.g., via UI/API).
    /// Call this BEFORE applying changes to the MVO when change tracking is needed.
    /// This is intended for future UI implementations - sync operations use a different batched approach.
    /// </summary>
    /// <param name="metaverseObject">The MVO being updated.</param>
    /// <param name="additions">Attribute values being added.</param>
    /// <param name="removals">Attribute values being removed.</param>
    /// <param name="initiatedBy">The user or system initiating the change.</param>
    public async Task CreateMetaverseObjectChangeAsync(
        MetaverseObject metaverseObject,
        List<MetaverseObjectAttributeValue> additions,
        List<MetaverseObjectAttributeValue> removals,
        MetaverseObject? initiatedBy = null)
    {
        // Check if MVO change tracking is enabled
        var changeTrackingEnabled = await Application.ServiceSettings.GetMvoChangeTrackingEnabledAsync();
        if (!changeTrackingEnabled)
            return;

        if (additions.Count == 0 && removals.Count == 0)
            return;

        // Create MVO change object
        var change = new MetaverseObjectChange
        {
            MetaverseObject = metaverseObject,
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = DateTime.UtcNow,
            ChangeInitiator = initiatedBy,
            ChangeInitiatorType = initiatedBy != null
                ? MetaverseObjectChangeInitiatorType.User
                : MetaverseObjectChangeInitiatorType.NotSet
        };

        // Create attribute change records (reuse helper from sync processor pattern)
        foreach (var addition in additions)
        {
            AddMvoChangeAttributeValueObject(change, addition, ValueChangeType.Add);
        }

        foreach (var removal in removals)
        {
            AddMvoChangeAttributeValueObject(change, removal, ValueChangeType.Remove);
        }

        // Add to MVO's Changes collection
        metaverseObject.Changes.Add(change);
    }

    /// <summary>
    /// Helper method to create attribute change records for MVO changes.
    /// Mirrors the pattern used in SyncTaskProcessorBase for consistency.
    /// </summary>
    private static void AddMvoChangeAttributeValueObject(
        MetaverseObjectChange metaverseObjectChange,
        MetaverseObjectAttributeValue metaverseObjectAttributeValue,
        ValueChangeType valueChangeType)
    {
        var attributeChange = metaverseObjectChange.AttributeChanges.SingleOrDefault(
            ac => ac.Attribute.Id == metaverseObjectAttributeValue.Attribute.Id);

        if (attributeChange == null)
        {
            attributeChange = new MetaverseObjectChangeAttribute
            {
                Attribute = metaverseObjectAttributeValue.Attribute,
                MetaverseObjectChange = metaverseObjectChange
            };
            metaverseObjectChange.AttributeChanges.Add(attributeChange);
        }

        switch (metaverseObjectAttributeValue.Attribute.Type)
        {
            case AttributeDataType.Text when metaverseObjectAttributeValue.StringValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, metaverseObjectAttributeValue.StringValue));
                break;
            case AttributeDataType.Number when metaverseObjectAttributeValue.IntValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, (int)metaverseObjectAttributeValue.IntValue));
                break;
            case AttributeDataType.LongNumber when metaverseObjectAttributeValue.LongValue != null:
                // TODO: MetaverseObjectChangeAttributeValue needs LongValue support
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, (int)metaverseObjectAttributeValue.LongValue.Value));
                break;
            case AttributeDataType.Guid when metaverseObjectAttributeValue.GuidValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, (Guid)metaverseObjectAttributeValue.GuidValue));
                break;
            case AttributeDataType.Boolean when metaverseObjectAttributeValue.BoolValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, (bool)metaverseObjectAttributeValue.BoolValue));
                break;
            case AttributeDataType.DateTime when metaverseObjectAttributeValue.DateTimeValue.HasValue:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, metaverseObjectAttributeValue.DateTimeValue.Value));
                break;
            case AttributeDataType.Binary when metaverseObjectAttributeValue.ByteValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, true, metaverseObjectAttributeValue.ByteValue.Length));
                break;
            case AttributeDataType.Reference when metaverseObjectAttributeValue.ReferenceValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, metaverseObjectAttributeValue.ReferenceValue));
                break;
            case AttributeDataType.Reference when metaverseObjectAttributeValue.UnresolvedReferenceValue != null:
                // Don't track unresolved references
                break;
            default:
                throw new NotImplementedException(
                    $"Attribute data type {metaverseObjectAttributeValue.Attribute.Type} is not yet supported for MVO change tracking.");
        }
    }

    public async Task<MetaverseObject?> GetMetaverseObjectByTypeAndAttributeAsync(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(metaverseObjectType, metaverseAttribute, attributeValue);
    }

    public async Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        await Application.Repository.Metaverse.CreateMetaverseObjectAsync(metaverseObject);
    }

    /// <summary>
    /// Creates multiple Metaverse Objects in a single batch operation.
    /// This is more efficient than calling CreateMetaverseObjectAsync for each object.
    /// </summary>
    /// <param name="metaverseObjects">The list of Metaverse Objects to create.</param>
    public async Task CreateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
    {
        await Application.Repository.Metaverse.CreateMetaverseObjectsAsync(metaverseObjects);
    }

    /// <summary>
    /// Updates multiple Metaverse Objects in a single batch operation.
    /// This is more efficient than calling UpdateMetaverseObjectAsync for each object.
    /// </summary>
    /// <param name="metaverseObjects">The list of Metaverse Objects to update.</param>
    public async Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
    {
        await Application.Repository.Metaverse.UpdateMetaverseObjectsAsync(metaverseObjects);
    }

    public async Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        // Check if MVO change tracking is enabled
        var changeTrackingEnabled = await Application.ServiceSettings.GetMvoChangeTrackingEnabledAsync();

        if (changeTrackingEnabled)
        {
            // Create a deletion change record before deleting
            var change = new MetaverseObjectChange
            {
                // MetaverseObject is intentionally null for DELETE operations (MVO no longer exists after delete)
                ChangeType = ObjectChangeType.Deleted,
                ChangeTime = DateTime.UtcNow,
                ChangeInitiatorType = MetaverseObjectChangeInitiatorType.NotSet
                // TODO: When UI is implemented, pass initiatedBy parameter to set ChangeInitiator
            };

            // Add to MVO's Changes collection - will be persisted before deletion
            metaverseObject.Changes.Add(change);
        }

        await Application.Repository.Metaverse.DeleteMetaverseObjectAsync(metaverseObject);
    }

    /// <summary>
    /// Gets Metaverse Objects that are eligible for automatic deletion based on deletion rules.
    /// Returns MVOs where the grace period has elapsed after all connectors were disconnected.
    /// Protected objects (Origin=Internal) are never returned.
    /// </summary>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <returns>List of MVOs eligible for deletion.</returns>
    public async Task<List<MetaverseObject>> GetMetaverseObjectsEligibleForDeletionAsync(int maxResults = 100)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync(maxResults);
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
    /// Gets a paginated list of metaverse objects with optional filtering by type, search query, or specific attribute value.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <param name="searchQuery">Optional search query to filter by display name.</param>
    /// <param name="sortDescending">Whether to sort in descending order by created date.</param>
    /// <param name="attributes">Optional list of attribute names to include. DisplayName is always included.</param>
    /// <param name="filterAttributeName">Optional attribute name to filter by (must be used with filterAttributeValue).</param>
    /// <param name="filterAttributeValue">Optional attribute value to filter by (exact match, case-insensitive).</param>
    /// <returns>A paged result set of metaverse object headers.</returns>
    public async Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectsAsync(
        int page = 1,
        int pageSize = 20,
        int? objectTypeId = null,
        string? searchQuery = null,
        bool sortDescending = true,
        IEnumerable<string>? attributes = null,
        string? filterAttributeName = null,
        string? filterAttributeValue = null)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectsAsync(
            page, pageSize, objectTypeId, searchQuery, sortDescending, attributes, filterAttributeName, filterAttributeValue);
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

    /// <summary>
    /// Marks MVOs as disconnected that will become orphaned when the specified Connected System is deleted.
    /// This sets LastConnectorDisconnectedDate so housekeeping will delete them after the grace period.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System being deleted.</param>
    /// <returns>The number of MVOs marked for deletion.</returns>
    public async Task<int> MarkOrphanedMvosForDeletionAsync(int connectedSystemId)
    {
        Log.Information("MarkOrphanedMvosForDeletionAsync: Finding orphaned MVOs for Connected System {Id}", connectedSystemId);

        // Find MVOs that will become orphaned when this Connected System is deleted
        var orphanedMvos = await Application.Repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(connectedSystemId);

        if (orphanedMvos.Count == 0)
        {
            Log.Information("MarkOrphanedMvosForDeletionAsync: No orphaned MVOs found for Connected System {Id}", connectedSystemId);
            return 0;
        }

        Log.Information("MarkOrphanedMvosForDeletionAsync: Found {Count} orphaned MVOs for Connected System {Id}", orphanedMvos.Count, connectedSystemId);

        // Mark them as disconnected so housekeeping will delete them after the grace period
        var mvoIds = orphanedMvos.Select(mvo => mvo.Id).ToList();
        var markedCount = await Application.Repository.Metaverse.MarkMvosAsDisconnectedAsync(mvoIds);

        Log.Information("MarkOrphanedMvosForDeletionAsync: Marked {Count} MVOs for deletion for Connected System {Id}", markedCount, connectedSystemId);

        return markedCount;
    }
    #endregion
}
