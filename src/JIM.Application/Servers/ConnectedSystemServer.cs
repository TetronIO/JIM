// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using JIM.Application.Staging;
using JIM.Connectors;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using JIM.Application.Diagnostics;
using JIM.Application.Services;
using JIM.Application.Utilities;
using JIM.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace JIM.Application.Servers;

public class ConnectedSystemServer
{
    private JimApplication Application { get; }

    /// <summary>
    /// Internal and settable so tests can substitute a stub Connector for the server's own schema handling;
    /// production code never assigns it.
    /// </summary>
    internal IConnectorFactory ConnectorFactory { private get; set; } = new ConnectorFactory();

    internal ConnectedSystemServer(JimApplication application, IConnectorFactory? connectorFactory = null)
    {
        Application = application;

        // The constructor parameter is a convenience over the property above, not a second seam: tests that
        // build a whole JimApplication (the password fan-out ones) can hand the factory in at construction
        // rather than reaching into the server afterwards. Both routes end at the same field.
        if (connectorFactory != null)
            ConnectorFactory = connectorFactory;
    }

    /// <summary>
    /// Resolves the Connector implementation for a Connected System's Connector Definition, configuring it with
    /// credential protection and certificate validation when it supports them.
    /// </summary>
    /// <exception cref="NotSupportedException">Thrown when the Connector Definition is not recognised.</exception>
    private IConnector CreateConnector(ConnectedSystem connectedSystem)
    {
        return ConnectorFactory.Create(connectedSystem.ConnectorDefinition.Name, Application.CredentialProtection, Application.Certificates);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Configuration change capture
    // Captures a redacted, versioned configuration snapshot onto a configuration-change Activity. Called after the
    // entity has been persisted (so its id and graph are current) and before the Activity is completed, so the snapshot
    // fields are saved as part of the existing CompleteActivityAsync update. The toggle, dedupe-guard, versioning and
    // best-effort behaviours are owned by the shared ConfigurationChangeCaptureService; these wrappers supply only the
    // type-specific snapshot builders.
    // -----------------------------------------------------------------------------------------------------------------

    private async Task CaptureConfigurationChangeAsync(Activity activity, ConnectedSystem connectedSystem, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.ConnectedSystem, connectedSystem.Id,
            hashKey => Task.FromResult<ConfigurationSnapshot?>(Application.ConfigurationSnapshots.CreateSnapshot(connectedSystem, hashKey)),
            $"Connected System {connectedSystem.Id}");
    }

    private async Task CaptureConfigurationChangeAsync(Activity activity, SyncRule syncRule, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.SynchronisationRule, syncRule.Id,
            hashKey => Task.FromResult<ConfigurationSnapshot?>(Application.ConfigurationSnapshots.CreateSnapshot(syncRule, hashKey)),
            $"Synchronisation Rule {syncRule.Id}");
    }

    // Captures a versioned configuration snapshot for a Synchronisation Rule whose change was made through a granular
    // sub-entity endpoint (an Attribute Flow mapping, a matching rule, etc.). The parent rule is reloaded in full so the
    // snapshot reflects persisted truth rather than the caller's partial in-memory sub-entity graph; without this the
    // rule's captured history drifts from reality and a later whole-rule save reports pre-existing children as "added".
    // The supplied Activity is already SyncRule-targeted, so capturing onto it surfaces the change in the rule's history.
    private async Task CaptureSyncRuleConfigurationChangeAsync(Activity activity, int syncRuleId)
    {
        if (syncRuleId <= 0)
            return;

        var rule = await Application.Repository.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (rule != null)
            await CaptureConfigurationChangeAsync(activity, rule, changeReason: null);
    }

    // Connected System counterpart of CaptureSyncRuleConfigurationChangeAsync: reloads the whole Connected System so a
    // change made through a granular sub-entity endpoint (a Run Profile, an object-type or attribute selection, a
    // partition or container selection) records a complete, versioned snapshot under the system's configuration history.
    private async Task CaptureConnectedSystemConfigurationChangeAsync(Activity activity, int connectedSystemId)
    {
        if (connectedSystemId <= 0)
            return;

        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem != null)
            await CaptureConfigurationChangeAsync(activity, connectedSystem, changeReason: null);
    }

    // Routes an Object Matching Rule change to the configuration history of whichever object owns the rule: the
    // Synchronisation Rule in Advanced Mode (the rule attaches to a SyncRule, resolved from the scalar FK or the
    // navigation), or the Connected System in Simple Mode (the rule attaches to a Connected System Object Type).
    // Simple Mode rules previously captured nothing at all.
    private async Task CaptureObjectMatchingRuleConfigurationChangeAsync(Activity activity, ObjectMatchingRule rule)
    {
        var syncRuleId = rule.SyncRuleId ?? rule.SyncRule?.Id;
        if (syncRuleId is > 0)
        {
            await CaptureSyncRuleConfigurationChangeAsync(activity, syncRuleId.Value);
            return;
        }

        var connectedSystemId = rule.ConnectedSystemObjectType?.ConnectedSystemId;
        if (connectedSystemId == null && rule.ConnectedSystemObjectTypeId.HasValue)
        {
            var objectType = await Application.Repository.ConnectedSystems.GetObjectTypeAsync(rule.ConnectedSystemObjectTypeId.Value);
            connectedSystemId = objectType?.ConnectedSystemId;
        }

        if (connectedSystemId is > 0)
            await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystemId.Value);
    }

    /// <summary>
    /// Captures a tombstone snapshot of a Synchronisation Rule onto its delete Activity, before the rule is removed.
    /// Unlike create/update capture this does not set <see cref="Activity.SyncRuleId"/> or a version: the rule is
    /// deleted before the Activity completes, so (matching the existing delete path) the Activity is left unlinked and
    /// the snapshot is surfaced via the Activity itself rather than the object's history.
    /// </summary>
    private async Task CaptureConfigurationDeletionAsync(Activity activity, SyncRule syncRule, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureDeletionAsync(activity, changeReason,
            hashKey => Task.FromResult<ConfigurationSnapshot?>(Application.ConfigurationSnapshots.CreateSnapshot(syncRule, hashKey)),
            $"Synchronisation Rule {syncRule.Id}");
    }

    // Captures a tombstone snapshot of a Connected System onto its delete Activity, before the system is removed. The
    // system is reloaded in full (its Run Profiles, object types, partitions and setting values, secrets redacted) so
    // the snapshot reflects persisted truth; if it has already gone (null reload) capture is skipped. Matching the
    // Synchronisation Rule and Connector Definition deletion behaviour, this sets neither the Activity's target column
    // nor a version; the snapshot is surfaced via the Activity itself rather than the object's history.
    private async Task CaptureConnectedSystemDeletionAsync(Activity activity, int connectedSystemId, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureDeletionAsync(activity, changeReason,
            async hashKey =>
            {
                var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
                return connectedSystem == null ? null : Application.ConfigurationSnapshots.CreateSnapshot(connectedSystem, hashKey);
            },
            $"Connected System {connectedSystemId}");
    }

    // Captures a redacted, versioned snapshot of a Connector Definition onto its audit Activity via the shared
    // ConfigurationChangeCaptureService. The definition is reloaded with its files and settings so the snapshot
    // reflects persisted truth rather than the caller's partial in-memory graph; call it after the change has been
    // persisted and before the Activity is completed. A file change rolls up here too (the file methods reload the
    // owning definition), matching the granular sub-entity precedent used for Synchronisation Rules and Schedules.
    private async Task CaptureConnectorDefinitionConfigurationChangeAsync(Activity activity, int connectorDefinitionId, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.ConnectorDefinition, connectorDefinitionId,
            async hashKey =>
            {
                var definition = await Application.Repository.ConnectedSystems.GetConnectorDefinitionAsync(connectorDefinitionId);
                return definition == null ? null : Application.ConfigurationSnapshots.CreateSnapshot(definition, hashKey);
            },
            $"Connector Definition {connectorDefinitionId}");
    }

    // Captures a tombstone snapshot of a Connector Definition onto its delete Activity, before it is removed. Matching
    // the Synchronisation Rule and Schedule deletion behaviour, this sets neither the Activity's target column nor a
    // version; the snapshot is surfaced via the Activity itself rather than the object's history.
    private async Task CaptureConnectorDefinitionDeletionAsync(Activity activity, ConnectorDefinition connectorDefinition, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureDeletionAsync(activity, changeReason,
            async hashKey =>
            {
                // Reload with files and settings for a complete tombstone; fall back to the caller's entity if already gone.
                var persisted = await Application.Repository.ConnectedSystems.GetConnectorDefinitionAsync(connectorDefinition.Id) ?? connectorDefinition;
                return Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"Connector Definition {connectorDefinition.Id}");
    }

    /// <summary>
    /// Records a System-attributed Create Activity and version-1 baseline snapshot for a built-in Connector Definition
    /// that has just been seeded, grouping it under the seeding pass's parent Activity. Like the built-in Predefined
    /// Searches, the built-in Connector Definitions are persisted together in one seeding repository batch, so the
    /// baseline is recorded after the batch rather than by re-routing each through
    /// <see cref="CreateConnectorDefinitionAsync"/>. Idempotency is the caller's responsibility:
    /// <see cref="SeedingServer"/> only calls this for definitions it created in the current pass, so it is safe even
    /// when configuration change tracking is disabled and no snapshot is recorded.
    /// </summary>
    internal async Task RecordSeededConnectorDefinitionBaselineAsync(int connectorDefinitionId, string connectorName, Guid parentActivityId)
    {
        var activity = new Activity
        {
            TargetName = connectorName,
            TargetType = ActivityTargetType.ConnectorDefinition,
            TargetOperationType = ActivityTargetOperationType.Create,
            ParentActivityId = parentActivityId,
            Message = $"Created built-in Connector Definition '{connectorName}'"
        };
        await Application.Activities.CreateSystemActivityAsync(activity);

        try
        {
            await CaptureConnectorDefinitionConfigurationChangeAsync(activity, connectorDefinitionId,
                "Built-in Connector Definition created automatically by JIM.");
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    #region Connector Definitions
    public async Task<IList<ConnectorDefinitionHeader>> GetConnectorDefinitionHeadersAsync()
    {
        return await Application.Repository.ConnectedSystems.GetConnectorDefinitionHeadersAsync();
    }

    public async Task<ConnectorDefinition?> GetConnectorDefinitionAsync(int id, bool withChangeTracking = false)
    {
        return await Application.Repository.ConnectedSystems.GetConnectorDefinitionAsync(id, withChangeTracking);
    }

    public async Task<ConnectorDefinition?> GetConnectorDefinitionAsync(string name, bool withChangeTracking = false)
    {
        return await Application.Repository.ConnectedSystems.GetConnectorDefinitionAsync(name, withChangeTracking);
    }

    /// <summary>
    /// Gets the wire standard a Connected System's schema follows, as declared by its Connector, so the portal
    /// can show the right Standard Mapping hints in the Attribute Flow editor (#1122). Returns
    /// <see cref="AttributeStandard.NotSet"/> when the Connector declares none, or the Connected System is gone.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    public async Task<AttributeStandard> GetConnectedSystemSchemaStandardAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemSchemaStandardAsync(connectedSystemId);
    }

    /// <summary>
    /// Creates a Connector Definition, recording a Create Activity and version-1 configuration snapshot. Attributed via
    /// the initiator triad, so seeding and any future upload UI/API share one audited path. No principal-carrying caller
    /// exists yet (built-in definitions are seeded); the triad lets that caller arrive without a signature change.
    /// </summary>
    public async Task CreateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason = null, Guid? parentActivityId = null)
    {
        var activity = new Activity
        {
            TargetName = connectorDefinition.Name,
            TargetType = ActivityTargetType.ConnectorDefinition,
            TargetOperationType = ActivityTargetOperationType.Create,
            ParentActivityId = parentActivityId
        };
        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);
        await Application.Repository.ConnectedSystems.CreateConnectorDefinitionAsync(connectorDefinition);
        await CaptureConnectorDefinitionConfigurationChangeAsync(activity, connectorDefinition.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates a Connector Definition, recording an Update Activity and versioned configuration snapshot. Used today by
    /// the startup drift-sync (<see cref="SeedingServer"/>), which passes <see cref="ActivityInitiatorType.System"/> and
    /// the seeding parent so capability/setting changes shipped in new connector code are audited under System
    /// Initialisation; the semantic dedupe guard means a no-change sync records no new version.
    /// </summary>
    public async Task UpdateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason = null, Guid? parentActivityId = null)
    {
        var activity = new Activity
        {
            TargetName = connectorDefinition.Name,
            TargetType = ActivityTargetType.ConnectorDefinition,
            TargetOperationType = ActivityTargetOperationType.Update,
            ParentActivityId = parentActivityId
        };
        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);
        await Application.Repository.ConnectedSystems.UpdateConnectorDefinitionAsync(connectorDefinition);
        await CaptureConnectorDefinitionConfigurationChangeAsync(activity, connectorDefinition.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes a Connector Definition, recording a Delete Activity and an unversioned tombstone snapshot before removal.
    /// </summary>
    public async Task DeleteConnectorDefinitionAsync(ConnectorDefinition connectorDefinition, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason = null)
    {
        var activity = new Activity
        {
            TargetName = connectorDefinition.Name,
            TargetType = ActivityTargetType.ConnectorDefinition,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);
        await CaptureConnectorDefinitionDeletionAsync(activity, connectorDefinition, changeReason);
        await Application.Repository.ConnectedSystems.DeleteConnectorDefinitionAsync(connectorDefinition);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Adds a file to a Connector Definition. The change rolls up into the owning definition's configuration history (an
    /// Update Activity targeting the definition), matching the granular sub-entity precedent; the owning definition must
    /// be populated on <see cref="ConnectorDefinitionFile.ConnectorDefinition"/> so the roll-up target can be resolved.
    /// </summary>
    public async Task CreateConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason = null)
    {
        await PersistConnectorDefinitionFileChangeAsync(connectorDefinitionFile, initiatorType, initiatorId, initiatorName, changeReason,
            () => Application.Repository.ConnectedSystems.CreateConnectorDefinitionFileAsync(connectorDefinitionFile));
    }

    /// <summary>
    /// Removes a file from a Connector Definition. Rolls up into the owning definition's configuration history, as
    /// <see cref="CreateConnectorDefinitionFileAsync"/>.
    /// </summary>
    public async Task DeleteConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason = null)
    {
        await PersistConnectorDefinitionFileChangeAsync(connectorDefinitionFile, initiatorType, initiatorId, initiatorName, changeReason,
            () => Application.Repository.ConnectedSystems.DeleteConnectorDefinitionFileAsync(connectorDefinitionFile));
    }

    // Shared core for the two file mutators: resolve the owning definition, record an Update Activity against it,
    // persist the file change, then capture the definition's post-change snapshot so the file change versions once.
    private async Task PersistConnectorDefinitionFileChangeAsync(ConnectorDefinitionFile connectorDefinitionFile, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason, Func<Task> persistAsync)
    {
        var owningDefinitionId = connectorDefinitionFile.ConnectorDefinition?.Id;
        var activity = new Activity
        {
            TargetName = connectorDefinitionFile.ConnectorDefinition?.Name ?? connectorDefinitionFile.Filename,
            TargetType = ActivityTargetType.ConnectorDefinition,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);
        await persistAsync();
        if (owningDefinitionId is > 0)
            await CaptureConnectorDefinitionConfigurationChangeAsync(activity, owningDefinitionId.Value, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }
    #endregion

    #region Connected Systems
    public async Task<List<ConnectedSystem>> GetConnectedSystemsAsync()
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemsAsync();
    }

    public async Task<List<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync()
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemHeadersAsync();
    }

    public async Task<ConnectedSystem?> GetConnectedSystemAsync(int id, bool withChangeTracking = false)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(id, withChangeTracking);
    }

    /// <summary>
    /// Loads a lightweight Connected System containing only <c>ConnectorDefinition</c>, <c>SettingValues</c>,
    /// and shallow <c>RunProfiles</c>. Use for API existence checks, write-path lookups, and any other caller
    /// that does not need the full schema, partition, or matching-rule graph.
    /// </summary>
    public async Task<ConnectedSystem?> GetConnectedSystemCoreAsync(int id, bool withChangeTracking = false)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(id, withChangeTracking);
    }

    public async Task<ConnectedSystemHeader?> GetConnectedSystemHeaderAsync(int id)
    {
        using var span = Diagnostics.Diagnostics.Database.StartSpan("Cso.GetConnectedSystemHeader")
            .SetTag("connectedSystemId", id);
        return await Application.Repository.ConnectedSystems.GetConnectedSystemHeaderAsync(id);
    }

    public int GetConnectedSystemCount()
    {
        return Application.Repository.ConnectedSystems.GetConnectedSystemCount();
    }
        
    public async Task CreateConnectedSystemAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy, string? changeReason = null)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        // Fetch the ConnectorDefinition with change tracking so that EF Core recognises
        // it and its Settings as existing entities. Without tracking, EF graph traversal
        // during Add() would treat them as new and attempt duplicate inserts.
        var connectorDefinition = connectedSystem.ConnectorDefinition
            ?? await Application.Repository.ConnectedSystems.GetConnectorDefinitionAsync(connectedSystem.ConnectorDefinitionId, withChangeTracking: true)
            ?? throw new ArgumentException($"ConnectorDefinition with ID {connectedSystem.ConnectorDefinitionId} not found.");

        connectedSystem.ConnectorDefinition = connectorDefinition;

        if (connectorDefinition.Settings == null || connectorDefinition.Settings.Count == 0)
            throw new ArgumentException("connectedSystem.ConnectorDefinition has no settings. Cannot construct a valid connectedSystem object!");

        if (!AreRunProfilesValid(connectedSystem))
            throw new ArgumentException("connectedSystem.RunProfiles has some of a run type that is not supported by the Connector.");

        // create the Connected System setting value objects from the Connected System definition settings
        foreach (var definitionSetting in connectorDefinition.Settings)
        {
            var settingValue = new ConnectedSystemSettingValue {
                Setting = definitionSetting
            };

            if (definitionSetting is { Type: ConnectedSystemSettingType.CheckBox, DefaultCheckboxValue: not null })
                settingValue.CheckboxValue = definitionSetting.DefaultCheckboxValue.Value;

            // Apply default string values for String, DropDown, and File settings
            if ((definitionSetting.Type == ConnectedSystemSettingType.String ||
                 definitionSetting.Type == ConnectedSystemSettingType.DropDown ||
                 definitionSetting.Type == ConnectedSystemSettingType.File) &&
                !string.IsNullOrEmpty(definitionSetting.DefaultStringValue))
                settingValue.StringValue = definitionSetting.DefaultStringValue.Trim();

            if (definitionSetting is { Type: ConnectedSystemSettingType.Integer, DefaultIntValue: not null })
                settingValue.IntValue = definitionSetting.DefaultIntValue.Value;

            connectedSystem.SettingValues.Add(settingValue);
        }

        SanitiseConnectedSystemUserInput(connectedSystem);

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        AuditHelper.SetCreated(connectedSystem, initiatedBy);
        await Application.Repository.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem);
        await CaptureConfigurationChangeAsync(activity, connectedSystem, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Creates a new Connected System (initiated by API key).
    /// </summary>
    public async Task CreateConnectedSystemAsync(ConnectedSystem connectedSystem, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        // Fetch the ConnectorDefinition with change tracking so that EF Core recognises
        // it and its Settings as existing entities. Without tracking, EF graph traversal
        // during Add() would treat them as new and attempt duplicate inserts.
        var connectorDefinition = connectedSystem.ConnectorDefinition
            ?? await Application.Repository.ConnectedSystems.GetConnectorDefinitionAsync(connectedSystem.ConnectorDefinitionId, withChangeTracking: true)
            ?? throw new ArgumentException($"ConnectorDefinition with ID {connectedSystem.ConnectorDefinitionId} not found.");

        connectedSystem.ConnectorDefinition = connectorDefinition;

        if (connectorDefinition.Settings == null || connectorDefinition.Settings.Count == 0)
            throw new ArgumentException("connectedSystem.ConnectorDefinition has no settings. Cannot construct a valid connectedSystem object!");

        if (!AreRunProfilesValid(connectedSystem))
            throw new ArgumentException("connectedSystem.RunProfiles has some of a run type that is not supported by the Connector.");

        // create the Connected System setting value objects from the Connected System definition settings
        foreach (var definitionSetting in connectorDefinition.Settings)
        {
            var settingValue = new ConnectedSystemSettingValue {
                Setting = definitionSetting
            };

            if (definitionSetting is { Type: ConnectedSystemSettingType.CheckBox, DefaultCheckboxValue: not null })
                settingValue.CheckboxValue = definitionSetting.DefaultCheckboxValue.Value;

            // Apply default string values for String, DropDown, and File settings
            if ((definitionSetting.Type == ConnectedSystemSettingType.String ||
                 definitionSetting.Type == ConnectedSystemSettingType.DropDown ||
                 definitionSetting.Type == ConnectedSystemSettingType.File) &&
                !string.IsNullOrEmpty(definitionSetting.DefaultStringValue))
                settingValue.StringValue = definitionSetting.DefaultStringValue.Trim();

            if (definitionSetting is { Type: ConnectedSystemSettingType.Integer, DefaultIntValue: not null })
                settingValue.IntValue = definitionSetting.DefaultIntValue.Value;

            connectedSystem.SettingValues.Add(settingValue);
        }

        SanitiseConnectedSystemUserInput(connectedSystem);

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        AuditHelper.SetCreated(connectedSystem, initiatedByApiKey);
        await Application.Repository.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem);
        await CaptureConfigurationChangeAsync(activity, connectedSystem, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <param name="previewActivityId">
    /// The Configuration Change Preview this change was made after reading, where one was run. Recorded on the
    /// Activity so "previewed, then applied" is auditable rather than a claim (#827).
    /// </param>
    public async Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy,
        string? changeReason = null, Guid? previewActivityId = null)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (!AreRunProfilesValid(connectedSystem))
            throw new ArgumentException("connectedSystem.RunProfiles has some of a run type that is not supported by the Connector.");

        Log.Verbose($"UpdateConnectedSystemAsync() called for {connectedSystem}");

        var validationResults = ValidateConnectedSystemSettings(connectedSystem);
        connectedSystem.SettingValuesValid = validationResults.All(q => q.IsValid);

        AuditHelper.SetUpdated(connectedSystem, initiatedBy);

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = connectedSystem.Id,
            PreviewActivityId = previewActivityId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        SanitiseConnectedSystemUserInput(connectedSystem);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);

        await CaptureConfigurationChangeAsync(activity, connectedSystem, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Connected System (initiated by API key).
    /// </summary>
    public async Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (!AreRunProfilesValid(connectedSystem))
            throw new ArgumentException("connectedSystem.RunProfiles has some of a run type that is not supported by the Connector.");

        Log.Verbose($"UpdateConnectedSystemAsync() called for {connectedSystem} (API key initiated)");

        var validationResults = ValidateConnectedSystemSettings(connectedSystem);
        connectedSystem.SettingValuesValid = validationResults.All(q => q.IsValid);

        AuditHelper.SetUpdated(connectedSystem, initiatedByApiKey);

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        SanitiseConnectedSystemUserInput(connectedSystem);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);

        await CaptureConfigurationChangeAsync(activity, connectedSystem, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Connected System including reconciliation of its schema (object types and attributes).
    /// Use this from the schema configuration UI, where the object type/attribute collection is the authoritative
    /// payload; <see cref="UpdateConnectedSystemAsync(ConnectedSystem, MetaverseObject?)"/> does not persist
    /// object type/attribute changes. See issue #782.
    /// </summary>
    public async Task UpdateConnectedSystemSchemaAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (!AreRunProfilesValid(connectedSystem))
            throw new ArgumentException("connectedSystem.RunProfiles has some of a run type that is not supported by the Connector.");

        Log.Verbose($"UpdateConnectedSystemSchemaAsync() called for {connectedSystem}");

        // Whole-graph save: the caller supplies the object types and attributes wholesale, so this is the last gate
        // before persistence. Credential attributes are forced back into a safe state here regardless of what the
        // caller sent, which closes any route that sets Selected outside the validated per-attribute endpoints.
        QuarantineCredentialAttributes(connectedSystem);

        var validationResults = ValidateConnectedSystemSettings(connectedSystem);
        connectedSystem.SettingValuesValid = validationResults.All(q => q.IsValid);

        AuditHelper.SetUpdated(connectedSystem, initiatedBy);

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        SanitiseConnectedSystemUserInput(connectedSystem);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem);

        // Reload for the snapshot: schema reconciliation assigns ids server-side, so the caller's graph is stale.
        await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystem.Id);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Validates, sanitises, and persists a Connected System update without creating an Activity record.
    /// Used internally by operations that already have their own parent activity (ImportSchema, ImportHierarchy,
    /// RefreshAndAutoSelectContainers), where creating a child Update activity would be noise.
    /// </summary>
    private async Task PersistConnectedSystemUpdateAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy)
    {
        var validationResults = ValidateConnectedSystemSettings(connectedSystem);
        connectedSystem.SettingValuesValid = validationResults.All(q => q.IsValid);
        AuditHelper.SetUpdated(connectedSystem, initiatedBy);
        SanitiseConnectedSystemUserInput(connectedSystem);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
    }

    /// <summary>
    /// Validates, sanitises, and persists a Connected System update without creating an Activity record.
    /// Used internally by operations that already have their own parent activity (ImportSchema, ImportHierarchy,
    /// RefreshAndAutoSelectContainers), where creating a child Update activity would be noise.
    /// </summary>
    private async Task PersistConnectedSystemUpdateAsync(ConnectedSystem connectedSystem, ApiKey initiatedByApiKey)
    {
        var validationResults = ValidateConnectedSystemSettings(connectedSystem);
        connectedSystem.SettingValuesValid = validationResults.All(q => q.IsValid);
        AuditHelper.SetUpdated(connectedSystem, initiatedByApiKey);
        SanitiseConnectedSystemUserInput(connectedSystem);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
    }

    /// <summary>
    /// Validates, sanitises, and persists a Connected System update without creating an Activity record.
    /// Used internally by operations that already have their own parent activity (ImportSchema, ImportHierarchy,
    /// RefreshAndAutoSelectContainers), where creating a child Update activity would be noise.
    /// </summary>
    private async Task PersistConnectedSystemUpdateAsync(ConnectedSystem connectedSystem, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName)
    {
        var validationResults = ValidateConnectedSystemSettings(connectedSystem);
        connectedSystem.SettingValuesValid = validationResults.All(q => q.IsValid);
        AuditHelper.SetUpdated(connectedSystem, initiatorType, initiatorId, initiatorName);
        SanitiseConnectedSystemUserInput(connectedSystem);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
    }

    /// <summary>
    /// As <see cref="PersistConnectedSystemUpdateAsync(ConnectedSystem, MetaverseObject?)"/>, but also reconciles
    /// the object types and attributes. Used by schema import, where the object type collection is the payload.
    /// </summary>
    private async Task PersistConnectedSystemSchemaUpdateAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy)
    {
        var validationResults = ValidateConnectedSystemSettings(connectedSystem);
        connectedSystem.SettingValuesValid = validationResults.All(q => q.IsValid);
        AuditHelper.SetUpdated(connectedSystem, initiatedBy);
        SanitiseConnectedSystemUserInput(connectedSystem);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem);
    }

    /// <summary>
    /// As <see cref="PersistConnectedSystemUpdateAsync(ConnectedSystem, ApiKey)"/>, but also reconciles the
    /// object types and attributes. Used by schema import (API key initiated).
    /// </summary>
    private async Task PersistConnectedSystemSchemaUpdateAsync(ConnectedSystem connectedSystem, ApiKey initiatedByApiKey)
    {
        var validationResults = ValidateConnectedSystemSettings(connectedSystem);
        connectedSystem.SettingValuesValid = validationResults.All(q => q.IsValid);
        AuditHelper.SetUpdated(connectedSystem, initiatedByApiKey);
        SanitiseConnectedSystemUserInput(connectedSystem);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem);
    }

    /// <summary>
    /// Persists the connector's watermark (<see cref="ConnectedSystem.PersistedConnectorData"/>, e.g. an LDAP sync
    /// cookie or USN) after an import, without creating an Activity or capturing a configuration snapshot. The
    /// watermark is machine-generated runtime state that changes on virtually every import; it is not a decision a
    /// security principal made, and the import itself is already audited by its Run Profile Execution Activity.
    /// Routing it through an Activity-creating update path would record a spurious Connected System Update on every
    /// import cycle.
    /// </summary>
    public async Task UpdateConnectedSystemPersistedConnectorDataAsync(ConnectedSystem connectedSystem, string? persistedConnectorData)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        // Keep the caller's instance in step with the database, then write ONLY the one column. This
        // deliberately does not go through UpdateConnectedSystemAsync: that path marks the entire graph
        // Modified, and the in-memory system handed in here can legitimately carry runtime-only
        // setting-value instances (a Setting navigation with no FK scalar) that must never be written
        // back; doing so failed export runs with a SettingId 0 foreign key violation the first time a
        // connector returned close-time state (the #230 pin establishment path).
        connectedSystem.PersistedConnectorData = persistedConnectorData;
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemPersistedConnectorDataAsync(connectedSystem.Id, persistedConnectorData);
    }

    /// <summary>
    /// Persists a runtime status change (e.g. resetting <see cref="ConnectedSystemStatus.Deleting"/> back to
    /// <see cref="ConnectedSystemStatus.Active"/> after a failed deletion) without creating an Activity or capturing a
    /// configuration snapshot. Status is runtime state, not configuration; routing it through the full
    /// <see cref="UpdateConnectedSystemAsync(ConnectedSystem, MetaverseObject?, string?)"/> would record a spurious
    /// configuration-change version.
    /// </summary>
    public async Task UpdateConnectedSystemStatusAsync(ConnectedSystem connectedSystem, ConnectedSystemStatus status)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        connectedSystem.Status = status;
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
    }

    /// <summary>
    /// Try and prevent the user from supplying unusable input.
    /// </summary>
    private static void SanitiseConnectedSystemUserInput(ConnectedSystem connectedSystem)
    {
        connectedSystem.Name = connectedSystem.Name.Trim();
        if (!string.IsNullOrEmpty(connectedSystem.Description))
            connectedSystem.Description = connectedSystem.Description.Trim();

        foreach (var settingValue in connectedSystem.SettingValues)
            if (!string.IsNullOrEmpty(settingValue.StringValue))
                settingValue.StringValue = settingValue.StringValue.Trim();
    }

    /// <summary>
    /// Switches the Object Matching Rule mode for a Connected System.
    /// When switching to Advanced Mode (SyncRule), copies matching rules from
    /// Connected System Object Types to all import Synchronisation Rules.
    /// When switching to Simple Mode (ConnectedSystem), analyses Synchronisation Rule matching rules,
    /// selects the most common configuration per object type, and clears Synchronisation Rule rules.
    /// </summary>
    /// <param name="connectedSystem">The Connected System to update</param>
    /// <param name="newMode">The new Object Matching Rule mode</param>
    /// <param name="initiatedBy">The user initiating the change</param>
    /// <returns>Result containing details about the switch operation</returns>
    public async Task<ObjectMatchingModeSwitchResult> SwitchObjectMatchingModeAsync(
        ConnectedSystem connectedSystem,
        ObjectMatchingRuleMode newMode,
        MetaverseObject? initiatedBy)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (connectedSystem.ObjectMatchingRuleMode == newMode)
        {
            Log.Debug("SwitchObjectMatchingModeAsync: Connected System {Id} is already in {Mode} mode",
                connectedSystem.Id, newMode);
            return ObjectMatchingModeSwitchResult.NoChange(newMode);
        }

        Log.Information("SwitchObjectMatchingModeAsync: Switching Connected System {Id} from {OldMode} to {NewMode}",
            connectedSystem.Id, connectedSystem.ObjectMatchingRuleMode, newMode);

        ObjectMatchingModeSwitchResult result;

        if (newMode == ObjectMatchingRuleMode.SyncRule)
        {
            // Switching to Advanced Mode - copy matching rules to import Synchronisation Rules
            result = await SwitchToAdvancedModeAsync(connectedSystem, initiatedBy);
        }
        else
        {
            // Switching to Simple Mode - migrate rules from Synchronisation Rules to object types
            result = await SwitchToSimpleModeAsync(connectedSystem, initiatedBy);
        }

        if (!result.Success)
            return result;

        // Update the Connected System mode
        connectedSystem.ObjectMatchingRuleMode = newMode;
        AuditHelper.SetUpdated(connectedSystem, initiatedBy);

        // Create activity for tracking
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);

        await CaptureConfigurationChangeAsync(activity, connectedSystem, changeReason: null);
        await Application.Activities.CompleteActivityAsync(activity);

        return result;
    }

    private async Task<ObjectMatchingModeSwitchResult> SwitchToAdvancedModeAsync(
        ConnectedSystem connectedSystem,
        MetaverseObject? initiatedBy)
    {
        var syncRulesUpdated = 0;
        var syncRules = await GetSyncRulesAsync(connectedSystem.Id, includeDisabledSyncRules: true);
        var importSyncRules = syncRules.Where(sr => sr.Direction == SyncRuleDirection.Import).ToList();

        foreach (var syncRule in importSyncRules)
        {
            // Find matching rules for the Synchronisation Rule's object type
            var objectType = connectedSystem.ObjectTypes?.FirstOrDefault(ot => ot.Id == syncRule.ConnectedSystemObjectTypeId);
            if (objectType == null || objectType.ObjectMatchingRules.Count == 0)
                continue;

            // Only copy if Synchronisation Rule doesn't already have matching rules
            if (syncRule.ObjectMatchingRules.Count > 0)
                continue;

            foreach (var sourceRule in objectType.ObjectMatchingRules)
            {
                var newRule = new ObjectMatchingRule
                {
                    Order = sourceRule.Order,
                    TargetMetaverseAttributeId = sourceRule.TargetMetaverseAttributeId,
                    CaseSensitive = sourceRule.CaseSensitive,
                    Sources = sourceRule.Sources.Select(s => new ObjectMatchingRuleSource
                    {
                        Order = s.Order,
                        ConnectedSystemAttributeId = s.ConnectedSystemAttributeId,
                        Expression = s.Expression
                    }).ToList()
                };
                syncRule.ObjectMatchingRules.Add(newRule);
            }

            await CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
            syncRulesUpdated++;
        }

        Log.Information("SwitchToAdvancedModeAsync: Copied matching rules to {Count} Synchronisation Rule(s)", syncRulesUpdated);
        return ObjectMatchingModeSwitchResult.ToAdvancedMode(syncRulesUpdated);
    }

    private async Task<ObjectMatchingModeSwitchResult> SwitchToSimpleModeAsync(
        ConnectedSystem connectedSystem,
        MetaverseObject? initiatedBy)
    {
        var migrations = new List<ObjectTypeMatchingRuleMigration>();
        var objectTypesUpdated = 0;

        var syncRules = await GetSyncRulesAsync(connectedSystem.Id, includeDisabledSyncRules: true);
        var importSyncRules = syncRules.Where(sr => sr.Direction == SyncRuleDirection.Import).ToList();

        // Group Synchronisation Rules by object type
        var syncRulesByObjectType = importSyncRules
            .GroupBy(sr => sr.ConnectedSystemObjectTypeId)
            .ToList();

        foreach (var objectTypeGroup in syncRulesByObjectType)
        {
            var objectTypeId = objectTypeGroup.Key;
            var objectType = connectedSystem.ObjectTypes?.FirstOrDefault(ot => ot.Id == objectTypeId);

            if (objectType == null)
                continue;

            var migration = new ObjectTypeMatchingRuleMigration
            {
                ObjectTypeId = objectTypeId,
                ObjectTypeName = objectType.Name,
                SyncRuleCount = objectTypeGroup.Count(),
                SyncRulesWithMatchingRules = objectTypeGroup.Count(sr => sr.ObjectMatchingRules.Count > 0)
            };

            // Get Synchronisation Rules that have matching rules defined
            var syncRulesWithRules = objectTypeGroup
                .Where(sr => sr.ObjectMatchingRules.Count > 0)
                .ToList();

            if (syncRulesWithRules.Count > 0)
            {
                // Create a signature for each Synchronisation Rule's matching rules configuration
                var ruleConfigurations = syncRulesWithRules
                    .Select(sr => GetMatchingRulesSignature(sr.ObjectMatchingRules))
                    .ToList();

                migration.UniqueSyncRuleConfigurations = ruleConfigurations.Distinct().Count();

                // Find the most common configuration
                var mostCommonSignature = ruleConfigurations
                    .GroupBy(sig => sig)
                    .OrderByDescending(g => g.Count())
                    .First()
                    .Key;

                // Get the Synchronisation Rule with the most common configuration
                var sourceSyncRule = syncRulesWithRules
                    .First(sr => GetMatchingRulesSignature(sr.ObjectMatchingRules) == mostCommonSignature);

                // Copy matching rules to the object type (if it doesn't already have rules)
                if (objectType.ObjectMatchingRules.Count == 0)
                {
                    foreach (var sourceRule in sourceSyncRule.ObjectMatchingRules)
                    {
                        var newRule = new ObjectMatchingRule
                        {
                            Order = sourceRule.Order,
                            ConnectedSystemObjectTypeId = objectTypeId,
                            MetaverseObjectTypeId = sourceSyncRule.MetaverseObjectTypeId,
                            TargetMetaverseAttributeId = sourceRule.TargetMetaverseAttributeId,
                            CaseSensitive = sourceRule.CaseSensitive,
                            Sources = sourceRule.Sources.Select(s => new ObjectMatchingRuleSource
                            {
                                Order = s.Order,
                                ConnectedSystemAttributeId = s.ConnectedSystemAttributeId,
                                Expression = s.Expression
                            }).ToList()
                        };
                        objectType.ObjectMatchingRules.Add(newRule);
                    }

                    migration.MatchingRulesSet = sourceSyncRule.ObjectMatchingRules.Count;
                    objectTypesUpdated++;

                    Log.Information("SwitchToSimpleModeAsync: Set {Count} matching rule(s) on object type {ObjectType} " +
                        "(selected from {SyncRuleCount} Synchronisation Rules with {UniqueConfigs} unique configuration(s))",
                        migration.MatchingRulesSet, objectType.Name, migration.SyncRulesWithMatchingRules,
                        migration.UniqueSyncRuleConfigurations);
                }
            }

            // Clear matching rules from all Synchronisation Rules for this object type
            // (will be done automatically by CreateOrUpdateSyncRuleAsync due to Simple Mode validation)
            foreach (var syncRule in objectTypeGroup.Where(sr => sr.ObjectMatchingRules.Count > 0))
            {
                syncRule.ObjectMatchingRules.Clear();
                await Application.Repository.ConnectedSystems.UpdateSyncRuleAsync(syncRule);
                migration.SyncRulesCleared++;
            }

            migrations.Add(migration);
        }

        Log.Information("SwitchToSimpleModeAsync: Updated {Count} object type(s) with matching rules", objectTypesUpdated);
        return ObjectMatchingModeSwitchResult.ToSimpleMode(objectTypesUpdated, migrations);
    }

    /// <summary>
    /// Creates a signature string representing a set of matching rules for comparison.
    /// </summary>
    private static string GetMatchingRulesSignature(ICollection<ObjectMatchingRule> rules)
    {
        if (rules.Count == 0)
            return string.Empty;

        var ruleSignatures = rules
            .OrderBy(r => r.Order)
            .Select(r =>
            {
                var sourceSignatures = r.Sources
                    .OrderBy(s => s.Order)
                    .Select(s => $"{s.ConnectedSystemAttributeId}:{s.Expression}")
                    .ToList();

                return $"{r.TargetMetaverseAttributeId}|{r.CaseSensitive}|{string.Join(",", sourceSignatures)}";
            })
            .ToList();

        return string.Join(";", ruleSignatures);
    }
    #endregion

    #region Connected System Deletion
    /// <summary>
    /// Threshold for CSO count above which deletion runs as a background job.
    /// </summary>
    private const int BackgroundDeletionThreshold = 1000;

    /// <summary>
    /// Generates a preview of the impact of deleting a Connected System.
    /// This allows administrators to understand what will be affected before confirming deletion.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <returns>A preview showing counts of affected objects and any warnings.</returns>
    public async Task<ConnectedSystemDeletionPreview?> GetDeletionPreviewAsync(int connectedSystemId)
    {
        // Core: only Name and Status are read below; the rest of the preview comes from dedicated count queries.
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return null;

        var preview = new ConnectedSystemDeletionPreview
        {
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemName = connectedSystem.Name
        };

        // Get counts of related objects
        preview.ConnectedSystemObjectCount = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync(connectedSystemId);
        preview.SyncRuleCount = await Application.Repository.ConnectedSystems.GetSyncRuleCountAsync(connectedSystemId);
        preview.RunProfileCount = await Application.Repository.ConnectedSystems.GetRunProfileCountAsync(connectedSystemId);
        preview.PartitionCount = await Application.Repository.ConnectedSystems.GetPartitionCountAsync(connectedSystemId);
        preview.ContainerCount = await Application.Repository.ConnectedSystems.GetContainerCountAsync(connectedSystemId);
        preview.PendingExportCount = await Application.Repository.ConnectedSystems.GetPendingExportsCountAsync(connectedSystemId);
        preview.ActivityCount = await Application.Repository.ConnectedSystems.GetActivityCountAsync(connectedSystemId);

        // Get MVO impact counts
        preview.JoinedMvoCount = await Application.Repository.ConnectedSystems.GetJoinedMvoCountAsync(connectedSystemId);

        // Count the MVOs deletion rule evaluation will mark for deletion, via the same mode-aware
        // predicate ExecuteDeletionAsync's marking uses, so the preview always agrees with what
        // execution does (#119).
        preview.MvosWithDeletionRuleCount = await Application.Metaverse.GetMvosOrphanedByConnectedSystemDeletionCountAsync(connectedSystemId);

        // Check for running sync operations
        var runningSyncTask = await Application.Repository.ConnectedSystems.GetRunningSyncTaskAsync(connectedSystemId);
        preview.HasRunningSyncOperation = runningSyncTask != null;

        // Determine if deletion will run as a background job
        preview.WillRunAsBackgroundJob = preview.ConnectedSystemObjectCount > BackgroundDeletionThreshold;

        // Estimate deletion time (rough estimate: ~100 CSOs per second for bulk delete)
        var estimatedSeconds = preview.ConnectedSystemObjectCount / 100.0;
        preview.EstimatedDeletionTime = TimeSpan.FromSeconds(Math.Max(1, estimatedSeconds));

        // Add warnings
        if (preview.HasRunningSyncOperation)
            preview.Warnings.Add("A synchronisation operation is currently running. Deletion will be queued to run after it completes.");

        if (preview.SyncRuleCount > 0)
            preview.Warnings.Add($"{preview.SyncRuleCount} Synchronisation Rule(s) will be permanently deleted.");

        if (preview.JoinedMvoCount > 0)
            preview.Warnings.Add($"{preview.JoinedMvoCount} Metaverse Object(s) are joined to CSOs in this system. They will be disconnected.");

        if (preview.MvosWithDeletionRuleCount > 0)
            preview.Warnings.Add($"{preview.MvosWithDeletionRuleCount} Metaverse Object(s) will be marked for deletion by their type's Deletion Rule.");

        if (preview.PendingExportCount > 0)
            preview.Warnings.Add($"{preview.PendingExportCount} Pending Export(s) will be deleted.");

        if (connectedSystem.Status == ConnectedSystemStatus.Deleting)
            preview.Warnings.Add("This Connected System is already being deleted.");

        return preview;
    }

    /// <summary>
    /// Deletes a Connected System and all its related data.
    /// Implements the queue-based deletion approach:
    /// 1. Sets status to Deleting (blocks new operations)
    /// 2. If sync is running, queues deletion to run after sync completes
    /// 3. Otherwise, executes deletion (sync or async based on CSO count)
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to delete.</param>
    /// <param name="initiatedBy">The user who initiated the deletion.</param>
    /// <param name="deleteChangeHistory">Whether to delete change history for the deleted CSOs. Default: false (preserves audit trail).</param>
    /// <returns>The result of the deletion request.</returns>
    public async Task<ConnectedSystemDeletionResult> DeleteAsync(int connectedSystemId, MetaverseObject? initiatedBy, bool deleteChangeHistory = false, string? changeReason = null)
    {
        Log.Information("DeleteAsync: Starting deletion for Connected System {Id}, initiated by {User}, deleteChangeHistory={DeleteHistory}",
            connectedSystemId, initiatedBy?.NameOrId ?? "System", deleteChangeHistory);

        // Get the Connected System (Core: only Name and Status are read, and Status is updated via the entity).
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
        {
            Log.Warning("DeleteAsync: Connected System {Id} not found", connectedSystemId);
            return ConnectedSystemDeletionResult.Failed($"Connected System with ID {connectedSystemId} not found.");
        }

        // Check if already being deleted
        if (connectedSystem.Status == ConnectedSystemStatus.Deleting)
        {
            Log.Warning("DeleteAsync: Connected System {Id} is already being deleted", connectedSystemId);
            return ConnectedSystemDeletionResult.Failed("Connected System is already being deleted.");
        }

        // Set status to Deleting to block new operations
        connectedSystem.Status = ConnectedSystemStatus.Deleting;
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
        Log.Information("DeleteAsync: Set Connected System {Id} status to Deleting", connectedSystemId);

        // Check for running sync operations
        var runningSyncTask = await Application.Repository.ConnectedSystems.GetRunningSyncTaskAsync(connectedSystemId);
        if (runningSyncTask != null)
        {
            // Queue deletion to run after sync completes
            Log.Information("DeleteAsync: Sync task {TaskId} is running for Connected System {CsId}. Queuing deletion.",
                runningSyncTask.Id, connectedSystemId);

            var deleteTask = initiatedBy != null
                ? DeleteConnectedSystemWorkerTask.ForUser(connectedSystemId, initiatedBy.Id, initiatedBy.NameOrId, evaluateMvoDeletionRules: true, deleteChangeHistory)
                : new DeleteConnectedSystemWorkerTask(connectedSystemId, evaluateMvoDeletionRules: true, deleteChangeHistory);
            deleteTask.ChangeReason = changeReason;
            _ = await Application.Tasking.CreateWorkerTaskAsync(deleteTask);

            return ConnectedSystemDeletionResult.QueuedAfterSync(deleteTask.Id, deleteTask.Activity!.Id);
        }

        // Get CSO count to determine sync vs async deletion
        var csoCount = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync(connectedSystemId);

        if (csoCount > BackgroundDeletionThreshold)
        {
            // Large system - queue as background job
            Log.Information("DeleteAsync: Connected System {Id} has {Count} CSOs (>{Threshold}). Queueing as background job.",
                connectedSystemId, csoCount, BackgroundDeletionThreshold);

            var deleteTask = initiatedBy != null
                ? DeleteConnectedSystemWorkerTask.ForUser(connectedSystemId, initiatedBy.Id, initiatedBy.NameOrId, evaluateMvoDeletionRules: true, deleteChangeHistory)
                : new DeleteConnectedSystemWorkerTask(connectedSystemId, evaluateMvoDeletionRules: true, deleteChangeHistory);
            deleteTask.ChangeReason = changeReason;
            _ = await Application.Tasking.CreateWorkerTaskAsync(deleteTask);

            return ConnectedSystemDeletionResult.QueuedAsBackgroundJob(deleteTask.Id, deleteTask.Activity!.Id);
        }

        // Small system - execute synchronously
        Log.Information("DeleteAsync: Connected System {Id} has {Count} CSOs (<={Threshold}). Executing synchronously.",
            connectedSystemId, csoCount, BackgroundDeletionThreshold);

        // Create activity for the synchronous deletion
        // Note: We don't set ConnectedSystemId because the deletion will remove the Connected System,
        // and we need to be able to complete/fail the activity after deletion.
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Delete
            // ConnectedSystemId intentionally not set - the CS will be deleted before activity completes
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        try
        {
            // Capture the tombstone, mark orphaned MVOs, and delete, via the shared delete implementation so the
            // synchronous and worker paths cannot drift.
            await ExecuteDeletionAsync(connectedSystemId, activity, changeReason, evaluateMvoDeletionRules: true, deleteChangeHistory);

            // Complete the activity
            await Application.Activities.CompleteActivityAsync(activity);

            Log.Information("DeleteAsync: Connected System {Id} deleted successfully", connectedSystemId);
            return ConnectedSystemDeletionResult.CompletedImmediately(activity.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DeleteAsync: Failed to delete Connected System {Id}", connectedSystemId);

            // Build full error message including inner exceptions
            var errorMessage = GetFullExceptionMessage(ex);

            // Mark activity as failed
            await Application.Activities.FailActivityWithErrorAsync(activity, errorMessage);

            // Reset status so deletion can be retried
            connectedSystem.Status = ConnectedSystemStatus.Active;
            await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);

            return ConnectedSystemDeletionResult.Failed($"Failed to delete Connected System: {errorMessage}");
        }
    }

    /// <summary>
    /// Deletes a Connected System (initiated by API key).
    /// </summary>
    public async Task<ConnectedSystemDeletionResult> DeleteAsync(int connectedSystemId, ApiKey initiatedByApiKey, bool deleteChangeHistory = false, string? changeReason = null)
    {
        Log.Information("DeleteAsync: Starting deletion for Connected System {Id}, initiated by API key {ApiKeyName}, deleteChangeHistory={DeleteHistory}",
            connectedSystemId, initiatedByApiKey.Name, deleteChangeHistory);

        // Get the Connected System (Core: only Name and Status are read, and Status is updated via the entity).
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
        {
            Log.Warning("DeleteAsync: Connected System {Id} not found", connectedSystemId);
            return ConnectedSystemDeletionResult.Failed($"Connected System with ID {connectedSystemId} not found.");
        }

        // Check if already being deleted
        if (connectedSystem.Status == ConnectedSystemStatus.Deleting)
        {
            Log.Warning("DeleteAsync: Connected System {Id} is already being deleted", connectedSystemId);
            return ConnectedSystemDeletionResult.Failed("Connected System is already being deleted.");
        }

        // Set status to Deleting to block new operations
        connectedSystem.Status = ConnectedSystemStatus.Deleting;
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
        Log.Information("DeleteAsync: Set Connected System {Id} status to Deleting", connectedSystemId);

        // Check for running sync operations
        var runningSyncTask = await Application.Repository.ConnectedSystems.GetRunningSyncTaskAsync(connectedSystemId);
        if (runningSyncTask != null)
        {
            Log.Information("DeleteAsync: Sync task {TaskId} is running for Connected System {CsId}. Queuing deletion.",
                runningSyncTask.Id, connectedSystemId);

            var deleteTask = DeleteConnectedSystemWorkerTask.ForApiKey(connectedSystemId, initiatedByApiKey.Id, initiatedByApiKey.Name, evaluateMvoDeletionRules: true, deleteChangeHistory);
            deleteTask.ChangeReason = changeReason;
            _ = await Application.Tasking.CreateWorkerTaskAsync(deleteTask);

            return ConnectedSystemDeletionResult.QueuedAfterSync(deleteTask.Id, deleteTask.Activity!.Id);
        }

        // Get CSO count to determine sync vs async deletion
        var csoCount = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync(connectedSystemId);

        if (csoCount > BackgroundDeletionThreshold)
        {
            Log.Information("DeleteAsync: Connected System {Id} has {Count} CSOs (>{Threshold}). Queueing as background job.",
                connectedSystemId, csoCount, BackgroundDeletionThreshold);

            var deleteTask = DeleteConnectedSystemWorkerTask.ForApiKey(connectedSystemId, initiatedByApiKey.Id, initiatedByApiKey.Name, evaluateMvoDeletionRules: true, deleteChangeHistory);
            deleteTask.ChangeReason = changeReason;
            _ = await Application.Tasking.CreateWorkerTaskAsync(deleteTask);

            return ConnectedSystemDeletionResult.QueuedAsBackgroundJob(deleteTask.Id, deleteTask.Activity!.Id);
        }

        // Small system - execute synchronously
        Log.Information("DeleteAsync: Connected System {Id} has {Count} CSOs (<={Threshold}). Executing synchronously.",
            connectedSystemId, csoCount, BackgroundDeletionThreshold);

        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        try
        {
            // Capture the tombstone, mark orphaned MVOs, and delete, via the shared delete implementation so the
            // synchronous and worker paths cannot drift.
            await ExecuteDeletionAsync(connectedSystemId, activity, changeReason, evaluateMvoDeletionRules: true, deleteChangeHistory);
            await Application.Activities.CompleteActivityAsync(activity);

            Log.Information("DeleteAsync: Connected System {Id} deleted successfully", connectedSystemId);
            return ConnectedSystemDeletionResult.CompletedImmediately(activity.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DeleteAsync: Failed to delete Connected System {Id}", connectedSystemId);

            var errorMessage = GetFullExceptionMessage(ex);
            await Application.Activities.FailActivityWithErrorAsync(activity, errorMessage);

            connectedSystem.Status = ConnectedSystemStatus.Active;
            await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);

            return ConnectedSystemDeletionResult.Failed($"Failed to delete Connected System: {errorMessage}");
        }
    }

    /// <summary>
    /// Executes the deletion of a Connected System. This is the single delete implementation shared by the worker
    /// service (for queued and background deletions) and the synchronous small-system path. It captures a tombstone
    /// configuration snapshot onto the supplied delete Activity before the system is removed, so a deleted Connected
    /// System's final state remains in configuration change history.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to delete.</param>
    /// <param name="activity">The in-flight delete Activity; the tombstone snapshot is recorded on it (the caller
    /// completes it after this returns).</param>
    /// <param name="changeReason">Optional reason for the deletion. Null on the worker path, where the reason is
    /// already carried on the queued Activity from request time.</param>
    /// <param name="evaluateMvoDeletionRules">Whether to mark orphaned MVOs for deletion before deleting the Connected System.</param>
    /// <param name="deleteChangeHistory">Whether to delete change history for the deleted CSOs. Default: false (preserves audit trail).</param>
    public async Task ExecuteDeletionAsync(int connectedSystemId, Activity activity, string? changeReason = null, bool evaluateMvoDeletionRules = true, bool deleteChangeHistory = false)
    {
        Log.Information("ExecuteDeletionAsync: Starting for Connected System {Id}, EvaluateMvoDeletionRules={EvaluateMvo}, deleteChangeHistory={DeleteHistory}",
            connectedSystemId, evaluateMvoDeletionRules, deleteChangeHistory);

        // Capture the tombstone before anything is removed, while the full system graph is still readable.
        await CaptureConnectedSystemDeletionAsync(activity, connectedSystemId, changeReason);

        if (evaluateMvoDeletionRules)
        {
            // Mark orphaned MVOs for deletion before deleting the Connected System
            // This sets LastConnectorDisconnectedDate so housekeeping will delete them after grace period
            await Application.Metaverse.MarkOrphanedMvosForDeletionAsync(connectedSystemId);
        }

        await Application.Repository.ConnectedSystems.DeleteConnectedSystemAsync(connectedSystemId, deleteChangeHistory);

        Log.Information("ExecuteDeletionAsync: Completed for Connected System {Id}", connectedSystemId);
    }
    #endregion

    #region Connected System Settings
    /// <summary>
    /// Use this when a connector is being parsed for persistence as a connector definition to create the connector definition settings from the connector instance.
    /// </summary>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public void CopyConnectorSettingsToConnectorDefinition(IConnectorSettings connector, ConnectorDefinition connectorDefinition)
    {
        foreach (var connectorSetting in connector.GetSettings())
        {
            connectorDefinition.Settings.Add(new ConnectorDefinitionSetting
            {
                Category = connectorSetting.Category,
                DefaultCheckboxValue = connectorSetting.DefaultCheckboxValue,
                DefaultStringValue = connectorSetting.DefaultStringValue,
                DefaultIntValue = connectorSetting.DefaultIntValue,
                Description = connectorSetting.Description,
                DropDownValues = connectorSetting.DropDownValues,
                Name = connectorSetting.Name,
                Type = connectorSetting.Type,
                Required = connectorSetting.Required,
                RequiredGroup = connectorSetting.RequiredGroup,
                RequiredGroupCardinality = connectorSetting.RequiredGroupCardinality,
                RequiredWhenSetting = connectorSetting.RequiredWhenSetting,
                RequiredWhenValue = connectorSetting.RequiredWhenValue
            });
        }
    }

    /// <summary>
    /// Checks that all setting values are valid, according to business rules.
    /// </summary>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public IList<ConnectorSettingValueValidationResult> ValidateConnectedSystemSettings(ConnectedSystem connectedSystem)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        // generic validation that applies to all connectors: required, required-group (either/or) and required-when
        // constraints declared in setting metadata
        var results = ConnectorSettingValidator.Validate(connectedSystem.SettingValues);

        // resolve the connector so its own, connector-specific validation can run too. connectors that don't
        // implement IConnectorSettings have no such validation to add; the generic results above stand alone.
        // validation opens real connections, so the connector is disposed here rather than left to the collector:
        // it holds the connection and any temporary files prepared for it.
        var connector = CreateConnector(connectedSystem);
        try
        {
            if (connector is IConnectorSettings settingsConnector)
                results.AddRange(settingsConnector.ValidateSettingValues(connectedSystem.SettingValues, Log.Logger));
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }

        return results;
    }

    private static void ValidateConnectedSystemParameter(ConnectedSystem connectedSystem)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (connectedSystem.ConnectorDefinition == null)
            throw new ArgumentException("The supplied ConnectedSystem doesn't have a valid ConnectorDefinition.", nameof(connectedSystem));

        if (connectedSystem.SettingValues == null || connectedSystem.SettingValues.Count == 0)
            throw new ArgumentException("The supplied ConnectedSystem doesn't have any valid SettingValues.", nameof(connectedSystem));
    }

    #endregion

    #region Connected System Schema
    /// <summary>
    /// Causes the associated Connector to be instantiated and the schema imported from the Connected System.
    /// Changes will be persisted, even if they are destructive, i.e. an attribute is removed.
    /// </summary>
    /// <returns>A result object containing details about what changed during the schema refresh.</returns>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<SchemaRefreshResult> ImportConnectedSystemSchemaAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        // resolve the connector, and confirm it supports schema import, before creating the activity: an
        // unsupported connector must never leave an in-flight activity behind.
        var connector = CreateConnector(connectedSystem);
        if (connector is not IConnectorSchema schemaConnector)
            throw new NotSupportedException($"The '{connectedSystem.ConnectorDefinition.Name}' connector does not support schema import.");

        // every operation that results, either directly or indirectly in a data change requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.ImportSchema,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        // Everything from here on is covered, so that a Connected System whose schema cannot be read finishes
        // its Activity as a failure carrying the reason, rather than leaving one in flight for ever with nothing
        // recorded against it. The exception still reaches the caller; the Activity is the audit record, not the
        // response.
        try
        {
            var schema = await schemaConnector.GetSchemaAsync(connectedSystem.SettingValues, Log.Logger);
            var result = MergeSchemaIntoConnectedSystem(connectedSystem, schema);

            // Read the target's password policy while connected, so initial password settings can be pre-filled
            // from the system rather than retyped by an administrator.
            await DiscoverPasswordPolicyAsync(connector, connectedSystem, result);

            await PersistConnectedSystemSchemaUpdateAsync(connectedSystem, initiatedBy);

            // A schema import changes the system's configuration (object types and attributes); capture it onto the
            // ImportSchema activity so the change is versioned in the system's history. Reloaded, as ids are assigned on save.
            await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystem.Id);

            // finish the activity
            await CompleteSchemaImportActivityAsync(activity, result);

            return result;
        }
        catch (Exception ex)
        {
            // Discard whatever the aborted merge left staged on the shared DbContext before recording the
            // failure: FailActivityWithErrorAsync saves on that same context, and without this it flushed
            // the half-merged schema alongside the Activity's failure row, so a failed import both
            // reported an error AND partially applied (found via #1171). The Activity write survives the
            // cleared tracker by design: UpdateActivityAsync attaches detach-safe.
            Application.Repository.ClearChangeTracker();
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Imports a Connected System schema (initiated by API key).
    /// </summary>
    public async Task<SchemaRefreshResult> ImportConnectedSystemSchemaAsync(ConnectedSystem connectedSystem, ApiKey initiatedByApiKey)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        // resolve the connector, and confirm it supports schema import, before creating the activity: an
        // unsupported connector must never leave an in-flight activity behind.
        var connector = CreateConnector(connectedSystem);
        if (connector is not IConnectorSchema schemaConnector)
            throw new NotSupportedException($"The '{connectedSystem.ConnectorDefinition.Name}' connector does not support schema import.");

        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.ImportSchema,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        // Covered from here on: see the user-initiated overload above.
        try
        {
            var schema = await schemaConnector.GetSchemaAsync(connectedSystem.SettingValues, Log.Logger);
            var result = MergeSchemaIntoConnectedSystem(connectedSystem, schema);

            // Read the target's password policy while connected, so initial password settings can be pre-filled
            // from the system rather than retyped by an administrator.
            await DiscoverPasswordPolicyAsync(connector, connectedSystem, result);

            await PersistConnectedSystemSchemaUpdateAsync(connectedSystem, initiatedByApiKey);

            // Capture the configuration change onto the ImportSchema activity: see the user-initiated overload above.
            await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystem.Id);

            await CompleteSchemaImportActivityAsync(activity, result);

            return result;
        }
        catch (Exception ex)
        {
            // Discard staged schema changes before recording the failure: see the user-initiated overload above.
            Application.Repository.ClearChangeTracker();
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Completes a schema import's Activity, downgraded to complete-with-warning when discovery reported
    /// shortfalls, so an import that discovered less than it should have never presents as an unqualified success.
    /// </summary>
    private async Task CompleteSchemaImportActivityAsync(Activity activity, SchemaRefreshResult result)
    {
        if (result.DiscoveryWarnings.Count > 0)
        {
            activity.WarningMessage = string.Join(Environment.NewLine, result.DiscoveryWarnings);
            await Application.Activities.CompleteActivityWithWarningAsync(activity);
        }
        else
        {
            await Application.Activities.CompleteActivityAsync(activity);
        }
    }

    /// <summary>
    /// Merges a schema retrieved from a Connected System into that system's object types and attributes, and
    /// reports what changed. Object types and attributes are matched by name so that existing ids survive the
    /// refresh; this is what stops a Synchronisation Rule's mappings from being invalidated by one. Removal of
    /// object types and attributes that the schema no longer offers is reported but not applied here.
    /// </summary>
    /// <remarks>
    /// Shared by both <see cref="ImportConnectedSystemSchemaAsync(ConnectedSystem, MetaverseObject?)"/> and
    /// <see cref="ImportConnectedSystemSchemaAsync(ConnectedSystem, ApiKey)"/>, so the same schema reaches the same
    /// conclusion whichever surface asked for it. They were separate copies of this logic, and the copies had
    /// drifted: only the user-initiated one auto-selected a single newly-discovered object type, so an import run
    /// through the REST API or PowerShell left different configuration behind than the same import run through the
    /// portal. The initiator decides who the Activity is attributed to; it does not decide what a schema means.
    /// </remarks>
    internal static SchemaRefreshResult MergeSchemaIntoConnectedSystem(ConnectedSystem connectedSystem, ConnectorSchema schema)
    {
        // Discovery warnings travel on the result so the portal can show them beside what changed; the import's
        // Activity carries the same warnings for the other surfaces.
        var result = new SchemaRefreshResult { Success = true, DiscoveryWarnings = schema.Warnings.ToList() };

        // Credential attributes must never enter JIM's schema as new, manageable attributes.
        FilterCredentialAttributesFromSchema(connectedSystem, schema, result);

        // Merge the new schema with the existing one, preserving IDs for attributes that are referenced by Synchronisation Rules
        // This prevents FK constraint violations when attributes are used in Synchronisation Rule mappings
        schema.ObjectTypes = schema.ObjectTypes.OrderBy(q => q.Name).ToList();

        // Keep track of existing object types for merging and change tracking
        var existingObjectTypes = connectedSystem.ObjectTypes?.ToList() ?? new List<ConnectedSystemObjectType>();
        var existingObjectTypeNames = existingObjectTypes.Select(ot => ot.Name).ToHashSet();
        var newObjectTypeNames = schema.ObjectTypes.Select(ot => ot.Name).ToHashSet();

        connectedSystem.ObjectTypes = new List<ConnectedSystemObjectType>();

        // Track removed object types
        foreach (var removedObjectTypeName in existingObjectTypeNames.Except(newObjectTypeNames))
        {
            result.RemovedObjectTypes.Add(removedObjectTypeName);
        }

        foreach (var schemaObjectType in schema.ObjectTypes)
        {
            schemaObjectType.Attributes = schemaObjectType.Attributes.OrderBy(a => a.Name).ToList();

            // Try to find an existing object type with the same name
            var existingObjectType = existingObjectTypes.FirstOrDefault(ot => ot.Name == schemaObjectType.Name);

            ConnectedSystemObjectType connectedSystemObjectType;
            if (existingObjectType != null)
            {
                // Update existing object type, preserving its ID and merging attributes
                result.UpdatedObjectTypes.Add(schemaObjectType.Name);
                connectedSystemObjectType = existingObjectType;
                var existingAttributes = existingObjectType.Attributes?.ToList() ?? new List<ConnectedSystemObjectTypeAttribute>();
                var existingAttributeNames = existingAttributes.Select(a => a.Name).ToHashSet();
                var newAttributeNames = schemaObjectType.Attributes.Select(a => a.Name).ToHashSet();

                // Attributes an administrator's selected auxiliary classes contribute are not in the discovered
                // schema for this type, because an RFC 4512 directory attaches an auxiliary class to an entry rather
                // than to the class. They are carried across as the same rows rather than dropped and rebuilt: a new
                // row would hand every Synchronisation Rule mapping that references one a dangling attribute id.
                var contributedAttributes = ContributedAuxiliaryAttributes(existingObjectType, existingAttributes, existingObjectTypes);

                connectedSystemObjectType.Attributes = new List<ConnectedSystemObjectTypeAttribute>();

                // Track removed attributes for this object type
                var removedAttributeNames = existingAttributeNames
                    .Except(newAttributeNames)
                    .Except(contributedAttributes.Select(a => a.Name))
                    .ToList();
                if (removedAttributeNames.Count > 0)
                {
                    result.RemovedAttributes[schemaObjectType.Name] = removedAttributeNames;
                }

                // Track added attributes for this object type
                var addedAttributeNames = new List<string>();

                foreach (var schemaAttribute in schemaObjectType.Attributes)
                {
                    // Try to find existing attribute by name
                    var existingAttribute = existingAttributes.FirstOrDefault(a => a.Name == schemaAttribute.Name);

                    if (existingAttribute != null)
                    {
                        // Update existing attribute properties but preserve the ID
                        existingAttribute.Description = schemaAttribute.Description;
                        existingAttribute.AttributePlurality = schemaAttribute.AttributePlurality;
                        existingAttribute.Type = schemaAttribute.Type;
                        existingAttribute.ClassName = schemaAttribute.ClassName;
                        existingAttribute.Writability = schemaAttribute.Writability;
                        connectedSystemObjectType.Attributes.Add(existingAttribute);
                    }
                    else
                    {
                        // Add new attribute
                        addedAttributeNames.Add(schemaAttribute.Name);
                        connectedSystemObjectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                        {
                            Name = schemaAttribute.Name,
                            Description = schemaAttribute.Description,
                            AttributePlurality = schemaAttribute.AttributePlurality,
                            Type = schemaAttribute.Type,
                            ClassName = schemaAttribute.ClassName,
                            Writability = schemaAttribute.Writability
                        });
                    }
                }

                // Put the auxiliary contributions back, as the rows they already were. A directory that has since
                // declared one of them on the structural class itself wins: that attribute is now native, and the
                // discovery pass above has already carried its row across.
                foreach (var contributedAttribute in contributedAttributes.Where(a => !newAttributeNames.Contains(a.Name)))
                    connectedSystemObjectType.Attributes.Add(contributedAttribute);

                if (addedAttributeNames.Count > 0)
                {
                    result.AddedAttributes[schemaObjectType.Name] = addedAttributeNames;
                }
            }
            else
            {
                // Create new object type
                result.AddedObjectTypes.Add(schemaObjectType.Name);
                connectedSystemObjectType = new ConnectedSystemObjectType
                {
                    Name = schemaObjectType.Name,
                    Attributes = schemaObjectType.Attributes.Select(a => new ConnectedSystemObjectTypeAttribute
                    {
                        Name = a.Name,
                        Description = a.Description,
                        AttributePlurality = a.AttributePlurality,
                        Type = a.Type,
                        ClassName = a.ClassName,
                        Writability = a.Writability
                    }).ToList()
                };

                // All attributes in a new object type are considered "added"
                result.AddedAttributes[schemaObjectType.Name] = schemaObjectType.Attributes.Select(a => a.Name).ToList();
            }

            MergeObjectTypeTags(connectedSystemObjectType, schemaObjectType);

            // if there's an External Id attribute recommendation from the connector, use that. otherwise the user will have to pick one.
            // External ID attributes are automatically selected and locked to ensure the system always has the required anchor attributes.
            var attribute = connectedSystemObjectType.Attributes.SingleOrDefault(a => schemaObjectType.RecommendedExternalIdAttribute != null && a.Name == schemaObjectType.RecommendedExternalIdAttribute.Name);
            if (attribute != null)
            {
                attribute.IsExternalId = true;
                attribute.Selected = true;
                attribute.SelectionLocked = true;
            }

            // if the connector supports it (requires it), take the secondary external id from the schema and mark the attribute as such
            // Secondary External ID attributes are also automatically selected and locked.
            if (connectedSystem.ConnectorDefinition.SupportsSecondaryExternalId && schemaObjectType.RecommendedSecondaryExternalIdAttribute != null)
            {
                var secondaryExternalIdAttribute = connectedSystemObjectType.Attributes.SingleOrDefault(a => a.Name == schemaObjectType.RecommendedSecondaryExternalIdAttribute.Name);
                if (secondaryExternalIdAttribute != null)
                {
                    secondaryExternalIdAttribute.IsSecondaryExternalId = true;
                    secondaryExternalIdAttribute.Selected = true;
                    secondaryExternalIdAttribute.SelectionLocked = true;
                }
                else
                    Log.Error($"Recommended Secondary External Id attribute '{schemaObjectType.RecommendedSecondaryExternalIdAttribute.Name}' was not found in the objects list of attributes!");
            }

            connectedSystem.ObjectTypes.Add(connectedSystemObjectType);
        }

        // Bring the auxiliary classes an administrator selected onto the structural types that extend them. This
        // runs after every type has been rebuilt, because an extension names another object type and that type may
        // not have been reached yet while the loop above was running.
        ApplyAuxiliaryClassSelections(connectedSystem, result);

        // Any credential attribute that survived the merge is one that was already persisted; force it into a
        // state where JIM neither manages it nor lets an administrator turn it back on.
        QuarantineCredentialAttributes(connectedSystem);

        // Set totals
        result.TotalObjectTypes = connectedSystem.ObjectTypes.Count;
        result.TotalAttributes = connectedSystem.ObjectTypes.Sum(ot => ot.Attributes?.Count ?? 0);

        // If the schema yielded exactly one, newly-discovered object type, auto-select it so the admin lands
        // straight on attribute selection. Gated on "newly added" so a refresh never re-selects a type the
        // admin previously deselected.
        if (connectedSystem.ObjectTypes.Count == 1 && result.AddedObjectTypes.Count == 1)
            connectedSystem.ObjectTypes[0].Selected = true;

        return result;
    }

    /// <summary>
    /// The attributes on a persisted Object Type that got there by an administrator selecting an auxiliary class,
    /// rather than by the Connector discovering them.
    /// </summary>
    /// <remarks>
    /// Recognised by the attribute's <c>ClassName</c> naming a currently-selected auxiliary type: discovery stamps
    /// every attribute with the class its Object Type was built from, so nothing native ever carries another class's
    /// name. Selections pointing at a type this refresh removed contribute nothing, which is the documented
    /// data-loss semantic of a refresh and is reported by the merge that follows.
    /// </remarks>
    private static List<ConnectedSystemObjectTypeAttribute> ContributedAuxiliaryAttributes(
        ConnectedSystemObjectType existingObjectType,
        List<ConnectedSystemObjectTypeAttribute> existingAttributes,
        List<ConnectedSystemObjectType> existingObjectTypes)
    {
        if (existingObjectType.Extensions.Count == 0)
            return [];

        var contributingClassNames = existingObjectType.Extensions
            .Select(extension => existingObjectTypes.FirstOrDefault(ot => ot.Id == extension.ExtensionObjectTypeId)?.Name)
            .Where(name => name != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        return existingAttributes
            .Where(attribute => attribute.ClassName != null && contributingClassNames.Contains(attribute.ClassName))
            .ToList();
    }

    /// <summary>
    /// Merges the auxiliary classes an administrator selected onto the structural Object Types that extend them, and
    /// folds what changed into the refresh result so the portal reports it beside everything else that changed.
    /// </summary>
    private static void ApplyAuxiliaryClassSelections(ConnectedSystem connectedSystem, SchemaRefreshResult result)
    {
        var merge = AuxiliaryClassAttributeMerger.Merge(connectedSystem);

        foreach (var (objectTypeName, attributeNames) in merge.AddedAttributes)
            result.AddedAttributes[objectTypeName] = result.AddedAttributes.TryGetValue(objectTypeName, out var added)
                ? added.Union(attributeNames).ToList()
                : attributeNames;

        foreach (var (objectTypeName, attributeNames) in merge.RemovedAttributes)
            result.RemovedAttributes[objectTypeName] = result.RemovedAttributes.TryGetValue(objectTypeName, out var removed)
                ? removed.Union(attributeNames).ToList()
                : attributeNames;

        // An auxiliary class the directory no longer publishes takes its selection with it. Say so on the refresh
        // rather than letting an administrator discover it as attributes that quietly stopped being there.
        result.DiscoveryWarnings.AddRange(merge.UnresolvedExtensions);
    }

    /// <summary>
    /// Applies the classification a Connector reported for an Object Type (structural, auxiliary, internal, and
    /// whatever else it defines) to the persisted Object Type.
    /// </summary>
    /// <remarks>
    /// Tags are connector-owned, so what the Connector reports now is the complete classification for this type:
    /// the persisted set is replaced rather than added to, which is what makes a reclassification at the Connected
    /// System (or a Connector that stops classifying) show up instead of accumulating contradictory tags. Rows whose
    /// key and value are unchanged are reused so that a refresh does not churn the table, and repeated tags are
    /// collapsed because the persisted tags are uniquely indexed per object type, key and value.
    /// </remarks>
    private static void MergeObjectTypeTags(ConnectedSystemObjectType connectedSystemObjectType, ConnectorSchemaObjectType schemaObjectType)
    {
        var existingTagsByClassification = connectedSystemObjectType.Tags
            .GroupBy(t => (t.Key, t.Value))
            .ToDictionary(g => g.Key, g => g.First());

        connectedSystemObjectType.Tags = schemaObjectType.Tags
            .Select(t => (t.Key, t.Value))
            .Distinct()
            .Select(classification => existingTagsByClassification.TryGetValue(classification, out var existingTag)
                ? existingTag
                : new ConnectedSystemObjectTypeTag { Key = classification.Key, Value = classification.Value })
            .ToList();
    }

    /// <summary>
    /// Strips credential attributes out of an incoming Connected System schema so they can never be added to JIM
    /// as new, manageable attributes, and discards any Connector recommendation that would make one an External Id
    /// or Secondary External Id (the merge force-selects and locks whatever is recommended). Blocked names are
    /// recorded on the result so the outcome is reported to the administrator rather than being silent.
    /// </summary>
    /// <remarks>
    /// A credential attribute that is <b>already persisted</b> is deliberately left in the incoming schema. The
    /// merge that follows derives removed attributes from <c>existing.Except(incoming)</c> and rebuilds each object
    /// type's attribute collection, so filtering a persisted attribute out would orphan its row: EF turns that into
    /// a DELETE, which is a foreign-key violation at save time when a Synchronisation Rule Mapping references the
    /// attribute, and it would report a bogus "attribute removed" to the administrator when the Connected System
    /// still has it. Preserved attributes are instead forced into a safe state by
    /// <see cref="QuarantineCredentialAttributes"/> once the merge has run.
    /// </remarks>
    /// <param name="connectedSystem">The Connected System being refreshed, whose persisted object types decide what must be preserved.</param>
    /// <param name="schema">The schema just retrieved from the Connected System. Modified in place.</param>
    /// <param name="result">The schema refresh result to record blocked attributes on.</param>
    internal static void FilterCredentialAttributesFromSchema(ConnectedSystem connectedSystem, ConnectorSchema schema, SchemaRefreshResult result)
    {
        foreach (var schemaObjectType in schema.ObjectTypes)
        {
            var recommendedExternalId = schemaObjectType.RecommendedExternalIdAttribute;
            if (recommendedExternalId != null && CredentialAttributes.IsCredentialAttribute(recommendedExternalId.Name))
            {
                Log.Warning("Connected System {ConnectedSystem} recommended credential attribute {Attribute} as the External Id for object type {ObjectType}. The recommendation has been discarded; a credential attribute can never be an anchor.",
                    LogSanitiser.Sanitise(connectedSystem.Name), LogSanitiser.Sanitise(recommendedExternalId.Name), LogSanitiser.Sanitise(schemaObjectType.Name));
                schemaObjectType.RecommendedExternalIdAttribute = null!;
            }

            var recommendedSecondaryExternalId = schemaObjectType.RecommendedSecondaryExternalIdAttribute;
            if (recommendedSecondaryExternalId != null && CredentialAttributes.IsCredentialAttribute(recommendedSecondaryExternalId.Name))
            {
                Log.Warning("Connected System {ConnectedSystem} recommended credential attribute {Attribute} as the Secondary External Id for object type {ObjectType}. The recommendation has been discarded; a credential attribute can never be an anchor.",
                    LogSanitiser.Sanitise(connectedSystem.Name), LogSanitiser.Sanitise(recommendedSecondaryExternalId.Name), LogSanitiser.Sanitise(schemaObjectType.Name));
                schemaObjectType.RecommendedSecondaryExternalIdAttribute = null;
            }

            var deniedAttributes = schemaObjectType.Attributes.Where(a => CredentialAttributes.IsCredentialAttribute(a.Name)).ToList();
            if (deniedAttributes.Count == 0)
                continue;

            // Persisted attributes stay in the incoming schema so the merge matches and preserves them; everything
            // else is dropped before it can be added.
            var persistedAttributeNames = connectedSystem.ObjectTypes?
                .FirstOrDefault(ot => ot.Name == schemaObjectType.Name)?
                .Attributes.Select(a => a.Name)
                .ToHashSet() ?? [];

            foreach (var deniedAttribute in deniedAttributes.Where(a => !persistedAttributeNames.Contains(a.Name)))
                schemaObjectType.Attributes.Remove(deniedAttribute);

            result.BlockedCredentialAttributes[schemaObjectType.Name] = deniedAttributes
                .Select(a => a.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (result.BlockedCredentialAttributeCount > 0)
            Log.Information("Blocked {Count} credential attribute(s) while importing the schema for Connected System {ConnectedSystem}. Passwords are handled by JIM's dedicated password channel, not Attribute Flow.",
                result.BlockedCredentialAttributeCount, LogSanitiser.Sanitise(connectedSystem.Name));
    }

    /// <summary>
    /// Forces every credential attribute still present on a Connected System into a state JIM will not act on:
    /// deselected, selection locked, and not an anchor. Runs after the schema merge, so it covers attributes that
    /// were persisted before credential attributes were denied.
    /// </summary>
    /// <param name="connectedSystem">The Connected System whose merged schema should be quarantined.</param>
    internal static void QuarantineCredentialAttributes(ConnectedSystem connectedSystem)
    {
        if (connectedSystem.ObjectTypes == null)
            return;

        foreach (var objectType in connectedSystem.ObjectTypes)
        {
            foreach (var attribute in objectType.Attributes.Where(a => CredentialAttributes.IsCredentialAttribute(a.Name)))
            {
                var wasManaged = attribute.Selected || attribute.IsExternalId || attribute.IsSecondaryExternalId;

                attribute.Selected = false;
                attribute.SelectionLocked = true;
                attribute.IsExternalId = false;
                attribute.IsSecondaryExternalId = false;

                if (wasManaged)
                    Log.Warning("Credential attribute {Attribute} on object type {ObjectType} in Connected System {ConnectedSystem} was managed by JIM. It has been deselected and locked. Remove any Attribute Flow that references it; passwords are handled by JIM's dedicated password channel instead.",
                        LogSanitiser.Sanitise(attribute.Name), LogSanitiser.Sanitise(objectType.Name), LogSanitiser.Sanitise(connectedSystem.Name));
            }
        }
    }

    /// <summary>
    /// Reads the Connected System's password policy, where the Connector can, and records it against the system.
    /// <para>
    /// Enrichment, not a prerequisite: a schema import must never fail because a policy could not be read. Any
    /// Connector fault is logged and discovery is skipped, leaving whatever was previously discovered in place
    /// rather than discarding it on the strength of one bad read.
    /// </para>
    /// </summary>
    private static async Task DiscoverPasswordPolicyAsync(IConnector connector, ConnectedSystem connectedSystem, SchemaRefreshResult result)
    {
        if (connector is not IConnectorPasswordPolicyDiscovery policyConnector)
            return;

        ConnectedSystemPasswordPolicy? discovered;
        try
        {
            discovered = await policyConnector.GetPasswordPolicyAsync(connectedSystem.SettingValues, Log.Logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad, with the cancellation exclusion the fallback-dispatcher rule requires: this is
            // optional enrichment degrading to "no policy discovered", and no Connector fault justifies failing an
            // otherwise successful schema import. A cancelled run must still propagate.
            Log.Warning(ex, "DiscoverPasswordPolicyAsync: Could not read the password policy for Connected System {ConnectedSystemId}. The schema import continues without it.",
                connectedSystem.Id);
            return;
        }

        if (discovered == null)
        {
            Log.Debug("DiscoverPasswordPolicyAsync: Connected System {ConnectedSystemId} exposed no password policy.", connectedSystem.Id);
            return;
        }

        result.PasswordPolicyDiscovered = true;

        // Update the existing row in place where there is one. Replacing the navigation with a fresh object would
        // leave it with no id, which the persistence path reads as an insert, and the one-to-one unique index then
        // rejects the save.
        if (connectedSystem.PasswordPolicy == null)
        {
            connectedSystem.PasswordPolicy = discovered;
            return;
        }

        var existing = connectedSystem.PasswordPolicy;
        existing.Discovered = discovered.Discovered;
        existing.MinimumLength = discovered.MinimumLength;
        existing.ComplexityRequired = discovered.ComplexityRequired;
        existing.RequiredCharacterClassCount = discovered.RequiredCharacterClassCount;
        existing.RecognisedCharacterClasses = discovered.RecognisedCharacterClasses;
        existing.PasswordHistoryLength = discovered.PasswordHistoryLength;
        existing.MaximumPasswordAge = discovered.MaximumPasswordAge;
        existing.MinimumPasswordAge = discovered.MinimumPasswordAge;
        existing.FineGrainedPolicySignal = discovered.FineGrainedPolicySignal;
    }
    #endregion

    #region Connected System Password Channel
    /// <summary>
    /// Checks whether this Connected System's password channel is likely to work, without setting a password on
    /// anything.
    /// <para>
    /// Deliberately not recorded as an Activity. Activities exist to account for changes, and this changes nothing
    /// in JIM or in the Connected System; it reads. Recording every diagnostic read would bury the changes that
    /// Activities are there to make findable.
    /// </para>
    /// <para>
    /// The result is returned rather than stored, and is meant to be read now. A target's reachability, its
    /// permissions and its policy all change without JIM being told, so a preflight kept on file would go on
    /// reassuring an administrator long after it stopped being true.
    /// </para>
    /// </summary>
    /// <exception cref="NotSupportedException">Thrown when the Connector cannot manage passwords at all.</exception>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<PasswordPreflightResult> RunPasswordPreflightAsync(ConnectedSystem connectedSystem, CancellationToken cancellationToken)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        var connector = CreateConnector(connectedSystem);
        if (connector is not IConnectorPasswordManagement passwordConnector)
            throw new NotSupportedException($"The '{connectedSystem.ConnectorDefinition.Name}' connector does not support setting passwords, so there is no password channel to check.");

        var containerExternalIds = connectedSystem.GetSelectedContainerExternalIds();
        Log.Debug("RunPasswordPreflightAsync: Checking the password channel for Connected System {ConnectedSystemId} against {ContainerCount} selected container(s).",
            connectedSystem.Id, containerExternalIds.Count);

        return await passwordConnector.RunPasswordPreflightAsync(connectedSystem.SettingValues, containerExternalIds, Log.Logger, cancellationToken);
    }

    /// <summary>
    /// The password policy JIM last discovered on a Connected System, or null where none was discovered.
    /// <para>
    /// Read explicitly rather than off a Connected System navigation, because a caller that reached the system
    /// through a Synchronisation Rule would find that navigation unloaded, which looks exactly like a target
    /// that published no policy.
    /// </para>
    /// </summary>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<ConnectedSystemPasswordPolicy?> GetPasswordPolicyAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetPasswordPolicyAsync(connectedSystemId);
    }

    /// <summary>
    /// The password expiry behaviours this Connected System's Connector is able to apply.
    /// <para>
    /// Read from the Connector rather than from anything persisted, because it is a property of the code and
    /// changes with a Connector upgrade rather than with configuration. Offering an administrator a behaviour
    /// the Connector cannot apply would let them save a setting that is quietly downgraded on every account.
    /// </para>
    /// <para>
    /// Empty when the Connector cannot set passwords at all, which is the caller's cue that there is no initial
    /// password to configure here.
    /// </para>
    /// </summary>
    /// <para>
    /// Takes an id and loads the Connected System itself rather than accepting one from the caller. A caller
    /// that reached the system through a Synchronisation Rule holds one whose ConnectorDefinition navigation is
    /// not loaded, and this needs the Connector's name to instantiate it; accepting that graph threw and took
    /// the whole editor down with it. Loading here means the method cannot be handed a graph it cannot use.
    /// </para>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<IReadOnlyCollection<PasswordExpiryBehaviour>> GetSupportedPasswordExpiryBehavioursAsync(int connectedSystemId)
    {
        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return [];

        return CreateConnector(connectedSystem) is IConnectorPasswordManagement passwordConnector
            ? passwordConnector.SupportedExpiryBehaviours
            : [];
    }

    /// <summary>
    /// Sets the password on one account in a Connected System, at an administrator's request (issue #1121).
    /// <para>
    /// This is the manual counterpart to the initial password an export delivers: the account whose provisioning
    /// password was parked, the person who never received theirs, the reset that has to happen now. It writes
    /// straight to the target and records the attempt as an Activity; nothing is staged, retried or persisted,
    /// because there is nowhere to keep a password and no second chance worth keeping one for.
    /// </para>
    /// <para>
    /// <b>The password value goes to the Connector and nowhere else.</b> It is never logged, never written to the
    /// Activity, and never returned. Callers must hold it no longer than the call.
    /// </para>
    /// <para>
    /// This is a password-reset primitive, and JIM's Administrator role is the whole of the authorisation on it:
    /// an administrator who can reach this can reset the password of any account in the connector space, up to
    /// and including privileged ones, subject only to what the Connected System's own service account is
    /// permitted to do. That is the same authority the provisioning path already exercises unattended, so it
    /// grants nothing new; it does make the target selection a person's rather than a Synchronisation Rule's,
    /// which is why every attempt is recorded.
    /// </para>
    /// </summary>
    /// <param name="connectedSystemId">The Connected System the account lives in.</param>
    /// <param name="connectedSystemObjectId">The Connected System Object to set the password on.</param>
    /// <param name="password">The password to set. Never logged, never persisted, never returned.</param>
    /// <param name="options">How to apply it: the expiry behaviour, and whether to enable the account.</param>
    /// <param name="initiatedBy">The administrator making the request, for attribution on the Activity.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>
    /// The classified outcome. A target that refuses the password is a result, not an exception: its verbatim
    /// reason is the single most useful thing to show the administrator who has to choose another one.
    /// </returns>
    /// <exception cref="ArgumentException">The password is empty, or no such Connected System Object exists.</exception>
    /// <exception cref="NotSupportedException">The Connector cannot set passwords.</exception>
    public async Task<PasswordSetResult> SetConnectedSystemObjectPasswordAsync(
        int connectedSystemId,
        Guid connectedSystemObjectId,
        string password,
        PasswordSetOptions options,
        MetaverseObject? initiatedBy,
        CancellationToken cancellationToken)
    {
        return await SetConnectedSystemObjectPasswordCoreAsync(connectedSystemId, connectedSystemObjectId, password, options,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy), parentActivityId: null, cancellationToken);
    }

    /// <summary>
    /// Sets the password on one account in a Connected System. API-key initiator overload; see the
    /// user-initiated overload for the behaviour and the security note.
    /// </summary>
    public async Task<PasswordSetResult> SetConnectedSystemObjectPasswordAsync(
        int connectedSystemId,
        Guid connectedSystemObjectId,
        string password,
        PasswordSetOptions options,
        ApiKey initiatedByApiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initiatedByApiKey);

        return await SetConnectedSystemObjectPasswordCoreAsync(connectedSystemId, connectedSystemObjectId, password, options,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey), parentActivityId: null, cancellationToken);
    }

    /// <summary>
    /// The accounts a Metaverse Object is joined to, with what each one's Connected System can do about
    /// passwords (issue #1172).
    /// <para>
    /// Systems whose Connector cannot set a password are returned marked as such rather than left out, so an
    /// administrator looking for an account that is not offered can see that JIM knows about it and why it is
    /// not on the list.
    /// </para>
    /// </summary>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<IReadOnlyList<MetaverseObjectAccount>> GetAccountsForPasswordSetAsync(Guid metaverseObjectId)
    {
        var connectedSystemObjects = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObjectId);

        // Each Connected System is resolved once and reused across its accounts. The retrieval above does not
        // load the Connected System navigation, and an unloaded navigation is indistinguishable from an absent
        // value, so the system is loaded here by id rather than read off the object.
        var systems = new Dictionary<int, (string Name, IReadOnlyCollection<PasswordExpiryBehaviour> ExpiryBehaviours, ConnectedSystemPasswordPolicy? Policy, bool CanDiscoverPolicy)>();
        foreach (var connectedSystemId in connectedSystemObjects.Select(cso => cso.ConnectedSystemId).Distinct())
        {
            var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemId);
            if (connectedSystem == null)
                continue;

            var expiryBehaviours = CreateConnector(connectedSystem) is IConnectorPasswordManagement passwordConnector
                ? passwordConnector.SupportedExpiryBehaviours
                : [];

            systems[connectedSystemId] = (
                connectedSystem.Name,
                expiryBehaviours,
                expiryBehaviours.Count > 0 ? await GetPasswordPolicyAsync(connectedSystemId) : null,
                connectedSystem.ConnectorDefinition.SupportsPasswordPolicyDiscovery);
        }

        return connectedSystemObjects
            .Where(cso => systems.ContainsKey(cso.ConnectedSystemId))
            .Select(cso =>
            {
                var system = systems[cso.ConnectedSystemId];
                return new MetaverseObjectAccount
                {
                    ConnectedSystemObjectId = cso.Id,
                    ConnectedSystemId = cso.ConnectedSystemId,
                    ConnectedSystemName = system.Name,
                    AccountIdentifier = cso.NameOrId ?? cso.Id.ToString(),
                    ConnectorCanSetPasswords = system.ExpiryBehaviours.Count > 0,
                    SupportedExpiryBehaviours = system.ExpiryBehaviours,
                    DiscoveredPolicy = system.Policy,
                    ConnectorCanDiscoverPasswordPolicy = system.CanDiscoverPolicy
                };
            })
            .OrderBy(account => account.ConnectedSystemName)
            .ToList();
    }

    /// <summary>
    /// Sets the same password on several of a person's accounts, one Connected System at a time (issue #1172).
    /// <para>
    /// <b>There is no transaction across systems.</b> Each write is independent, and a fan-out routinely ends
    /// with some accounts changed and others not; that is reported per account rather than rolled into a count,
    /// because which accounts took the password is what the administrator has to act on.
    /// </para>
    /// <para>
    /// Sequential on purpose. A handful of accounts makes the wall-clock saving of running them at once
    /// negligible, and sequence is what lets the caller narrate progress at all. It also means a target
    /// refusing everything is discovered on the first account rather than the fourth.
    /// </para>
    /// <para>
    /// Two or more accounts are grouped under a parent Activity, so the fan-out is findable afterwards as one
    /// action; a single account gets none, because a group of one is a row in the Activity list that says
    /// nothing and hides the row that does.
    /// </para>
    /// </summary>
    /// <param name="metaverseObjectId">The person whose accounts these are, for the parent Activity.</param>
    /// <param name="accounts">The accounts to set the password on, in the order to attempt them.</param>
    /// <param name="password">The password to set. Never logged, never persisted, never returned.</param>
    /// <param name="options">How to apply it, applied identically to every account.</param>
    /// <param name="initiatedBy">The administrator making the request, for attribution.</param>
    /// <param name="progress">
    /// Reports each account's outcome as it lands, so a caller can show progress while the rest are still
    /// being written. Optional; the full set is returned regardless.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops before the accounts not yet reached. It cannot undo the ones already written, and their outcomes
    /// are still returned, because a password that landed has landed whatever the administrator did next.
    /// </param>
    public async Task<MultiAccountPasswordSetResult> SetPasswordOnAccountsAsync(
        Guid metaverseObjectId,
        IReadOnlyList<MetaverseObjectAccount> accounts,
        string password,
        PasswordSetOptions options,
        MetaverseObject? initiatedBy,
        IProgress<AccountPasswordSetOutcome>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (accounts.Count == 0)
            throw new ArgumentException("At least one account is required.", nameof(accounts));

        Activity? parentActivity = null;
        if (accounts.Count > 1)
        {
            parentActivity = new Activity
            {
                TargetName = $"{accounts.Count} accounts",
                TargetType = ActivityTargetType.MetaverseObject,
                TargetOperationType = ActivityTargetOperationType.SetPassword,
                MetaverseObjectId = metaverseObjectId
            };
            await Application.Activities.CreateActivityAsync(parentActivity, initiatedBy);
        }

        var outcomes = new List<AccountPasswordSetOutcome>(accounts.Count);
        try
        {
            foreach (var account in accounts)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var startedAt = DateTime.UtcNow;
                PasswordSetResult result;
                try
                {
                    result = await SetConnectedSystemObjectPasswordCoreAsync(
                        account.ConnectedSystemId, account.ConnectedSystemObjectId, password, options,
                        activity => Application.Activities.CreateActivityAsync(activity, initiatedBy),
                        parentActivity?.Id, cancellationToken);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    // A missing account or a Connector that cannot do this stops that account, not the fan-out.
                    // The administrator picked several systems and the rest of them may work perfectly.
                    result = PasswordSetResult.Failed(
                        ex is NotSupportedException ? PasswordSetFailureReason.UnsupportedOperation : PasswordSetFailureReason.TargetObjectNotFound,
                        ex.Message);
                }

                var outcome = new AccountPasswordSetOutcome
                {
                    ConnectedSystemObjectId = account.ConnectedSystemObjectId,
                    ConnectedSystemId = account.ConnectedSystemId,
                    ConnectedSystemName = account.ConnectedSystemName,
                    Result = result,
                    Duration = DateTime.UtcNow - startedAt
                };
                outcomes.Add(outcome);
                progress?.Report(outcome);
            }
        }
        finally
        {
            if (parentActivity != null)
                await CompleteFanOutActivityAsync(parentActivity, outcomes);
        }

        // Synchronisation Integrity: summary statistics at the end of every batch operation. The Connected
        // System Object ids are logged so an administrator can find the accounts that refused; the password is
        // not, and no part of it ever is.
        var failed = outcomes.Where(o => !o.Result.Success).ToList();
        Log.Information("SetPasswordOnAccountsAsync: Password set on {Succeeded} of {Attempted} accounts for Metaverse Object {MetaverseObjectId}. Refused by: {Refused}",
            outcomes.Count - failed.Count, outcomes.Count, metaverseObjectId,
            failed.Count == 0 ? "none" : string.Join(", ", failed.Select(f => f.ConnectedSystemObjectId)));

        return new MultiAccountPasswordSetResult { Outcomes = outcomes };
    }

    /// <summary>
    /// Finishes the parent Activity with what the fan-out achieved. Failed rather than completed where any
    /// account refused, because the administrator asked for a password on all of them and did not get one.
    /// </summary>
    private async Task CompleteFanOutActivityAsync(Activity parentActivity, IReadOnlyList<AccountPasswordSetOutcome> outcomes)
    {
        var failed = outcomes.Where(o => !o.Result.Success).ToList();
        if (failed.Count == 0)
        {
            parentActivity.Message = $"Password set on {outcomes.Count} accounts.";
            await Application.Activities.CompleteActivityAsync(parentActivity);
            return;
        }

        await Application.Activities.FailActivityWithErrorAsync(parentActivity,
            $"Password set on {outcomes.Count - failed.Count} of {outcomes.Count} accounts. Not set on: {string.Join(", ", failed.Select(f => f.ConnectedSystemName))}.");
    }

    private async Task<PasswordSetResult> SetConnectedSystemObjectPasswordCoreAsync(
        int connectedSystemId,
        Guid connectedSystemObjectId,
        string password,
        PasswordSetOptions options,
        Func<Activity, Task> createActivityAsync,
        Guid? parentActivityId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A password is required.", nameof(password));

        // Deliberately without a parameter name: these messages are shown to an administrator and returned by the
        // REST API, where "(Parameter 'connectedSystemObjectId')" is noise about JIM's own method signature.
        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemId)
            ?? throw new ArgumentException($"Connected System {connectedSystemId} does not exist.");

        var connectedSystemObject = await GetConnectedSystemObjectAsync(connectedSystemId, connectedSystemObjectId)
            ?? throw new ArgumentException(
                $"Connected System Object {connectedSystemObjectId} does not exist in Connected System {connectedSystemId}.");

        // Both resolved before the Activity is created, so a Connector that cannot do this never leaves an
        // in-flight Activity behind. Same reasoning as the hierarchy import above.
        if (CreateConnector(connectedSystem) is not IConnectorPasswordManagement passwordConnector)
            throw new NotSupportedException(
                $"The '{connectedSystem.ConnectorDefinition.Name}' connector does not support setting passwords.");

        var activity = new Activity
        {
            TargetName = connectedSystemObject.NameOrId ?? connectedSystemObjectId.ToString(),
            TargetType = ActivityTargetType.ConnectedSystemObject,
            TargetOperationType = ActivityTargetOperationType.SetPassword,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectId = connectedSystemObjectId,
            MetaverseObjectId = connectedSystemObject.MetaverseObjectId,
            ParentActivityId = parentActivityId
        };
        await createActivityAsync(activity);

        var result = await ApplyPasswordAsync(passwordConnector, connectedSystem, connectedSystemObject, password, options, cancellationToken);

        if (result.Success)
        {
            // The applied behaviour, not the requested one: a target that could not honour the request says so,
            // and recording what was asked for would misstate the account's actual state.
            activity.Message = result.ExpiryBehaviourWarning == null
                ? $"Password set. Expiry behaviour applied: {result.AppliedExpiryBehaviour}."
                : $"Password set. Expiry behaviour applied: {result.AppliedExpiryBehaviour}. {result.ExpiryBehaviourWarning}";
            await Application.Activities.CompleteActivityAsync(activity);
        }
        else
        {
            // Failed rather than completed: the administrator asked for a password to be set and it was not.
            // The target's own words are kept verbatim, since why a directory refuses a password is a property
            // of that directory's policy and is what the administrator has to act on.
            await Application.Activities.FailActivityWithErrorAsync(activity,
                $"The password could not be set ({result.FailureReason}): {result.ErrorMessage}");
        }

        return result;
    }

    /// <summary>
    /// Opens the password connection, sets the password, and closes the connection whatever happens.
    /// <para>
    /// A connection that could not be opened, or a Connector that threw rather than classifying, is reported as
    /// a transient failure: it says nothing about whether the password itself would be acceptable, and an
    /// administrator's next move is to try again once the target is reachable.
    /// </para>
    /// </summary>
    private static async Task<PasswordSetResult> ApplyPasswordAsync(
        IConnectorPasswordManagement passwordConnector,
        ConnectedSystem connectedSystem,
        ConnectedSystemObject connectedSystemObject,
        string password,
        PasswordSetOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            passwordConnector.OpenPasswordConnection(connectedSystem.SettingValues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "SetConnectedSystemObjectPasswordAsync: Could not open the password connection to Connected System {ConnectedSystemId}", connectedSystem.Id);
            return PasswordSetResult.Failed(PasswordSetFailureReason.Transient,
                $"JIM could not open a password connection to the Connected System: {ex.Message}");
        }

        try
        {
            return await passwordConnector.SetPasswordAsync(connectedSystemObject, password, options, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "SetConnectedSystemObjectPasswordAsync: The Connector threw setting a password on Connected System Object {CsoId}", connectedSystemObject.Id);
            return PasswordSetResult.Failed(PasswordSetFailureReason.Transient, ex.Message);
        }
        finally
        {
            passwordConnector.ClosePasswordConnection();
        }
    }

    /// <summary>
    /// The Connector-detected capabilities for a Connected System (issue #231), e.g. an LDAP directory's type,
    /// vendor, DNS host name, and paging support: facts detected from the target during a previous connection
    /// and persisted onto <see cref="ConnectedSystem.PersistedConnectorData"/>. Purely a display concern for the
    /// "Directory Capabilities" card on the Connected System details page; JIM never interprets the persisted
    /// data itself, it is only ever replayed to the owning Connector to interpret.
    /// <para>
    /// Null when the Connected System does not exist or its Connector does not implement
    /// <see cref="IConnectorDetectedCapabilities"/> (the UI hides the card entirely); an empty list when the
    /// Connector supports detection but nothing has been detected yet (for example, before the first
    /// successful connection), which the UI renders as a hint.
    /// </para>
    /// </summary>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<List<ConnectorCapability>?> GetConnectedSystemDetectedCapabilitiesAsync(int connectedSystemId)
    {
        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return null;

        return CreateConnector(connectedSystem) is IConnectorDetectedCapabilities capabilitiesConnector
            ? capabilitiesConnector.GetDetectedCapabilities(connectedSystem.PersistedConnectorData, Log.Logger)
            : null;
    }

    #endregion

    #region Connected System Directory Servers
    /// <summary>
    /// Discovers the domain controllers in this Connected System's forest, with the Active Directory Site each
    /// belongs to, so an administrator can be shown a choice for the Preferred Domain Controller setting rather
    /// than having to already know a hostname (issue #1167).
    /// <para>
    /// Deliberately not recorded as an Activity, for the same reason as <see cref="RunPasswordPreflightAsync"/>:
    /// it changes nothing in JIM or in the Connected System, it only reads. It also never writes to the
    /// Preferred Domain Controller setting itself; the setting is intent, and only the administrator's own
    /// selection in the portal, REST API caller, or PowerShell caller updates it.
    /// </para>
    /// </summary>
    /// <param name="connectedSystemId">The Connected System whose directory to discover domain controllers in.</param>
    /// <param name="draftSettingValues">Connectivity settings entered on screen but not yet saved, applied over the saved ones (encrypted settings always come from the saved values). Supplied by the portal so an administrator configuring a system can discover before saving, mirroring <see cref="CertificateServer.ReadServerCertificateAsync"/>.</param>
    /// <exception cref="ArgumentException">No Connected System exists with <paramref name="connectedSystemId"/>.</exception>
    /// <exception cref="NotSupportedException">The Connector does not support directory server discovery, or (thrown by the Connector) the connected directory is not AD-family.</exception>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<List<ConnectorDirectoryServer>> GetConnectedSystemDirectoryServersAsync(int connectedSystemId, IReadOnlyCollection<ConnectedSystemSettingValueDraft>? draftSettingValues = null)
    {
        // Loaded without change tracking, so applying the drafts below cannot reach the database.
        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            throw new ArgumentException($"No Connected System found with id {connectedSystemId}.", nameof(connectedSystemId));

        if (draftSettingValues is { Count: > 0 })
            ConnectedSystemDraftSettings.Apply(connectedSystem, draftSettingValues);

        var connector = CreateConnector(connectedSystem);
        if (connector is not IConnectorDirectoryServers directoryServersConnector)
            throw new NotSupportedException($"The '{connectedSystem.ConnectorDefinition.Name}' connector does not support directory server discovery.");

        Log.Debug("GetConnectedSystemDirectoryServersAsync: Discovering directory servers for Connected System {ConnectedSystemId}.", connectedSystemId);
        return await directoryServersConnector.GetDirectoryServersAsync(connectedSystem.SettingValues, Log.Logger);
    }

    /// <summary>
    /// Whether this Connected System's Connector supports discovering directory servers at all, so the portal can
    /// show the Discover action beside the Preferred Domain Controller field only where it means something.
    /// <para>
    /// A property of the Connector, not of the current settings: stable for the life of a Connected System, so it
    /// can be asked once, mirroring <see cref="JIM.Application.Servers.CertificateServer.SupportsServerCertificateReadAsync"/>.
    /// </para>
    /// </summary>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<bool> SupportsDirectoryServerDiscoveryAsync(int connectedSystemId)
    {
        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return false;

        return CreateConnector(connectedSystem) is IConnectorDirectoryServers;
    }
    #endregion

    #region Connected System Hierarchy
    /// <summary>
    /// Causes the associated Connector to be instantiated and the hierarchy (partitions and containers) to be imported from the Connected System.
    /// You will need update the ConnectedSystem after if happy with the changes, to persist them.
    /// </summary>
    /// <returns>A result object describing what changed during the hierarchy refresh.</returns>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<HierarchyRefreshResult> ImportConnectedSystemHierarchyAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        // resolve the connector, and confirm it supports hierarchy import, before creating the activity: an
        // unsupported connector must never leave an in-flight activity behind.
        var connector = CreateConnector(connectedSystem);
        if (connector is not IConnectorPartitions partitionsConnector)
            throw new NotSupportedException($"The '{connectedSystem.ConnectorDefinition.Name}' connector does not support hierarchy import.");

        // every operation that results, either directly or indirectly in a data change requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.ImportHierarchy,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        // Everything from here on is covered, so that a Connected System that cannot be read finishes its Activity
        // as a failure carrying the reason, rather than leaving one in flight for ever with nothing recorded
        // against it. The exception still reaches the caller; the Activity is the audit record, not the response.
        try
        {
            var result = await RetrieveAndMergeHierarchyAsync(connectedSystem, partitionsConnector, activity);

            // Persist the changes
            await PersistConnectedSystemUpdateAsync(connectedSystem, initiatedBy);

            // A hierarchy import changes the system's configuration (partitions and containers); capture it onto the
            // ImportHierarchy activity so the change is versioned in the system's history. Reloaded, as ids are assigned on save.
            await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystem.Id);

            // finish the activity
            await Application.Activities.CompleteActivityAsync(activity);

            return result;
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Import the hierarchy (partitions and containers) from the Connected System (initiated by API key).
    /// </summary>
    /// <param name="connectedSystem">The Connected System to import hierarchy for.</param>
    /// <param name="initiatedByApiKey">The API key that initiated this operation.</param>
    /// <returns>A result object describing what changed during the hierarchy refresh.</returns>
    public async Task<HierarchyRefreshResult> ImportConnectedSystemHierarchyAsync(ConnectedSystem connectedSystem, ApiKey initiatedByApiKey)
    {
        ValidateConnectedSystemParameter(connectedSystem);
        ArgumentNullException.ThrowIfNull(initiatedByApiKey);

        // resolve the connector, and confirm it supports hierarchy import, before creating the activity: an
        // unsupported connector must never leave an in-flight activity behind.
        var connector = CreateConnector(connectedSystem);
        if (connector is not IConnectorPartitions partitionsConnector)
            throw new NotSupportedException($"The '{connectedSystem.ConnectorDefinition.Name}' connector does not support hierarchy import.");

        // every operation that results, either directly or indirectly in a data change requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.ImportHierarchy,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        // Covered from here on: see the user-initiated overload above.
        try
        {
            var result = await RetrieveAndMergeHierarchyAsync(connectedSystem, partitionsConnector, activity);

            // Persist the changes
            await PersistConnectedSystemUpdateAsync(connectedSystem, initiatedByApiKey);

            // Capture the configuration change onto the ImportHierarchy activity: see the user-initiated overload above.
            await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystem.Id);

            // finish the activity
            await Application.Activities.CompleteActivityAsync(activity);

            return result;
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Adds newly created containers to the hierarchy and auto-selects them if their parent is selected.
    /// Uses the connector's interface methods to parse container identifiers without connector-specific
    /// knowledge in the application layer.
    /// </summary>
    /// <param name="connectedSystem">The Connected System to update.</param>
    /// <param name="connector">The connector that created the containers (must implement IConnectorContainerCreation).</param>
    /// <param name="createdContainerExternalIds">List of container external IDs that were created during export.</param>
    /// <param name="initiatedByApiKey">Optional API key that initiated this operation.</param>
    /// <param name="initiatedByUser">Optional user that initiated this operation.</param>
    /// <param name="parentActivity">Optional parent activity to link this operation to (e.g., the export activity).</param>
    public async Task RefreshAndAutoSelectContainersAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        IReadOnlyList<string> createdContainerExternalIds,
        ApiKey? initiatedByApiKey = null,
        MetaverseObject? initiatedByUser = null,
        Activity? parentActivity = null)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        if (createdContainerExternalIds.Count == 0)
            return;

        // The connector must implement IConnectorContainerCreation to provide hierarchy parsing methods
        if (connector is not IConnectorContainerCreation containerCreator)
        {
            Log.Warning("RefreshAndAutoSelectContainersAsync: Connector does not implement IConnectorContainerCreation, skipping auto-selection");
            return;
        }

        Log.Information("RefreshAndAutoSelectContainersAsync: Processing {Count} created container(s) for system {SystemName}",
            createdContainerExternalIds.Count, connectedSystem.Name);

        // Create activity for tracking - link to parent activity if provided so this doesn't
        // appear as a separate top-level activity in the Activity list
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = connectedSystem.Id,
            ParentActivityId = parentActivity?.Id,
            Message = $"Auto-selecting {createdContainerExternalIds.Count} container(s) created during export"
        };

        if (initiatedByApiKey != null)
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        else
            await Application.Activities.CreateActivityAsync(activity, initiatedByUser);

        var containersAdded = 0;

        foreach (var containerExternalId in createdContainerExternalIds)
        {
            try
            {
                // Find which partition this container belongs to
                var partition = FindPartitionForContainer(connectedSystem, containerExternalId);
                if (partition == null)
                {
                    Log.Warning("RefreshAndAutoSelectContainersAsync: Could not find partition for container {ContainerExternalId}", containerExternalId);
                    continue;
                }

                // Check if container already exists in hierarchy
                if (partition.Containers != null && FindContainerByExternalId(partition.Containers, containerExternalId) != null)
                {
                    Log.Debug("RefreshAndAutoSelectContainersAsync: Container {ContainerExternalId} already exists in hierarchy", containerExternalId);
                    continue;
                }

                // Find the parent container using connector's method (no connector-specific knowledge here)
                var parentExternalId = containerCreator.GetParentContainerExternalId(containerExternalId);
                var parentContainer = parentExternalId != null && partition.Containers != null
                    ? FindContainerByExternalId(partition.Containers, parentExternalId)
                    : null;

                // Whether the new container needs selecting turns on Container Scope: a selected Subtree ancestor's
                // search already covers it (selecting it too would import the same objects twice), whereas a selected
                // OneLevel ancestor stops short of it (leaving it unselected would mean the objects just provisioned
                // into it are never imported). ConnectedSystemUtilities owns that rule for every caller.
                var shouldSelect = ConnectedSystemUtilities.NewContainerNeedsSelecting(parentContainer, partition.Selected);

                // Create the new container using connector's method to extract display name
                var containerName = containerCreator.GetContainerDisplayName(containerExternalId);
                var newContainer = new ConnectedSystemContainer
                {
                    ExternalId = containerExternalId,
                    Name = containerName,
                    Selected = shouldSelect
                };

                if (parentContainer != null)
                {
                    parentContainer.AddChildContainer(newContainer);
                }
                else
                {
                    // Top-level container in partition — only root containers get Partition set
                    newContainer.Partition = partition;
                    partition.Containers ??= new HashSet<ConnectedSystemContainer>();
                    partition.Containers.Add(newContainer);
                }

                containersAdded++;
                Log.Information("RefreshAndAutoSelectContainersAsync: Added container {ContainerExternalId}, Selected: {Selected}",
                    containerExternalId, shouldSelect);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "RefreshAndAutoSelectContainersAsync: Error processing container {ContainerExternalId}", containerExternalId);
            }
        }

        if (containersAdded > 0)
        {
            // Persist the changes
            if (initiatedByApiKey != null)
                await PersistConnectedSystemUpdateAsync(connectedSystem, initiatedByApiKey);
            else
                await PersistConnectedSystemUpdateAsync(connectedSystem, initiatedByUser);

            activity.Message = $"Auto-selected {containersAdded} container(s) created during export";

            // Container additions change the system's import scope; capture the configuration change onto this
            // activity so it is versioned in the system's history. Reloaded, as container ids are assigned on save.
            await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystem.Id);
        }

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Adds newly created containers to the hierarchy using initiator triad.
    /// </summary>
    public async Task RefreshAndAutoSelectContainersWithTriadAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        IReadOnlyList<string> createdContainerExternalIds,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName,
        Activity? parentActivity = null)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        if (createdContainerExternalIds.Count == 0)
            return;

        if (connector is not IConnectorContainerCreation containerCreator)
        {
            Log.Warning("RefreshAndAutoSelectContainersWithTriadAsync: Connector does not implement IConnectorContainerCreation, skipping auto-selection");
            return;
        }

        Log.Information("RefreshAndAutoSelectContainersWithTriadAsync: Processing {Count} created container(s) for system {SystemName}",
            createdContainerExternalIds.Count, connectedSystem.Name);

        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = connectedSystem.Id,
            ParentActivityId = parentActivity?.Id,
            Message = $"Auto-selecting {createdContainerExternalIds.Count} container(s) created during export"
        };

        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);

        var containersAdded = 0;

        foreach (var containerExternalId in createdContainerExternalIds)
        {
            try
            {
                var partition = FindPartitionForContainer(connectedSystem, containerExternalId);
                if (partition == null)
                {
                    Log.Warning("RefreshAndAutoSelectContainersWithTriadAsync: Could not find partition for container {ContainerExternalId}", containerExternalId);
                    continue;
                }

                if (partition.Containers != null && FindContainerByExternalId(partition.Containers, containerExternalId) != null)
                {
                    Log.Debug("RefreshAndAutoSelectContainersWithTriadAsync: Container {ContainerExternalId} already exists in hierarchy", containerExternalId);
                    continue;
                }

                // Find the parent container using connector's method
                var parentExternalId = containerCreator.GetParentContainerExternalId(containerExternalId);
                var parentContainer = parentExternalId != null && partition.Containers != null
                    ? FindContainerByExternalId(partition.Containers, parentExternalId)
                    : null;

                // Scope-aware coverage; see the sibling overload above for why a OneLevel ancestor does not cover this.
                var shouldSelect = ConnectedSystemUtilities.NewContainerNeedsSelecting(parentContainer, partition.Selected);

                // Create the new container using connector's method to extract display name
                var containerName = containerCreator.GetContainerDisplayName(containerExternalId);
                var newContainer = new ConnectedSystemContainer
                {
                    ExternalId = containerExternalId,
                    Name = containerName,
                    Selected = shouldSelect
                };

                if (parentContainer != null)
                {
                    parentContainer.AddChildContainer(newContainer);
                }
                else
                {
                    // Top-level container in partition — only root containers get Partition set
                    newContainer.Partition = partition;
                    partition.Containers ??= new HashSet<ConnectedSystemContainer>();
                    partition.Containers.Add(newContainer);
                }

                containersAdded++;
                Log.Information("RefreshAndAutoSelectContainersWithTriadAsync: Added container {ContainerExternalId}, Selected: {Selected}",
                    containerExternalId, shouldSelect);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "RefreshAndAutoSelectContainersWithTriadAsync: Error processing container {ContainerExternalId}", containerExternalId);
            }
        }

        if (containersAdded > 0)
        {
            await PersistConnectedSystemUpdateAsync(connectedSystem, initiatorType, initiatorId, initiatorName);
            activity.Message = $"Auto-selected {containersAdded} container(s) created during export";

            // Container additions change the system's import scope; capture the configuration change onto this
            // activity so it is versioned in the system's history. Reloaded, as container ids are assigned on save.
            await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystem.Id);
        }

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Finds the partition that a container external ID belongs to based on suffix matching.
    /// </summary>
    private static ConnectedSystemPartition? FindPartitionForContainer(ConnectedSystem connectedSystem, string containerExternalId)
    {
        // Container external ID should end with the partition's external ID
        return connectedSystem.Partitions?.FirstOrDefault(p =>
            containerExternalId.EndsWith(p.ExternalId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Recursively searches for a container by its external ID (DN) in a container hierarchy.
    /// </summary>
    private static ConnectedSystemContainer? FindContainerByExternalId(IEnumerable<ConnectedSystemContainer> containers, string externalId)
    {
        foreach (var container in containers)
        {
            if (container.ExternalId.Equals(externalId, StringComparison.OrdinalIgnoreCase))
                return container;

            var found = FindContainerByExternalId(container.ChildContainers, externalId);
            if (found != null)
                return found;
        }

        return null;
    }

    #region Hierarchy Merge Methods
    /// <summary>
    /// Merges discovered partitions and containers with existing ones, preserving user selections.
    /// This replaces the previous destructive approach that wiped all selections on refresh.
    /// </summary>
    /// <param name="connectedSystem">The Connected System to merge hierarchy into.</param>
    /// <param name="discoveredPartitions">The partitions discovered from the connector.</param>
    /// <returns>A result object describing what changed during the merge.</returns>
    internal static HierarchyRefreshResult MergeHierarchy(ConnectedSystem connectedSystem, List<ConnectorPartition> discoveredPartitions)
    {
        // A connector returning zero partitions almost always indicates a retrieval failure (connection,
        // authentication, or scope problem) rather than a directory that genuinely has no partitions. Treating
        // it as "every partition was removed" would destroy the configured hierarchy and the user's container
        // selections, so leave the existing hierarchy untouched and report no changes. Callers surface a warning
        // on the Activity so the admin knows the refresh returned nothing (#876).
        if (discoveredPartitions.Count == 0)
            return HierarchyRefreshResult.NoChanges(
                connectedSystem.Partitions?.Count ?? 0,
                CountAllContainers(connectedSystem.Partitions));

        var result = new HierarchyRefreshResult { Success = true };

        // Build lookup of existing items by ExternalId for efficient matching
        var existingPartitionLookup = (connectedSystem.Partitions ?? new List<ConnectedSystemPartition>())
            .ToDictionary(p => p.ExternalId, StringComparer.OrdinalIgnoreCase);
        var existingContainerLookup = BuildContainerLookup(connectedSystem.Partitions);
        var existingContainerStableIdLookup = BuildContainerStableIdLookup(connectedSystem.Partitions);

        // Track which existing partitions we've matched
        var matchedPartitionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Ensure Partitions list exists
        connectedSystem.Partitions ??= new List<ConnectedSystemPartition>();

        // Process each discovered partition
        foreach (var discovered in discoveredPartitions)
        {
            if (existingPartitionLookup.TryGetValue(discovered.Id, out var existing))
            {
                // MATCHED: Update name if changed, preserve Selected flag
                matchedPartitionIds.Add(discovered.Id);

                if (!string.Equals(existing.Name, discovered.Name, StringComparison.Ordinal))
                {
                    result.RenamedPartitions.Add(new HierarchyRenameItem
                    {
                        ExternalId = discovered.Id,
                        OldName = existing.Name,
                        NewName = discovered.Name,
                        ItemType = HierarchyItemType.Partition
                    });
                    existing.Name = discovered.Name;
                }

                // Merge containers recursively within this partition
                existing.Containers ??= new HashSet<ConnectedSystemContainer>();
                MergeContainersRecursive(
                    existing.Containers,
                    discovered.Containers,
                    null, // parent ExternalId for root containers
                    result,
                    existingContainerLookup,
                    existingContainerStableIdLookup);
            }
            else
            {
                // NEW: Add partition with Selected=false
                // Note: Must set ConnectedSystem explicitly for EF Core change tracking to work correctly
                // when the Partitions collection was loaded separately from the ConnectedSystem entity
                var newPartition = new ConnectedSystemPartition
                {
                    Name = discovered.Name,
                    ExternalId = discovered.Id,
                    Selected = false,
                    ConnectedSystem = connectedSystem,
                    Containers = discovered.Containers.Select(BuildConnectedSystemContainerTree).ToHashSet()
                };
                connectedSystem.Partitions.Add(newPartition);

                // Track this new partition so it doesn't get removed in the cleanup phase
                matchedPartitionIds.Add(discovered.Id);

                result.AddedPartitions.Add(new HierarchyChangeItem
                {
                    ExternalId = discovered.Id,
                    Name = discovered.Name,
                    ItemType = HierarchyItemType.Partition
                });

                // Count all new containers within the new partition
                CountAddedContainersRecursive(newPartition.Containers, result.AddedContainers);
            }
        }

        // Remove unmatched partitions (they no longer exist in the external system)
        var toRemove = connectedSystem.Partitions
            .Where(p => !matchedPartitionIds.Contains(p.ExternalId))
            .ToList();

        foreach (var partition in toRemove)
        {
            result.RemovedPartitions.Add(new HierarchyChangeItem
            {
                ExternalId = partition.ExternalId,
                Name = partition.Name,
                WasSelected = partition.Selected,
                ItemType = HierarchyItemType.Partition
            });

            // Also record all containers within the removed partition
            if (partition.Containers != null)
                CollectRemovedContainersRecursive(partition.Containers, result);

            connectedSystem.Partitions.Remove(partition);
        }

        // Calculate totals
        result.TotalPartitions = connectedSystem.Partitions.Count;
        result.TotalContainers = CountAllContainers(connectedSystem.Partitions);

        return result;
    }

    /// <summary>
    /// Builds a flat lookup dictionary of all containers by ExternalId for efficient matching.
    /// </summary>
    private static Dictionary<string, ConnectedSystemContainer> BuildContainerLookup(IEnumerable<ConnectedSystemPartition>? partitions)
    {
        var lookup = new Dictionary<string, ConnectedSystemContainer>(StringComparer.OrdinalIgnoreCase);
        if (partitions == null) return lookup;

        foreach (var partition in partitions)
        {
            if (partition.Containers != null)
                FlattenContainersIntoLookup(partition.Containers, lookup);
        }

        return lookup;
    }

    /// <summary>
    /// Builds a lookup of existing containers keyed on the Connected System's own immutable identifier, for those
    /// that carry one.
    /// </summary>
    /// <remarks>
    /// This is what lets a rename or a move be recognised as such. Keying identity on the Distinguished Name alone
    /// meant either operation presented as a removal plus an addition, and the re-added container arrived
    /// unselected: import scope narrowed because somebody tidied an OU name, and the next Full Import obsoleted
    /// everything beneath it. Containers enumerated before stable identifiers were recorded have none until their
    /// next hierarchy refresh, so the Distinguished Name lookup remains the fallback.
    /// </remarks>
    private static Dictionary<string, ConnectedSystemContainer> BuildContainerStableIdLookup(IEnumerable<ConnectedSystemPartition>? partitions)
    {
        var lookup = new Dictionary<string, ConnectedSystemContainer>(StringComparer.OrdinalIgnoreCase);
        if (partitions == null) return lookup;

        foreach (var partition in partitions.Where(p => p.Containers != null))
            FlattenContainersIntoStableIdLookup(partition.Containers!, lookup);

        return lookup;
    }

    /// <summary>
    /// Recursively flattens the container hierarchy into a lookup keyed on stable identifier, skipping containers
    /// that do not have one.
    /// </summary>
    private static void FlattenContainersIntoStableIdLookup(IEnumerable<ConnectedSystemContainer> containers, Dictionary<string, ConnectedSystemContainer> lookup)
    {
        foreach (var container in containers)
        {
            if (!string.IsNullOrEmpty(container.StableId))
                lookup.TryAdd(container.StableId, container);

            if (container.ChildContainers.Count > 0)
                FlattenContainersIntoStableIdLookup(container.ChildContainers, lookup);
        }
    }

    /// <summary>
    /// Recursively flattens container hierarchy into a lookup dictionary.
    /// </summary>
    private static void FlattenContainersIntoLookup(IEnumerable<ConnectedSystemContainer> containers, Dictionary<string, ConnectedSystemContainer> lookup)
    {
        foreach (var container in containers)
        {
            // Use TryAdd to handle potential duplicates gracefully
            lookup.TryAdd(container.ExternalId, container);

            if (container.ChildContainers.Count > 0)
                FlattenContainersIntoLookup(container.ChildContainers, lookup);
        }
    }

    /// <summary>
    /// Resolves a discovered container to the stored container it is, preferring the Connected System's own
    /// immutable identifier and falling back to the Distinguished Name.
    /// </summary>
    /// <remarks>
    /// Order matters and is the whole point: the Distinguished Name changes on rename and move, the stable
    /// identifier does not. Falling back keeps containers stored before stable identifiers existed, and Connectors
    /// that cannot supply one, working exactly as before.
    /// </remarks>
    private static bool TryResolveExistingContainer(
        ConnectorContainer discovered,
        Dictionary<string, ConnectedSystemContainer> globalLookup,
        Dictionary<string, ConnectedSystemContainer> globalStableIdLookup,
        [NotNullWhen(true)] out ConnectedSystemContainer? existing)
    {
        if (!string.IsNullOrEmpty(discovered.StableId) && globalStableIdLookup.TryGetValue(discovered.StableId, out existing))
            return true;

        return globalLookup.TryGetValue(discovered.Id, out existing);
    }

    /// <summary>
    /// Recursively merges discovered containers with existing ones.
    /// </summary>
    private static void MergeContainersRecursive(
        HashSet<ConnectedSystemContainer> existingContainers,
        List<ConnectorContainer> discoveredContainers,
        string? parentExternalId,
        HierarchyRefreshResult result,
        Dictionary<string, ConnectedSystemContainer> globalLookup,
        Dictionary<string, ConnectedSystemContainer> globalStableIdLookup)
    {
        var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var discovered in discoveredContainers)
        {
            if (TryResolveExistingContainer(discovered, globalLookup, globalStableIdLookup, out var existing))
            {
                matchedIds.Add(discovered.Id);

                // Record the identifier the first time the Connector supplies one, so a container selected before
                // stable identifiers existed survives its next rename.
                if (string.IsNullOrEmpty(existing.StableId) && !string.IsNullOrEmpty(discovered.StableId))
                    existing.StableId = discovered.StableId;

                // Adopt the current Distinguished Name. This is only ever different when the container was matched
                // on its stable identifier, which is precisely the rename or move that used to read as a removal.
                if (!string.Equals(existing.ExternalId, discovered.Id, StringComparison.OrdinalIgnoreCase))
                    existing.ExternalId = discovered.Id;

                // Check for rename
                if (!string.Equals(existing.Name, discovered.Name, StringComparison.Ordinal))
                {
                    result.RenamedContainers.Add(new HierarchyRenameItem
                    {
                        ExternalId = discovered.Id,
                        OldName = existing.Name,
                        NewName = discovered.Name,
                        ItemType = HierarchyItemType.Container
                    });
                    existing.Name = discovered.Name;
                }

                // Check for move (different parent)
                var existingParentId = existing.ParentContainer?.ExternalId;
                if (!string.Equals(existingParentId, parentExternalId, StringComparison.OrdinalIgnoreCase))
                {
                    result.MovedContainers.Add(new HierarchyMoveItem
                    {
                        ExternalId = discovered.Id,
                        Name = discovered.Name,
                        OldParentExternalId = existingParentId,
                        NewParentExternalId = parentExternalId
                    });
                    // Note: The actual parent relationship will be corrected by rebuilding the tree structure
                    // while preserving the Selected flag. For now we just track the move.
                }

                // Recurse into children
                MergeContainersRecursive(
                    existing.ChildContainers,
                    discovered.ChildContainers,
                    discovered.Id,
                    result,
                    globalLookup,
                    globalStableIdLookup);
            }
            else
            {
                // NEW container - add it
                var newContainer = BuildConnectedSystemContainerTree(discovered);
                existingContainers.Add(newContainer);

                // Record it as present, or the cleanup pass below deletes it again in this same refresh: it is not in
                // matchedIds, and "not matched" is how that pass recognises a container that has left the directory.
                // A container created since the last refresh was therefore reported as added and then silently
                // dropped, so it never appeared on the Partitions & Containers tab to be selected.
                matchedIds.Add(discovered.Id);

                result.AddedContainers.Add(new HierarchyChangeItem
                {
                    ExternalId = discovered.Id,
                    Name = discovered.Name,
                    ItemType = HierarchyItemType.Container
                });

                // Count all child containers as added too
                CountAddedContainersRecursive(newContainer.ChildContainers, result.AddedContainers);
            }
        }

        // Remove unmatched containers (they no longer exist in the external system)
        var toRemove = existingContainers
            .Where(c => !matchedIds.Contains(c.ExternalId))
            .ToList();

        foreach (var container in toRemove)
        {
            CollectRemovedContainerRecursive(container, result);
            existingContainers.Remove(container);
        }
    }

    /// <summary>
    /// Counts all containers in a hierarchy and adds them to the added containers list.
    /// </summary>
    private static void CountAddedContainersRecursive(IEnumerable<ConnectedSystemContainer>? containers, List<HierarchyChangeItem> addedContainers)
    {
        if (containers == null) return;

        foreach (var container in containers)
        {
            addedContainers.Add(new HierarchyChangeItem
            {
                ExternalId = container.ExternalId,
                Name = container.Name,
                ItemType = HierarchyItemType.Container
            });

            CountAddedContainersRecursive(container.ChildContainers, addedContainers);
        }
    }

    /// <summary>
    /// Recursively collects all containers that are being removed into the result.
    /// </summary>
    private static void CollectRemovedContainersRecursive(IEnumerable<ConnectedSystemContainer> containers, HierarchyRefreshResult result)
    {
        foreach (var container in containers)
        {
            CollectRemovedContainerRecursive(container, result);
        }
    }

    /// <summary>
    /// Collects a single container and all its children into the removed containers list.
    /// </summary>
    private static void CollectRemovedContainerRecursive(ConnectedSystemContainer container, HierarchyRefreshResult result)
    {
        result.RemovedContainers.Add(new HierarchyChangeItem
        {
            ExternalId = container.ExternalId,
            Name = container.Name,
            WasSelected = container.Selected,
            ItemType = HierarchyItemType.Container
        });

        foreach (var child in container.ChildContainers)
        {
            CollectRemovedContainerRecursive(child, result);
        }
    }

    /// <summary>
    /// Counts the total number of containers across all partitions.
    /// </summary>
    private static int CountAllContainers(IEnumerable<ConnectedSystemPartition>? partitions)
    {
        if (partitions == null) return 0;

        var count = 0;
        foreach (var partition in partitions)
        {
            if (partition.Containers != null)
                count += CountContainersRecursive(partition.Containers);
        }
        return count;
    }

    /// <summary>
    /// Recursively counts containers in a hierarchy.
    /// </summary>
    private static int CountContainersRecursive(IEnumerable<ConnectedSystemContainer> containers)
    {
        var count = 0;
        foreach (var container in containers)
        {
            count++;
            count += CountContainersRecursive(container.ChildContainers);
        }
        return count;
    }

    /// <summary>
    /// Retrieves the hierarchy (partitions and containers) from a Connected System and merges it into that
    /// system's existing hierarchy, preserving selections, and records what changed on the supplied Activity.
    /// </summary>
    /// <remarks>
    /// Shared by both <see cref="ImportConnectedSystemHierarchyAsync(ConnectedSystem, MetaverseObject?)"/> and
    /// <see cref="ImportConnectedSystemHierarchyAsync(ConnectedSystem, ApiKey)"/>. They were separate copies of
    /// this logic; the initiator decides who the Activity is attributed to, not what a hierarchy means.
    /// </remarks>
    private static async Task<HierarchyRefreshResult> RetrieveAndMergeHierarchyAsync(ConnectedSystem connectedSystem, IConnectorPartitions partitionsConnector, Activity activity)
    {
        var partitions = await partitionsConnector.GetPartitionsAsync(connectedSystem.SettingValues, Log.Logger);
        if (partitions.Count == 0)
        {
            // Zero partitions almost always means the connector could not enumerate them (connection,
            // authentication, or scope problem) rather than a genuinely empty directory. Warn the admin;
            // MergeHierarchy deliberately leaves the existing hierarchy untouched in this case (#876).
            activity.WarningMessage = "The hierarchy refresh retrieved no partitions from the Connected System, so the existing hierarchy was left unchanged. This usually indicates a connection, authentication, or scope problem rather than an empty directory; check the Connected System's settings and connectivity, then try again.";
        }

        // Merge discovered partitions with existing ones, preserving user selections
        var result = MergeHierarchy(connectedSystem, partitions);

        // Log the changes
        if (result.HasChanges)
        {
            Log.Information("Hierarchy refresh for {ConnectedSystem}: {Summary}", connectedSystem.Name, result.GetSummary());
            if (result.HasSelectedItemsRemoved)
            {
                Log.Warning("Hierarchy refresh for {ConnectedSystem} removed selected items. Removed partitions: {RemovedPartitions}, Removed containers: {RemovedContainers}",
                    connectedSystem.Name,
                    result.RemovedPartitions.Where(p => p.WasSelected).Select(p => p.Name),
                    result.RemovedContainers.Where(c => c.WasSelected).Select(c => c.Name));
            }
            activity.Message = result.GetSummary();
        }

        return result;
    }
    #endregion

    private static ConnectedSystemContainer BuildConnectedSystemContainerTree(ConnectorContainer connectorContainer)
    {
        var connectedSystemContainer = new ConnectedSystemContainer
        {
            ExternalId = connectorContainer.Id,
            StableId = connectorContainer.StableId,
            Name = connectorContainer.Name,
            Description = connectorContainer.Description,
            Hidden = connectorContainer.Hidden
        };

        foreach (var childContainer in connectorContainer.ChildContainers)
            connectedSystemContainer.AddChildContainer(BuildConnectedSystemContainerTree(childContainer));

        return connectedSystemContainer;
    }
    #endregion

    #region Connected System Object Types
    /// <summary>
    /// Retrieves all the Connected System Object Types for a given Connected System.
    /// Includes Attributes.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to return the types for.</param>
    public async Task<List<ConnectedSystemObjectType>> GetObjectTypesAsync(int connectedSystemId)
    {
        using var span = Diagnostics.Diagnostics.Database.StartSpan("Cso.GetObjectTypes")
            .SetTag("connectedSystemId", connectedSystemId);
        return await Application.Repository.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);
    }

    /// <summary>
    /// Gets a Connected System Object Type by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the object type.</param>
    public async Task<ConnectedSystemObjectType?> GetObjectTypeAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetObjectTypeAsync(id);
    }

    #region Object Type extensions (auxiliary classes)

    /// <summary>
    /// Gets every auxiliary class selection an administrator has made on a Connected System.
    /// </summary>
    public async Task<List<ConnectedSystemObjectTypeExtension>> GetObjectTypeExtensionsAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetObjectTypeExtensionsAsync(connectedSystemId);
    }

    /// <summary>
    /// Records that one Object Type should be extended with the attributes of another.
    /// </summary>
    /// <returns>True if a new selection was recorded; false if it was already there.</returns>
    public async Task<bool> AddObjectTypeExtensionAsync(int baseObjectTypeId, int extensionObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.AddObjectTypeExtensionAsync(baseObjectTypeId, extensionObjectTypeId);
    }

    /// <summary>
    /// Withdraws an auxiliary class selection.
    /// </summary>
    /// <returns>True if a selection was removed; false if there was nothing to remove.</returns>
    public async Task<bool> RemoveObjectTypeExtensionAsync(int baseObjectTypeId, int extensionObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.RemoveObjectTypeExtensionAsync(baseObjectTypeId, extensionObjectTypeId);
    }

    /// <summary>
    /// Names the structural Object Type to use as the carrier when creating objects of a type that cannot stand
    /// alone, or clears it when passed null.
    /// </summary>
    public async Task SetStructuralCarrierObjectTypeAsync(int objectTypeId, int? carrierObjectTypeId)
    {
        await Application.Repository.ConnectedSystems.SetStructuralCarrierObjectTypeAsync(objectTypeId, carrierObjectTypeId);
    }

    #endregion

    #region Auxiliary class discovery

    /// <summary>
    /// Starts an auxiliary class discovery run for a Connected System.
    /// </summary>
    public async Task<AuxiliaryClassDiscoveryRun> CreateAuxiliaryClassDiscoveryRunAsync(AuxiliaryClassDiscoveryRun run)
    {
        return await Application.Repository.ConnectedSystems.CreateAuxiliaryClassDiscoveryRunAsync(run);
    }

    /// <summary>
    /// Queues an auxiliary class discovery run for a Connected System, refusing if one is already in flight.
    /// </summary>
    /// <remarks>
    /// One at a time per Connected System, because a full scan reads every object and two of them would double the
    /// load on a directory an administrator is still using for authentication. The database enforces the same rule
    /// with a filtered unique index; this check is what turns that into an explanation rather than a constraint
    /// violation.
    /// </remarks>
    public async Task<AuxiliaryClassDiscoveryStartResult> StartAuxiliaryClassDiscoveryAsync(
        int connectedSystemId,
        AuxiliaryClassDiscoveryScope scope,
        int? sampleSizePerObjectType,
        MetaverseObject? initiatedBy)
    {
        if (scope == AuxiliaryClassDiscoveryScope.NotSet)
            return AuxiliaryClassDiscoveryStartResult.Failed("A discovery scope must be chosen: a quick sample, or a full scan.");

        if (scope == AuxiliaryClassDiscoveryScope.QuickSample && sampleSizePerObjectType is null or < 1)
            return AuxiliaryClassDiscoveryStartResult.Failed("A quick sample needs to know how many objects of each Object Type to read.");

        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return AuxiliaryClassDiscoveryStartResult.Failed($"Connected System {connectedSystemId} does not exist.");

        var inFlight = await GetInProgressAuxiliaryClassDiscoveryRunAsync(connectedSystemId);
        if (inFlight != null)
            return AuxiliaryClassDiscoveryStartResult.Failed(
                $"A discovery run for '{connectedSystem.Name}' is already in progress. Wait for it to finish, or cancel it, before starting another.");

        var workerTask = new AuxiliaryClassDiscoveryWorkerTask
        {
            ConnectedSystemId = connectedSystemId,
            Scope = scope,

            // A full scan reads everything, so a sample size on one would be a number that silently did nothing.
            SampleSizePerObjectType = scope == AuxiliaryClassDiscoveryScope.QuickSample ? sampleSizePerObjectType : null,
            InitiatedByType = initiatedBy != null ? ActivityInitiatorType.User : ActivityInitiatorType.System,
            InitiatedById = initiatedBy?.Id,
            InitiatedByName = initiatedBy?.NameOrId
        };

        var creationResult = await Application.Tasking.CreateWorkerTaskAsync(workerTask);
        return creationResult.Success
            ? AuxiliaryClassDiscoveryStartResult.Queued(workerTask.Id, workerTask.Activity.Id)
            : AuxiliaryClassDiscoveryStartResult.Failed(creationResult.ErrorMessage ?? "The discovery task could not be queued.");
    }

    /// <summary>
    /// Executes a queued auxiliary class discovery run. Called by JIM.Worker.
    /// </summary>
    /// <remarks>
    /// The Connector is created here rather than in the worker, so that connector construction stays in one place;
    /// the progress reporter comes from the worker, because it is the thing holding the Activity being narrated.
    /// </remarks>
    public async Task<AuxiliaryClassDiscoveryRun> RunAuxiliaryClassDiscoveryAsync(
        AuxiliaryClassDiscoveryWorkerTask workerTask,
        Activity activity,
        IConnectorProgress progress,
        CancellationToken cancellationToken)
    {
        // The full graph: the runner needs the Object Types an administrator selected, their classification tags,
        // and the containers that bound what is in scope.
        var connectedSystem = await GetConnectedSystemAsync(workerTask.ConnectedSystemId)
                              ?? throw new InvalidDataException($"Connected System {workerTask.ConnectedSystemId} does not exist.");

        var connector = CreateConnector(connectedSystem);
        try
        {
            var runner = new AuxiliaryClassDiscoveryRunner(Application, Log.Logger);
            return await runner.RunAsync(connectedSystem, workerTask.Scope, workerTask.SampleSizePerObjectType,
                activity, connector, progress, cancellationToken);
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Gets the most recently started discovery run for a Connected System, with its results.
    /// </summary>
    public async Task<AuxiliaryClassDiscoveryRun?> GetLatestAuxiliaryClassDiscoveryRunAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetLatestAuxiliaryClassDiscoveryRunAsync(connectedSystemId);
    }

    /// <summary>
    /// Gets the discovery run currently in flight for a Connected System, or null if none is.
    /// </summary>
    public async Task<AuxiliaryClassDiscoveryRun?> GetInProgressAuxiliaryClassDiscoveryRunAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetInProgressAuxiliaryClassDiscoveryRunAsync(connectedSystemId);
    }

    /// <summary>
    /// Persists a discovery run's progress, outcome and results. The run must have been loaded on the same
    /// JimApplication instance used to save it.
    /// </summary>
    public async Task UpdateAuxiliaryClassDiscoveryRunAsync(AuxiliaryClassDiscoveryRun run)
    {
        await Application.Repository.ConnectedSystems.UpdateAuxiliaryClassDiscoveryRunAsync(run);
    }

    #endregion

    /// <summary>
    /// Updates a Connected System Object Type.
    /// </summary>
    /// <param name="objectType">The object type to update.</param>
    /// <param name="initiatedBy">The user who initiated the update.</param>
    public async Task UpdateObjectTypeAsync(ConnectedSystemObjectType objectType, MetaverseObject? initiatedBy)
    {
        if (objectType == null)
            throw new ArgumentNullException(nameof(objectType));

        Log.Debug("UpdateObjectTypeAsync() called for {ObjectType}", objectType.Name);

        var activity = new Activity
        {
            TargetName = objectType.ConnectedSystem?.Name ?? "Unknown",
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = objectType.ConnectedSystemId,
            Message = $"Update object type: {objectType.Name}"
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await Application.Repository.ConnectedSystems.UpdateObjectTypeAsync(objectType);

        await CaptureConnectedSystemConfigurationChangeAsync(activity, objectType.ConnectedSystemId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Gets a Connected System Attribute by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the attribute.</param>
    public async Task<ConnectedSystemObjectTypeAttribute?> GetAttributeAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetAttributeAsync(id);
    }

    /// <summary>
    /// Updates a Connected System Attribute.
    /// </summary>
    /// <param name="attribute">The attribute to update.</param>
    /// <param name="initiatedBy">The user who initiated the update.</param>
    public async Task UpdateAttributeAsync(ConnectedSystemObjectTypeAttribute attribute, MetaverseObject? initiatedBy)
    {
        if (attribute == null)
            throw new ArgumentNullException(nameof(attribute));

        Log.Debug("UpdateAttributeAsync() called for {Attribute}", attribute.Name);

        var activity = new Activity
        {
            TargetName = attribute.ConnectedSystemObjectType?.ConnectedSystem?.Name ?? "Unknown",
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = attribute.ConnectedSystemObjectType?.ConnectedSystemId,
            Message = $"Update attribute: {attribute.ConnectedSystemObjectType?.Name}.{attribute.Name}"
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await Application.Repository.ConnectedSystems.UpdateAttributeAsync(attribute);

        await CaptureConnectedSystemConfigurationChangeAsync(activity, attribute.ConnectedSystemObjectType?.ConnectedSystemId ?? 0);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates a Connected System Object Type (initiated by API key).
    /// </summary>
    /// <param name="objectType">The object type to update.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the update.</param>
    public async Task UpdateObjectTypeAsync(ConnectedSystemObjectType objectType, ApiKey initiatedByApiKey)
    {
        if (objectType == null)
            throw new ArgumentNullException(nameof(objectType));

        Log.Debug("UpdateObjectTypeAsync() called for {ObjectType} (API key initiated)", objectType.Name);

        var activity = new Activity
        {
            TargetName = objectType.ConnectedSystem?.Name ?? "Unknown",
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = objectType.ConnectedSystemId,
            Message = $"Update object type: {objectType.Name}"
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        await Application.Repository.ConnectedSystems.UpdateObjectTypeAsync(objectType);

        await CaptureConnectedSystemConfigurationChangeAsync(activity, objectType.ConnectedSystemId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates a Connected System Attribute (initiated by API key).
    /// </summary>
    /// <param name="attribute">The attribute to update.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the update.</param>
    public async Task UpdateAttributeAsync(ConnectedSystemObjectTypeAttribute attribute, ApiKey initiatedByApiKey)
    {
        if (attribute == null)
            throw new ArgumentNullException(nameof(attribute));

        Log.Debug("UpdateAttributeAsync() called for {Attribute} (API key initiated)", attribute.Name);

        var activity = new Activity
        {
            TargetName = attribute.ConnectedSystemObjectType?.ConnectedSystem?.Name ?? "Unknown",
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = attribute.ConnectedSystemObjectType?.ConnectedSystemId,
            Message = $"Update attribute: {attribute.ConnectedSystemObjectType?.Name}.{attribute.Name}"
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        await Application.Repository.ConnectedSystems.UpdateAttributeAsync(attribute);

        await CaptureConnectedSystemConfigurationChangeAsync(activity, attribute.ConnectedSystemObjectType?.ConnectedSystemId ?? 0);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Bulk updates multiple Connected System Attributes with a single parent activity.
    /// </summary>
    /// <param name="connectedSystem">The Connected System containing the attributes.</param>
    /// <param name="objectType">The object type containing the attributes.</param>
    /// <param name="attributeUpdates">Dictionary of attribute IDs to update requests.</param>
    /// <param name="initiatedBy">The user who initiated the update.</param>
    /// <returns>Tuple containing the activity, updated attributes, and any errors.</returns>
    public async Task<(Activity Activity, List<ConnectedSystemObjectTypeAttribute> Updated, List<(int AttributeId, string Error)> Errors)>
        BulkUpdateAttributesAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemObjectType objectType,
            Dictionary<int, (bool? Selected, bool? IsExternalId, bool? IsSecondaryExternalId)> attributeUpdates,
            MetaverseObject? initiatedBy)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));
        if (objectType == null)
            throw new ArgumentNullException(nameof(objectType));
        if (attributeUpdates == null)
            throw new ArgumentNullException(nameof(attributeUpdates));

        Log.Debug("BulkUpdateAttributesAsync() called for {Count} attributes on {ObjectType}", attributeUpdates.Count, objectType.Name);

        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = connectedSystem.Id,
            Message = $"Bulk update of {attributeUpdates.Count} attribute(s) on {objectType.Name}",
            ObjectsToProcess = attributeUpdates.Count
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        var updated = new List<ConnectedSystemObjectTypeAttribute>();
        var errors = new List<(int AttributeId, string Error)>();

        foreach (var (attributeId, updates) in attributeUpdates)
        {
            var attribute = objectType.Attributes?.FirstOrDefault(a => a.Id == attributeId);
            if (attribute == null)
            {
                errors.Add((attributeId, $"Attribute {attributeId} not found on object type {objectType.Name}"));
                continue;
            }

            // Validate: a credential attribute can never be managed by JIM. Deselecting one stays allowed.
            if (CredentialAttributes.IsCredentialAttribute(attribute.Name) &&
                (updates.Selected == true || updates.IsExternalId == true || updates.IsSecondaryExternalId == true))
            {
                errors.Add((attributeId, $"Attribute '{attribute.Name}' holds credential material and cannot be managed by JIM. Passwords are synchronised through JIM's dedicated password channel, not through Attribute Flow."));
                continue;
            }

            // Validate: Cannot unselect an External ID or Secondary External ID attribute
            if (updates.Selected.HasValue && !updates.Selected.Value && (attribute.IsExternalId || attribute.IsSecondaryExternalId))
            {
                var idType = attribute.IsExternalId ? "External ID" : "Secondary External ID";
                errors.Add((attributeId, $"Cannot unselect attribute '{attribute.Name}' because it is the {idType} attribute. These attributes must remain selected."));
                continue;
            }

            if (updates.Selected.HasValue)
                attribute.Selected = updates.Selected.Value;

            if (updates.IsExternalId.HasValue)
            {
                attribute.IsExternalId = updates.IsExternalId.Value;
                // External ID attributes must always be selected for sync operations to work
                if (updates.IsExternalId.Value)
                    attribute.Selected = true;
            }

            if (updates.IsSecondaryExternalId.HasValue)
            {
                attribute.IsSecondaryExternalId = updates.IsSecondaryExternalId.Value;
                // Secondary External ID attributes must always be selected for sync operations to work
                if (updates.IsSecondaryExternalId.Value)
                    attribute.Selected = true;
            }

            updated.Add(attribute);
            activity.ObjectsProcessed++;
        }

        if (updated.Count > 0)
        {
            await Application.Repository.ConnectedSystems.UpdateAttributesAsync(updated);

            // Attribute selection changes are configuration; capture the change onto this activity so it is
            // versioned in the system's history. Reloaded so the snapshot reflects persisted truth.
            await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystem.Id);
        }

        if (errors.Count > 0)
            await Application.Activities.CompleteActivityWithWarningAsync(activity);
        else
            await Application.Activities.CompleteActivityAsync(activity);

        return (activity, updated, errors);
    }

    /// <summary>
    /// Bulk updates multiple Connected System Attributes with a single parent activity (initiated by API key).
    /// </summary>
    /// <param name="connectedSystem">The Connected System containing the attributes.</param>
    /// <param name="objectType">The object type containing the attributes.</param>
    /// <param name="attributeUpdates">Dictionary of attribute IDs to update requests.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the update.</param>
    /// <returns>Tuple containing the activity, updated attributes, and any errors.</returns>
    public async Task<(Activity Activity, List<ConnectedSystemObjectTypeAttribute> Updated, List<(int AttributeId, string Error)> Errors)>
        BulkUpdateAttributesAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemObjectType objectType,
            Dictionary<int, (bool? Selected, bool? IsExternalId, bool? IsSecondaryExternalId)> attributeUpdates,
            ApiKey initiatedByApiKey)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));
        if (objectType == null)
            throw new ArgumentNullException(nameof(objectType));
        if (attributeUpdates == null)
            throw new ArgumentNullException(nameof(attributeUpdates));
        if (initiatedByApiKey == null)
            throw new ArgumentNullException(nameof(initiatedByApiKey));

        Log.Debug("BulkUpdateAttributesAsync() called for {Count} attributes on {ObjectType} (API key initiated)", attributeUpdates.Count, objectType.Name);

        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = connectedSystem.Id,
            Message = $"Bulk update of {attributeUpdates.Count} attribute(s) on {objectType.Name}",
            ObjectsToProcess = attributeUpdates.Count
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        var updated = new List<ConnectedSystemObjectTypeAttribute>();
        var errors = new List<(int AttributeId, string Error)>();

        foreach (var (attributeId, updates) in attributeUpdates)
        {
            var attribute = objectType.Attributes?.FirstOrDefault(a => a.Id == attributeId);
            if (attribute == null)
            {
                errors.Add((attributeId, $"Attribute {attributeId} not found on object type {objectType.Name}"));
                continue;
            }

            // Validate: a credential attribute can never be managed by JIM. Deselecting one stays allowed.
            if (CredentialAttributes.IsCredentialAttribute(attribute.Name) &&
                (updates.Selected == true || updates.IsExternalId == true || updates.IsSecondaryExternalId == true))
            {
                errors.Add((attributeId, $"Attribute '{attribute.Name}' holds credential material and cannot be managed by JIM. Passwords are synchronised through JIM's dedicated password channel, not through Attribute Flow."));
                continue;
            }

            // Validate: Cannot unselect an External ID or Secondary External ID attribute
            if (updates.Selected.HasValue && !updates.Selected.Value && (attribute.IsExternalId || attribute.IsSecondaryExternalId))
            {
                var idType = attribute.IsExternalId ? "External ID" : "Secondary External ID";
                errors.Add((attributeId, $"Cannot unselect attribute '{attribute.Name}' because it is the {idType} attribute. These attributes must remain selected."));
                continue;
            }

            if (updates.Selected.HasValue)
                attribute.Selected = updates.Selected.Value;

            if (updates.IsExternalId.HasValue)
            {
                attribute.IsExternalId = updates.IsExternalId.Value;
                // External ID attributes must always be selected for sync operations to work
                if (updates.IsExternalId.Value)
                    attribute.Selected = true;
            }

            if (updates.IsSecondaryExternalId.HasValue)
            {
                attribute.IsSecondaryExternalId = updates.IsSecondaryExternalId.Value;
                // Secondary External ID attributes must always be selected for sync operations to work
                if (updates.IsSecondaryExternalId.Value)
                    attribute.Selected = true;
            }

            updated.Add(attribute);
            activity.ObjectsProcessed++;
        }

        if (updated.Count > 0)
        {
            await Application.Repository.ConnectedSystems.UpdateAttributesAsync(updated);

            // Attribute selection changes are configuration; capture the change onto this activity so it is
            // versioned in the system's history. Reloaded so the snapshot reflects persisted truth.
            await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystem.Id);
        }

        if (errors.Count > 0)
            await Application.Activities.CompleteActivityWithWarningAsync(activity);
        else
            await Application.Activities.CompleteActivityAsync(activity);

        return (activity, updated, errors);
    }
    #endregion

    #region Connected System Objects
    /// <summary>
    /// Deletes a Connected System Object, and it's attribute values from a Connected System.
    /// Also prepares a Connected System Object Change for persistence with the activityRunProfileExecutionItem by the caller.
    /// </summary>
    public async Task DeleteConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
    {
        // Capture the external ID and display name BEFORE deletion.
        // We cannot reference attribute values after deletion because they get cascade deleted with the CSO.
        // Use ToStringNoName() to get just the value without "attributeName: " prefix.
        var externalIdDisplayValue = connectedSystemObject.ExternalIdAttributeValue?.ToStringNoName();
        // Name only, never NameOrId: the external id is captured separately just above, and letting it
        // stand in for the name would persist the same value into both snapshot fields.
        var displayName = connectedSystemObject.Name;

        // Snapshot all attribute values BEFORE deletion for change tracking.
        // Attribute values are cascade-deleted with the CSO, so we must capture them now.
        // Filter out attributes with NotSet type (e.g., incomplete test data) to avoid errors.
        var finalAttributeValues = connectedSystemObject.AttributeValues
            .Where(av => av.Attribute != null && av.Attribute.Type != AttributeDataType.NotSet)
            .ToList();

        await Application.Repository.ConnectedSystems.DeleteConnectedSystemObjectAsync(connectedSystemObject);

        // Check if CSO change tracking is enabled
        var changeTrackingEnabled = await Application.ServiceSettings.GetCsoChangeTrackingEnabledAsync();
        if (!changeTrackingEnabled)
        {
            // Clear the navigation property and FK to the deleted CSO to prevent FK constraint violations.
            activityRunProfileExecutionItem.ConnectedSystemObject = null;
            activityRunProfileExecutionItem.ConnectedSystemObjectId = null;
            return;
        }

        // Create a Change Object for this deletion.
        // Note: ConnectedSystemObject and DeletedObjectExternalIdAttributeValue are intentionally NOT set
        // because the CSO and its attribute values have been cascade deleted from the database.
        // The DeletedObjectType, DeletedObjectExternalId, and DeletedObjectDisplayName fields preserve the object identity.
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemObject.ConnectedSystemId,
            // ConnectedSystemObject is null for DELETE operations (CSO no longer exists)
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = DateTime.UtcNow,
            DeletedObjectType = connectedSystemObject.Type,
            // DeletedObjectExternalIdAttributeValue cannot be set - the attribute value is cascade deleted with the CSO
            // Use string fields to preserve the values for UI display:
            DeletedObjectExternalId = externalIdDisplayValue,
            DeletedObjectDisplayName = displayName,
            // The id survives here as a plain column; ConnectedSystemObjectId is a foreign key and is nulled
            // with the object, so this is the only way back to this record from a reference to what was deleted.
            DeletedConnectedSystemObjectId = connectedSystemObject.Id,
            ActivityRunProfileExecutionItem = activityRunProfileExecutionItem,
            // Copy initiator info from the Activity for audit trail (if Activity is loaded)
            InitiatedByType = activityRunProfileExecutionItem.Activity?.InitiatedByType ?? ActivityInitiatorType.NotSet,
            InitiatedById = activityRunProfileExecutionItem.Activity?.InitiatedById,
            InitiatedByName = activityRunProfileExecutionItem.Activity?.InitiatedByName
        };

        // Capture all final attribute values as removals for audit purposes.
        // Attribute value entities reference ConnectedSystemObjectTypeAttribute schema entities
        // that may already be tracked by EF Core's change tracker, so we capture in a separate
        // step and associate with the change record only if no tracking conflicts occur.
        CaptureDeletedCsoAttributeValues(change, finalAttributeValues);

        // Log the external ID for audit purposes
        if (!string.IsNullOrEmpty(externalIdDisplayValue))
        {
            Log.Debug("DeleteConnectedSystemObjectAsync: Deleted CSO with external ID: {ExternalId}, captured {AttrCount} final attribute values",
                externalIdDisplayValue, change.AttributeChanges.Count);
        }

        // The change object will be persisted with the activity Run Profile execution item further up the stack.
        // We just need to associate the change with the execution item.
        activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;

        // Clear the navigation property and FK to the deleted CSO to prevent FK constraint violations.
        // The CSO is now deleted, so we cannot maintain a reference to it.
        activityRunProfileExecutionItem.ConnectedSystemObject = null;
        activityRunProfileExecutionItem.ConnectedSystemObjectId = null;
    }

    /// <summary>
    /// Batch deletes multiple Connected System Objects and their attribute values.
    /// This is more efficient than calling DeleteConnectedSystemObjectAsync in a loop.
    /// </summary>
    public async Task DeleteConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> activityRunProfileExecutionItems)
    {
        if (connectedSystemObjects.Count != activityRunProfileExecutionItems.Count)
            throw new ArgumentException("CSO count must match execution item count");

        // Capture external ID, display name, and all attribute values before deletion.
        // We cannot reference attribute values after deletion because they get cascade deleted with the CSO.
        // Use ToStringNoName() to get just the value without "attributeName: " prefix.
        // Name only, never NameOrId: the external id is captured separately alongside it.
        var deletedObjectInfo = connectedSystemObjects
            .Select(cso => (
                ExternalId: cso.ExternalIdAttributeValue?.ToStringNoName(),
                DisplayName: cso.Name,
                FinalAttributeValues: cso.AttributeValues
                    .Where(av => av.Attribute != null && av.Attribute.Type != AttributeDataType.NotSet)
                    .ToList()))
            .ToList();

        // Batch delete from database
        await Application.Repository.ConnectedSystems.DeleteConnectedSystemObjectsAsync(connectedSystemObjects);

        // Check if CSO change tracking is enabled
        var changeTrackingEnabled = await Application.ServiceSettings.GetCsoChangeTrackingEnabledAsync();

        // Create change objects for each deletion (if enabled)
        for (int i = 0; i < connectedSystemObjects.Count; i++)
        {
            var cso = connectedSystemObjects[i];
            var executionItem = activityRunProfileExecutionItems[i];
            var (externalId, displayName, finalAttributeValues) = deletedObjectInfo[i];

            if (changeTrackingEnabled)
            {
                var change = new ConnectedSystemObjectChange
                {
                    ConnectedSystemId = cso.ConnectedSystemId,
                    ChangeType = ObjectChangeType.Deleted,
                    ChangeTime = DateTime.UtcNow,
                    DeletedObjectType = cso.Type,
                    // Use string fields to preserve the values for UI display:
                    DeletedObjectExternalId = externalId,
                    DeletedObjectDisplayName = displayName,
                    DeletedConnectedSystemObjectId = cso.Id,
                    ActivityRunProfileExecutionItem = executionItem,
                    // Copy initiator info from the Activity for audit trail (if Activity is loaded)
                    InitiatedByType = executionItem.Activity?.InitiatedByType ?? ActivityInitiatorType.NotSet,
                    InitiatedById = executionItem.Activity?.InitiatedById,
                    InitiatedByName = executionItem.Activity?.InitiatedByName
                };

                // Capture all final attribute values as removals for audit purposes.
                CaptureDeletedCsoAttributeValues(change, finalAttributeValues);

                executionItem.ConnectedSystemObjectChange = change;
            }

            executionItem.ConnectedSystemObject = null;
            executionItem.ConnectedSystemObjectId = null;
        }

        Log.Debug("DeleteConnectedSystemObjectsAsync: Batch deleted {Count} CSOs", connectedSystemObjects.Count);
    }

    /// <summary>
    /// Batch deletes multiple Connected System Objects without creating change history or RPEIs.
    /// Use this for quiet deletions where the disconnection was already recorded elsewhere
    /// (e.g., pre-disconnected CSOs from synchronous MVO deletion).
    /// </summary>
    public async Task DeleteConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
    {
        await Application.Repository.ConnectedSystems.DeleteConnectedSystemObjectsAsync(connectedSystemObjects);
        Log.Debug("DeleteConnectedSystemObjectsAsync: Quietly batch deleted {Count} CSOs (no RPEI)", connectedSystemObjects.Count);
    }

    public async Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeStringAsync(connectedSystemId, connectedSystemObjectTypeId);
    }
    
    public async Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeIntAsync(connectedSystemId, connectedSystemObjectTypeId);
    }

    public async Task<List<long>> GetAllExternalIdAttributeValuesOfTypeLongAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeLongAsync(connectedSystemId, connectedSystemObjectTypeId);
    }

    public async Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeGuidAsync(connectedSystemId, connectedSystemObjectTypeId);
    }
    
    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, id);
    }

    /// <summary>
    /// Loads a Connected System Object with attribute loading controlled by the specified strategy.
    /// <see cref="CsoAttributeLoadStrategy.CappedMva"/> caps MVA values and includes per-attribute total counts.
    /// </summary>
    public async Task<CsoDetailResult?> GetConnectedSystemObjectDetailAsync(
        int connectedSystemId,
        Guid id,
        CsoAttributeLoadStrategy loadStrategy)
    {
        using var span = Diagnostics.Diagnostics.Database.StartSpan("Cso.GetDetail")
            .SetTag("connectedSystemId", connectedSystemId)
            .SetTag("id", id)
            .SetTag("strategy", loadStrategy.ToString());
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectDetailAsync(connectedSystemId, id, loadStrategy);
    }

    /// <summary>
    /// Returns a paginated set of attribute values for a specific attribute on a Connected System Object.
    /// Supports server-side search and pagination for large multi-valued attributes.
    /// </summary>
    public async Task<PagedResultSet<ConnectedSystemObjectAttributeValue>> GetAttributeValuesPagedAsync(
        Guid connectedSystemObjectId,
        string attributeName,
        int page,
        int pageSize,
        string? searchText = null)
    {
        return await Application.Repository.ConnectedSystems.GetAttributeValuesPagedAsync(
            connectedSystemObjectId, attributeName, page, pageSize, searchText);
    }

    public async Task<PagedResultSet<ConnectedSystemObjectHeader>> GetConnectedSystemObjectHeadersAsync(
        int connectedSystemId,
        int page = 1,
        int pageSize = 20,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        IEnumerable<ConnectedSystemObjectStatus>? statusFilter = null,
        IEnumerable<int>? objectTypeFilter = null,
        IEnumerable<ConnectedSystemObjectJoinType>? joinTypeFilter = null)
    {
        using var span = Diagnostics.Diagnostics.Database.StartSpan("Cso.GetHeaders")
            .SetTag("connectedSystemId", connectedSystemId)
            .SetTag("page", page)
            .SetTag("pageSize", pageSize)
            .SetTag("hasSearch", !string.IsNullOrWhiteSpace(searchQuery))
            .SetTag("sortBy", sortBy ?? "default")
            .SetTag("hasStatusFilter", statusFilter != null)
            .SetTag("hasObjectTypeFilter", objectTypeFilter != null)
            .SetTag("hasJoinTypeFilter", joinTypeFilter != null);
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(
            connectedSystemId, page, pageSize, searchQuery, sortBy, sortDescending, statusFilter,
            objectTypeFilter, joinTypeFilter);
    }
    
    /// <summary>
    /// Retrieves a page's worth of Connected System Objects for a specific system.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="page">Which page to return results for, i.e. 1-n.</param>
    /// <param name="pageSize">How many Connected System Objects to return in this page of result. By default it's 100.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public async Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(int connectedSystemId, int page = 1, int pageSize = 100)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsAsync(connectedSystemId, page, pageSize);
    }

    /// <summary>
    /// Batch loads Connected System Objects by their IDs with navigation properties needed
    /// for cross-page reference resolution during sync.
    /// </summary>
    public async Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsForReferenceResolutionAsync(IList<Guid> csoIds)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsForReferenceResolutionAsync(csoIds);
    }

    /// <summary>
    /// Returns a dictionary mapping ReferenceValueId (the referenced CSO's ID) to its external ID string
    /// for all reference attribute values on the given CSO. Uses direct SQL to bypass EF's AsSplitQuery()
    /// materialisation issues.
    /// </summary>
    public async Task<Dictionary<Guid, string>> GetReferenceExternalIdsAsync(Guid csoId)
    {
        return await Application.Repository.ConnectedSystems.GetReferenceExternalIdsAsync(csoId);
    }

    /// <summary>
    /// Returns all the CSOs for a Connected System that are marked as Obsolete.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="returnAttributes">Controls whether ConnectedSystemObject.AttributeValues[n].Attribute is populated. By default, it isn't for performance reasons.</param>
    public async Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsObsoleteAsync(int connectedSystemId, bool returnAttributes)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsObsoleteAsync(connectedSystemId, returnAttributes);
    }
    
    /// <summary>
    /// Returns all the CSOs for a Connected System that are not joined to Metaverse Objects.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="returnAttributes">Controls whether ConnectedSystemObject.AttributeValues[n].Attribute is populated. By default, it isn't for performance reasons.</param>
    public async Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsUnJoinedAsync(int connectedSystemId, bool returnAttributes)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsUnJoinedAsync(connectedSystemId, returnAttributes);
    }

    /// <summary>
    /// Retrieves a page's worth of Connected System Objects for a specific system that have been modified since a given timestamp.
    /// Used for delta synchronisation to process only changed objects.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="modifiedSince">Only return CSOs where LastUpdated is greater than this timestamp.</param>
    /// <param name="page">Which page to return results for, i.e. 1-n.</param>
    /// <param name="pageSize">How many Connected System Objects to return in this page of result.</param>
    public async Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsModifiedSinceAsync(
        int connectedSystemId,
        DateTime modifiedSince,
        int page,
        int pageSize)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, modifiedSince, page, pageSize);
    }

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System that have been modified since a given timestamp.
    /// Used for delta synchronisation statistics.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="modifiedSince">Only count CSOs where LastUpdated is greater than this timestamp.</param>
    public async Task<int> GetConnectedSystemObjectModifiedSinceCountAsync(int connectedSystemId, DateTime modifiedSince)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, modifiedSince);
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue)
    {
        var cacheKey = BuildCsoCacheKey(connectedSystemId, connectedSystemAttributeId, attributeValue.ToLowerInvariant());
        return await GetCsoWithCacheLookupAsync(connectedSystemId, cacheKey, () =>
            Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue));
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, int attributeValue)
    {
        var cacheKey = BuildCsoCacheKey(connectedSystemId, connectedSystemAttributeId, attributeValue.ToString());
        return await GetCsoWithCacheLookupAsync(connectedSystemId, cacheKey, () =>
            Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue));
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, long attributeValue)
    {
        var cacheKey = BuildCsoCacheKey(connectedSystemId, connectedSystemAttributeId, attributeValue.ToString());
        return await GetCsoWithCacheLookupAsync(connectedSystemId, cacheKey, () =>
            Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue));
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, Guid attributeValue)
    {
        var cacheKey = BuildCsoCacheKey(connectedSystemId, connectedSystemAttributeId, attributeValue.ToString().ToLowerInvariant());
        return await GetCsoWithCacheLookupAsync(connectedSystemId, cacheKey, () =>
            Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue));
    }

    #region CSO Lookup Cache

    /// <summary>
    /// Builds the cache key for a CSO external ID lookup.
    /// Format: "cso:{connectedSystemId}:{attributeId}:{lowerExternalIdValue}"
    /// </summary>
    public static string BuildCsoCacheKey(int connectedSystemId, int attributeId, string externalIdValue)
    {
        return $"cso:{connectedSystemId}:{attributeId}:{externalIdValue}";
    }

    /// <summary>
    /// Looks up a CSO using the cache index. On cache hit, loads the entity by PK.
    /// On cache miss, falls back to the provided DB query and populates the cache.
    /// </summary>
    private async Task<ConnectedSystemObject?> GetCsoWithCacheLookupAsync(int connectedSystemId, string cacheKey, Func<Task<ConnectedSystemObject?>> dbFallback)
    {
        var cache = Application.Cache;
        if (cache == null)
        {
            // No cache available (e.g., JIM.Web) — fall back to direct DB query
            return await dbFallback();
        }

        // Check cache for CSO GUID
        if (cache.TryGetValue(cacheKey, out Guid cachedCsoId))
        {
            Log.Verbose("GetCsoWithCacheLookupAsync: Cache hit for key '{CacheKey}' → CSO {CsoId}", cacheKey, cachedCsoId);

            // Cache hit — load entity by PK (fast indexed lookup)
            var cso = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, cachedCsoId);
            if (cso != null)
                return cso;

            // CSO was deleted since cached — evict stale entry and fall through to DB query
            cache.Remove(cacheKey);
            Log.Debug("GetCsoWithCacheLookupAsync: Cache hit for key '{CacheKey}' but CSO {CsoId} no longer exists. Evicted stale entry.", cacheKey, cachedCsoId);
        }
        else
        {
            Log.Verbose("GetCsoWithCacheLookupAsync: Cache miss for key '{CacheKey}'. Falling back to DB query.", cacheKey);
        }

        // Cache miss — query DB by attribute value
        var result = await dbFallback();
        if (result != null)
        {
            // Populate cache with the result
            cache.Set(cacheKey, result.Id);
            Log.Verbose("GetCsoWithCacheLookupAsync: Auto-populated cache for key '{CacheKey}' → CSO {CsoId}", cacheKey, result.Id);
        }

        return result;
    }

    /// <summary>
    /// Warms the CSO lookup cache for a Connected System by bulk-loading all external ID → GUID mappings.
    /// Should be called at Worker startup for each Connected System.
    /// </summary>
    public async Task WarmCsoCacheAsync(int connectedSystemId, string? connectedSystemName = null)
    {
        var cache = Application.Cache;
        if (cache == null) return;

        var csLabel = connectedSystemName != null
            ? $"{connectedSystemName} ({connectedSystemId})"
            : connectedSystemId.ToString();

        Log.Debug("WarmCsoCacheAsync: Starting cache warm for Connected System {ConnectedSystem}", csLabel);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var mappings = await Application.Repository.ConnectedSystems.GetAllCsoExternalIdMappingsAsync(connectedSystemId);

        var total = mappings.Count;
        var processed = 0;
        var lastReportedPercentage = 0;

        foreach (var mapping in mappings)
        {
            cache.Set(mapping.Key, mapping.Value);
            processed++;

            // Report progress at every 10% increment
            if (total > 0)
            {
                var percentage = processed * 100 / total;
                if (percentage >= lastReportedPercentage + 10)
                {
                    lastReportedPercentage = percentage / 10 * 10; // Round down to nearest 10
                    Log.Verbose("WarmCsoCacheAsync: Connected System {ConnectedSystem} cache warm progress: {Percentage}% ({Processed}/{Total})",
                        csLabel, lastReportedPercentage, processed, total);
                }
            }
        }

        stopwatch.Stop();
        Log.Debug("WarmCsoCacheAsync: Completed cache warm for Connected System {ConnectedSystem}. Loaded {Count} mappings in {ElapsedMs}ms",
            csLabel, total, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Adds a CSO to the lookup cache after it has been created and persisted.
    /// </summary>
    public void AddCsoToCache(int connectedSystemId, int attributeId, string externalIdValue, Guid csoId)
    {
        var cache = Application.Cache;
        if (cache == null) return;

        var cacheKey = BuildCsoCacheKey(connectedSystemId, attributeId, externalIdValue.ToLowerInvariant());
        cache.Set(cacheKey, csoId);
        Log.Verbose("AddCsoToCache: Added cache entry '{CacheKey}' → CSO {CsoId}", cacheKey, csoId);
    }

    /// <summary>
    /// Evicts a CSO from the lookup cache when it has been deleted.
    /// </summary>
    public void EvictCsoFromCache(int connectedSystemId, int attributeId, string externalIdValue)
    {
        var cache = Application.Cache;
        if (cache == null) return;

        var cacheKey = BuildCsoCacheKey(connectedSystemId, attributeId, externalIdValue.ToLowerInvariant());
        cache.Remove(cacheKey);
        Log.Verbose("EvictCsoFromCache: Evicted cache entry '{CacheKey}'", cacheKey);
    }

    #endregion

    /// <summary>
    /// Gets a Connected System Object by its secondary external ID attribute value.
    /// Used to find PendingProvisioning CSOs during import reconciliation.
    /// Routes through the CSO lookup cache using the secondary external ID attribute ID as the cache key component.
    /// </summary>
    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(int connectedSystemId, int objectTypeId, string secondaryExternalIdValue, int? secondaryExternalIdAttributeId = null)
    {
        // If we have the attribute ID, route through the cache for O(1) lookup
        if (secondaryExternalIdAttributeId.HasValue)
        {
            var cacheKey = BuildCsoCacheKey(connectedSystemId, secondaryExternalIdAttributeId.Value, secondaryExternalIdValue.ToLowerInvariant());
            return await GetCsoWithCacheLookupAsync(connectedSystemId, cacheKey, () =>
                Application.Repository.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAsync(connectedSystemId, objectTypeId, secondaryExternalIdValue));
        }

        // No attribute ID available — fall back to direct DB query
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAsync(connectedSystemId, objectTypeId, secondaryExternalIdValue);
    }

    /// <summary>
    /// Gets a Connected System Object by its secondary external ID attribute value across ALL object types.
    /// This is used for reference resolution where the referenced object can be of any type.
    /// </summary>
    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(int connectedSystemId, string secondaryExternalIdValue)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(connectedSystemId, secondaryExternalIdValue);
    }

    /// <summary>
    /// Batch loads Connected System Objects by multiple primary external ID string values.
    /// Used for reference resolution to eliminate N+1 individual lookups.
    /// </summary>
    public async Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsByAttributeValuesAsync(int connectedSystemId, int attributeId, IEnumerable<string> attributeValues)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsByAttributeValuesAsync(connectedSystemId, attributeId, attributeValues);
    }

    /// <summary>
    /// Batch loads Connected System Objects by multiple secondary external ID string values across ALL object types.
    /// Used for reference resolution where referenced objects can be of any type.
    /// </summary>
    public async Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(int connectedSystemId, IEnumerable<string> secondaryExternalIdValues)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(connectedSystemId, secondaryExternalIdValues);
    }

    public async Task<Guid?> GetConnectedSystemObjectIdByAttributeValueAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectIdByAttributeValueAsync(connectedSystemId , connectedSystemAttributeId, attributeValue);
    }

    /// <summary>
    /// Returns the count of all Connected System Objects across all Connected Systems.
    /// </summary>
    public async Task<int> GetConnectedSystemObjectCountAsync()
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync();
    }

    /// <summary>
    /// Streams every Connected System Object in a Connected System, reduced to where it sits and what it is joined
    /// to, for evaluating what a change to the partition and container selection would take out of import scope
    /// (#1251).
    /// </summary>
    public IAsyncEnumerable<ConnectedSystemObjectScopeCandidate> StreamConnectedSystemObjectScopeCandidates(int connectedSystemId)
    {
        return Application.Repository.ConnectedSystems.StreamConnectedSystemObjectScopeCandidates(connectedSystemId);
    }

    /// <summary>
    /// The Connector's containment rule, for a Connected System whose Connector can express one; null otherwise.
    /// </summary>
    /// <remarks>
    /// Creating the Connector opens no connection to the Connected System, which matters here: a preview asks where
    /// objects sit using data JIM already holds, and must not reach out to a directory that may be unreachable to
    /// answer a question about a tick box.
    /// </remarks>
    public IConnectorContainment? GetConnectorContainment(ConnectedSystem connectedSystem)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        if (connectedSystem.ConnectorDefinition == null)
            return null;

        return CreateConnector(connectedSystem) as IConnectorContainment;
    }

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System, where the status is Obosolete.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the Obosolete object count for.</param>
    public async Task<int> GetConnectedSystemObjectObsoleteCountAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectObsoleteCountAsync(connectedSystemId);
    }

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System, that are not joined to a Metaverse Object.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the unjoined object count for.</param>
    public async Task<int> GetConnectedSystemObjectUnJoinedCountAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectUnJoinedCountAsync(connectedSystemId);
    }

    /// <summary>
    /// Returns the count of CSOs in a Connected System that are joined to a specific MVO.
    /// Used during sync to check if an MVO already has a join in this Connected System (1:1 constraint).
    /// </summary>
    public async Task<int> GetConnectedSystemObjectCountByMvoAsync(int connectedSystemId, Guid metaverseObjectId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountByMvoAsync(connectedSystemId, metaverseObjectId);
    }

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the object count for.</param>s
    public async Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync(connectedSystemId);
    }

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System, optionally filtered by Object Type and/or Partition.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the object count for.</param>
    /// <param name="objectTypeId">Optional Object Type ID to filter by.</param>
    /// <param name="partitionId">Optional Partition ID to filter by.</param>
    public async Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId, int? objectTypeId, int? partitionId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync(connectedSystemId, objectTypeId, partitionId);
    }

    /// <summary>
    /// Returns the count of reference attribute values across all CSOs in a Connected System that are unresolved
    /// (i.e. the referenced object could not be found during the last import run).
    /// A non-zero result indicates that group member references or other reference attributes are broken.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    public async Task<int> GetUnresolvedReferenceCountAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetUnresolvedReferenceCountAsync(connectedSystemId);
    }

    /// <summary>
    /// Bulk persists Connected System Objects without activity tracking.
    /// Use this for provisioning CSOs created during sync where activity execution items are not needed.
    /// </summary>
    public async Task CreateConnectedSystemObjectsAsync(IEnumerable<ConnectedSystemObject> connectedSystemObjects)
    {
        var csoList = connectedSystemObjects.ToList();
        if (csoList.Count == 0)
            return;

        await Application.Repository.ConnectedSystems.CreateConnectedSystemObjectsAsync(csoList);
    }

    /// <summary>
    /// Bulk persists Connected System Objects and appends a Change Object to the Activity Run Profile Execution Item.
    /// </summary>
    public async Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, Activity activity)
    {
        await CreateConnectedSystemObjectsAsync(connectedSystemObjects, activity.RunProfileExecutionItems);
    }
    
    /// <summary>
    /// Bulk persists Connected System Objects and appends a Change Object to the Activity Run Profile Execution Item.
    /// </summary>
    public async Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, List<ActivityRunProfileExecutionItem> activityRunProfileExecutionItems, Func<int, Task>? onBatchPersisted = null)
    {
        // bulk persist csos creates
        await Application.Repository.ConnectedSystems.CreateConnectedSystemObjectsAsync(connectedSystemObjects, onBatchPersisted);

        // Check if CSO change tracking is enabled
        var changeTrackingEnabled = await Application.ServiceSettings.GetCsoChangeTrackingEnabledAsync();

        // Build O(1) lookup by CSO ID to avoid O(n²) linear scan at scale.
        // At 100K CSOs, the previous SingleOrDefault scan caused 10 billion comparisons.
        var rpeisByCsoId = new Dictionary<Guid, ActivityRunProfileExecutionItem>(activityRunProfileExecutionItems.Count);
        foreach (var rpei in activityRunProfileExecutionItems)
        {
            if (rpei.ConnectedSystemObject != null)
                rpeisByCsoId.TryAdd(rpei.ConnectedSystemObject.Id, rpei);
        }

        // Add a Change Object to the relevant Activity Run Profile Execution Item for each CSO.
        // They will be persisted further up the call stack, when the activity gets persisted.
        foreach (var cso in connectedSystemObjects)
        {
            if (!rpeisByCsoId.TryGetValue(cso.Id, out var activityRunProfileExecutionItem))
                throw new InvalidDataException($"Couldn't find an ActivityRunProfileExecutionItem referencing CSO {cso.Id}! It should have been created before now.");

            // Explicitly set the FK now that the CSO has been persisted and has an ID.
            // This ensures the FK is properly tracked when the execution item is saved later.
            activityRunProfileExecutionItem.ConnectedSystemObjectId = cso.Id;

            AddConnectedSystemObjectChange(cso, activityRunProfileExecutionItem, changeTrackingEnabled);
        }
    }
    
    /// <summary>
    /// Bulk persists Connected System Object updates and appends a Change Object to the Activity Run Profile Execution Item for each one.
    /// </summary>
    public async Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, Activity activity)
    {
        await UpdateConnectedSystemObjectsAsync(connectedSystemObjects, activity.RunProfileExecutionItems);
    }
    
    /// <summary>
    /// Bulk persists Connected System Object updates and appends a Change Object to the Activity Run Profile Execution Item for each one.
    /// CSOs without a corresponding RPEI (e.g., no attribute changes occurred) are still persisted but without change tracking.
    /// </summary>
    public async Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, List<ActivityRunProfileExecutionItem> activityRunProfileExecutionItems)
    {
        // Check if CSO change tracking is enabled
        var changeTrackingEnabled = await Application.ServiceSettings.GetCsoChangeTrackingEnabledAsync();

        // Build O(1) lookup by CSO ID to avoid O(n²) linear scan at scale.
        var rpeisByCsoId = new Dictionary<Guid, ActivityRunProfileExecutionItem>(activityRunProfileExecutionItems.Count);
        foreach (var rpei in activityRunProfileExecutionItems)
        {
            if (rpei.ConnectedSystemObject != null)
                rpeisByCsoId.TryAdd(rpei.ConnectedSystemObject.Id, rpei);
        }

        // Add a change object to the relevant activity Run Profile execution item for each CSO to be updated.
        // The change objects will be persisted later, further up the call stack, when the activity gets persisted.
        foreach (var cso in connectedSystemObjects)
        {
            // Find the RPEI for this CSO - may be null if no attribute changes occurred (CSO was added to update list
            // for reference resolution purposes only)
            rpeisByCsoId.TryGetValue(cso.Id, out var activityRunProfileExecutionItem);

            if (activityRunProfileExecutionItem != null)
            {
                // Explicitly set the FK to ensure it's properly tracked when the execution item is saved.
                activityRunProfileExecutionItem.ConnectedSystemObjectId = cso.Id;

                ProcessConnectedSystemObjectAttributeValueChanges(cso, activityRunProfileExecutionItem, changeTrackingEnabled);
            }
            // If no RPEI exists, CSO was added to update list for reference resolution but had no changes - skip change tracking
        }

        // bulk persist csos updates
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemObjectsAsync(connectedSystemObjects);
    }

    /// <summary>
    /// Links RPEI change records to CSOs after creation. Pure business logic — no data access.
    /// Called by SyncServer after persisting CSOs via ISyncRepository.
    /// </summary>
    public void LinkCreateChangeRecords(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis,
        bool changeTrackingEnabled)
    {
        var rpeisByCsoId = new Dictionary<Guid, ActivityRunProfileExecutionItem>(rpeis.Count);
        foreach (var rpei in rpeis)
        {
            if (rpei.ConnectedSystemObject != null)
                rpeisByCsoId.TryAdd(rpei.ConnectedSystemObject.Id, rpei);
        }

        foreach (var cso in connectedSystemObjects)
        {
            if (!rpeisByCsoId.TryGetValue(cso.Id, out var rpei))
                throw new InvalidDataException($"Couldn't find an ActivityRunProfileExecutionItem referencing CSO {cso.Id}! It should have been created before now.");

            rpei.ConnectedSystemObjectId = cso.Id;
            AddConnectedSystemObjectChange(cso, rpei, changeTrackingEnabled);
        }
    }

    /// <summary>
    /// Creates an "Added" change record for a single CSO linked to a specific RPEI.
    /// Used for provisioning CSOs where the RPEI-to-CSO relationship is resolved externally
    /// (e.g., via MVO ID lookup) rather than via <c>rpei.ConnectedSystemObject</c>.
    /// The RPEI's ConnectedSystemObjectId is intentionally NOT overwritten here; it must
    /// continue to reference the source CSO that triggered the sync, not the provisioning CSO.
    /// The CSO change record links to the provisioning CSO via its own ConnectedSystemObjectId.
    /// </summary>
    public void CreateChangeRecordForCso(
        ConnectedSystemObject cso,
        ActivityRunProfileExecutionItem rpei,
        bool changeTrackingEnabled)
    {
        AddConnectedSystemObjectChange(cso, rpei, changeTrackingEnabled);
    }

    /// <summary>
    /// Links RPEI change records to CSOs before update. Pure business logic — no data access.
    /// Called by SyncServer before persisting CSO updates via ISyncRepository.
    /// </summary>
    public void LinkUpdateChangeRecords(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis,
        bool changeTrackingEnabled)
    {
        var rpeisByCsoId = new Dictionary<Guid, ActivityRunProfileExecutionItem>(rpeis.Count);
        foreach (var rpei in rpeis)
        {
            if (rpei.ConnectedSystemObject != null)
                rpeisByCsoId.TryAdd(rpei.ConnectedSystemObject.Id, rpei);
        }

        foreach (var cso in connectedSystemObjects)
        {
            rpeisByCsoId.TryGetValue(cso.Id, out var rpei);
            if (rpei != null)
            {
                rpei.ConnectedSystemObjectId = cso.Id;
                ProcessConnectedSystemObjectAttributeValueChanges(cso, rpei, changeTrackingEnabled);
            }
        }
    }

    /// <summary>
    /// Links RPEI change records to CSOs before deletion. Pure business logic — no data access.
    /// Captures final attribute snapshots for audit trail before the CSOs are deleted.
    /// Called by SyncServer before deleting CSOs via ISyncRepository.
    /// </summary>
    public void LinkDeleteChangeRecords(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis,
        bool changeTrackingEnabled)
    {
        if (connectedSystemObjects.Count != rpeis.Count)
            throw new ArgumentException("CSO count must match execution item count");

        // Name only, never NameOrId: the external id is captured separately alongside it.
        var deletedObjectInfo = connectedSystemObjects
            .Select(cso => (
                ExternalId: cso.ExternalIdAttributeValue?.ToStringNoName(),
                DisplayName: cso.Name,
                FinalAttributeValues: cso.AttributeValues
                    .Where(av => av.Attribute != null && av.Attribute.Type != AttributeDataType.NotSet)
                    .ToList()))
            .ToList();

        if (changeTrackingEnabled)
        {
            for (int i = 0; i < connectedSystemObjects.Count; i++)
            {
                var cso = connectedSystemObjects[i];
                var executionItem = rpeis[i];
                var (externalId, displayName, finalAttributeValues) = deletedObjectInfo[i];

                var change = new ConnectedSystemObjectChange
                {
                    ConnectedSystemId = cso.ConnectedSystemId,
                    ChangeType = ObjectChangeType.Deleted,
                    ChangeTime = DateTime.UtcNow,
                    DeletedObjectType = cso.Type,
                    DeletedObjectExternalId = externalId,
                    DeletedObjectDisplayName = displayName,
                    DeletedConnectedSystemObjectId = cso.Id,
                    ActivityRunProfileExecutionItem = executionItem,
                    InitiatedByType = executionItem.Activity?.InitiatedByType ?? ActivityInitiatorType.NotSet,
                    InitiatedById = executionItem.Activity?.InitiatedById,
                    InitiatedByName = executionItem.Activity?.InitiatedByName
                };
                executionItem.ConnectedSystemObjectChange = change;

                foreach (var av in finalAttributeValues)
                    AddChangeAttributeValueObject(change, av, ValueChangeType.Remove);
            }
        }

        // Null out the CSO FK on all RPEIs. The CSOs are about to be deleted from the database,
        // so when BulkInsertRpeisAsync runs later, the FK must be null to avoid FK constraint violations.
        // The ExternalIdSnapshot (set earlier by SnapshotCsoDisplayFields) preserves the CSO identity for display.
        for (int i = 0; i < rpeis.Count; i++)
        {
            rpeis[i].ConnectedSystemObject = null;
            rpeis[i].ConnectedSystemObjectId = null;
        }
    }

    /// <summary>
    /// Batch updates only the join-related columns (JoinType, DateJoined, MetaverseObjectId) on
    /// Connected System Objects. Used during sync page flush where AutoDetectChangesEnabled is
    /// disabled and EF cannot detect CSO scalar property changes automatically.
    /// </summary>
    public async Task UpdateConnectedSystemObjectJoinStatesAsync(List<ConnectedSystemObject> connectedSystemObjects)
    {
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemObjectJoinStatesAsync(connectedSystemObjects);
    }

    /// <summary>
    /// Adds a Change Object to a Run Profile Execution Item for a CSO that's being created.
    /// </summary>
    private static void AddConnectedSystemObjectChange(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem, bool changeTrackingEnabled)
    {
        if (!changeTrackingEnabled)
            return;

        // now populate the Connected System Object Change Object with the cso attribute values.
        // create a change object we can add attribute changes to.
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemObject.ConnectedSystemId,
            ConnectedSystemObjectId = connectedSystemObject.Id,
            ConnectedSystemObject = connectedSystemObject,
            ChangeType = ObjectChangeType.Added,
            ChangeTime = DateTime.UtcNow,
            ActivityRunProfileExecutionItem = activityRunProfileExecutionItem,
            ActivityRunProfileExecutionItemId = activityRunProfileExecutionItem.Id,
            // Copy initiator info from the Activity for audit trail (if Activity is loaded)
            InitiatedByType = activityRunProfileExecutionItem.Activity?.InitiatedByType ?? ActivityInitiatorType.NotSet,
            InitiatedById = activityRunProfileExecutionItem.Activity?.InitiatedById,
            InitiatedByName = activityRunProfileExecutionItem.Activity?.InitiatedByName,
            // Store external ID as string to enable linking change history even after CSO deletion
            // Use ToStringNoName() to match the format used in deletion changes
            DeletedObjectExternalId = connectedSystemObject.ExternalIdAttributeValue?.ToStringNoName()
        };
        activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;

        foreach (var attributeValue in connectedSystemObject.AttributeValues)
        {
            // Skip attribute values without a loaded Attribute navigation property.
            // This occurs for provisioning CSOs where attribute values have AttributeId set
            // but the navigation property is not loaded (e.g., after bulk persistence with AsNoTracking).
            if (attributeValue.Attribute == null)
                continue;

            AddChangeAttributeValueObject(change, attributeValue, ValueChangeType.Add);
        }
    }

    /// <summary>
    /// Adds a Change object to the Run Profile Execution Item for a CSO that's being updated.
    /// </summary>
    private static void ProcessConnectedSystemObjectAttributeValueChanges(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem, bool changeTrackingEnabled)
    {
        if (connectedSystemObject == null)
            throw new ArgumentNullException(nameof(connectedSystemObject));

        if (connectedSystemObject.AttributeValues.Any(v => v.Attribute == null))
            throw new ArgumentException($"One or more AttributeValue {nameof(ConnectedSystemObjectAttributeValue)} objects do not have an Attribute property set.", nameof(connectedSystemObject));

        if (connectedSystemObject.AttributeValues.Any(v => v.ConnectedSystemObject == null))
            throw new ArgumentException($"One or more AttributeValue {nameof(ConnectedSystemObjectAttributeValue)} objects do not have a ConnectedSystemObject property set.", nameof(connectedSystemObject));

        // check if there's any work to do. we need something in the pending attribute value additions, or removals to continue
        if (connectedSystemObject.PendingAttributeValueAdditions.Count == 0 && connectedSystemObject.PendingAttributeValueRemovals.Count == 0)
        {
            Log.Verbose($"UpdateConnectedSystemObjectAttributeValuesAsync: No work to do. No pending attribute value changes for CSO: {connectedSystemObject.Id}");
            return;
        }

        // make sure the CSO is linked to the activity Run Profile execution item
        activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
        activityRunProfileExecutionItem.ConnectedSystemObjectId = connectedSystemObject.Id;

        // persist new attribute values from addition list and create change object (if enabled)
        foreach (var pendingAttributeValueAddition in connectedSystemObject.PendingAttributeValueAdditions)
        {
            connectedSystemObject.AttributeValues.Add(pendingAttributeValueAddition);
        }

        // delete attribute values to be removed and create change (if enabled)
        foreach (var pendingAttributeValueRemoval in connectedSystemObject.PendingAttributeValueRemovals)
        {
            // Use reference equality when Id is Guid.Empty (newly created, not yet persisted).
            // With EF Core, attribute values get DB-generated IDs on SaveChanges. In InMemoryData
            // or before persistence, they remain Guid.Empty — matching by ID would incorrectly
            // remove ALL unassigned attribute values, including ones just added above.
            if (pendingAttributeValueRemoval.Id == Guid.Empty)
                connectedSystemObject.AttributeValues.Remove(pendingAttributeValueRemoval);
            else
                connectedSystemObject.AttributeValues.RemoveAll(av => av.Id == pendingAttributeValueRemoval.Id);
        }

        // Only create change object if tracking is enabled
        if (changeTrackingEnabled)
        {
            // create a change object we can track attribute changes with
            var change = new ConnectedSystemObjectChange
            {
                ConnectedSystemId = connectedSystemObject.ConnectedSystemId,
                ConnectedSystemObjectId = connectedSystemObject.Id,
                ConnectedSystemObject = connectedSystemObject,
                ChangeType = ObjectChangeType.Updated,
                ChangeTime = DateTime.UtcNow,
                ActivityRunProfileExecutionItem = activityRunProfileExecutionItem,
                // Copy initiator info from the Activity for audit trail (if Activity is loaded)
                InitiatedByType = activityRunProfileExecutionItem.Activity?.InitiatedByType ?? ActivityInitiatorType.NotSet,
                InitiatedById = activityRunProfileExecutionItem.Activity?.InitiatedById,
                InitiatedByName = activityRunProfileExecutionItem.Activity?.InitiatedByName,
                // Store external ID as string to enable linking change history even after CSO deletion
                // Use ToStringNoName() to match the format used in deletion changes
                DeletedObjectExternalId = connectedSystemObject.ExternalIdAttributeValue?.ToStringNoName()
            };

            // the change object will be persisted with the activity Run Profile execution item further up the stack.
            // we just need to associate the change with the detail item.
            activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;

            // Record attribute additions
            foreach (var pendingAttributeValueAddition in connectedSystemObject.PendingAttributeValueAdditions)
            {
                AddChangeAttributeValueObject(change, pendingAttributeValueAddition, ValueChangeType.Add);
            }

            // Record attribute removals
            foreach (var pendingAttributeValueRemoval in connectedSystemObject.PendingAttributeValueRemovals)
            {
                AddChangeAttributeValueObject(change, pendingAttributeValueRemoval, ValueChangeType.Remove);
            }
        }
        
        // we can now reset the pending attribute value lists
        connectedSystemObject.PendingAttributeValueAdditions = new List<ConnectedSystemObjectAttributeValue>();
        connectedSystemObject.PendingAttributeValueRemovals = new List<ConnectedSystemObjectAttributeValue>();
    }

    /// <summary>
    /// Causes all the Connected System Objects and their dependencies to be deleted for a Connected System.
    /// This includes: Pending Exports, CSO attribute values, change history, and disconnects CSOs from MVOs.
    /// Once performed, an admin must then re-synchronise all connectors to re-calculate any metaverse and Connected System Object changes.
    /// </summary>
    /// <remarks>
    /// Only intended to be called by JIM.Service, i.e. this action should always be queued.
    /// That's why this method is lightweight and doesn't create its own activity.
    /// Uses the shared DeleteAllConnectedSystemObjectsAndDependenciesAsync method with deleteChangeHistory=true
    /// to remove all CSO-related data including change history (since objects will be re-imported).
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to clear.</param>
    /// <param name="deleteChangeHistory">Whether to delete change history for the cleared CSOs. Default: true (recommended for re-import scenarios).</param>
    /// <exception cref="InvalidOperationException">Thrown if the Connected System is being deleted.</exception>
    public async Task<ClearConnectedSystemResult> ClearConnectedSystemObjectsAsync(int connectedSystemId, bool deleteChangeHistory = true)
    {
        Log.Information("ClearConnectedSystemObjectsAsync: Starting for Connected System {Id}, deleteChangeHistory={DeleteHistory}",
            connectedSystemId, deleteChangeHistory);

        // Check for concurrency — don't clear if the system is being deleted. We only need the
        // Status scalar here, so use the lightweight Core retrieval variant.
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
        {
            Log.Warning("ClearConnectedSystemObjectsAsync: Connected System {Id} not found", connectedSystemId);
            throw new InvalidOperationException($"Connected System {connectedSystemId} not found.");
        }

        if (connectedSystem.Status == ConnectedSystemStatus.Deleting)
        {
            Log.Warning("ClearConnectedSystemObjectsAsync: Connected System {Id} is being deleted, cannot clear", connectedSystemId);
            throw new InvalidOperationException($"Connected System {connectedSystemId} is being deleted and cannot be cleared.");
        }

        // Use the shared method that handles all CSO dependencies properly.
        var result = await Application.Repository.ConnectedSystems.DeleteAllConnectedSystemObjectsAndDependenciesAsync(connectedSystemId, deleteChangeHistory);

        Log.Information("ClearConnectedSystemObjectsAsync: Completed for Connected System {Id}. Removed {PendingExports} Pending Exports, {Csos} CSOs",
            connectedSystemId, result.PendingExportsRemoved, result.ConnectedSystemObjectsRemoved);

        return result;
    }
        
    /// <summary>
    /// Creates the necessary attribute change audit item for when a CSO is created, updated, or deleted, and adds it to the change object.
    /// </summary>
    /// <param name="connectedSystemObjectChange">The ConnectedSystemObjectChange that's associated with a ActivityRunProfileExecutionItem (the audit object for a sync run).</param>
    /// <param name="connectedSystemObjectAttributeValue">The attribute and value pair for the new value.</param>
    /// <param name="valueChangeType">The type of change, i.e. CREATE/UPDATE/DELETE.</param>
    private static void AddChangeAttributeValueObject(ConnectedSystemObjectChange connectedSystemObjectChange, ConnectedSystemObjectAttributeValue connectedSystemObjectAttributeValue, ValueChangeType valueChangeType)
    {
        var attributeChange = connectedSystemObjectChange.AttributeChanges.SingleOrDefault(ac => ac.Attribute!.Id == connectedSystemObjectAttributeValue.Attribute.Id);
        if (attributeChange == null)
        {
            // create the attribute change object that provides an audit trail of changes to a cso's attributes
            attributeChange = new ConnectedSystemObjectChangeAttribute
            {
                Attribute = connectedSystemObjectAttributeValue.Attribute,
                AttributeName = connectedSystemObjectAttributeValue.Attribute.Name,
                AttributeType = connectedSystemObjectAttributeValue.Attribute.Type,
                ConnectedSystemChange = connectedSystemObjectChange
            };
            connectedSystemObjectChange.AttributeChanges.Add(attributeChange);
        }

        switch (connectedSystemObjectAttributeValue.Attribute.Type)
        {
            case AttributeDataType.Text when connectedSystemObjectAttributeValue.StringValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, connectedSystemObjectAttributeValue.StringValue));
                break;
            case AttributeDataType.Number when connectedSystemObjectAttributeValue.IntValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, (int)connectedSystemObjectAttributeValue.IntValue));
                break;
            case AttributeDataType.LongNumber when connectedSystemObjectAttributeValue.LongValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, (long)connectedSystemObjectAttributeValue.LongValue));
                break;
            case AttributeDataType.Decimal when connectedSystemObjectAttributeValue.DecimalValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, connectedSystemObjectAttributeValue.DecimalValue.Value));
                break;
            case AttributeDataType.Guid when connectedSystemObjectAttributeValue.GuidValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, (Guid)connectedSystemObjectAttributeValue.GuidValue));
                break;
            case AttributeDataType.Boolean when connectedSystemObjectAttributeValue.BoolValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, (bool)connectedSystemObjectAttributeValue.BoolValue));
                break;
            case AttributeDataType.DateTime when connectedSystemObjectAttributeValue.DateTimeValue.HasValue:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, connectedSystemObjectAttributeValue.DateTimeValue.Value));
                break;
            case AttributeDataType.Binary when connectedSystemObjectAttributeValue.ByteValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, true, connectedSystemObjectAttributeValue.ByteValue.Length));
                break;
            case AttributeDataType.Reference when connectedSystemObjectAttributeValue.ReferenceValue != null && connectedSystemObjectAttributeValue.ReferenceValue.Id != Guid.Empty:
                // Reference resolved to a CSO with a known ID. Store the FK relationship for a clickable
                // link in the UI. Also preserve the DN/identifier in StringValue so that if the FK is
                // nulled out during bulk persistence (referenced CSO in a later batch hasn't been persisted
                // yet), the UI can still display meaningful text instead of "(identifier not recorded)".
                var changeValue = new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, connectedSystemObjectAttributeValue.ReferenceValue);
                if (!string.IsNullOrEmpty(connectedSystemObjectAttributeValue.UnresolvedReferenceValue))
                    changeValue.StringValue = connectedSystemObjectAttributeValue.UnresolvedReferenceValue;
                attributeChange.ValueChanges.Add(changeValue);
                break;
            case AttributeDataType.Reference when connectedSystemObjectAttributeValue.ReferenceValueId.HasValue && connectedSystemObjectAttributeValue.ReferenceValueId.Value != Guid.Empty:
                // ReferenceValueId is set but the navigation property was cleared (e.g., by EF Core
                // change tracking during bulk COPY persistence). Create a stub CSO with just the ID
                // so the change record can carry the FK through to persistence.
                var stubCso = new ConnectedSystemObject { Id = connectedSystemObjectAttributeValue.ReferenceValueId.Value };
                var changeValueFromFk = new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, stubCso);
                if (!string.IsNullOrEmpty(connectedSystemObjectAttributeValue.UnresolvedReferenceValue))
                    changeValueFromFk.StringValue = connectedSystemObjectAttributeValue.UnresolvedReferenceValue;
                attributeChange.ValueChanges.Add(changeValueFromFk);
                break;
            case AttributeDataType.Reference when connectedSystemObjectAttributeValue.UnresolvedReferenceValue != null:
                // Store the raw DN/identifier for display in the UI. The reference could not be resolved
                // to a CSO (referenced object may be out of container scope).
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, connectedSystemObjectAttributeValue.UnresolvedReferenceValue));
                break;
            case AttributeDataType.NotSet:
                // The attribute has no data type configured; we cannot record a typed value change for it.
                throw new InvalidDataException(
                    $"AddChangeAttributeValueObject: attribute '{connectedSystemObjectAttributeValue.Attribute.Name}' has no data type configured (NotSet); cannot record a Connected System Object change for it.");
            default:
                // Reached when a *known*, switch-handled data type's value holder was unexpectedly null (a corrupt
                // attribute value), or when the AttributeDataType enum has gained a member this switch does not yet
                // handle. The message distinguishes the two; the exception type is deliberately kept uniform
                // (InvalidDataException) because the deletion path's CaptureDeletedCsoAttributeValues catch filters
                // on InvalidOperationException/InvalidDataException to degrade gracefully.
                throw Enum.IsDefined(connectedSystemObjectAttributeValue.Attribute.Type)
                    ? new InvalidDataException(
                        $"AddChangeAttributeValueObject: attribute '{connectedSystemObjectAttributeValue.Attribute.Name}' of type '{connectedSystemObjectAttributeValue.Attribute.Type}' has no value; the Connected System Object attribute value is corrupt.")
                    : new InvalidDataException(
                        $"AddChangeAttributeValueObject: attribute '{connectedSystemObjectAttributeValue.Attribute.Name}' has an unhandled data type '{connectedSystemObjectAttributeValue.Attribute.Type}' for Connected System Object change tracking.");
        }
    }

    /// <summary>
    /// Captures all final attribute values from a deleted CSO as Remove records on the change.
    /// Handles EF Core entity tracking conflicts gracefully — the attribute schema entities
    /// (ConnectedSystemObjectTypeAttribute) may already be tracked by the change tracker,
    /// causing InvalidOperationException when the change record is associated with the context.
    /// In that case, we clear the attribute changes and preserve only the basic identity info.
    /// </summary>
    private static void CaptureDeletedCsoAttributeValues(
        ConnectedSystemObjectChange change,
        List<ConnectedSystemObjectAttributeValue> finalAttributeValues)
    {
        try
        {
            foreach (var attributeValue in finalAttributeValues)
            {
                AddChangeAttributeValueObject(change, attributeValue, ValueChangeType.Remove);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            // EF Core tracking conflict or invalid attribute type (e.g., NotSet).
            // Clear any partially-built attribute changes and fall back to basic identity info.
            Log.Warning(ex, "CaptureDeletedCsoAttributeValues: Could not capture final attribute values. Basic identity info preserved.");
            change.AttributeChanges.Clear();
        }
    }

    public async Task<bool> IsObjectTypeAttributeBeingReferencedAsync(ConnectedSystemObjectTypeAttribute connectedSystemObjectTypeAttribute)
    {
        return await Application.Repository.ConnectedSystems.IsObjectTypeAttributeBeingReferencedAsync(connectedSystemObjectTypeAttribute);
    }
    #endregion

    #region Connected System Partitions
    // Partition and container writes change the system's import scope, which is configuration: every mutation is
    // recorded with an Activity and a versioned snapshot via ExecuteScopeConfigurationChangeAsync. There are
    // deliberately no activity-less overloads, so any future caller inherits capture automatically.

    /// <summary>
    /// Creates a Connected System Partition, recording the change with an Activity and a versioned configuration
    /// snapshot of the owning Connected System.
    /// </summary>
    public async Task CreateConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition, int connectedSystemId, MetaverseObject? initiatedBy)
    {
        if (connectedSystemPartition == null)
            throw new ArgumentNullException(nameof(connectedSystemPartition));

        await ExecuteScopeConfigurationChangeAsync(
            connectedSystemId,
            $"Create partition: {connectedSystemPartition.Name}",
            () => Application.Repository.ConnectedSystems.CreateConnectedSystemPartitionAsync(connectedSystemPartition),
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy));
    }

    public async Task<IList<ConnectedSystemPartition>> GetConnectedSystemPartitionsAsync(ConnectedSystem connectedSystem)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        return await Application.Repository.ConnectedSystems.GetConnectedSystemPartitionsAsync(connectedSystem);
    }

    public async Task<ConnectedSystemPartition?> GetConnectedSystemPartitionAsync(int id, bool withChangeTracking = false)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemPartitionAsync(id, withChangeTracking);
    }

    /// <summary>
    /// Updates a Connected System Partition (e.g. its import-scope selection), recording the change with an Activity
    /// and a versioned configuration snapshot of the owning Connected System.
    /// </summary>
    public async Task UpdateConnectedSystemPartitionAsync(ConnectedSystemPartition partition, int connectedSystemId, MetaverseObject? initiatedBy)
    {
        if (partition == null)
            throw new ArgumentNullException(nameof(partition));

        await ExecuteScopeConfigurationChangeAsync(
            connectedSystemId,
            $"Update partition: {partition.Name}",
            () => Application.Repository.ConnectedSystems.UpdateConnectedSystemPartitionAsync(partition),
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Updates a Connected System Partition (initiated by API key), recording the change with an Activity and a
    /// versioned configuration snapshot of the owning Connected System.
    /// </summary>
    public async Task UpdateConnectedSystemPartitionAsync(ConnectedSystemPartition partition, int connectedSystemId, ApiKey initiatedByApiKey)
    {
        if (partition == null)
            throw new ArgumentNullException(nameof(partition));

        await ExecuteScopeConfigurationChangeAsync(
            connectedSystemId,
            $"Update partition: {partition.Name}",
            () => Application.Repository.ConnectedSystems.UpdateConnectedSystemPartitionAsync(partition),
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    /// <summary>
    /// Deletes a Connected System Partition, recording the change with an Activity and a versioned configuration
    /// snapshot of the owning Connected System.
    /// </summary>
    public async Task DeleteConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition, int connectedSystemId, MetaverseObject? initiatedBy)
    {
        if (connectedSystemPartition == null)
            throw new ArgumentNullException(nameof(connectedSystemPartition));

        await ExecuteScopeConfigurationChangeAsync(
            connectedSystemId,
            $"Delete partition: {connectedSystemPartition.Name}",
            () => Application.Repository.ConnectedSystems.DeleteConnectedSystemPartitionAsync(connectedSystemPartition),
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy));
    }
    #endregion

    #region Connected System Containers
    /// <summary>
    /// Used to create a top-level container (optionally with children), when the connector does not implement Partitions.
    /// If the connector implements Partitions, then use CreateConnectedSystemPartitionAsync and add the container to that.
    /// The change is recorded with an Activity and a versioned configuration snapshot of the owning Connected System.
    /// </summary>
    public async Task CreateConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer, int connectedSystemId, MetaverseObject? initiatedBy)
    {
        if (connectedSystemContainer == null)
            throw new ArgumentNullException(nameof(connectedSystemContainer));

        await ExecuteScopeConfigurationChangeAsync(
            connectedSystemId,
            $"Create container: {connectedSystemContainer.Name}",
            () => Application.Repository.ConnectedSystems.CreateConnectedSystemContainerAsync(connectedSystemContainer),
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy));
    }

    public async Task<IList<ConnectedSystemContainer>> GetConnectedSystemContainersAsync(ConnectedSystem connectedSystem)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        return await Application.Repository.ConnectedSystems.GetConnectedSystemContainersAsync(connectedSystem);
    }

    public async Task<ConnectedSystemContainer?> GetConnectedSystemContainerAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemContainerAsync(id);
    }

    /// <summary>
    /// Updates a Connected System Container (e.g. its import-scope selection), recording the change with an Activity
    /// and a versioned configuration snapshot of the owning Connected System.
    /// </summary>
    public async Task UpdateConnectedSystemContainerAsync(ConnectedSystemContainer container, int connectedSystemId, MetaverseObject? initiatedBy)
    {
        if (container == null)
            throw new ArgumentNullException(nameof(container));

        await ExecuteScopeConfigurationChangeAsync(
            connectedSystemId,
            $"Update container: {container.Name}",
            () => Application.Repository.ConnectedSystems.UpdateConnectedSystemContainerAsync(container),
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Updates a Connected System Container (initiated by API key), recording the change with an Activity and a
    /// versioned configuration snapshot of the owning Connected System.
    /// </summary>
    public async Task UpdateConnectedSystemContainerAsync(ConnectedSystemContainer container, int connectedSystemId, ApiKey initiatedByApiKey)
    {
        if (container == null)
            throw new ArgumentNullException(nameof(container));

        await ExecuteScopeConfigurationChangeAsync(
            connectedSystemId,
            $"Update container: {container.Name}",
            () => Application.Repository.ConnectedSystems.UpdateConnectedSystemContainerAsync(container),
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    /// <summary>
    /// Deletes a Connected System Container, recording the change with an Activity and a versioned configuration
    /// snapshot of the owning Connected System.
    /// </summary>
    public async Task DeleteConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer, int connectedSystemId, MetaverseObject? initiatedBy)
    {
        if (connectedSystemContainer == null)
            throw new ArgumentNullException(nameof(connectedSystemContainer));

        await ExecuteScopeConfigurationChangeAsync(
            connectedSystemId,
            $"Delete container: {connectedSystemContainer.Name}",
            () => Application.Repository.ConnectedSystems.DeleteConnectedSystemContainerAsync(connectedSystemContainer),
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Shared execution shape for partition/container configuration changes: create an Update Activity against the
    /// owning Connected System, persist the change, capture a versioned configuration snapshot (reloaded so it
    /// reflects persisted truth), then complete the Activity.
    /// </summary>
    private async Task ExecuteScopeConfigurationChangeAsync(
        int connectedSystemId,
        string message,
        Func<Task> persistAsync,
        Func<Activity, Task> createActivityAsync)
    {
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);

        // The activity targets the owning Connected System (whose configuration changed), so the operation is always
        // Update; whether a partition/container was created, updated or deleted is carried by the message.
        var activity = new Activity
        {
            TargetName = connectedSystem?.Name ?? "Unknown",
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = connectedSystemId,
            Message = message
        };
        await createActivityAsync(activity);

        await persistAsync();

        await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystemId);
        await Application.Activities.CompleteActivityAsync(activity);
    }
    #endregion

    #region Synchronisation Rule Mappings
    /// <summary>
    /// Gets all mappings for a Synchronisation Rule.
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    public async Task<List<SyncRuleMapping>> GetSyncRuleMappingsAsync(int syncRuleId)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRuleMappingsAsync(syncRuleId);
    }

    /// <summary>
    /// Gets a specific Synchronisation Rule mapping by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the mapping.</param>
    public async Task<SyncRuleMapping?> GetSyncRuleMappingAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRuleMappingAsync(id);
    }

    /// <summary>
    /// Validates that direct attribute mappings have compatible types and plurality.
    /// Expression-based sources are skipped as their output type cannot be statically determined.
    /// </summary>
    /// <param name="mapping">The mapping to validate.</param>
    /// <exception cref="ArgumentException">Thrown when attribute types are incompatible or plurality is invalid.</exception>
    private static void ValidateMappingTypeCompatibility(SyncRuleMapping mapping)
    {
        foreach (var source in mapping.Sources)
        {
            // Skip expression-based sources - output type cannot be statically determined
            if (!string.IsNullOrWhiteSpace(source.Expression))
                continue;

            // Determine source and target attribute details based on Synchronisation Rule direction
            string? sourceAttrName;
            AttributeDataType sourceType;
            AttributePlurality sourcePlurality;
            string? targetAttrName;
            AttributeDataType targetType;
            AttributePlurality targetPlurality;

            if (source.ConnectedSystemAttribute != null && mapping.TargetMetaverseAttribute != null)
            {
                // Import: CS attribute -> MV attribute
                sourceAttrName = source.ConnectedSystemAttribute.Name;
                sourceType = source.ConnectedSystemAttribute.Type;
                sourcePlurality = source.ConnectedSystemAttribute.AttributePlurality;
                targetAttrName = mapping.TargetMetaverseAttribute.Name;
                targetType = mapping.TargetMetaverseAttribute.Type;
                targetPlurality = mapping.TargetMetaverseAttribute.AttributePlurality;
            }
            else if (source.MetaverseAttribute != null && mapping.TargetConnectedSystemAttribute != null)
            {
                // Export: MV attribute -> CS attribute
                sourceAttrName = source.MetaverseAttribute.Name;
                sourceType = source.MetaverseAttribute.Type;
                sourcePlurality = source.MetaverseAttribute.AttributePlurality;
                targetAttrName = mapping.TargetConnectedSystemAttribute.Name;
                targetType = mapping.TargetConnectedSystemAttribute.Type;
                targetPlurality = mapping.TargetConnectedSystemAttribute.AttributePlurality;
            }
            else
            {
                // Cannot determine source/target pair - skip validation for this source
                continue;
            }

            // Reject NotSet types - indicates schema issues
            if (sourceType == AttributeDataType.NotSet)
                throw new ArgumentException(
                    $"Source attribute '{sourceAttrName}' has type NotSet. Attributes must have a defined type before they can be used in mappings.");

            if (targetType == AttributeDataType.NotSet)
                throw new ArgumentException(
                    $"Target attribute '{targetAttrName}' has type NotSet. Attributes must have a defined type before they can be used in mappings.");

            // Validate type compatibility
            if (sourceType != targetType)
                throw new ArgumentException(
                    $"Type mismatch: source attribute '{sourceAttrName}' ({sourceType}) is not compatible with target attribute '{targetAttrName}' ({targetType}). Source and target attributes must have the same type.");

            // Multi-valued to single-valued is permitted at configuration time (#435). At runtime, if a
            // source holds more than one value for a single-valued target, the attribute is skipped and a
            // MultiValuedToSingleValued RPEI error is raised (a single value flows normally).
        }
    }

    /// <summary>
    /// Validates that export Attribute Flow mappings do not target read-only attributes.
    /// Read-only attributes (system-managed, constructed, back-links) cannot be written to
    /// and will cause export failures at runtime.
    /// <para>
    /// <see cref="AttributeWritability.WritableOnCreate"/> is deliberately permitted: the value has to
    /// flow during provisioning or the object can never be created. Keeping it out of Update Pending
    /// Exports is enforced on the export path by <see cref="SyncRuleMapping.FlowsOnUpdateExport"/>,
    /// not here.
    /// </para>
    /// </summary>
    /// <param name="mapping">The mapping to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the target attribute is read-only.</exception>
    private static void ValidateMappingWritability(SyncRuleMapping mapping)
    {
        // only applies to export rules (target is a Connected System attribute)
        if (mapping.TargetConnectedSystemAttribute == null)
            return;

        if (mapping.TargetConnectedSystemAttribute.Writability == AttributeWritability.ReadOnly)
            throw new ArgumentException(
                $"Cannot create export Attribute Flow to read-only attribute '{mapping.TargetConnectedSystemAttribute.Name}'. " +
                $"This attribute is marked as read-only by the Connected System and cannot be written to.");
    }

    /// <summary>
    /// Creates a new Synchronisation Rule mapping.
    /// </summary>
    /// <param name="mapping">The mapping to create.</param>
    /// <param name="initiatedBy">The user who initiated the creation.</param>
    public async Task CreateSyncRuleMappingAsync(SyncRuleMapping mapping, MetaverseObject? initiatedBy)
    {
        if (mapping == null)
            throw new ArgumentNullException(nameof(mapping));

        ValidateMappingTypeCompatibility(mapping);
        ValidateMappingWritability(mapping);

        Log.Debug("CreateSyncRuleMappingAsync() called for Synchronisation Rule {SyncRuleId}", mapping.SyncRule?.Id);

        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
        var activity = new Activity
        {
            TargetName = $"{Activity.SyncRuleMappingTargetNamePrefix}{targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            SyncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId ?? 0;
        // Capture the object type before ClearMappingNavigationProperties detaches the SyncRule nav; auto-assign
        // needs it to scope the attribute's priority list.
        var metaverseObjectTypeId = mapping.SyncRule?.MetaverseObjectTypeId;

        AuditHelper.SetCreated(mapping, initiatedBy);
        ClearMappingNavigationProperties(mapping);
        await Application.Repository.ConnectedSystems.CreateSyncRuleMappingAsync(mapping);

        await AutoAssignImportMappingPriorityAsync(mapping, metaverseObjectTypeId);
        await CaptureSyncRuleConfigurationChangeAsync(activity, syncRuleId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Creates a new Synchronisation Rule mapping (initiated by API key).
    /// </summary>
    public async Task CreateSyncRuleMappingAsync(SyncRuleMapping mapping, ApiKey initiatedByApiKey)
    {
        if (mapping == null)
            throw new ArgumentNullException(nameof(mapping));

        ValidateMappingTypeCompatibility(mapping);
        ValidateMappingWritability(mapping);

        Log.Debug("CreateSyncRuleMappingAsync() called for Synchronisation Rule {SyncRuleId} (API key initiated)", mapping.SyncRule?.Id);

        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
        var activity = new Activity
        {
            TargetName = $"{Activity.SyncRuleMappingTargetNamePrefix}{targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            SyncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId ?? 0;
        // Capture the object type before ClearMappingNavigationProperties detaches the SyncRule nav; auto-assign
        // needs it to scope the attribute's priority list.
        var metaverseObjectTypeId = mapping.SyncRule?.MetaverseObjectTypeId;

        AuditHelper.SetCreated(mapping, initiatedByApiKey);
        ClearMappingNavigationProperties(mapping);
        await Application.Repository.ConnectedSystems.CreateSyncRuleMappingAsync(mapping);

        await AutoAssignImportMappingPriorityAsync(mapping, metaverseObjectTypeId);
        await CaptureSyncRuleConfigurationChangeAsync(activity, syncRuleId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Synchronisation Rule mapping.
    /// </summary>
    /// <param name="mapping">The mapping to update.</param>
    /// <param name="initiatedBy">The user who initiated the update.</param>
    public async Task UpdateSyncRuleMappingAsync(SyncRuleMapping mapping, MetaverseObject? initiatedBy)
    {
        if (mapping == null)
            throw new ArgumentNullException(nameof(mapping));

        ValidateMappingTypeCompatibility(mapping);
        ValidateMappingWritability(mapping);

        Log.Debug("UpdateSyncRuleMappingAsync() called for mapping {Id}", mapping.Id);

        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
        var activity = new Activity
        {
            TargetName = $"{Activity.SyncRuleMappingTargetNamePrefix}{targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            SyncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId ?? 0;
        AuditHelper.SetUpdated(mapping, initiatedBy);
        await Application.Repository.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping);

        await CaptureSyncRuleConfigurationChangeAsync(activity, syncRuleId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes a Synchronisation Rule mapping.
    /// </summary>
    /// <param name="mapping">The mapping to delete.</param>
    /// <param name="initiatedBy">The user who initiated the deletion.</param>
    public async Task DeleteSyncRuleMappingAsync(SyncRuleMapping mapping, MetaverseObject? initiatedBy)
    {
        if (mapping == null)
            throw new ArgumentNullException(nameof(mapping));

        Log.Debug("DeleteSyncRuleMappingAsync() called for mapping {Id}", mapping.Id);

        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
        var activity = new Activity
        {
            TargetName = $"{Activity.SyncRuleMappingTargetNamePrefix}{targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            SyncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId ?? 0;
        // Capture the import mapping's attribute scope before deletion so the remaining contributors can be re-densified.
        var metaverseObjectTypeId = mapping.SyncRule?.MetaverseObjectTypeId;
        var targetMetaverseAttributeId = mapping.TargetMetaverseAttributeId;

        await Application.Repository.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping);

        if (metaverseObjectTypeId.HasValue && targetMetaverseAttributeId.HasValue)
            await RedensifyAttributePriorityAfterRemovalAsync(metaverseObjectTypeId.Value, targetMetaverseAttributeId.Value);

        await CaptureSyncRuleConfigurationChangeAsync(activity, syncRuleId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes a Synchronisation Rule Mapping (initiated by API key).
    /// </summary>
    /// <param name="mapping">The mapping to delete.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the deletion.</param>
    public async Task DeleteSyncRuleMappingAsync(SyncRuleMapping mapping, ApiKey initiatedByApiKey)
    {
        if (mapping == null)
            throw new ArgumentNullException(nameof(mapping));

        Log.Debug("DeleteSyncRuleMappingAsync() called for mapping {Id} (API key initiated)", mapping.Id);

        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
        var activity = new Activity
        {
            TargetName = $"{Activity.SyncRuleMappingTargetNamePrefix}{targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            SyncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId ?? 0;
        // Capture the import mapping's attribute scope before deletion so the remaining contributors can be re-densified.
        var metaverseObjectTypeId = mapping.SyncRule?.MetaverseObjectTypeId;
        var targetMetaverseAttributeId = mapping.TargetMetaverseAttributeId;

        await Application.Repository.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping);

        if (metaverseObjectTypeId.HasValue && targetMetaverseAttributeId.HasValue)
            await RedensifyAttributePriorityAfterRemovalAsync(metaverseObjectTypeId.Value, targetMetaverseAttributeId.Value);

        await CaptureSyncRuleConfigurationChangeAsync(activity, syncRuleId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Gets the attribute priority list for a (Metaverse Object Type, Metaverse attribute) pair: the import
    /// mappings contributing to that attribute, ordered by priority (#91). Disabled Synchronisation Rules are
    /// included (they hold position). Returns an empty list when the attribute has no import contributors.
    /// </summary>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type that scopes the priority list.</param>
    /// <param name="metaverseAttributeId">The target Metaverse attribute.</param>
    public async Task<List<SyncRuleMapping>> GetAttributePriorityOrderAsync(int metaverseObjectTypeId, int metaverseAttributeId)
    {
        return await Application.Repository.ConnectedSystems.GetImportSyncRuleMappingsForMetaverseAttributeAsync(metaverseObjectTypeId, metaverseAttributeId);
    }

    /// <summary>
    /// Gets the number of import contributors for each Metaverse attribute of a Metaverse Object Type (#91), keyed
    /// by Metaverse attribute id. Only attributes with at least one contributor appear. Drives the Surface 2
    /// multi-contributor badge: an attribute with more than one contributor has a priority order worth managing,
    /// whereas a single-contributor attribute needs no priority. Disabled Synchronisation Rules are counted (they
    /// hold position in a priority list), matching <see cref="GetAttributePriorityOrderAsync"/>.
    /// </summary>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type whose attributes are counted.</param>
    public async Task<Dictionary<int, int>> GetAttributeContributorCountsAsync(int metaverseObjectTypeId)
    {
        var mappings = await Application.Repository.ConnectedSystems.GetImportSyncRuleMappingsForMetaverseObjectTypeAsync(metaverseObjectTypeId);
        return mappings
            .Where(m => m.TargetMetaverseAttributeId.HasValue)
            .GroupBy(m => m.TargetMetaverseAttributeId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Snapshots the current priority/null-handling of a set of mappings, keyed by mapping id, so a subsequent
    /// renumber can determine which rows actually changed (and avoid auditing/persisting no-op rows).
    /// </summary>
    private static Dictionary<int, (int Priority, bool NullIsValue)> SnapshotPriorityState(IEnumerable<SyncRuleMapping> mappings)
    {
        return mappings.ToDictionary(m => m.Id, m => (m.Priority, m.NullIsValue));
    }

    /// <summary>
    /// Renumbers an ordered list of mappings to a deterministic 1..N (1 = highest priority) and returns the subset
    /// whose <see cref="SyncRuleMapping.Priority"/> or <see cref="SyncRuleMapping.NullIsValue"/> differs from the
    /// supplied pre-change snapshot. Reordering one mapping inherently renumbers its siblings, so the changed set
    /// may be larger than the single mapping an admin moved; rows whose number did not actually change are left
    /// untouched (no audit churn, no redundant write).
    /// </summary>
    private static List<SyncRuleMapping> RenumberAndCollectChanges(List<SyncRuleMapping> ordered, IReadOnlyDictionary<int, (int Priority, bool NullIsValue)> snapshot)
    {
        var changed = new List<SyncRuleMapping>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var mapping = ordered[i];
            mapping.Priority = i + 1; // 1 = highest priority
            var before = snapshot[mapping.Id];
            if (before.Priority != mapping.Priority || before.NullIsValue != mapping.NullIsValue)
                changed.Add(mapping);
        }
        return changed;
    }

    /// <summary>
    /// Gives a newly-created import mapping an explicit priority when its target Metaverse attribute already has
    /// other contributors for the object type (#91, "safe addition"). The whole contributor list is densified to a
    /// dense 1..N in its existing precedence order ((Priority asc, Id asc), as the query returns it) with the new
    /// mapping last, so the new flow never wins resolution until an admin reorders it. This both makes the priorities
    /// explicit (replacing the int.MaxValue sentinels) and avoids the overflow of a literal "max + 1" while every
    /// existing contributor is still at the sentinel. A no-op for export mappings (priority is an inbound concern)
    /// and when the attribute has a single contributor (priority is meaningless, so the new mapping keeps the
    /// sentinel). Order-preserving: the densified ranks match the precedence the id tie-break already produced, so no
    /// resolution outcome changes; only the stored numbers become explicit.
    /// </summary>
    /// <param name="mapping">The just-persisted mapping.</param>
    /// <param name="metaverseObjectTypeId">The object type that scopes the priority list (from the mapping's
    /// Synchronisation Rule). When null the attribute's priority list cannot be scoped, so the mapping is left at the
    /// safe-addition sentinel.</param>
    private async Task AutoAssignImportMappingPriorityAsync(SyncRuleMapping mapping, int? metaverseObjectTypeId)
    {
        // Export mappings do not participate in attribute priority; only import mappings target a Metaverse attribute.
        if (!mapping.TargetMetaverseAttributeId.HasValue || !metaverseObjectTypeId.HasValue)
            return;

        var contributors = await Application.Repository.ConnectedSystems
            .GetImportSyncRuleMappingsForMetaverseAttributeAsync(metaverseObjectTypeId.Value, mapping.TargetMetaverseAttributeId.Value);

        // Sole contributor: priority is meaningless, so leave the safe-addition sentinel untouched.
        if (contributors.Count <= 1)
            return;

        var snapshot = SnapshotPriorityState(contributors);
        var changed = RenumberAndCollectChanges(contributors, snapshot);
        if (changed.Count > 0)
            await Application.Repository.ConnectedSystems.UpdateSyncRuleMappingsAsync(changed);
    }

    /// <summary>
    /// After an import mapping (or its rule) is removed, re-densifies the target Metaverse attribute's remaining
    /// contributor list to a dense 1..N (#91), so deleting a contributor never leaves a gap in the priority numbers.
    /// Mirrors the safe-addition densify on creation, keeping the contributor list dense across add, reorder, and
    /// delete. A sole remaining contributor is reset to the int.MaxValue sentinel (priority is meaningless with one
    /// source, matching the invariant that explicit priorities exist only when an attribute has more than one
    /// contributor); zero remaining contributors is a no-op. Order-preserving, so no resolution outcome changes.
    /// Must be called after the removal is persisted, so the query returns only the surviving contributors.
    /// </summary>
    /// <param name="metaverseObjectTypeId">The object type that scopes the attribute's priority list.</param>
    /// <param name="metaverseAttributeId">The target Metaverse attribute whose contributor list was reduced.</param>
    private async Task RedensifyAttributePriorityAfterRemovalAsync(int metaverseObjectTypeId, int metaverseAttributeId)
    {
        var contributors = await Application.Repository.ConnectedSystems
            .GetImportSyncRuleMappingsForMetaverseAttributeAsync(metaverseObjectTypeId, metaverseAttributeId);

        if (contributors.Count == 0)
            return;

        if (contributors.Count == 1)
        {
            // Sole remaining contributor: reset to the safe-addition sentinel (explicit priorities exist only when an
            // attribute has more than one contributor).
            var sole = contributors[0];
            if (sole.Priority != int.MaxValue)
            {
                sole.Priority = int.MaxValue;
                await Application.Repository.ConnectedSystems.UpdateSyncRuleMappingsAsync([sole]);
            }

            return;
        }

        var snapshot = SnapshotPriorityState(contributors);
        var changed = RenumberAndCollectChanges(contributors, snapshot);
        if (changed.Count > 0)
            await Application.Repository.ConnectedSystems.UpdateSyncRuleMappingsAsync(changed);
    }

    /// <summary>
    /// Builds the renumbered ordered list from a complete-order request: the request must list every current
    /// contributor for the attribute exactly once and no others, so renumbering produces no gaps or duplicate
    /// priorities. Used by the "replace the whole order" surface (drag-reorder-then-save).
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="orderedContributors"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the attribute has no contributors, or the requested order
    /// does not match the attribute's current contributor set exactly.</exception>
    private async Task<(List<SyncRuleMapping> Ordered, List<SyncRuleMapping> Changed)> BuildAttributePriorityFromFullOrderAsync(int metaverseObjectTypeId, int metaverseAttributeId, IReadOnlyList<(int MappingId, bool NullIsValue)> orderedContributors)
    {
        if (orderedContributors == null)
            throw new ArgumentNullException(nameof(orderedContributors));

        var existing = await Application.Repository.ConnectedSystems.GetImportSyncRuleMappingsForMetaverseAttributeAsync(metaverseObjectTypeId, metaverseAttributeId);
        if (existing.Count == 0)
            throw new ArgumentException($"No import attribute contributions exist for Metaverse attribute {metaverseAttributeId} on Metaverse Object Type {metaverseObjectTypeId}.");

        var requestedIds = orderedContributors.Select(c => c.MappingId).ToList();
        var requestedDistinct = new HashSet<int>(requestedIds);
        if (requestedDistinct.Count != requestedIds.Count)
            throw new ArgumentException("The attribute priority order contains duplicate mapping identifiers.");

        var existingIds = existing.Select(m => m.Id).ToHashSet();
        if (!requestedDistinct.SetEquals(existingIds))
            throw new ArgumentException("The attribute priority order must list every contributing mapping for the attribute exactly once, and no others.");

        var snapshot = SnapshotPriorityState(existing);
        var byId = existing.ToDictionary(m => m.Id);
        var ordered = new List<SyncRuleMapping>(orderedContributors.Count);
        foreach (var contributor in orderedContributors)
        {
            var mapping = byId[contributor.MappingId];
            mapping.NullIsValue = contributor.NullIsValue;
            ordered.Add(mapping);
        }

        var changed = RenumberAndCollectChanges(ordered, snapshot);
        return (ordered, changed);
    }

    /// <summary>
    /// Builds the renumbered ordered list from a single-mapping move: the named mapping is repositioned to the
    /// 1-based <paramref name="targetPosition"/> and every other contributor shuffles to accommodate it. This is
    /// the ergonomic, footgun-free reorder: the caller states only "put this mapping at position N" and the engine
    /// keeps the rest of the list contiguous and duplicate-free. The target position is clamped to the valid range.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the attribute has no contributors, or the named mapping is
    /// not one of them.</exception>
    private async Task<(List<SyncRuleMapping> Ordered, List<SyncRuleMapping> Changed)> BuildAttributePriorityFromMoveAsync(int metaverseObjectTypeId, int metaverseAttributeId, int mappingId, int targetPosition, bool? nullIsValue)
    {
        var existing = await Application.Repository.ConnectedSystems.GetImportSyncRuleMappingsForMetaverseAttributeAsync(metaverseObjectTypeId, metaverseAttributeId);
        if (existing.Count == 0)
            throw new ArgumentException($"No import attribute contributions exist for Metaverse attribute {metaverseAttributeId} on Metaverse Object Type {metaverseObjectTypeId}.");

        var moving = existing.SingleOrDefault(m => m.Id == mappingId);
        if (moving == null)
            throw new ArgumentException($"Mapping {mappingId} is not a contributor to Metaverse attribute {metaverseAttributeId} on Metaverse Object Type {metaverseObjectTypeId}.");

        var snapshot = SnapshotPriorityState(existing);

        if (nullIsValue.HasValue)
            moving.NullIsValue = nullIsValue.Value;

        // existing is already ordered by Priority then Id. Remove the moving mapping and re-insert it at the
        // requested position, clamped to [1, N], so the rest of the list shuffles around it.
        var targetIndex = Math.Clamp(targetPosition, 1, existing.Count) - 1;
        var ordered = new List<SyncRuleMapping>(existing);
        ordered.Remove(moving);
        ordered.Insert(targetIndex, moving);

        var changed = RenumberAndCollectChanges(ordered, snapshot);
        return (ordered, changed);
    }

    /// <summary>
    /// Builds an Activity describing an attribute priority order change, for audit attribution.
    /// </summary>
    private static Activity BuildAttributePriorityActivity(int metaverseAttributeId, List<SyncRuleMapping> ordered)
    {
        var attributeName = ordered.Count > 0 ? ordered[0].TargetMetaverseAttribute?.Name ?? $"#{metaverseAttributeId}" : $"#{metaverseAttributeId}";
        return new Activity
        {
            TargetName = $"Attribute priority order for {attributeName}",
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.Update
        };
    }

    /// <summary>
    /// Audits and persists the changed mappings of an attribute priority change in a single transaction
    /// (user-initiated). A no-op change (nothing actually moved) writes nothing and records no Activity.
    /// </summary>
    private async Task PersistAttributePriorityChangesAsync(int metaverseAttributeId, List<SyncRuleMapping> ordered, List<SyncRuleMapping> changed, MetaverseObject? initiatedBy)
    {
        if (changed.Count == 0)
            return;

        var activity = BuildAttributePriorityActivity(metaverseAttributeId, ordered);
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        foreach (var mapping in changed)
            AuditHelper.SetUpdated(mapping, initiatedBy);

        await Application.Repository.ConnectedSystems.UpdateSyncRuleMappingsAsync(changed);

        await CaptureAttributePriorityChangeOnAffectedRulesAsync(activity, changed,
            child => Application.Activities.CreateActivityAsync(child, initiatedBy));
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Audits and persists the changed mappings of an attribute priority change in a single transaction
    /// (API key initiated). A no-op change writes nothing and records no Activity.
    /// </summary>
    private async Task PersistAttributePriorityChangesAsync(int metaverseAttributeId, List<SyncRuleMapping> ordered, List<SyncRuleMapping> changed, ApiKey initiatedByApiKey)
    {
        if (changed.Count == 0)
            return;

        var activity = BuildAttributePriorityActivity(metaverseAttributeId, ordered);
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        foreach (var mapping in changed)
            AuditHelper.SetUpdated(mapping, initiatedByApiKey);

        await Application.Repository.ConnectedSystems.UpdateSyncRuleMappingsAsync(changed);

        await CaptureAttributePriorityChangeOnAffectedRulesAsync(activity, changed,
            child => Application.Activities.CreateActivityAsync(child, initiatedByApiKey));
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// An attribute priority change spans the Synchronisation Rules of every changed mapping, so one snapshot cannot
    /// represent it. Each affected rule instead captures its own versioned snapshot onto a child Activity linked to
    /// the parent reorder Activity, so every rule's configuration history shows the priority change. The rule is
    /// reloaded in full so the snapshot reflects persisted truth.
    /// </summary>
    private async Task CaptureAttributePriorityChangeOnAffectedRulesAsync(Activity parentActivity, List<SyncRuleMapping> changed, Func<Activity, Task> createChildActivityAsync)
    {
        var affectedRuleIds = changed
            .Select(m => m.SyncRuleId ?? m.SyncRule?.Id)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct();

        foreach (var ruleId in affectedRuleIds)
        {
            var rule = await Application.Repository.ConnectedSystems.GetSyncRuleAsync(ruleId);
            if (rule == null)
                continue;

            var childActivity = new Activity
            {
                TargetName = rule.Name,
                TargetType = ActivityTargetType.SynchronisationRule,
                TargetOperationType = ActivityTargetOperationType.Update,
                SyncRuleId = ruleId,
                ParentActivityId = parentActivity.Id,
                Message = parentActivity.TargetName
            };
            await createChildActivityAsync(childActivity);
            await CaptureConfigurationChangeAsync(childActivity, rule, changeReason: null);
            await Application.Activities.CompleteActivityAsync(childActivity);
        }
    }

    /// <summary>
    /// Replaces the entire attribute priority order for a (Metaverse Object Type, Metaverse attribute) pair (#91),
    /// transactionally renumbering all contributing mappings' priorities and applying their "Null is a value" flags.
    /// The request must list every current contributor exactly once. Use <see cref="MoveAttributePriorityAsync(int, int, int, int, bool?, MetaverseObject?)"/>
    /// for the simpler "move one mapping to position N" gesture. Returns the resulting order (highest priority first).
    /// </summary>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type that scopes the priority list.</param>
    /// <param name="metaverseAttributeId">The target Metaverse attribute.</param>
    /// <param name="orderedContributors">The contributors in the desired priority order (highest first), each with its "Null is a value" flag.</param>
    /// <param name="initiatedBy">The user who initiated the change.</param>
    public async Task<List<SyncRuleMapping>> SetAttributePriorityOrderAsync(int metaverseObjectTypeId, int metaverseAttributeId, IReadOnlyList<(int MappingId, bool NullIsValue)> orderedContributors, MetaverseObject? initiatedBy)
    {
        var (ordered, changed) = await BuildAttributePriorityFromFullOrderAsync(metaverseObjectTypeId, metaverseAttributeId, orderedContributors);
        await PersistAttributePriorityChangesAsync(metaverseAttributeId, ordered, changed, initiatedBy);
        return ordered;
    }

    /// <summary>
    /// Replaces the entire attribute priority order for a (Metaverse Object Type, Metaverse attribute) pair (#91, API key initiated).
    /// Returns the resulting order (highest priority first).
    /// </summary>
    public async Task<List<SyncRuleMapping>> SetAttributePriorityOrderAsync(int metaverseObjectTypeId, int metaverseAttributeId, IReadOnlyList<(int MappingId, bool NullIsValue)> orderedContributors, ApiKey initiatedByApiKey)
    {
        var (ordered, changed) = await BuildAttributePriorityFromFullOrderAsync(metaverseObjectTypeId, metaverseAttributeId, orderedContributors);
        await PersistAttributePriorityChangesAsync(metaverseAttributeId, ordered, changed, initiatedByApiKey);
        return ordered;
    }

    /// <summary>
    /// Moves a single contributing mapping to the given 1-based priority position for a (Metaverse Object Type,
    /// Metaverse attribute) pair (#91), shuffling the other contributors to keep the list contiguous, then
    /// transactionally renumbering all affected rows. Optionally updates the moved mapping's "Null is a value"
    /// flag. This is the deterministic, single-request reorder: the admin states only the new position and the
    /// engine maintains a gap-free, duplicate-free order. Returns the resulting order (highest priority first).
    /// </summary>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type that scopes the priority list.</param>
    /// <param name="metaverseAttributeId">The target Metaverse attribute.</param>
    /// <param name="mappingId">The contributing mapping to move.</param>
    /// <param name="targetPosition">The desired 1-based priority position (1 = highest). Clamped to the valid range.</param>
    /// <param name="nullIsValue">When supplied, also sets the moved mapping's "Null is a value" flag.</param>
    /// <param name="initiatedBy">The user who initiated the change.</param>
    public async Task<List<SyncRuleMapping>> MoveAttributePriorityAsync(int metaverseObjectTypeId, int metaverseAttributeId, int mappingId, int targetPosition, bool? nullIsValue, MetaverseObject? initiatedBy)
    {
        var (ordered, changed) = await BuildAttributePriorityFromMoveAsync(metaverseObjectTypeId, metaverseAttributeId, mappingId, targetPosition, nullIsValue);
        await PersistAttributePriorityChangesAsync(metaverseAttributeId, ordered, changed, initiatedBy);
        return ordered;
    }

    /// <summary>
    /// Moves a single contributing mapping to the given 1-based priority position (#91, API key initiated).
    /// Returns the resulting order (highest priority first).
    /// </summary>
    public async Task<List<SyncRuleMapping>> MoveAttributePriorityAsync(int metaverseObjectTypeId, int metaverseAttributeId, int mappingId, int targetPosition, bool? nullIsValue, ApiKey initiatedByApiKey)
    {
        var (ordered, changed) = await BuildAttributePriorityFromMoveAsync(metaverseObjectTypeId, metaverseAttributeId, mappingId, targetPosition, nullIsValue);
        await PersistAttributePriorityChangesAsync(metaverseAttributeId, ordered, changed, initiatedByApiKey);
        return ordered;
    }
    #endregion

    #region Connected System Run Profiles
    public async Task CreateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, MetaverseObject? initiatedBy)
    {
        if (connectedSystemRunProfile == null)
            throw new ArgumentNullException(nameof(connectedSystemRunProfile));

        // Core: IsRunProfileValid only reads ConnectorDefinition.Supports* and we read .Name for activity context.
        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemRunProfile.ConnectedSystemId) ?? throw new ArgumentException("No such Connected System found!");
        if (!IsRunProfileValid(connectedSystem, connectedSystemRunProfile))
            throw new ArgumentException("Run Profile is not valid for the Connector!");

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystemRunProfile.Name,
            TargetContext = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Create,
            ConnectedSystemId = connectedSystemRunProfile.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        AuditHelper.SetCreated(connectedSystemRunProfile, initiatedBy);
        await Application.Repository.ConnectedSystems.CreateConnectedSystemRunProfileAsync(connectedSystemRunProfile);

        // now the Run Profile has been persisted, associated it with the activity and complete it.
        activity.ConnectedSystemRunProfileId = connectedSystemRunProfile.Id;
        await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystemRunProfile.ConnectedSystemId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Creates a Connected System Run Profile (initiated by API key).
    /// </summary>
    public async Task CreateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, ApiKey initiatedByApiKey)
    {
        if (connectedSystemRunProfile == null)
            throw new ArgumentNullException(nameof(connectedSystemRunProfile));

        // Core: IsRunProfileValid only reads ConnectorDefinition.Supports* and we read .Name for activity context.
        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemRunProfile.ConnectedSystemId) ?? throw new ArgumentException("No such Connected System found!");
        if (!IsRunProfileValid(connectedSystem, connectedSystemRunProfile))
            throw new ArgumentException("Run Profile is not valid for the Connector!");

        var activity = new Activity
        {
            TargetName = connectedSystemRunProfile.Name,
            TargetContext = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Create,
            ConnectedSystemId = connectedSystemRunProfile.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        AuditHelper.SetCreated(connectedSystemRunProfile, initiatedByApiKey);
        await Application.Repository.ConnectedSystems.CreateConnectedSystemRunProfileAsync(connectedSystemRunProfile);

        activity.ConnectedSystemRunProfileId = connectedSystemRunProfile.Id;
        await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystemRunProfile.ConnectedSystemId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, MetaverseObject? initiatedBy)
    {
        if (connectedSystemRunProfile == null)
            return;

        // Get Connected System name for activity context (Core: only .Name is read).
        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemRunProfile.ConnectedSystemId);

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystemRunProfile.Name,
            TargetContext = connectedSystem?.Name,
            ConnectedSystemRunType = connectedSystemRunProfile.RunType,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Delete,
            ConnectedSystemId = connectedSystemRunProfile.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        await Application.Repository.ConnectedSystems.DeleteConnectedSystemRunProfileAsync(connectedSystemRunProfile);
        await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystemRunProfile.ConnectedSystemId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes a Connected System Run Profile (initiated by API key).
    /// </summary>
    public async Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, ApiKey initiatedByApiKey)
    {
        if (connectedSystemRunProfile == null)
            return;

        // Get Connected System name for activity context (Core: only .Name is read).
        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemRunProfile.ConnectedSystemId);

        var activity = new Activity
        {
            TargetName = connectedSystemRunProfile.Name,
            TargetContext = connectedSystem?.Name,
            ConnectedSystemRunType = connectedSystemRunProfile.RunType,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Delete,
            ConnectedSystemId = connectedSystemRunProfile.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        await Application.Repository.ConnectedSystems.DeleteConnectedSystemRunProfileAsync(connectedSystemRunProfile);
        await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystemRunProfile.ConnectedSystemId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task UpdateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, MetaverseObject? initiatedBy)
    {
        if (connectedSystemRunProfile == null)
            throw new ArgumentNullException(nameof(connectedSystemRunProfile));

        // SPEC-1082 D10: Verification Mode only applies to Full Import.
        if (connectedSystemRunProfile.VerifyImportContentHashes && connectedSystemRunProfile.RunType != ConnectedSystemRunType.FullImport)
            throw new ArgumentException("VerifyImportContentHashes can only be enabled on a Full Import Run Profile.");

        // Get Connected System name for activity context (Core: only .Name is read).
        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemRunProfile.ConnectedSystemId);

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystemRunProfile.Name,
            TargetContext = connectedSystem?.Name,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemRunProfileId = connectedSystemRunProfile.Id,
            ConnectedSystemId = connectedSystemRunProfile.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        AuditHelper.SetUpdated(connectedSystemRunProfile, initiatedBy);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(connectedSystemRunProfile);
        await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystemRunProfile.ConnectedSystemId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates a Connected System Run Profile (initiated by API key).
    /// </summary>
    public async Task UpdateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, ApiKey initiatedByApiKey)
    {
        if (connectedSystemRunProfile == null)
            throw new ArgumentNullException(nameof(connectedSystemRunProfile));

        // SPEC-1082 D10: Verification Mode only applies to Full Import.
        if (connectedSystemRunProfile.VerifyImportContentHashes && connectedSystemRunProfile.RunType != ConnectedSystemRunType.FullImport)
            throw new ArgumentException("VerifyImportContentHashes can only be enabled on a Full Import Run Profile.");

        // Get Connected System name for activity context (Core: only .Name is read).
        var connectedSystem = await GetConnectedSystemCoreAsync(connectedSystemRunProfile.ConnectedSystemId);

        var activity = new Activity
        {
            TargetName = connectedSystemRunProfile.Name,
            TargetContext = connectedSystem?.Name,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemRunProfileId = connectedSystemRunProfile.Id,
            ConnectedSystemId = connectedSystemRunProfile.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        AuditHelper.SetUpdated(connectedSystemRunProfile, initiatedByApiKey);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(connectedSystemRunProfile);
        await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystemRunProfile.ConnectedSystemId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(ConnectedSystem connectedSystem)
    {
        return await GetConnectedSystemRunProfilesAsync(connectedSystem.Id);
    }

    public async Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
    }

    public async Task<ConnectedSystemRunProfileHeader?> GetConnectedSystemRunProfileHeaderAsync(int connectedSystemRunProfileId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemRunProfileHeaderAsync(connectedSystemRunProfileId);
    }

    /// <summary>
    /// Captures the foreign-key id of every navigation property on a new Synchronisation Rule that points to an
    /// already-persisted entity (the Connected System, the object types, and the attributes referenced by its
    /// matching rules, Attribute Flow mappings and scoping criteria), then nulls those navigation properties.
    /// The web and API callers build the rule from entities loaded in an earlier, now-disposed scope: the FK
    /// scalars are unset and the loaded graph can contain duplicate instances of the same entity. Reducing every
    /// reference to an FK scalar leaves the repository's Add() with only the new rows to insert, so EF neither
    /// re-inserts existing entities (FK violations / duplicate-key errors) nor trips over duplicate-instance
    /// tracking conflicts.
    /// </summary>
    private static void DetachExistingEntityReferences(SyncRule syncRule)
    {
        if (syncRule.ConnectedSystem != null)
        {
            syncRule.ConnectedSystemId = syncRule.ConnectedSystem.Id;
            syncRule.ConnectedSystem = null!;
        }
        if (syncRule.ConnectedSystemObjectType != null)
        {
            syncRule.ConnectedSystemObjectTypeId = syncRule.ConnectedSystemObjectType.Id;
            syncRule.ConnectedSystemObjectType = null!;
        }
        if (syncRule.MetaverseObjectType != null)
        {
            syncRule.MetaverseObjectTypeId = syncRule.MetaverseObjectType.Id;
            syncRule.MetaverseObjectType = null!;
        }

        foreach (var matchingRule in syncRule.ObjectMatchingRules)
        {
            if (matchingRule.TargetMetaverseAttribute != null)
            {
                matchingRule.TargetMetaverseAttributeId = matchingRule.TargetMetaverseAttribute.Id;
                matchingRule.TargetMetaverseAttribute = null;
            }
            foreach (var source in matchingRule.Sources)
            {
                if (source.ConnectedSystemAttribute != null)
                {
                    source.ConnectedSystemAttributeId = source.ConnectedSystemAttribute.Id;
                    source.ConnectedSystemAttribute = null;
                }
            }
        }

        foreach (var mapping in syncRule.AttributeFlowRules)
        {
            if (mapping.TargetMetaverseAttribute != null)
            {
                mapping.TargetMetaverseAttributeId = mapping.TargetMetaverseAttribute.Id;
                mapping.TargetMetaverseAttribute = null;
            }
            if (mapping.TargetConnectedSystemAttribute != null)
            {
                mapping.TargetConnectedSystemAttributeId = mapping.TargetConnectedSystemAttribute.Id;
                mapping.TargetConnectedSystemAttribute = null;
            }
            foreach (var source in mapping.Sources)
            {
                if (source.ConnectedSystemAttribute != null)
                {
                    source.ConnectedSystemAttributeId = source.ConnectedSystemAttribute.Id;
                    source.ConnectedSystemAttribute = null;
                }
                if (source.MetaverseAttribute != null)
                {
                    source.MetaverseAttributeId = source.MetaverseAttribute.Id;
                    source.MetaverseAttribute = null;
                }
            }
        }

        foreach (var group in syncRule.ObjectScopingCriteriaGroups)
            DetachScopingGroupReferences(group);
    }

    /// <summary>
    /// Recursively detaches the attribute references on a scoping criteria group and its child groups,
    /// capturing each criterion's FK id and nulling its navigation property. See <see cref="DetachExistingEntityReferences"/>.
    /// </summary>
    private static void DetachScopingGroupReferences(SyncRuleScopingCriteriaGroup group)
    {
        foreach (var criteria in group.Criteria)
        {
            if (criteria.MetaverseAttribute != null)
            {
                criteria.MetaverseAttributeId = criteria.MetaverseAttribute.Id;
                criteria.MetaverseAttribute = null;
            }
            if (criteria.ConnectedSystemAttribute != null)
            {
                criteria.ConnectedSystemAttributeId = criteria.ConnectedSystemAttribute.Id;
                criteria.ConnectedSystemAttribute = null;
            }
        }
        foreach (var child in group.ChildGroups)
            DetachScopingGroupReferences(child);
    }

    /// <summary>
    /// Validates that every scoping criterion uses a comparison operator applicable to its attribute's data type,
    /// using the shared <see cref="SearchComparisonOperators"/> rule. Throws <see cref="ArgumentException"/> on the
    /// first invalid combination found, so a Synchronisation Rule with, for example, "Starts With" on a DateTime
    /// attribute can never be persisted (the evaluator could never satisfy it, silently dropping objects from scope).
    /// Must run before <see cref="DetachExistingEntityReferences"/>, while the attribute navigation properties are
    /// still populated. Criteria whose attribute type cannot be resolved are left for the model's own validation.
    /// </summary>
    private static void ValidateScopingCriteriaOperators(SyncRule syncRule)
    {
        foreach (var group in syncRule.ObjectScopingCriteriaGroups)
            ValidateScopingGroupOperators(group);
    }

    private static void ValidateScopingGroupOperators(SyncRuleScopingCriteriaGroup group)
    {
        foreach (var criterion in group.Criteria)
        {
            var attributeType = criterion.GetAttributeDataType();
            if (attributeType == null)
                continue;

            if (!SearchComparisonOperators.IsValid(criterion.ComparisonType, attributeType.Value))
            {
                var message = $"Comparison operator '{criterion.ComparisonType}' is not valid for the {attributeType.Value} " +
                              $"attribute '{criterion.GetAttributeName()}' on scoping criteria.";
                Log.Warning("CreateOrUpdateSyncRuleAsync: rejecting Synchronisation Rule; {Message}", message);
                throw new ArgumentException(message);
            }
        }

        foreach (var child in group.ChildGroups)
            ValidateScopingGroupOperators(child);
    }

    /// <summary>
    /// Clears navigation properties on a new SyncRuleMapping (and its sources) that reference
    /// existing entities, so that EF Core's Add() graph traversal does not attempt to insert them
    /// as duplicates. FK IDs remain set.
    /// </summary>
    private static void ClearMappingNavigationProperties(SyncRuleMapping mapping)
    {
        // Clear SyncRule nav property (SyncRuleId FK is set)
        mapping.SyncRule = null;

        // Clear target attribute nav properties (FK IDs are set)
        mapping.TargetMetaverseAttribute = null;
        mapping.TargetConnectedSystemAttribute = null;

        // Clear source attribute nav properties (FK IDs are set)
        foreach (var source in mapping.Sources)
        {
            source.ConnectedSystemAttribute = null;
            source.MetaverseAttribute = null;
        }
    }

    private static bool AreRunProfilesValid(ConnectedSystem connectedSystem)
    {
        if (connectedSystem == null)
            return false;

        if (connectedSystem.RunProfiles == null || connectedSystem.RunProfiles.Count == 0)
            return true;

        foreach (var runProfile in connectedSystem.RunProfiles)
        {
            if (!IsRunProfileValid(connectedSystem, runProfile))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if any Run Profile types are not supported by the connectors capabilities.
    /// </summary>
    private static bool IsRunProfileValid(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile)
    {
        if (runProfile == null)
            return false;

        if (runProfile.RunType == ConnectedSystemRunType.FullImport && !connectedSystem.ConnectorDefinition.SupportsFullImport)
            return false;

        if (runProfile.RunType == ConnectedSystemRunType.DeltaImport && !connectedSystem.ConnectorDefinition.SupportsDeltaImport)
            return false;

        if (runProfile.RunType == ConnectedSystemRunType.Export && !connectedSystem.ConnectorDefinition.SupportsExport)
            return false;

        // SPEC-1082 D10: Verification Mode only applies to Full Import. Validated here (not just in
        // the REST controller) so the portal, which calls this Application-layer method directly, is
        // also protected.
        if (runProfile.VerifyImportContentHashes && runProfile.RunType != ConnectedSystemRunType.FullImport)
            return false;

        return true;
    }
    #endregion
    
    #region Pending Exports
    /// <summary>
    /// Retrieves all the Pending Exports for a given Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System the Pending Exports relate to.</param>
    public async Task<List<PendingExport>> GetPendingExportsAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportsAsync(connectedSystemId);
    }
    
    /// <summary>
    /// Retrieves the count of how many Pending Export objects there are for a particular Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System the Pending Exports relate to.</param>
    public async Task<int> GetPendingExportsCountAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportsCountAsync(connectedSystemId);
    }

    /// <summary>
    /// Retrieves the count of Pending Export objects for a Connected System with optional filtering
    /// by change type and status. Optimised for fast counting without loading entity data.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="changeType">Optional change type to filter by (Create, Update, Delete).</param>
    /// <param name="status">Optional status to filter by (Pending, Failed, etc.).</param>
    /// <returns>The count of matching Pending Export objects.</returns>
    public async Task<int> GetPendingExportsFilteredCountAsync(
        int connectedSystemId,
        PendingExportChangeType? changeType = null,
        PendingExportStatus? status = null)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportsFilteredCountAsync(connectedSystemId, changeType, status);
    }

    /// <summary>
    /// Deletes a Pending Export object.
    /// </summary>
    /// <param name="pendingExport">The Pending Export to delete.</param>
    public async Task DeletePendingExportAsync(PendingExport pendingExport)
    {
        await Application.Repository.ConnectedSystems.DeletePendingExportAsync(pendingExport);
    }

    /// <summary>
    /// Updates a Pending Export object.
    /// Used when removing successfully applied attribute changes and updating error tracking.
    /// </summary>
    /// <param name="pendingExport">The Pending Export to update.</param>
    public async Task UpdatePendingExportAsync(PendingExport pendingExport)
    {
        await Application.Repository.ConnectedSystems.UpdatePendingExportAsync(pendingExport);
    }

    /// <summary>
    /// Creates multiple Pending Export objects in a single batch operation.
    /// Used to efficiently create Pending Exports during sync export evaluation.
    /// </summary>
    /// <param name="pendingExports">The Pending Exports to create.</param>
    /// <summary>
    /// Updates multiple Pending Export objects in a single batch operation.
    /// Used to efficiently update Pending Exports during sync.
    /// </summary>
    /// <param name="pendingExports">The Pending Exports to update.</param>
    public async Task UpdatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
    {
        await Application.Repository.ConnectedSystems.UpdatePendingExportsAsync(pendingExports);
    }

    /// <summary>
    /// Retrieves a page of Pending Export headers for a Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="page">Which page to return results for, i.e. 1-n.</param>
    /// <param name="pageSize">How many results to return per page.</param>
    /// <param name="statusFilters">Optional filter by one or more statuses.</param>
    /// <param name="searchQuery">Optional search query to filter by target object identifier, source MVO display name, or error message.</param>
    /// <param name="sortBy">Optional column to sort by (e.g., "changetype", "status", "created", "errors").</param>
    /// <param name="sortDescending">Whether to sort in descending order (default: true).</param>
    public async Task<PagedResultSet<PendingExportHeader>> GetPendingExportHeadersAsync(
        int connectedSystemId,
        int page,
        int pageSize,
        IEnumerable<PendingExportStatus>? statusFilters = null,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportHeadersAsync(
            connectedSystemId, page, pageSize, statusFilters, searchQuery, sortBy, sortDescending);
    }

    /// <summary>
    /// Retrieves a single Pending Export by ID with all related data.
    /// </summary>
    /// <param name="id">The unique identifier of the Pending Export.</param>
    public async Task<PendingExport?> GetPendingExportAsync(Guid id)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportAsync(id);
    }

    /// <summary>
    /// Returns which of the supplied Pending Export ids still exist. A Pending Export is deleted once
    /// it has been exported, so anything holding historical ids (a causality record naming the Pending
    /// Export an event created) needs this to tell a live row from one that has since been run.
    /// </summary>
    /// <param name="pendingExportIds">The ids to test. An empty list returns an empty result without querying.</param>
    public async Task<List<Guid>> GetExistingPendingExportIdsAsync(IList<Guid> pendingExportIds)
    {
        return await Application.Repository.ConnectedSystems.GetExistingPendingExportIdsAsync(pendingExportIds);
    }

    /// <summary>
    /// Retrieves a single Pending Export with capped multi-valued attribute changes for the detail page.
    /// Multi-valued attribute changes are capped at 10 per attribute; total counts are returned separately.
    /// </summary>
    /// <param name="id">The unique identifier of the Pending Export.</param>
    /// <returns>A <see cref="PendingExportDetailResult"/> containing the Pending Export and per-attribute
    /// total change counts, or null if not found.</returns>
    public async Task<PendingExportDetailResult?> GetPendingExportDetailAsync(Guid id)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportDetailAsync(id);
    }

    /// <summary>
    /// Retrieves a paged list of attribute value changes for a specific attribute on a Pending Export.
    /// Used by the MVA dialog for server-side pagination.
    /// </summary>
    /// <param name="pendingExportId">The unique identifier of the Pending Export.</param>
    /// <param name="attributeName">The name of the attribute to retrieve changes for.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">The number of results per page (max 100).</param>
    /// <param name="searchText">Optional search text to filter changes by string value.</param>
    /// <returns>A paged result set of attribute value changes.</returns>
    public async Task<PagedResultSet<PendingExportAttributeValueChange>> GetPendingExportAttributeChangesPagedAsync(
        Guid pendingExportId,
        string attributeName,
        int page,
        int pageSize,
        string? searchText = null)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportAttributeChangesPagedAsync(
            pendingExportId, attributeName, page, pageSize, searchText);
    }

    /// <summary>
    /// Retrieves a paged list of all attribute value changes across all attributes for a Pending Export.
    /// Used by the CSO detail page for server-side pagination of the Pending Exports table.
    /// </summary>
    /// <param name="pendingExportId">The unique identifier of the Pending Export.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">The number of results per page (max 100).</param>
    /// <param name="searchText">Optional search text to filter changes by value or attribute name.</param>
    /// <returns>A paged result set of attribute value changes.</returns>
    public async Task<PagedResultSet<PendingExportAttributeValueChange>> GetAllPendingExportChangesPagedAsync(
        Guid pendingExportId,
        int page,
        int pageSize,
        string? searchText = null)
    {
        return await Application.Repository.ConnectedSystems.GetAllPendingExportChangesPagedAsync(
            pendingExportId, page, pageSize, searchText);
    }

    /// <summary>
    /// Retrieves the Pending Export header (without attribute value changes) for a specific Connected System Object,
    /// along with the total count of attribute value changes.
    /// </summary>
    /// <param name="connectedSystemObjectId">The unique identifier of the Connected System Object.</param>
    /// <returns>A tuple of the PendingExport and total change count, or null if none exists.</returns>
    public async Task<(PendingExport PendingExport, int ChangeCount)?> GetPendingExportHeaderForObjectAsync(
        Guid connectedSystemObjectId)
    {
        using var span = Diagnostics.Diagnostics.Database.StartSpan("Cso.GetPendingExportHeaderForObject")
            .SetTag("csoId", connectedSystemObjectId);
        return await Application.Repository.ConnectedSystems.GetPendingExportHeaderByConnectedSystemObjectIdAsync(
            connectedSystemObjectId);
    }

    /// <summary>
    /// Retrieves the Pending Export for a specific Connected System Object.
    /// </summary>
    /// <param name="connectedSystemObjectId">The unique identifier of the Connected System Object.</param>
    /// <returns>The PendingExport for the CSO, or null if none exists.</returns>
    public async Task<PendingExport?> GetPendingExportForObjectAsync(Guid connectedSystemObjectId)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportByConnectedSystemObjectIdAsync(connectedSystemObjectId);
    }

    /// <summary>
    /// Retrieves the change history for a Connected System Object.
    /// </summary>
    /// <param name="connectedSystemObjectId">The unique identifier of the Connected System Object.</param>
    /// <param name="limit">Maximum number of changes to return. Defaults to 100.</param>
    /// <returns>List of changes ordered by ChangeTime descending (most recent first).</returns>
    public async Task<List<ConnectedSystemObjectChange>> GetConnectedSystemObjectChangesAsync(Guid connectedSystemObjectId, int limit = 100)
    {
        using var span = Diagnostics.Diagnostics.Database.StartSpan("Cso.GetChanges")
            .SetTag("csoId", connectedSystemObjectId)
            .SetTag("limit", limit);
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectChangesAsync(connectedSystemObjectId, limit);
    }

    /// <summary>
    /// Returns a page of change-history rows for a Connected System Object, projected into a flat DTO.
    /// Ordered by change time descending. <paramref name="pageSize"/> is clamped to [1, 100].
    /// </summary>
    public async Task<(List<CsoChangeHistoryDto> Items, int TotalCount)> GetCsoChangeHistoryAsync(Guid connectedSystemObjectId, int page, int pageSize)
    {
        if (page < 1)
            page = 1;
        if (pageSize < 1)
            pageSize = 1;
        if (pageSize > 100)
            pageSize = 100;

        using var span = Diagnostics.Diagnostics.Database.StartSpan("Cso.GetChangeHistory")
            .SetTag("csoId", connectedSystemObjectId)
            .SetTag("page", page)
            .SetTag("pageSize", pageSize);
        return await Application.Repository.ConnectedSystems.GetCsoChangeHistoryAsync(connectedSystemObjectId, page, pageSize);
    }

    /// <summary>
    /// Gets CSO changes where the CSO has been deleted (ChangeType = Deleted and ConnectedSystemObject is null).
    /// Used for the deleted objects browser.
    /// </summary>
    /// <param name="connectedSystemId">Optional filter by Connected System ID.</param>
    /// <param name="fromDate">Optional filter for changes on or after this date.</param>
    /// <param name="toDate">Optional filter for changes on or before this date.</param>
    /// <param name="externalIdSearch">Optional search term for external ID.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <returns>Paginated list of deleted CSO changes ordered by ChangeTime descending.</returns>
    public async Task<(List<ConnectedSystemObjectChange> Items, int TotalCount)> GetDeletedCsoChangesAsync(
        int? connectedSystemId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? externalIdSearch = null,
        int page = 1,
        int pageSize = 50)
    {
        return await Application.Repository.ConnectedSystems.GetDeletedCsoChangesAsync(
            connectedSystemId, fromDate, toDate, externalIdSearch, page, pageSize);
    }

    /// <summary>
    /// Gets the full change history for a deleted CSO by its change ID.
    /// </summary>
    /// <param name="changeId">The ID of the CSO change record.</param>
    /// <returns>List of all changes for that CSO ordered by ChangeTime descending.</returns>
    public async Task<List<ConnectedSystemObjectChange>> GetDeletedCsoChangeHistoryAsync(Guid changeId)
    {
        return await Application.Repository.ConnectedSystems.GetDeletedCsoChangeHistoryAsync(changeId);
    }

    /// <summary>
    /// Gets the deletion record for a Connected System Object that no longer exists, keyed on the object's
    /// own id. Backs the Deleted Objects page's deep link, reached from a causality view that holds the
    /// deleted record's id rather than its change record's.
    /// </summary>
    /// <param name="deletedConnectedSystemObjectId">The id the Connected System Object had before deletion.</param>
    /// <returns>The Deleted change record, or null when there is none for that id.</returns>
    public async Task<ConnectedSystemObjectChange?> GetDeletedCsoChangeAsync(Guid deletedConnectedSystemObjectId)
    {
        return await Application.Repository.ConnectedSystems.GetDeletedCsoChangeAsync(deletedConnectedSystemObjectId);
    }
    #endregion

    #region Synchronisation Rules
    public async Task<List<SyncRule>> GetSyncRulesAsync()
    {
        return await Application.Repository.ConnectedSystems.GetSyncRulesAsync();
    }

    /// <summary>
    /// Retrieves all the Synchronisation Rules for a given Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="includeDisabledSyncRules">Controls whether to return Synchronisation Rules that are disabled</param>
    public async Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabledSyncRules)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRulesAsync(connectedSystemId, includeDisabledSyncRules);
    }

    /// <summary>
    /// Retrieves lightweight Synchronisation Rule headers for list views, optionally filtered
    /// by Metaverse Object Type and/or direction.
    /// </summary>
    /// <param name="metaverseObjectTypeId">When supplied, only rules targeting this Metaverse Object Type are returned.</param>
    /// <param name="direction">When supplied, only rules with this direction are returned.</param>
    public async Task<IList<SyncRuleHeader>> GetSyncRuleHeadersAsync(int? metaverseObjectTypeId = null, SyncRuleDirection? direction = null)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRuleHeadersAsync(metaverseObjectTypeId, direction);
    }

    public async Task<SyncRule?> GetSyncRuleAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRuleAsync(id);
    }

    public async Task<bool> CreateOrUpdateSyncRuleAsync(SyncRule syncRule, MetaverseObject? initiatedBy, Activity? parentActivity = null, string? changeReason = null)
    {
        // validate the Synchronisation Rule
        if (syncRule == null)
            throw new NullReferenceException(nameof(syncRule));

        Log.Verbose($"CreateOrUpdateSyncRuleAsync() called for: {syncRule}");

        if (!syncRule.IsValid())
            return false;

        // reject any scoping criterion whose comparison operator is invalid for its attribute's data type
        // (for example "Starts With" on a DateTime). Hard-fail rather than persist a criterion the evaluator
        // can never satisfy, which would silently drop objects out of scope.
        ValidateScopingCriteriaOperators(syncRule);

        // remove any mutually-exclusive property combinations
        if (syncRule.Direction == SyncRuleDirection.Import)
        {
            // import rule cannot have these properties:
            syncRule.ProvisionToConnectedSystem = null;
            // Note: ObjectScopingCriteriaGroups IS valid for import rules - evaluates CSO attributes

            // In Simple Mode, matching rules are defined on the Connected System, not Synchronisation Rules
            // Clear any matching rules that may have been provided
            if (syncRule.ConnectedSystemId > 0)
            {
                // Core: only ObjectMatchingRuleMode (a scalar on the entity) is read below.
                var connectedSystem = syncRule.ConnectedSystem ??
                    await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(syncRule.ConnectedSystemId);

                if (connectedSystem?.ObjectMatchingRuleMode == ObjectMatchingRuleMode.ConnectedSystem)
                {
                    if (syncRule.ObjectMatchingRules.Count > 0)
                    {
                        Log.Warning("CreateOrUpdateSyncRuleAsync: Clearing {Count} matching rules from Synchronisation Rule {Id} " +
                            "because Connected System {CsId} is in Simple Mode",
                            syncRule.ObjectMatchingRules.Count, syncRule.Id, syncRule.ConnectedSystemId);
                        syncRule.ObjectMatchingRules.Clear();
                    }
                }
            }
        }
        else
        {
            // export rule cannot have these properties:
            syncRule.ObjectMatchingRules.Clear();
            syncRule.ProjectToMetaverse = null;
        }


        // Get Connected System name for activity context (Core: only .Name is read).
        var connectedSystemForContext = syncRule.ConnectedSystem ??
            (syncRule.ConnectedSystemId > 0 ? await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(syncRule.ConnectedSystemId) : null);

        // every crud operation must be tracked via an Activity
        var activity = new Activity
        {
            TargetName = syncRule.Name,
            TargetContext = connectedSystemForContext?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            ParentActivityId = parentActivity?.Id
        };

        if (syncRule.Id == 0)
        {
            // new Synchronisation Rule - create
            activity.TargetOperationType = ActivityTargetOperationType.Create;
            AuditHelper.SetCreated(syncRule, initiatedBy);
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
            // Detach references to existing entities (capture FK ids, null navs) so the insert adds only the new rows.
            DetachExistingEntityReferences(syncRule);
            await Application.Repository.ConnectedSystems.CreateSyncRuleAsync(syncRule);
        }
        else
        {
            // existing Synchronisation Rule - update
            activity.TargetOperationType = ActivityTargetOperationType.Update;
            AuditHelper.SetUpdated(syncRule, initiatedBy);
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);

            // Read before the write, or there is nothing left to compare the new configuration against.
            var previousInitialPassword = await Application.Repository.ConnectedSystems.GetSyncRuleInitialPasswordAsync(syncRule.Id);

            await Application.Repository.ConnectedSystems.UpdateSyncRuleAsync(syncRule);
            await ReleaseParkedInitialPasswordsIfDeliveryChangedAsync(syncRule, previousInitialPassword);
        }

        await CaptureConfigurationChangeAsync(activity, syncRule, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
        return true;
    }

    /// <summary>
    /// Sets a Synchronisation Rule's parked initial passwords retrying, when the save changed what would be
    /// delivered (#1221).
    /// <para>
    /// A policy rejection parks an account because the same configuration produces another password the target
    /// refuses for the same reason. The administrator correcting that configuration is the only event that makes
    /// another attempt worth making, and this is where it reaches the parked work: without it, parking is a
    /// one-way door and the account never gets a password.
    /// </para>
    /// <para>
    /// Gated on the configuration actually changing rather than firing on every save. An unrelated edit to the
    /// rule would otherwise set those accounts retrying against settings the target has already answered, which
    /// fails identically and inflates an attempt count that is supposed to count distinct configurations tried.
    /// </para>
    /// </summary>
    private async Task ReleaseParkedInitialPasswordsIfDeliveryChangedAsync(SyncRule syncRule, SyncRuleInitialPassword? previous)
    {
        if (SyncRuleInitialPassword.WouldDeliverTheSameAs(previous, syncRule.InitialPassword))
            return;

        await Application.InitialPasswords.ReleaseParkedForSyncRuleAsync(syncRule.Id);
    }

    /// <summary>
    /// Creates or updates a Synchronisation Rule (initiated by API key).
    /// </summary>
    public async Task<bool> CreateOrUpdateSyncRuleAsync(SyncRule syncRule, ApiKey initiatedByApiKey, Activity? parentActivity = null, string? changeReason = null)
    {
        if (syncRule == null)
            throw new NullReferenceException(nameof(syncRule));

        Log.Verbose($"CreateOrUpdateSyncRuleAsync() called for: {syncRule} (API key initiated)");

        if (!syncRule.IsValid())
            return false;

        // reject any scoping criterion whose comparison operator is invalid for its attribute's data type
        // (for example "Starts With" on a DateTime). Hard-fail rather than persist a criterion the evaluator
        // can never satisfy, which would silently drop objects out of scope.
        ValidateScopingCriteriaOperators(syncRule);

        if (syncRule.Direction == SyncRuleDirection.Import)
        {
            syncRule.ProvisionToConnectedSystem = null;

            if (syncRule.ConnectedSystemId > 0)
            {
                // Core: only ObjectMatchingRuleMode (a scalar on the entity) is read below.
                var connectedSystem = syncRule.ConnectedSystem ??
                    await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(syncRule.ConnectedSystemId);

                if (connectedSystem?.ObjectMatchingRuleMode == ObjectMatchingRuleMode.ConnectedSystem)
                {
                    if (syncRule.ObjectMatchingRules.Count > 0)
                    {
                        Log.Warning("CreateOrUpdateSyncRuleAsync: Clearing {Count} matching rules from Synchronisation Rule {Id} " +
                            "because Connected System {CsId} is in Simple Mode",
                            syncRule.ObjectMatchingRules.Count, syncRule.Id, syncRule.ConnectedSystemId);
                        syncRule.ObjectMatchingRules.Clear();
                    }
                }
            }
        }
        else
        {
            syncRule.ObjectMatchingRules.Clear();
            syncRule.ProjectToMetaverse = null;
        }

        // Get Connected System name for activity context (Core: only .Name is read).
        var connectedSystemForContext = syncRule.ConnectedSystem ??
            (syncRule.ConnectedSystemId > 0 ? await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(syncRule.ConnectedSystemId) : null);

        var activity = new Activity
        {
            TargetName = syncRule.Name,
            TargetContext = connectedSystemForContext?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            ParentActivityId = parentActivity?.Id
        };

        if (syncRule.Id == 0)
        {
            activity.TargetOperationType = ActivityTargetOperationType.Create;
            AuditHelper.SetCreated(syncRule, initiatedByApiKey);
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
            // Detach references to existing entities (capture FK ids, null navs) so the insert adds only the new rows.
            DetachExistingEntityReferences(syncRule);
            await Application.Repository.ConnectedSystems.CreateSyncRuleAsync(syncRule);
        }
        else
        {
            activity.TargetOperationType = ActivityTargetOperationType.Update;
            AuditHelper.SetUpdated(syncRule, initiatedByApiKey);
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
            await Application.Repository.ConnectedSystems.UpdateSyncRuleAsync(syncRule);
        }

        await CaptureConfigurationChangeAsync(activity, syncRule, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
        return true;
    }

    public async Task DeleteSyncRuleAsync(SyncRule syncRule, MetaverseObject? initiatedBy, string? changeReason = null)
    {
        // Get Connected System name for activity context (Core: only .Name is read).
        var connectedSystem = syncRule.ConnectedSystem ??
            (syncRule.ConnectedSystemId > 0 ? await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(syncRule.ConnectedSystemId) : null);

        // every crud operation must be tracked via an Activity
        var activity = new Activity
        {
            TargetName = syncRule.Name,
            TargetContext = connectedSystem?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.Delete,
            // Deletion capture deliberately records no SyncRuleId (the rule is about to cease to exist), which would
            // otherwise leave the deletion unattributable to any system and invisible to the "configuration changed
            // since last Full Synchronisation" indicator: a false negative on one of the most consequential changes
            // there is. The Connected System survives the deletion, so its id is the durable link. This does not
            // pollute the system's own configuration history, which additionally requires a captured version.
            ConnectedSystemId = syncRule.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        // Capture the attributes this import rule contributes to before deletion, so each can be re-densified after.
        var affectedAttributeIds = GetContributingImportAttributeIds(syncRule);

        await CaptureConfigurationDeletionAsync(activity, syncRule, changeReason);
        await Application.Repository.ConnectedSystems.DeleteSyncRuleAsync(syncRule);

        foreach (var attributeId in affectedAttributeIds)
            await RedensifyAttributePriorityAfterRemovalAsync(syncRule.MetaverseObjectTypeId, attributeId);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes a Synchronisation Rule (initiated by API key).
    /// </summary>
    public async Task DeleteSyncRuleAsync(SyncRule syncRule, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        // Get Connected System name for activity context (Core: only .Name is read).
        var connectedSystem = syncRule.ConnectedSystem ??
            (syncRule.ConnectedSystemId > 0 ? await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(syncRule.ConnectedSystemId) : null);

        var activity = new Activity
        {
            TargetName = syncRule.Name,
            TargetContext = connectedSystem?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.Delete,
            // See the MetaverseObject-initiated overload above: the Connected System id is what keeps a rule deletion
            // attributable once the rule itself is gone.
            ConnectedSystemId = syncRule.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        // Capture the attributes this import rule contributes to before deletion, so each can be re-densified after.
        var affectedAttributeIds = GetContributingImportAttributeIds(syncRule);

        await CaptureConfigurationDeletionAsync(activity, syncRule, changeReason);
        await Application.Repository.ConnectedSystems.DeleteSyncRuleAsync(syncRule);

        foreach (var attributeId in affectedAttributeIds)
            await RedensifyAttributePriorityAfterRemovalAsync(syncRule.MetaverseObjectTypeId, attributeId);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// The distinct target Metaverse attribute ids that an import Synchronisation Rule contributes to (#91), used to
    /// re-densify each affected attribute's priority list after the rule (and its mappings) are deleted. Empty for
    /// export rules, which do not participate in attribute priority.
    /// </summary>
    private static List<int> GetContributingImportAttributeIds(SyncRule syncRule) =>
        syncRule.Direction == SyncRuleDirection.Import
            ? syncRule.AttributeFlowRules
                .Where(m => m.TargetMetaverseAttributeId.HasValue)
                .Select(m => m.TargetMetaverseAttributeId!.Value)
                .Distinct()
                .ToList()
            : [];
    #endregion

    #region Object Matching Rules
    /// <summary>
    /// Gets the target context for an ObjectMatchingRule activity.
    /// Returns Connected System name for Mode A (rules on ConnectedSystemObjectType) or Synchronisation Rule name for Mode B.
    /// </summary>
    private async Task<string?> GetObjectMatchingRuleContextAsync(ObjectMatchingRule rule)
    {
        // Mode B: Rule is on a SyncRule - show the Synchronisation Rule name
        if (rule.SyncRule != null)
            return rule.SyncRule.Name;

        // Mode A: Rule is on a ConnectedSystemObjectType - show the Connected System name
        // First check if navigation property is loaded
        if (rule.ConnectedSystemObjectType?.ConnectedSystem != null)
            return rule.ConnectedSystemObjectType.ConnectedSystem.Name;

        // Navigation property not loaded - fetch the Connected System (Core: only .Name is read).
        if (rule.ConnectedSystemObjectType != null)
        {
            var connectedSystem = await GetConnectedSystemCoreAsync(rule.ConnectedSystemObjectType.ConnectedSystemId);
            return connectedSystem?.Name;
        }

        if (rule.ConnectedSystemObjectTypeId.HasValue)
        {
            var objectType = await Application.Repository.ConnectedSystems.GetObjectTypeAsync(rule.ConnectedSystemObjectTypeId.Value);
            if (objectType != null)
            {
                var connectedSystem = await GetConnectedSystemCoreAsync(objectType.ConnectedSystemId);
                return connectedSystem?.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a new Object Matching Rule for a Connected System Object Type.
    /// </summary>
    public async Task CreateObjectMatchingRuleAsync(ObjectMatchingRule rule, MetaverseObject? initiatedBy)
    {
        var activity = new Activity
        {
            TargetName = $"Rule for {rule.ConnectedSystemObjectType?.Name ?? "Object Type"}",
            TargetContext = await GetObjectMatchingRuleContextAsync(rule),
            TargetType = ActivityTargetType.ObjectMatchingRule,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        AuditHelper.SetCreated(rule, initiatedBy);
        await Application.Repository.ConnectedSystems.CreateObjectMatchingRuleAsync(rule);
        await CaptureObjectMatchingRuleConfigurationChangeAsync(activity, rule);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Creates a new Object Matching Rule (initiated by API key).
    /// </summary>
    public async Task CreateObjectMatchingRuleAsync(ObjectMatchingRule rule, ApiKey initiatedByApiKey)
    {
        var activity = new Activity
        {
            TargetName = $"Rule for {rule.ConnectedSystemObjectType?.Name ?? "Object Type"}",
            TargetContext = await GetObjectMatchingRuleContextAsync(rule),
            TargetType = ActivityTargetType.ObjectMatchingRule,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        AuditHelper.SetCreated(rule, initiatedByApiKey);
        await Application.Repository.ConnectedSystems.CreateObjectMatchingRuleAsync(rule);
        await CaptureObjectMatchingRuleConfigurationChangeAsync(activity, rule);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Object Matching Rule.
    /// </summary>
    public async Task UpdateObjectMatchingRuleAsync(ObjectMatchingRule rule, MetaverseObject? initiatedBy)
    {
        var activity = new Activity
        {
            TargetName = $"Rule for {rule.ConnectedSystemObjectType?.Name ?? "Object Type"}",
            TargetContext = await GetObjectMatchingRuleContextAsync(rule),
            TargetType = ActivityTargetType.ObjectMatchingRule,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        AuditHelper.SetUpdated(rule, initiatedBy);
        await Application.Repository.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule);
        await CaptureObjectMatchingRuleConfigurationChangeAsync(activity, rule);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Object Matching Rule (initiated by API key).
    /// </summary>
    public async Task UpdateObjectMatchingRuleAsync(ObjectMatchingRule rule, ApiKey initiatedByApiKey)
    {
        var activity = new Activity
        {
            TargetName = $"Rule for {rule.ConnectedSystemObjectType?.Name ?? "Object Type"}",
            TargetContext = await GetObjectMatchingRuleContextAsync(rule),
            TargetType = ActivityTargetType.ObjectMatchingRule,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        AuditHelper.SetUpdated(rule, initiatedByApiKey);
        await Application.Repository.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule);
        await CaptureObjectMatchingRuleConfigurationChangeAsync(activity, rule);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes an Object Matching Rule and its sources.
    /// </summary>
    public async Task DeleteObjectMatchingRuleAsync(ObjectMatchingRule rule, MetaverseObject? initiatedBy)
    {
        var activity = new Activity
        {
            TargetName = $"Rule for {rule.ConnectedSystemObjectType?.Name ?? "Object Type"}",
            TargetContext = await GetObjectMatchingRuleContextAsync(rule),
            TargetType = ActivityTargetType.ObjectMatchingRule,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        await Application.Repository.ConnectedSystems.DeleteObjectMatchingRuleAsync(rule);
        await CaptureObjectMatchingRuleConfigurationChangeAsync(activity, rule);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes an Object Matching Rule and its sources (initiated by API key).
    /// </summary>
    public async Task DeleteObjectMatchingRuleAsync(ObjectMatchingRule rule, ApiKey initiatedByApiKey)
    {
        var activity = new Activity
        {
            TargetName = $"Rule for {rule.ConnectedSystemObjectType?.Name ?? "Object Type"}",
            TargetContext = await GetObjectMatchingRuleContextAsync(rule),
            TargetType = ActivityTargetType.ObjectMatchingRule,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        await Application.Repository.ConnectedSystems.DeleteObjectMatchingRuleAsync(rule);
        await CaptureObjectMatchingRuleConfigurationChangeAsync(activity, rule);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Gets an Object Matching Rule by ID.
    /// </summary>
    public async Task<ObjectMatchingRule?> GetObjectMatchingRuleAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetObjectMatchingRuleAsync(id);
    }
    #endregion

    #region Helpers
    /// <summary>
    /// Builds a full error message including all inner exceptions.
    /// </summary>
    private static string GetFullExceptionMessage(Exception ex)
    {
        var messages = new List<string>();
        var current = ex;

        while (current != null)
        {
            messages.Add(current.Message);
            current = current.InnerException;
        }

        return string.Join(" --> ", messages);
    }
    #endregion
}
