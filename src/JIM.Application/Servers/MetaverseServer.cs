// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
using JIM.Application.Diagnostics;
using JIM.Application.Exceptions;
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

    #region Metaverse Object Types
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
        using var span = Diagnostics.Diagnostics.Database.StartSpan("Mvo.GetTypeByPluralName")
            .SetTag("pluralName", pluralName)
            .SetTag("includeChildObjects", includeChildObjects);
        return await Application.Repository.Metaverse.GetMetaverseObjectTypeByPluralNameAsync(pluralName, includeChildObjects);
    }

    /// <summary>
    /// Creates a new Metaverse Object Type, audited and tracked as an Activity.
    /// </summary>
    /// <param name="objectType">The object type to create. Name and PluralName must be unique.</param>
    /// <param name="initiatedBy">The Metaverse Object that initiated the creation (may be null for system-initiated).</param>
    public async Task CreateMetaverseObjectTypeAsync(MetaverseObjectType objectType, MetaverseObject? initiatedBy, string? changeReason = null)
    {
        if (objectType == null)
            throw new ArgumentNullException(nameof(objectType));

        Log.Debug("CreateMetaverseObjectTypeAsync() called for {ObjectType}", objectType.Name);

        var activity = new Activity
        {
            TargetName = objectType.Name,
            TargetType = ActivityTargetType.MetaverseObjectType,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        AuditHelper.SetCreated(objectType, initiatedBy);
        await Application.Repository.Metaverse.CreateMetaverseObjectTypeAsync(objectType);

        await CaptureObjectTypeConfigurationChangeAsync(activity, objectType.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Creates a new Metaverse Object Type, audited and tracked as an Activity. API-key initiator overload.
    /// </summary>
    /// <param name="objectType">The object type to create. Name and PluralName must be unique.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the creation.</param>
    public async Task CreateMetaverseObjectTypeAsync(MetaverseObjectType objectType, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        if (objectType == null)
            throw new ArgumentNullException(nameof(objectType));

        Log.Debug("CreateMetaverseObjectTypeAsync() called for {ObjectType} (API key initiated)", objectType.Name);

        var activity = new Activity
        {
            TargetName = objectType.Name,
            TargetType = ActivityTargetType.MetaverseObjectType,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        AuditHelper.SetCreated(objectType, initiatedByApiKey);
        await Application.Repository.Metaverse.CreateMetaverseObjectTypeAsync(objectType);

        await CaptureObjectTypeConfigurationChangeAsync(activity, objectType.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Metaverse Object Type, audited and tracked as an Activity.
    /// </summary>
    /// <param name="objectType">The object type to update.</param>
    /// <param name="initiatedBy">The Metaverse Object that initiated the update (may be null for system-initiated).</param>
    /// <param name="changeReason">Optional reason for the change, recorded on the audit Activity.</param>
    public async Task UpdateMetaverseObjectTypeAsync(MetaverseObjectType objectType, MetaverseObject? initiatedBy, string? changeReason = null)
    {
        if (objectType == null)
            throw new ArgumentNullException(nameof(objectType));

        Log.Debug("UpdateMetaverseObjectTypeAsync() called for {ObjectType}", objectType.Name);

        var activity = new Activity
        {
            TargetName = objectType.Name,
            TargetType = ActivityTargetType.MetaverseObjectType,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        AuditHelper.SetUpdated(objectType, initiatedBy);
        await Application.Repository.Metaverse.UpdateMetaverseObjectTypeAsync(objectType);

        await CaptureObjectTypeConfigurationChangeAsync(activity, objectType.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Metaverse Object Type, audited and tracked as an Activity. API-key initiator overload.
    /// </summary>
    /// <param name="objectType">The object type to update.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the update.</param>
    /// <param name="changeReason">Optional reason for the change, recorded on the audit Activity.</param>
    public async Task UpdateMetaverseObjectTypeAsync(MetaverseObjectType objectType, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        if (objectType == null)
            throw new ArgumentNullException(nameof(objectType));

        Log.Debug("UpdateMetaverseObjectTypeAsync() called for {ObjectType} (API key initiated)", objectType.Name);

        var activity = new Activity
        {
            TargetName = objectType.Name,
            TargetType = ActivityTargetType.MetaverseObjectType,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        AuditHelper.SetUpdated(objectType, initiatedByApiKey);
        await Application.Repository.Metaverse.UpdateMetaverseObjectTypeAsync(objectType);

        await CaptureObjectTypeConfigurationChangeAsync(activity, objectType.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Determines whether a Metaverse Object Type <paramref name="name"/> is available, comparing case-insensitively.
    /// Backs the real-time create/rename availability check. Supply <paramref name="excludeObjectTypeId"/> when
    /// validating a rename so the type does not clash with its own current name.
    /// </summary>
    public async Task<bool> IsMetaverseObjectTypeNameAvailableAsync(string name, int? excludeObjectTypeId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var existing = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(name, includeChildObjects: false);
        return existing == null || existing.Id == excludeObjectTypeId;
    }

    /// <summary>
    /// Determines whether a Metaverse Object Type <paramref name="pluralName"/> is available, comparing
    /// case-insensitively. Supply <paramref name="excludeObjectTypeId"/> when validating a rename.
    /// </summary>
    public async Task<bool> IsMetaverseObjectTypePluralNameAvailableAsync(string pluralName, int? excludeObjectTypeId = null)
    {
        if (string.IsNullOrWhiteSpace(pluralName))
            return false;

        var existing = await Application.Repository.Metaverse.GetMetaverseObjectTypeByPluralNameAsync(pluralName, includeChildObjects: false);
        return existing == null || existing.Id == excludeObjectTypeId;
    }

    /// <summary>
    /// Updates a custom Metaverse Object Type's identity (name, plural name, icon) through the audited path, re-checking
    /// name and plural-name uniqueness case-insensitively (excluding the type itself). Built-in types are immutable in
    /// their identity (their deletion rules are changed via <see cref="UpdateMetaverseObjectTypeAsync(MetaverseObjectType, MetaverseObject?, string?)"/>).
    /// This is the guarded path the UI edit dialog uses so that the built-in and uniqueness rules hold regardless of the
    /// REST controller.
    /// </summary>
    /// <exception cref="ArgumentException">No object type exists with the given id, or a required name is blank.</exception>
    /// <exception cref="InvalidOperationException">The type is built-in, or the new name / plural name is already used.</exception>
    public Task RenameMetaverseObjectTypeAsync(int objectTypeId, string newName, string newPluralName, string? icon, MetaverseObject? initiatedBy, string? changeReason = null) =>
        RenameMetaverseObjectTypeCoreAsync(objectTypeId, newName, newPluralName, icon, objectType => UpdateMetaverseObjectTypeAsync(objectType, initiatedBy, changeReason));

    /// <summary>
    /// Updates a custom Metaverse Object Type's identity through the audited path (API-key initiator overload).
    /// </summary>
    public Task RenameMetaverseObjectTypeAsync(int objectTypeId, string newName, string newPluralName, string? icon, ApiKey initiatedByApiKey, string? changeReason = null) =>
        RenameMetaverseObjectTypeCoreAsync(objectTypeId, newName, newPluralName, icon, objectType => UpdateMetaverseObjectTypeAsync(objectType, initiatedByApiKey, changeReason));

    private async Task RenameMetaverseObjectTypeCoreAsync(int objectTypeId, string newName, string newPluralName, string? icon, Func<MetaverseObjectType, Task> auditedUpdateAsync)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("A Metaverse Object Type name is required.", nameof(newName));
        if (string.IsNullOrWhiteSpace(newPluralName))
            throw new ArgumentException("A Metaverse Object Type plural name is required.", nameof(newPluralName));

        var objectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(objectTypeId, includeChildObjects: false)
            ?? throw new ArgumentException($"Metaverse Object Type {objectTypeId} not found.", nameof(objectTypeId));

        if (objectType.BuiltIn)
            throw new InvalidOperationException($"Cannot rename or re-icon built-in Metaverse Object Type '{objectType.Name}'.");

        var trimmedName = newName.Trim();
        var trimmedPluralName = newPluralName.Trim();

        if (!await IsMetaverseObjectTypeNameAvailableAsync(trimmedName, objectTypeId))
            throw new InvalidOperationException($"A Metaverse Object Type named '{trimmedName}' already exists.");
        if (!await IsMetaverseObjectTypePluralNameAvailableAsync(trimmedPluralName, objectTypeId))
            throw new InvalidOperationException($"A Metaverse Object Type with plural name '{trimmedPluralName}' already exists.");

        objectType.Name = trimmedName;
        objectType.PluralName = trimmedPluralName;
        objectType.Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();
        await auditedUpdateAsync(objectType);
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

    public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(int id, bool withChangeTracking = false)
    {
        return await Application.Repository.Metaverse.GetMetaverseAttributeAsync(id, withChangeTracking);
    }

    public async Task<MetaverseAttribute?> GetMetaverseAttributeWithObjectTypesAsync(int id, bool withChangeTracking = false)
    {
        return await Application.Repository.Metaverse.GetMetaverseAttributeWithObjectTypesAsync(id, withChangeTracking);
    }

    public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name, bool withChangeTracking = false)
    {
        return await Application.Repository.Metaverse.GetMetaverseAttributeAsync(name, withChangeTracking);
    }

    /// <summary>
    /// Creates a new Metaverse Attribute.
    /// </summary>
    /// <param name="attribute">The attribute to create.</param>
    /// <param name="initiatedBy">The user who initiated the creation.</param>
    public async Task CreateMetaverseAttributeAsync(MetaverseAttribute attribute, MetaverseObject? initiatedBy, string? changeReason = null)
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

        await CaptureAttributeConfigurationChangeAsync(activity, attribute.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Metaverse Attribute.
    /// </summary>
    /// <param name="attribute">The attribute to update.</param>
    /// <param name="initiatedBy">The user who initiated the update.</param>
    public async Task UpdateMetaverseAttributeAsync(MetaverseAttribute attribute, MetaverseObject? initiatedBy, string? changeReason = null)
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

        await CaptureAttributeConfigurationChangeAsync(activity, attribute.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Creates a new Metaverse Attribute (initiated by API key).
    /// </summary>
    /// <param name="attribute">The attribute to create.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the creation.</param>
    public async Task CreateMetaverseAttributeAsync(MetaverseAttribute attribute, ApiKey initiatedByApiKey, string? changeReason = null)
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

        await CaptureAttributeConfigurationChangeAsync(activity, attribute.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Metaverse Attribute (initiated by API key).
    /// </summary>
    /// <param name="attribute">The attribute to update.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the update.</param>
    public async Task UpdateMetaverseAttributeAsync(MetaverseAttribute attribute, ApiKey initiatedByApiKey, string? changeReason = null)
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

        await CaptureAttributeConfigurationChangeAsync(activity, attribute.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Captures a versioned configuration snapshot of a Metaverse Object Type onto its audit Activity via the shared
    /// ConfigurationChangeCaptureService (which owns the toggle, dedupe-guard, versioning and best-effort behaviours).
    /// The object type is reloaded with its attributes so the snapshot reflects persisted truth rather than the
    /// caller's partial in-memory graph; call it after the change has been persisted.
    /// </summary>
    private async Task CaptureObjectTypeConfigurationChangeAsync(Activity activity, int objectTypeId, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.MetaverseObjectType, objectTypeId,
            async hashKey =>
            {
                var persisted = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(objectTypeId, includeChildObjects: true);
                return persisted == null ? null : Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"Metaverse Object Type {objectTypeId}");
    }

    /// <summary>
    /// Captures a tombstone snapshot of a Metaverse Object Type onto its delete Activity, before the type is removed.
    /// Matching the Metaverse Attribute deletion behaviour, this does not link the Activity to the object or set a
    /// version: the type is deleted before the Activity completes, so the snapshot is surfaced via the Activity itself
    /// rather than the object's history.
    /// </summary>
    private async Task CaptureObjectTypeConfigurationDeletionAsync(Activity activity, MetaverseObjectType objectType, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureDeletionAsync(activity, changeReason,
            async hashKey =>
            {
                // Reload with associations for a complete tombstone; fall back to the caller's entity if already gone.
                var persisted = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(objectType.Id, includeChildObjects: true) ?? objectType;
                return Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"Metaverse Object Type {objectType.Id}");
    }

    /// <summary>
    /// Captures a versioned configuration snapshot of a Metaverse Attribute onto its audit Activity via the shared
    /// ConfigurationChangeCaptureService. The attribute is reloaded with its object type associations so the snapshot
    /// reflects persisted truth; call it after the change has been persisted.
    /// </summary>
    private async Task CaptureAttributeConfigurationChangeAsync(Activity activity, int attributeId, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.MetaverseAttribute, attributeId,
            async hashKey =>
            {
                var persisted = await Application.Repository.Metaverse.GetMetaverseAttributeWithObjectTypesAsync(attributeId);
                return persisted == null ? null : Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"Metaverse Attribute {attributeId}");
    }

    /// <summary>
    /// Captures a tombstone snapshot of a Metaverse Attribute onto its delete Activity, before the attribute is
    /// removed. Matching the Synchronisation Rule and Schedule deletion behaviour, this does not set
    /// <see cref="Activity.MetaverseAttributeId"/> or a version: the attribute is deleted before the Activity
    /// completes, so the Activity is left unlinked and the snapshot is surfaced via the Activity itself rather than
    /// the object's history.
    /// </summary>
    private async Task CaptureAttributeConfigurationDeletionAsync(Activity activity, MetaverseAttribute attribute, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureDeletionAsync(activity, changeReason,
            async hashKey =>
            {
                // Reload with associations for a complete tombstone; fall back to the caller's entity if already gone.
                var persisted = await Application.Repository.Metaverse.GetMetaverseAttributeWithObjectTypesAsync(attribute.Id) ?? attribute;
                return Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"Metaverse Attribute {attribute.Id}");
    }

    /// <summary>
    /// Records a System-attributed Create Activity and version-1 baseline snapshot for a built-in Metaverse Object Type
    /// seeded in the current pass, grouped under the seeding pass's parent Activity. The built-in Metaverse Object Types
    /// and Attributes are persisted together in one cross-referencing repository batch (attributes are bound to object
    /// types), so re-routing that batch through individual audited creates would risk the reference resolution;
    /// recording the baseline after the batch keeps the persistence untouched while still giving each built-in object
    /// type a visible, System-attributed origin in its change history and under System Initialisation (matching the
    /// built-in Predefined Searches and Connector Definitions). Idempotency is the caller's responsibility:
    /// <see cref="SeedingServer"/> only calls this for object types it created this pass, so a restart where they already
    /// exist records nothing and it is safe even when configuration change tracking is disabled.
    /// </summary>
    internal async Task RecordSeededMetaverseObjectTypeBaselineAsync(int objectTypeId, string objectTypeName, Guid parentActivityId)
    {
        var activity = new Activity
        {
            TargetName = objectTypeName,
            TargetType = ActivityTargetType.MetaverseObjectType,
            TargetOperationType = ActivityTargetOperationType.Create,
            ParentActivityId = parentActivityId,
            Message = $"Created built-in Metaverse Object Type '{objectTypeName}'"
        };
        await Application.Activities.CreateSystemActivityAsync(activity);

        try
        {
            await CaptureObjectTypeConfigurationChangeAsync(activity, objectTypeId,
                "Built-in Metaverse Object Type created automatically by JIM.");
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Records a System-attributed Create Activity and version-1 baseline snapshot for a built-in Metaverse Attribute
    /// seeded in the current pass, grouped under the seeding pass's parent Activity. See
    /// <see cref="RecordSeededMetaverseObjectTypeBaselineAsync"/> for why the baseline is recorded after the shared seed
    /// batch rather than through an individual audited create, and for the caller's idempotency contract.
    /// </summary>
    internal async Task RecordSeededMetaverseAttributeBaselineAsync(int attributeId, string attributeName, Guid parentActivityId)
    {
        var activity = new Activity
        {
            TargetName = attributeName,
            TargetType = ActivityTargetType.MetaverseAttribute,
            TargetOperationType = ActivityTargetOperationType.Create,
            ParentActivityId = parentActivityId,
            Message = $"Created built-in Metaverse Attribute '{attributeName}'"
        };
        await Application.Activities.CreateSystemActivityAsync(activity);

        try
        {
            await CaptureAttributeConfigurationChangeAsync(activity, attributeId,
                "Built-in Metaverse Attribute created automatically by JIM.");
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }
    #endregion

    #region metaverse attribute uniqueness, rename and bindings (issue #377)
    /// <summary>
    /// Determines whether a Metaverse Attribute name is available, comparing case-insensitively (so <c>CostCentre</c>
    /// and <c>costCentre</c> cannot coexist). Names are stored and returned as-is; only the comparison ignores case.
    /// Backs the real-time create/rename availability check and the server-side rename guard.
    /// </summary>
    /// <param name="name">The candidate name.</param>
    /// <param name="excludeAttributeId">Optional attribute id to exclude (the attribute being renamed).</param>
    /// <returns>True if no other attribute already uses the name (case-insensitively); otherwise false.</returns>
    public async Task<bool> IsMetaverseAttributeNameUniqueAsync(string name, int? excludeAttributeId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return await Application.Repository.Metaverse.IsMetaverseAttributeNameUniqueAsync(name, excludeAttributeId);
    }

    /// <summary>
    /// Renames a custom Metaverse Attribute through the audited path. Re-checks uniqueness case-insensitively
    /// (excluding the attribute itself). Built-in attributes are immutable.
    /// </summary>
    /// <exception cref="ArgumentException">No attribute exists with the given id.</exception>
    /// <exception cref="InvalidOperationException">The attribute is built-in.</exception>
    /// <exception cref="MetaverseAttributeNameConflictException">The new name is already used by another attribute.</exception>
    public Task RenameMetaverseAttributeAsync(int attributeId, string newName, MetaverseObject? initiatedBy, string? changeReason = null) =>
        RenameMetaverseAttributeCoreAsync(attributeId, newName, attribute => UpdateMetaverseAttributeAsync(attribute, initiatedBy, changeReason));

    /// <summary>
    /// Renames a custom Metaverse Attribute through the audited path (API-key initiator overload).
    /// </summary>
    public Task RenameMetaverseAttributeAsync(int attributeId, string newName, ApiKey initiatedByApiKey, string? changeReason = null) =>
        RenameMetaverseAttributeCoreAsync(attributeId, newName, attribute => UpdateMetaverseAttributeAsync(attribute, initiatedByApiKey, changeReason));

    private async Task RenameMetaverseAttributeCoreAsync(int attributeId, string newName, Func<MetaverseAttribute, Task> auditedUpdateAsync)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("A Metaverse Attribute name is required.", nameof(newName));

        var attribute = await Application.Repository.Metaverse.GetMetaverseAttributeAsync(attributeId, withChangeTracking: true)
            ?? throw new ArgumentException($"Metaverse Attribute {attributeId} not found.", nameof(attributeId));

        if (attribute.BuiltIn)
            throw new InvalidOperationException($"Cannot rename built-in Metaverse Attribute '{attribute.Name}'.");

        if (!await Application.Repository.Metaverse.IsMetaverseAttributeNameUniqueAsync(newName, attributeId))
            throw new MetaverseAttributeNameConflictException(newName);

        attribute.Name = newName;
        await auditedUpdateAsync(attribute);
    }

    /// <summary>
    /// Binds a custom Metaverse Attribute to a Metaverse Object Type, recorded as an audited Activity. Idempotent.
    /// Built-in attributes cannot have their bindings modified.
    /// </summary>
    public Task BindAttributeToObjectTypeAsync(int attributeId, int metaverseObjectTypeId, MetaverseObject? initiatedBy, string? changeReason = null) =>
        BindAttributeToObjectTypeCoreAsync(attributeId, metaverseObjectTypeId, activity => Application.Activities.CreateActivityAsync(activity, initiatedBy), changeReason);

    /// <summary>
    /// Binds a custom Metaverse Attribute to a Metaverse Object Type (API-key initiator overload).
    /// </summary>
    public Task BindAttributeToObjectTypeAsync(int attributeId, int metaverseObjectTypeId, ApiKey initiatedByApiKey, string? changeReason = null) =>
        BindAttributeToObjectTypeCoreAsync(attributeId, metaverseObjectTypeId, activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey), changeReason);

    private async Task BindAttributeToObjectTypeCoreAsync(int attributeId, int metaverseObjectTypeId, Func<Activity, Task> createActivityAsync, string? changeReason)
    {
        var attribute = await Application.Repository.Metaverse.GetMetaverseAttributeAsync(attributeId)
            ?? throw new ArgumentException($"Metaverse Attribute {attributeId} not found.", nameof(attributeId));

        if (attribute.BuiltIn)
            throw new InvalidOperationException($"Cannot modify the bindings of built-in Metaverse Attribute '{attribute.Name}'.");

        var activity = new Activity
        {
            TargetName = attribute.Name,
            TargetType = ActivityTargetType.MetaverseAttribute,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await createActivityAsync(activity);

        try
        {
            await Application.Repository.Metaverse.AddAttributeObjectTypeBindingAsync(attributeId, metaverseObjectTypeId);
            await CaptureAttributeConfigurationChangeAsync(activity, attributeId, changeReason);
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            // Activity execution boundary: any failure must be recorded on the Activity, never escape silently.
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }
    #endregion

    #region metaverse attribute destructive-operation safeguards (issue #377)
    /// <summary>
    /// Evaluates the impact of deleting a Metaverse Attribute without performing any change: the per-Object-Type
    /// counts of Metaverse Objects holding a stored value (the only hard block), and the configuration references
    /// that would be cascade-removed. Backs the preview the UI/API renders to drive the type-the-name confirmation.
    /// </summary>
    public async Task<AttributeDeletionImpact> EvaluateAttributeDeletionAsync(MetaverseAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var valueCounts = await Application.Repository.Metaverse.GetAttributeValueObjectCountsByTypeAsync(attribute.Id);
        var references = await Application.Repository.Metaverse.GetAttributeReferencesAsync(attribute.Id);

        return new AttributeDeletionImpact
        {
            AttributeId = attribute.Id,
            AttributeName = attribute.Name,
            BuiltIn = attribute.BuiltIn,
            TotalObjectsWithValues = valueCounts.Sum(v => v.ObjectCount),
            ObjectTypeValueCounts = valueCounts,
            References = references
        };
    }

    /// <summary>
    /// Deletes a custom Metaverse Attribute, cascade-removing its configuration references (bindings, Attribute Flows,
    /// scoping criteria, Object Matching Rules) in dependency order as one transaction. Stored values are the only
    /// hard block: if any Metaverse Object holds a value, no change is made and the returned impact reports the block.
    /// The cascade is audited as a parent delete Activity with a child Activity per removed reference and one for the
    /// attribute removal itself. Built-in attributes cannot be deleted.
    /// </summary>
    /// <returns>The evaluated impact; <see cref="AttributeDeletionImpact.Deleted"/> is true when the delete happened.</returns>
    public Task<AttributeDeletionImpact> DeleteMetaverseAttributeWithCascadeAsync(MetaverseAttribute attribute, MetaverseObject? initiatedBy, string? changeReason = null) =>
        DeleteMetaverseAttributeWithCascadeCoreAsync(attribute, activity => Application.Activities.CreateActivityAsync(activity, initiatedBy), changeReason);

    /// <summary>
    /// Deletes a custom Metaverse Attribute with reference cascade (API-key initiator overload).
    /// </summary>
    public Task<AttributeDeletionImpact> DeleteMetaverseAttributeWithCascadeAsync(MetaverseAttribute attribute, ApiKey initiatedByApiKey, string? changeReason = null) =>
        DeleteMetaverseAttributeWithCascadeCoreAsync(attribute, activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey), changeReason);

    private async Task<AttributeDeletionImpact> DeleteMetaverseAttributeWithCascadeCoreAsync(MetaverseAttribute attribute, Func<Activity, Task> createActivityAsync, string? changeReason)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        if (attribute.BuiltIn)
            throw new InvalidOperationException($"Cannot delete built-in Metaverse Attribute '{attribute.Name}'.");

        var impact = await EvaluateAttributeDeletionAsync(attribute);

        // Stored values are the only hard block. Refuse without making any change; the caller surfaces the counts.
        if (impact.BlockedByValues)
            return impact;

        Log.Debug("DeleteMetaverseAttributeWithCascadeAsync() removing {Attribute} with {ReferenceCount} reference(s)", attribute.Name, impact.References.Count);

        var parent = new Activity
        {
            TargetName = attribute.Name,
            TargetType = ActivityTargetType.MetaverseAttribute,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await createActivityAsync(parent);

        try
        {
            // Capture the tombstone snapshot on the parent before the attribute is removed.
            await CaptureAttributeConfigurationDeletionAsync(parent, attribute, changeReason);

            // One child Activity per removed reference, grouped under the parent, so the audit log shows exactly
            // what was removed, by whom, under the one action.
            foreach (var referenceChild in impact.References.Select(reference => new Activity
                     {
                         ParentActivityId = parent.Id,
                         TargetName = reference.Description,
                         TargetContext = attribute.Name,
                         TargetType = ReferenceActivityTargetType(reference.Kind),
                         TargetOperationType = ActivityTargetOperationType.Delete,
                         Message = $"Removed {reference.Description} referencing Metaverse Attribute '{attribute.Name}'"
                     }))
            {
                await createActivityAsync(referenceChild);
                await Application.Activities.CompleteActivityAsync(referenceChild);
            }

            // A child Activity for the attribute/binding removal itself.
            var removalChild = new Activity
            {
                ParentActivityId = parent.Id,
                TargetName = attribute.Name,
                TargetType = ActivityTargetType.MetaverseAttribute,
                TargetOperationType = ActivityTargetOperationType.Delete,
                Message = $"Removed Metaverse Attribute '{attribute.Name}'"
            };
            await createActivityAsync(removalChild);
            await Application.Activities.CompleteActivityAsync(removalChild);

            // Execute the cascade in a single transaction that leaves nothing dangling.
            await Application.Repository.Metaverse.CascadeDeleteMetaverseAttributeAsync(attribute.Id);

            await Application.Activities.CompleteActivityAsync(parent);
        }
        catch (Exception ex)
        {
            // Activity execution boundary: any failure must be recorded on the parent Activity, never escape silently.
            await Application.Activities.FailActivityWithErrorAsync(parent, ex);
            throw;
        }

        impact.Deleted = true;
        return impact;
    }

    /// <summary>
    /// Evaluates the impact of unassigning a Metaverse Attribute from a single Metaverse Object Type without making
    /// any change. Metaverse Objects <em>of that type</em> holding a stored value are the only hard block. The impact
    /// carries the type-scoped references (the binding plus any references owned by Synchronisation Rules targeting the
    /// type, and Predefined Searches / Example Data templates belonging to it) that the unassignment would remove.
    /// </summary>
    public async Task<AttributeUnassignImpact> EvaluateAttributeUnassignAsync(int attributeId, int metaverseObjectTypeId)
    {
        var attribute = await Application.Repository.Metaverse.GetMetaverseAttributeWithObjectTypesAsync(attributeId)
            ?? throw new ArgumentException($"Metaverse Attribute {attributeId} not found.", nameof(attributeId));

        var objectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(metaverseObjectTypeId, false);
        var objectsWithValues = await Application.Repository.Metaverse.GetAttributeValueObjectCountByTypeAsync(attributeId, metaverseObjectTypeId);
        var references = await Application.Repository.Metaverse.GetAttributeReferencesForObjectTypeAsync(attributeId, metaverseObjectTypeId);

        return new AttributeUnassignImpact
        {
            AttributeId = attributeId,
            AttributeName = attribute.Name,
            BuiltIn = attribute.BuiltIn,
            MetaverseObjectTypeId = metaverseObjectTypeId,
            MetaverseObjectTypeName = objectType?.Name ?? metaverseObjectTypeId.ToString(),
            MetaverseObjectTypePluralName = objectType?.PluralName ?? string.Empty,
            WasBound = attribute.MetaverseObjectTypes.Any(t => t.Id == metaverseObjectTypeId),
            ObjectsWithValues = objectsWithValues,
            References = references
        };
    }

    /// <summary>
    /// Unassigns (unbinds) a custom Metaverse Attribute from a Metaverse Object Type, cascade-removing the references
    /// scoped to that type (references owned by Synchronisation Rules targeting the type, and Predefined Searches /
    /// Example Data templates belonging to it), in dependency order as one transaction, with source-less-parent repair
    /// applied within that set. Attribute-global references (rules targeting other types, and the Service Settings SSO
    /// mapping) are left untouched. Refused (no change) while any Metaverse Object of that type holds a stored value.
    /// Recorded as a parent unassign Activity with a child Activity per removed reference. Built-ins cannot be unassigned.
    /// </summary>
    /// <returns>The evaluated impact; <see cref="AttributeUnassignImpact.Unassigned"/> is true when the unassignment happened.</returns>
    public Task<AttributeUnassignImpact> UnassignAttributeFromObjectTypeAsync(int attributeId, int metaverseObjectTypeId, MetaverseObject? initiatedBy, string? changeReason = null) =>
        UnassignAttributeFromObjectTypeCoreAsync(attributeId, metaverseObjectTypeId, activity => Application.Activities.CreateActivityAsync(activity, initiatedBy), changeReason);

    /// <summary>
    /// Unassigns a custom Metaverse Attribute from a Metaverse Object Type with type-scoped cascade (API-key initiator).
    /// </summary>
    public Task<AttributeUnassignImpact> UnassignAttributeFromObjectTypeAsync(int attributeId, int metaverseObjectTypeId, ApiKey initiatedByApiKey, string? changeReason = null) =>
        UnassignAttributeFromObjectTypeCoreAsync(attributeId, metaverseObjectTypeId, activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey), changeReason);

    private async Task<AttributeUnassignImpact> UnassignAttributeFromObjectTypeCoreAsync(int attributeId, int metaverseObjectTypeId, Func<Activity, Task> createActivityAsync, string? changeReason)
    {
        var impact = await EvaluateAttributeUnassignAsync(attributeId, metaverseObjectTypeId);

        if (impact.BuiltIn)
            throw new InvalidOperationException($"Cannot unassign built-in Metaverse Attribute '{impact.AttributeName}'.");

        // Nothing bound to remove, or blocked by stored values: make no change and report via the impact.
        if (!impact.WasBound || impact.BlockedByValues)
            return impact;

        var parent = new Activity
        {
            TargetName = impact.AttributeName,
            TargetContext = impact.MetaverseObjectTypeName,
            TargetType = ActivityTargetType.MetaverseAttribute,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Unassigned Metaverse Attribute '{impact.AttributeName}' from Metaverse Object Type '{impact.MetaverseObjectTypeName}'"
        };
        await createActivityAsync(parent);

        try
        {
            // One child Activity per removed reference (the binding and each type-scoped cascade reference), grouped
            // under the parent unassign Activity, mirroring the delete cascade audit.
            foreach (var referenceChild in impact.References.Select(reference => new Activity
                     {
                         ParentActivityId = parent.Id,
                         TargetName = reference.Description,
                         TargetContext = impact.AttributeName,
                         TargetType = ReferenceActivityTargetType(reference.Kind),
                         TargetOperationType = ActivityTargetOperationType.Delete,
                         Message = $"Removed {reference.Description} scoped to Metaverse Object Type '{impact.MetaverseObjectTypeName}'"
                     }))
            {
                await createActivityAsync(referenceChild);
                await Application.Activities.CompleteActivityAsync(referenceChild);
            }

            await Application.Repository.Metaverse.CascadeUnassignAttributeFromObjectTypeAsync(attributeId, metaverseObjectTypeId);
            await CaptureAttributeConfigurationChangeAsync(parent, attributeId, changeReason);
            await Application.Activities.CompleteActivityAsync(parent);
        }
        catch (Exception ex)
        {
            // Activity execution boundary: any failure must be recorded on the parent Activity, never escape silently.
            await Application.Activities.FailActivityWithErrorAsync(parent, ex);
            throw;
        }

        impact.Unassigned = true;
        return impact;
    }

    /// <summary>
    /// Evaluates the impact of changing a Metaverse Attribute's data type or plurality without making any change.
    /// Any stored value across the Metaverse hard-blocks the change.
    /// </summary>
    public async Task<AttributeSchemaChangeImpact> EvaluateAttributeSchemaChangeAsync(MetaverseAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var total = await Application.Repository.Metaverse.GetAttributeValueObjectCountAsync(attribute.Id);
        return new AttributeSchemaChangeImpact
        {
            AttributeId = attribute.Id,
            AttributeName = attribute.Name,
            BuiltIn = attribute.BuiltIn,
            TotalObjectsWithValues = total
        };
    }

    /// <summary>
    /// Changes a custom Metaverse Attribute's data type and/or plurality through the audited path. Refused (no change)
    /// while any Metaverse Object holds a stored value; the returned impact reports the block. Built-in attributes
    /// cannot be changed.
    /// </summary>
    /// <returns>The evaluated impact; <see cref="AttributeSchemaChangeImpact.Applied"/> is true when the change was applied.</returns>
    public Task<AttributeSchemaChangeImpact> ChangeMetaverseAttributeSchemaAsync(int attributeId, AttributeDataType newType, AttributePlurality newPlurality, MetaverseObject? initiatedBy, string? changeReason = null) =>
        ChangeMetaverseAttributeSchemaCoreAsync(attributeId, newType, newPlurality, attribute => UpdateMetaverseAttributeAsync(attribute, initiatedBy, changeReason));

    /// <summary>
    /// Changes a custom Metaverse Attribute's data type and/or plurality (API-key initiator overload).
    /// </summary>
    public Task<AttributeSchemaChangeImpact> ChangeMetaverseAttributeSchemaAsync(int attributeId, AttributeDataType newType, AttributePlurality newPlurality, ApiKey initiatedByApiKey, string? changeReason = null) =>
        ChangeMetaverseAttributeSchemaCoreAsync(attributeId, newType, newPlurality, attribute => UpdateMetaverseAttributeAsync(attribute, initiatedByApiKey, changeReason));

    private async Task<AttributeSchemaChangeImpact> ChangeMetaverseAttributeSchemaCoreAsync(int attributeId, AttributeDataType newType, AttributePlurality newPlurality, Func<MetaverseAttribute, Task> auditedUpdateAsync)
    {
        var attribute = await Application.Repository.Metaverse.GetMetaverseAttributeAsync(attributeId, withChangeTracking: true)
            ?? throw new ArgumentException($"Metaverse Attribute {attributeId} not found.", nameof(attributeId));

        if (attribute.BuiltIn)
            throw new InvalidOperationException($"Cannot change the type or plurality of built-in Metaverse Attribute '{attribute.Name}'.");

        var impact = await EvaluateAttributeSchemaChangeAsync(attribute);

        // Stored values hard-block a type/plurality change; make no change and report via the impact.
        if (impact.BlockedByValues)
            return impact;

        attribute.Type = newType;
        attribute.AttributePlurality = newPlurality;
        await auditedUpdateAsync(attribute);

        impact.Applied = true;
        return impact;
    }

    /// <summary>
    /// Maps a reference kind to the Activity target type used for the child audit Activity that records its removal.
    /// </summary>
    private static ActivityTargetType ReferenceActivityTargetType(AttributeReferenceKind kind) => kind switch
    {
        AttributeReferenceKind.Binding => ActivityTargetType.MetaverseObjectType,
        AttributeReferenceKind.ObjectMatchingRuleTarget => ActivityTargetType.ObjectMatchingRule,
        AttributeReferenceKind.ObjectMatchingRuleSource => ActivityTargetType.ObjectMatchingRule,
        AttributeReferenceKind.SourcelessObjectMatchingRule => ActivityTargetType.ObjectMatchingRule,
        AttributeReferenceKind.PredefinedSearchAttribute => ActivityTargetType.PredefinedSearch,
        AttributeReferenceKind.PredefinedSearchCriterion => ActivityTargetType.PredefinedSearch,
        AttributeReferenceKind.ExampleDataTemplateAttribute => ActivityTargetType.ExampleDataTemplate,
        AttributeReferenceKind.ExampleDataTemplateAttributeDependency => ActivityTargetType.ExampleDataTemplate,
        AttributeReferenceKind.ServiceSettingsSsoIdentifier => ActivityTargetType.ServiceSetting,
        _ => ActivityTargetType.SynchronisationRule
    };
    #endregion

    #region metaverse object type destructive-operation safeguards (issue #376)
    /// <summary>
    /// Evaluates the impact of deleting a Metaverse Object Type without making any change: the two hard blocks
    /// (Metaverse Objects of the type, and Synchronisation Rules targeting it, both of which the database would
    /// otherwise silently cascade-delete) and the softer references (Predefined Searches, Example Data Templates and
    /// custom attribute bindings) that are cascade-removed when the deletion proceeds. Backs the preview the UI/API
    /// render to drive the type-the-name confirmation.
    /// </summary>
    public async Task<ObjectTypeDeletionImpact> EvaluateObjectTypeDeletionAsync(MetaverseObjectType objectType)
    {
        ArgumentNullException.ThrowIfNull(objectType);

        var metaverseObjectCount = await Application.Repository.Metaverse.GetMetaverseObjectOfTypeCountAsync(objectType.Id);
        var references = await Application.Repository.Metaverse.GetMetaverseObjectTypeReferencesAsync(objectType.Id);

        return new ObjectTypeDeletionImpact
        {
            ObjectTypeId = objectType.Id,
            ObjectTypeName = objectType.Name,
            BuiltIn = objectType.BuiltIn,
            MetaverseObjectCount = metaverseObjectCount,
            SynchronisationRules = references.Where(r => r.Kind == ObjectTypeReferenceKind.SynchronisationRule).ToList(),
            CascadeReferences = references.Where(r => r.Kind != ObjectTypeReferenceKind.SynchronisationRule).ToList()
        };
    }

    /// <summary>
    /// Deletes a custom Metaverse Object Type, cascade-removing its softer configuration references (Predefined
    /// Searches, Example Data Templates and custom attribute bindings) via the database delete cascade as one
    /// transaction. Two hard blocks refuse the deletion without making any change: Metaverse Objects of the type
    /// (identity data), and Synchronisation Rules targeting the type (deleting the type would otherwise cascade-delete
    /// the whole rule). The delete is audited as a parent delete Activity with a tombstone snapshot. Built-in types can
    /// never be deleted.
    /// </summary>
    /// <returns>The evaluated impact; <see cref="ObjectTypeDeletionImpact.Deleted"/> is true when the delete happened.</returns>
    public Task<ObjectTypeDeletionImpact> DeleteMetaverseObjectTypeAsync(MetaverseObjectType objectType, MetaverseObject? initiatedBy, string? changeReason = null) =>
        DeleteMetaverseObjectTypeCoreAsync(objectType, activity => Application.Activities.CreateActivityAsync(activity, initiatedBy), changeReason);

    /// <summary>
    /// Deletes a custom Metaverse Object Type with reference cascade (API-key initiator overload).
    /// </summary>
    public Task<ObjectTypeDeletionImpact> DeleteMetaverseObjectTypeAsync(MetaverseObjectType objectType, ApiKey initiatedByApiKey, string? changeReason = null) =>
        DeleteMetaverseObjectTypeCoreAsync(objectType, activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey), changeReason);

    private async Task<ObjectTypeDeletionImpact> DeleteMetaverseObjectTypeCoreAsync(MetaverseObjectType objectType, Func<Activity, Task> createActivityAsync, string? changeReason)
    {
        ArgumentNullException.ThrowIfNull(objectType);

        if (objectType.BuiltIn)
            throw new InvalidOperationException($"Cannot delete built-in Metaverse Object Type '{objectType.Name}'.");

        var impact = await EvaluateObjectTypeDeletionAsync(objectType);

        // Hard blocks: live identity data, or Synchronisation Rules whose target this type is. Refuse without making
        // any change; the caller surfaces the counts.
        if (impact.BlockedByObjects || impact.BlockedBySynchronisationRules)
            return impact;

        Log.Debug("DeleteMetaverseObjectTypeAsync() removing {ObjectType} with {ReferenceCount} cascade reference(s)", objectType.Name, impact.CascadeReferences.Count);

        var parent = new Activity
        {
            TargetName = objectType.Name,
            TargetType = ActivityTargetType.MetaverseObjectType,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await createActivityAsync(parent);

        try
        {
            // Capture the tombstone snapshot on the parent before the type is removed.
            await CaptureObjectTypeConfigurationDeletionAsync(parent, objectType, changeReason);

            // One child Activity per cascade-removed reference, grouped under the parent, so the audit log shows exactly
            // what was removed under the one action.
            foreach (var referenceChild in impact.CascadeReferences.Select(reference => new Activity
                     {
                         ParentActivityId = parent.Id,
                         TargetName = reference.Description,
                         TargetContext = objectType.Name,
                         TargetType = ObjectTypeReferenceActivityTargetType(reference.Kind),
                         TargetOperationType = ActivityTargetOperationType.Delete,
                         Message = $"Removed {reference.Description} scoped to Metaverse Object Type '{objectType.Name}'"
                     }))
            {
                await createActivityAsync(referenceChild);
                await Application.Activities.CompleteActivityAsync(referenceChild);
            }

            // A child Activity for the object type removal itself.
            var removalChild = new Activity
            {
                ParentActivityId = parent.Id,
                TargetName = objectType.Name,
                TargetType = ActivityTargetType.MetaverseObjectType,
                TargetOperationType = ActivityTargetOperationType.Delete,
                Message = $"Removed Metaverse Object Type '{objectType.Name}'"
            };
            await createActivityAsync(removalChild);
            await Application.Activities.CompleteActivityAsync(removalChild);

            // Execute the removal; the database cascade removes the softer references in one transaction.
            await Application.Repository.Metaverse.DeleteMetaverseObjectTypeAsync(objectType.Id);

            await Application.Activities.CompleteActivityAsync(parent);
        }
        catch (Exception ex)
        {
            // Activity execution boundary: any failure must be recorded on the parent Activity, never escape silently.
            await Application.Activities.FailActivityWithErrorAsync(parent, ex);
            throw;
        }

        impact.Deleted = true;
        return impact;
    }

    /// <summary>
    /// Maps a Metaverse Object Type reference kind to the Activity target type used for the child audit Activity that
    /// records its cascade removal.
    /// </summary>
    private static ActivityTargetType ObjectTypeReferenceActivityTargetType(ObjectTypeReferenceKind kind) => kind switch
    {
        ObjectTypeReferenceKind.PredefinedSearch => ActivityTargetType.PredefinedSearch,
        ObjectTypeReferenceKind.ExampleDataTemplate => ActivityTargetType.ExampleDataTemplate,
        ObjectTypeReferenceKind.AttributeBinding => ActivityTargetType.MetaverseAttribute,
        _ => ActivityTargetType.MetaverseObjectType
    };
    #endregion

    #region Metaverse Objects
    public async Task<MetaverseObject?> GetMetaverseObjectAsync(Guid id)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectAsync(id);
    }

    /// <summary>
    /// As <see cref="GetMetaverseObjectAsync"/>, but additionally loads the per-value Attribute Priority
    /// provenance (contributing Connected System and Synchronisation Rule) for single-object read paths.
    /// </summary>
    public async Task<MetaverseObject?> GetMetaverseObjectWithProvenanceAsync(Guid id)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectWithProvenanceAsync(id);
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
        using var span = Diagnostics.Diagnostics.Database.StartSpan("Mvo.GetDetail")
            .SetTag("id", id)
            .SetTag("strategy", loadStrategy.ToString());
        return await Application.Repository.Metaverse.GetMetaverseObjectDetailAsync(id, loadStrategy);
    }

    /// <summary>
    /// Returns a page of change-history rows for a Metaverse Object, projected into a flat DTO.
    /// Ordered by change time descending. <paramref name="pageSize"/> is clamped to [1, 100].
    /// </summary>
    public async Task<(List<MvoChangeHistoryDto> Items, int TotalCount)> GetMvoChangeHistoryAsync(Guid metaverseObjectId, int page, int pageSize)
    {
        if (page < 1)
            page = 1;
        if (pageSize < 1)
            pageSize = 1;
        if (pageSize > 100)
            pageSize = 100;

        using var span = Diagnostics.Diagnostics.Database.StartSpan("Mvo.GetChangeHistory")
            .SetTag("id", metaverseObjectId)
            .SetTag("page", page)
            .SetTag("pageSize", pageSize);
        return await Application.Repository.Metaverse.GetMvoChangeHistoryAsync(metaverseObjectId, page, pageSize);
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

        // Keep the denormalised CachedDisplayName in sync if DisplayName was affected.
        // Callers apply additions/removals to AttributeValues before calling this method,
        // so re-deriving from the current collection handles SET, UPDATE, and DELETE cases.
        var displayNameChanged = (additions?.Any(av => av.Attribute?.Name == Constants.BuiltInAttributes.DisplayName) ?? false)
            || (removals?.Any(av => av.Attribute?.Name == Constants.BuiltInAttributes.DisplayName) ?? false);
        if (displayNameChanged)
        {
            metaverseObject.CachedDisplayName = metaverseObject.AttributeValues
                .SingleOrDefault(av => av.Attribute?.Name == Constants.BuiltInAttributes.DisplayName)
                ?.StringValue;
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

        // Create attribute change records
        foreach (var addition in additions)
        {
            change.AddAttributeValueChange(addition, ValueChangeType.Add);
        }

        foreach (var removal in removals)
        {
            change.AddAttributeValueChange(removal, ValueChangeType.Remove);
        }

        // Add to MVO's Changes collection
        metaverseObject.Changes.Add(change);
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

        // Keep the denormalised CachedDisplayName in sync with the canonical attribute value.
        metaverseObject.CachedDisplayName = metaverseObject.AttributeValues
            .SingleOrDefault(av => av.Attribute?.Name == Constants.BuiltInAttributes.DisplayName)
            ?.StringValue;

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
                change.AddAttributeValueChange(attributeValue, ValueChangeType.Remove);
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

    /// <summary>
    /// Gets a paginated list of lightweight Metaverse Object headers with only the attributes defined
    /// in the PredefinedSearch projected directly in SQL for optimum performance at scale.
    /// </summary>
    public async Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectHeadersPagedAsync(
        PredefinedSearch predefinedSearch,
        int page = 1,
        int pageSize = 20,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        int? hasAttributeId = null)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectHeadersPagedAsync(
            predefinedSearch, page, pageSize, searchQuery, sortBy, sortDescending, hasAttributeId);
    }

    /// <summary>
    /// Gets a paginated list of Metaverse Objects with optional filtering by type, search query, or specific attribute value.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <param name="searchQuery">Optional search query to filter by display name.</param>
    /// <param name="sortDescending">Whether to sort in descending order by created date.</param>
    /// <param name="attributes">Optional list of attribute names to include. DisplayName is always included.</param>
    /// <param name="filterAttributeName">Optional attribute name to filter by (must be used with filterAttributeValue).</param>
    /// <param name="filterAttributeValue">Optional attribute value to filter by (exact match, case-insensitive).</param>
    /// <returns>A paged result set of Metaverse Object headers.</returns>
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
    /// Gets the count of Metaverse Objects with optional filtering by type, search query, or specific attribute value.
    /// Optimised for fast counting without loading entity data.
    /// </summary>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <param name="searchQuery">Optional search query to filter by display name (partial match, case-insensitive).</param>
    /// <param name="filterAttributeName">Optional attribute name to filter by (must be used with filterAttributeValue).</param>
    /// <param name="filterAttributeValue">Optional attribute value to filter by (exact match, case-insensitive).</param>
    /// <returns>The count of matching Metaverse Objects.</returns>
    public async Task<int> GetMetaverseObjectsCountAsync(
        int? objectTypeId = null,
        string? searchQuery = null,
        string? filterAttributeName = null,
        string? filterAttributeValue = null)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectsCountAsync(
            objectTypeId, searchQuery, filterAttributeName, filterAttributeValue);
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
    /// <exception cref="ArgumentNullException">Will be thrown if the Object Matching Rule source Connected System attribute is null.</exception>
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

    /// <summary>
    /// Gets MVOs that are pending deletion (have LastConnectorDisconnectedDate set but haven't been deleted yet).
    /// These are MVOs awaiting their grace period to expire before automatic deletion.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <returns>A paged result set of MVOs pending deletion.</returns>
    public async Task<PagedResultSet<MetaverseObject>> GetMetaverseObjectsPendingDeletionAsync(
        int page,
        int pageSize,
        int? objectTypeId = null)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectsPendingDeletionAsync(page, pageSize, objectTypeId);
    }

    /// <summary>
    /// Gets the count of MVOs that are pending deletion.
    /// </summary>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <returns>The count of MVOs pending deletion.</returns>
    public async Task<int> GetMetaverseObjectsPendingDeletionCountAsync(int? objectTypeId = null)
    {
        return await Application.Repository.Metaverse.GetMetaverseObjectsPendingDeletionCountAsync(objectTypeId);
    }

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
    public async Task<(List<MetaverseObjectChange> Items, int TotalCount)> GetDeletedMvoChangesAsync(
        int? objectTypeId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? displayNameSearch = null,
        int page = 1,
        int pageSize = 50)
    {
        return await Application.Repository.Metaverse.GetDeletedMvoChangesAsync(
            objectTypeId, fromDate, toDate, displayNameSearch, page, pageSize);
    }

    /// <summary>
    /// Gets the full change history for a deleted MVO by its change ID.
    /// </summary>
    /// <param name="changeId">The ID of the MVO change record.</param>
    /// <returns>List of all changes for that MVO ordered by ChangeTime descending.</returns>
    public async Task<List<MetaverseObjectChange>> GetDeletedMvoChangeHistoryAsync(Guid changeId)
    {
        return await Application.Repository.Metaverse.GetDeletedMvoChangeHistoryAsync(changeId);
    }
    #endregion
}
