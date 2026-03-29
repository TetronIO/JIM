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
using JIM.Application.Utilities;
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

    public async Task<PagedResultSet<MetaverseAttributeHeader>> GetMetaverseAttributeHeadersAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false)
    {
        return await Application.Repository.Metaverse.GetMetaverseAttributeHeadersAsync(
            page, pageSize, searchQuery, sortBy, sortDescending);
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

        AuditHelper.SetCreated(attribute, initiatedBy);
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

        AuditHelper.SetUpdated(attribute, initiatedBy);
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

        AuditHelper.SetCreated(attribute, initiatedByApiKey);
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

        AuditHelper.SetUpdated(attribute, initiatedByApiKey);
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

    public async Task<MetaverseObject?> GetMetaverseObjectWithChangeHistoryAsync(Guid id)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectWithChangeHistoryAsync(id);
    }

    /// <summary>
    /// Loads a Metaverse Object with attribute loading controlled by the specified strategy.
    /// <see cref="MvoAttributeLoadStrategy.CappedMva"/> caps MVA values and includes per-attribute total counts.
    /// </summary>
    public async Task<MvoDetailResult?> GetMetaverseObjectDetailAsync(Guid id, MvoAttributeLoadStrategy loadStrategy)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectDetailAsync(id, loadStrategy);
    }

    public async Task<MetaverseObjectHeader?> GetMetaverseObjectHeaderAsync(Guid id)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectHeaderAsync(id);
    }

    /// <summary>
    /// Updates a Metaverse Object and optionally records the update for audit trail.
    /// When additions/removals are provided and change tracking is enabled, a change record is created automatically.
    /// Callers that only update operational metadata (e.g. deletion dates) can omit additions/removals
    /// to skip change tracking.
    /// </summary>
    /// <param name="metaverseObject">The MVO to update.</param>
    /// <param name="additions">Attribute values being added (null to skip change tracking).</param>
    /// <param name="removals">Attribute values being removed (null to skip change tracking).</param>
    /// <param name="initiatedByType">The type of principal initiating the update.</param>
    /// <param name="initiatedById">The ID of the principal initiating the update.</param>
    /// <param name="initiatedByName">The display name of the principal initiating the update.</param>
    /// <param name="changeInitiatorType">The mechanism that initiated the update (e.g. System, User).</param>
    public async Task UpdateMetaverseObjectAsync(
        MetaverseObject metaverseObject,
        List<MetaverseObjectAttributeValue>? additions = null,
        List<MetaverseObjectAttributeValue>? removals = null,
        ActivityInitiatorType initiatedByType = ActivityInitiatorType.NotSet,
        Guid? initiatedById = null,
        string? initiatedByName = null,
        MetaverseObjectChangeInitiatorType? changeInitiatorType = null)
    {
        // Record change history if additions/removals are provided
        if (additions != null || removals != null)
        {
            await CreateMetaverseObjectChangeAsync(
                metaverseObject,
                additions ?? new List<MetaverseObjectAttributeValue>(),
                removals ?? new List<MetaverseObjectAttributeValue>(),
                initiatedByType,
                initiatedById,
                initiatedByName,
                ObjectChangeType.Updated,
                changeInitiatorType);
        }

        await Application.Repository.Metaverse.UpdateMetaverseObjectAsync(metaverseObject);
    }

    /// <summary>
    /// Creates a MetaverseObjectChange record and adds it to the MVO's Changes collection.
    /// Called internally by CreateMetaverseObjectAsync and UpdateMetaverseObjectAsync.
    /// Also available to ExampleDataServer for batch operations where change records
    /// are attached to MVOs before bulk persistence.
    /// Sync operations use a separate batched approach in SyncTaskProcessorBase.
    /// </summary>
    internal async Task CreateMetaverseObjectChangeAsync(
        MetaverseObject metaverseObject,
        List<MetaverseObjectAttributeValue> additions,
        List<MetaverseObjectAttributeValue> removals,
        ActivityInitiatorType initiatedByType = ActivityInitiatorType.NotSet,
        Guid? initiatedById = null,
        string? initiatedByName = null,
        ObjectChangeType changeType = ObjectChangeType.Updated,
        MetaverseObjectChangeInitiatorType? changeInitiatorType = null)
    {
        // Check if MVO change tracking is enabled
        var changeTrackingEnabled = await Application.ServiceSettings.GetMvoChangeTrackingEnabledAsync();
        if (!changeTrackingEnabled)
            return;

        if (additions.Count == 0 && removals.Count == 0)
            return;

        // Derive change initiator type if not explicitly specified
        var effectiveChangeInitiatorType = changeInitiatorType
            ?? (initiatedByType == ActivityInitiatorType.User
                ? MetaverseObjectChangeInitiatorType.User
                : MetaverseObjectChangeInitiatorType.NotSet);

        // Create MVO change object
        var change = new MetaverseObjectChange
        {
            MetaverseObject = metaverseObject,
            ChangeType = changeType,
            ChangeTime = DateTime.UtcNow,
            InitiatedByType = initiatedByType,
            InitiatedById = initiatedById,
            InitiatedByName = initiatedByName,
            ChangeInitiatorType = effectiveChangeInitiatorType
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
    internal static void AddMvoChangeAttributeValueObject(
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
                AttributeName = metaverseObjectAttributeValue.Attribute.Name,
                AttributeType = metaverseObjectAttributeValue.Attribute.Type,
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
            case AttributeDataType.Reference when metaverseObjectAttributeValue.ReferenceValueId.HasValue:
                // Navigation property not loaded but FK is set — record the referenced MVO ID as a GUID.
                // This happens when attribute values are snapshotted without eagerly loading navigation
                // properties (e.g., during MVO deletion where the referenced MVOs may not be in the
                // EF change tracker).
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, metaverseObjectAttributeValue.ReferenceValueId.Value));
                break;
            case AttributeDataType.Reference when metaverseObjectAttributeValue.UnresolvedReferenceValue != null:
                // Don't track unresolved references
                break;
            case AttributeDataType.Reference:
                // Reference attribute with no resolved or unresolved value — nothing to track
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

    /// <summary>
    /// Creates a Metaverse Object and optionally records the creation for audit trail.
    /// Change tracking is handled automatically when enabled — callers just pass initiator info.
    /// </summary>
    /// <param name="metaverseObject">The MVO to create.</param>
    /// <param name="initiatedByType">The type of principal initiating the creation.</param>
    /// <param name="initiatedById">The ID of the principal initiating the creation.</param>
    /// <param name="initiatedByName">The display name of the principal initiating the creation.</param>
    /// <param name="changeInitiatorType">The mechanism that initiated the creation (e.g. System, ExampleData).</param>
    public async Task CreateMetaverseObjectAsync(
        MetaverseObject metaverseObject,
        ActivityInitiatorType initiatedByType = ActivityInitiatorType.NotSet,
        Guid? initiatedById = null,
        string? initiatedByName = null,
        MetaverseObjectChangeInitiatorType? changeInitiatorType = null)
    {
        // Record change history if enabled (before persist so EF saves in same transaction)
        if (metaverseObject.AttributeValues.Count > 0)
        {
            await CreateMetaverseObjectChangeAsync(
                metaverseObject,
                metaverseObject.AttributeValues,
                new List<MetaverseObjectAttributeValue>(),
                initiatedByType,
                initiatedById,
                initiatedByName,
                ObjectChangeType.Created,
                changeInitiatorType);
        }

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

    /// <summary>
    /// Deletes a Metaverse Object and optionally records the deletion for audit trail.
    /// Captures the final attribute values on the deletion change record for audit purposes.
    /// </summary>
    /// <param name="metaverseObject">The MVO to delete.</param>
    /// <param name="initiatedByType">The type of principal initiating the deletion.</param>
    /// <param name="initiatedById">The ID of the principal initiating the deletion.</param>
    /// <param name="initiatedByName">The display name of the principal initiating the deletion.</param>
    /// <param name="finalAttributeValues">Pre-captured attribute values to record as the final state.
    /// Required when the sync processor has already recalled attributes from the MVO before deletion.
    /// When null, the method uses the MVO's current attribute values (loading from DB if needed).</param>
    public async Task DeleteMetaverseObjectAsync(
        MetaverseObject metaverseObject,
        ActivityInitiatorType initiatedByType = ActivityInitiatorType.NotSet,
        Guid? initiatedById = null,
        string? initiatedByName = null,
        List<MetaverseObjectAttributeValue>? finalAttributeValues = null)
    {
        // Check if MVO change tracking is enabled
        var changeTrackingEnabled = await Application.ServiceSettings.GetMvoChangeTrackingEnabledAsync();

        if (changeTrackingEnabled)
        {
            // Determine the attribute values to capture as final state.
            // The sync processor path passes pre-captured values (snapshotted before attribute recall).
            // The housekeeping path doesn't pass them, so we use the MVO's current values
            // (loading from DB if needed since GetMetaverseObjectsEligibleForDeletionAsync
            // doesn't include AttributeValues).
            var attributesToCapture = finalAttributeValues;
            if (attributesToCapture == null)
            {
                if (metaverseObject.AttributeValues.Count == 0)
                {
                    await Application.Repository.Metaverse.LoadMetaverseObjectAttributeValuesAsync(metaverseObject);
                }
                attributesToCapture = metaverseObject.AttributeValues.ToList();
            }

            // Capture the MVO ID before deletion — EF Core may clear the Id property after SaveChangesAsync.
            var mvoId = metaverseObject.Id;

            // Resolve display name: prefer the MVO's current DisplayName (computed from AttributeValues),
            // but if attributes were already recalled (sync processor path), derive it from the snapshot.
            var displayName = metaverseObject.DisplayName;
            if (displayName == null && attributesToCapture.Count > 0)
            {
                var displayNameAttrValue = attributesToCapture.SingleOrDefault(
                    av => av.Attribute?.Name == Constants.BuiltInAttributes.DisplayName);
                displayName = displayNameAttrValue?.StringValue;
            }

            // Create a deletion change record.
            // IMPORTANT: Do NOT set the MetaverseObject navigation property! Setting it causes EF Core
            // to track and attempt to process the MVO entity during SaveChangesAsync, which triggers
            // FK constraint violations when other MVOs in the batch are pending deletion.
            // The MetaverseObjectId FK is always nulled for deletion records anyway —
            // GetDeletedMvoChangeHistoryAsync uses DeletedObjectDisplayName/DeletedObjectTypeId instead.
            var change = new MetaverseObjectChange
            {
                ChangeType = ObjectChangeType.Deleted,
                ChangeTime = DateTime.UtcNow,
                InitiatedByType = initiatedByType,
                InitiatedById = initiatedById,
                InitiatedByName = initiatedByName,
                ChangeInitiatorType = initiatedByType == ActivityInitiatorType.User
                    ? MetaverseObjectChangeInitiatorType.User
                    : MetaverseObjectChangeInitiatorType.NotSet,
                // Preserve object identity for the deleted objects browser
                DeletedMetaverseObjectId = mvoId,
                DeletedObjectTypeId = metaverseObject.Type?.Id,
                DeletedObjectDisplayName = displayName
            };

            // Capture final attribute values as removals so the deletion change
            // record preserves the final state of the object for audit purposes.
            foreach (var attributeValue in attributesToCapture)
            {
                AddMvoChangeAttributeValueObject(change, attributeValue, ValueChangeType.Remove);
            }

            // Delete the MVO first, then save the change record afterwards.
            // IMPORTANT: The change record intentionally does NOT set MetaverseObject navigation property
            // and is saved AFTER deletion to avoid two problems:
            // 1. Setting MetaverseObject would cause EF Core to track the entity and trigger FK issues
            // 2. Calling SaveChangesAsync before deletion would flush ALL tracked changes in the
            //    change tracker (including pending MVO deletions from the sync batch), causing FK
            //    constraint violations on MetaverseObjectAttributeValues reference FKs.
            // The deletion change record always has null MetaverseObjectId since the MVO no longer
            // exists — GetDeletedMvoChangeHistoryAsync finds related changes via DeletedObjectDisplayName
            // and DeletedObjectTypeId instead.
            await Application.Repository.Metaverse.DeleteMetaverseObjectAsync(metaverseObject);

            // Insert the change record via raw SQL, bypassing EF Core's change tracker.
            // Using CreateMetaverseObjectChangeAsync (which calls SaveChangesAsync) would flush
            // ALL tracked entities, including CSOs with stale FK references to the just-deleted MVO,
            // causing FK constraint violations.
            await Application.Repository.Metaverse.CreateMetaverseObjectChangeDirectAsync(change);
            return;
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
    /// <exception cref="NotImplementedException">Will be thrown if more than one source is specified (advanced matching). This is not yet supported.</exception>
    /// <exception cref="ArgumentNullException">Will be thrown if the object matching rule source connected system attribute is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Will be thrown if an unsupported attribute type is specified.</exception>
    /// <exception cref="MultipleMatchesException">Will be thrown if there's more than one Metaverse Object that matches the matching rule criteria.</exception>
    public async Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, ObjectMatchingRule objectMatchingRule)
    {
        if (objectMatchingRule.Sources == null || objectMatchingRule.Sources.Count == 0)
            throw new ArgumentOutOfRangeException($"{nameof(objectMatchingRule)}.Sources is null or empty. Cannot continue.");

        return await Application.Repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(connectedSystemObject, metaverseObjectType, objectMatchingRule);
    }

    /// <summary>
    /// Returns a paginated set of attribute values for a specific attribute on a Metaverse Object.
    /// Supports server-side search and pagination for large multi-valued attributes.
    /// </summary>
    public async Task<PagedResultSet<MetaverseObjectAttributeValue>> GetAttributeValuesPagedAsync(
        Guid metaverseObjectId,
        string attributeName,
        int page,
        int pageSize,
        string? searchText = null)
    {
        return await Application.Repository.Metaverse.GetAttributeValuesPagedAsync(
            metaverseObjectId, attributeName, page, pageSize, searchText);
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
