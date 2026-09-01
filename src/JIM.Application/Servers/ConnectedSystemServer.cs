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
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
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

public partial class ConnectedSystemServer
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
    internal IConnector CreateConnector(ConnectedSystem connectedSystem)
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
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(id, withChangeTracking);
        if (connectedSystem == null)
            return null;

        // Each Container's own object count is stored; its subtree total is derived, so it has to be rebuilt on
        // load (#1276). Doing it here rather than at each call site is what stops the portal, the REST API and
        // PowerShell disagreeing about what a Subtree Container holds; a surface that forgot would silently report
        // the Container's own count and understate its branch.
        foreach (var partition in connectedSystem.Partitions ?? [])
            ContainerObjectCounts.RecalculateSubtreeTotals(partition);

        return connectedSystem;
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

        // Read before the write, so what parked password work is compared against is what was stored rather than
        // what is about to replace it.
        var passwordSynchronisationAsStored = await Application.Repository.ConnectedSystems.GetPasswordSynchronisationAsync(connectedSystem.Id);

        connectedSystem.SettingValuesValid = AreSettingValuesComplete(connectedSystem);

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
        await ReleaseParkedPasswordChangesIfDeliveryChangedAsync(connectedSystem, passwordSynchronisationAsStored);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Sets a Connected System's parked password changes retrying, when the save changed what would be delivered
    /// to it (#1119, requirement 3).
    /// <para>
    /// A change parks because the target refused it and the same configuration would produce the same refusal, so
    /// the administrator correcting that configuration is the only event that makes another attempt worth making.
    /// Without this, parking is a one-way door and a queued password never reaches the account it belongs to.
    /// </para>
    /// <para>
    /// Gated on delivery actually changing rather than firing on every save, matching the Synchronisation Rule
    /// precedent: an unrelated edit would otherwise retry against settings the target has already answered on,
    /// failing identically and inflating an attempt count that is supposed to count distinct configurations tried.
    /// </para>
    /// </summary>
    private async Task ReleaseParkedPasswordChangesIfDeliveryChangedAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemPasswordSynchronisation? asStored)
    {
        // A system that is not taking passwords has nothing to drain onto: requirement 2 has it accumulate while
        // it is off, and releasing work a disabled system will not deliver would only churn the queue.
        if (connectedSystem.PasswordSynchronisation is not { Enabled: true })
            return;

        if (ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(asStored, connectedSystem.PasswordSynchronisation))
            return;

        await Application.PasswordSynchronisation.ReleaseForDeliveryAsync(connectedSystem.Id);
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

        // Read before the write, for the same reason as the user-initiated overload above.
        var passwordSynchronisationAsStored = await Application.Repository.ConnectedSystems.GetPasswordSynchronisationAsync(connectedSystem.Id);

        connectedSystem.SettingValuesValid = AreSettingValuesComplete(connectedSystem);

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
        await ReleaseParkedPasswordChangesIfDeliveryChangedAsync(connectedSystem, passwordSynchronisationAsStored);
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

        // the selection is what this save changes, and some Connectors can only serve their settings for some
        // selections (#1424); refused here, before an Activity is opened for a save that will not happen.
        ThrowIfObjectTypeSelectionInvalid(connectedSystem, connectedSystem.ObjectTypes ?? []);

        connectedSystem.SettingValuesValid = AreSettingValuesComplete(connectedSystem);

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
        connectedSystem.SettingValuesValid = AreSettingValuesComplete(connectedSystem);
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
        connectedSystem.SettingValuesValid = AreSettingValuesComplete(connectedSystem);
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
        connectedSystem.SettingValuesValid = AreSettingValuesComplete(connectedSystem);
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
        connectedSystem.SettingValuesValid = AreSettingValuesComplete(connectedSystem);
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
        connectedSystem.SettingValuesValid = AreSettingValuesComplete(connectedSystem);
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
    public Task<ObjectMatchingModeSwitchResult> SwitchObjectMatchingModeAsync(
        ConnectedSystem connectedSystem,
        ObjectMatchingRuleMode newMode,
        MetaverseObject? initiatedBy)
    {
        return SwitchObjectMatchingModeInternalAsync(connectedSystem, newMode, initiatedBy, initiatedByApiKey: null);
    }

    /// <summary>
    /// Switches the Object Matching Rule mode for a Connected System (initiated by API key).
    /// Every Activity must be attributed to a security principal, so the API key initiator has to reach the
    /// Activities this switch creates; without this overload the switch could not be performed by automation at all.
    /// </summary>
    /// <param name="connectedSystem">The Connected System to update</param>
    /// <param name="newMode">The new Object Matching Rule mode</param>
    /// <param name="initiatedByApiKey">The API key initiating the change</param>
    /// <returns>Result containing details about the switch operation</returns>
    public Task<ObjectMatchingModeSwitchResult> SwitchObjectMatchingModeAsync(
        ConnectedSystem connectedSystem,
        ObjectMatchingRuleMode newMode,
        ApiKey initiatedByApiKey)
    {
        ArgumentNullException.ThrowIfNull(initiatedByApiKey);
        return SwitchObjectMatchingModeInternalAsync(connectedSystem, newMode, initiatedBy: null, initiatedByApiKey);
    }

    private async Task<ObjectMatchingModeSwitchResult> SwitchObjectMatchingModeInternalAsync(
        ConnectedSystem connectedSystem,
        ObjectMatchingRuleMode newMode,
        MetaverseObject? initiatedBy,
        ApiKey? initiatedByApiKey)
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
            result = await SwitchToAdvancedModeAsync(connectedSystem);
        }
        else
        {
            // Switching to Simple Mode - migrate rules from Synchronisation Rules to object types
            result = await SwitchToSimpleModeAsync(connectedSystem);
        }

        if (!result.Success)
            return result;

        // Update the Connected System mode
        connectedSystem.ObjectMatchingRuleMode = newMode;
        if (initiatedByApiKey != null)
            AuditHelper.SetUpdated(connectedSystem, initiatedByApiKey);
        else
            AuditHelper.SetUpdated(connectedSystem, initiatedBy);

        // Create activity for tracking
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = connectedSystem.Id
        };
        if (initiatedByApiKey != null)
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        else
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);

        await CaptureConfigurationChangeAsync(activity, connectedSystem, changeReason: null);
        await Application.Activities.CompleteActivityAsync(activity);

        return result;
    }

    private async Task<ObjectMatchingModeSwitchResult> SwitchToAdvancedModeAsync(ConnectedSystem connectedSystem)
    {
        var syncRulesUpdated = 0;
        // Change-tracked because the copies below are persisted through UpdateSyncRuleAsync, which refuses a
        // detached rule (nothing would save).
        var syncRules = await GetSyncRulesAsync(connectedSystem.Id, includeDisabledSyncRules: true, withChangeTracking: true);
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

            // Saved through the repository directly, not CreateOrUpdateSyncRuleAsync: the full save path's
            // simple-mode validation clears a Synchronisation Rule's own matching rules, and the system's mode
            // only flips to Advanced after this migration, so it would clear the very rules just copied. The
            // switch's own Activity and configuration change capture record the operation.
            await Application.Repository.ConnectedSystems.UpdateSyncRuleAsync(syncRule);
            syncRulesUpdated++;
        }

        // The switch strands two things silently without these warnings (#1569): the type-scoped rules it
        // deliberately retains (so a later switch back restores them), and export Synchronisation Rules, which it
        // never copies onto even though advanced-mode export matching reads only the export rule's own rules.
        var warnings = new List<string>();

        var objectTypesWithRetainedRules = connectedSystem.ObjectTypes?
            .Where(ot => ot.ObjectMatchingRules.Count > 0)
            .OrderBy(ot => ot.Name)
            .Select(ot => $"'{ot.Name}'")
            .ToList() ?? [];
        if (objectTypesWithRetainedRules.Count > 0)
            warnings.Add($"The Object Matching Rules on Connected System Object Type(s) {string.Join(", ", objectTypesWithRetainedRules)} " +
                "are no longer consulted in advanced matching mode. They are retained, and resume effect if the system returns to simple matching mode.");

        foreach (var exportSyncRule in syncRules.Where(sr => sr.Direction == SyncRuleDirection.Export && sr.ObjectMatchingRules.Count == 0).OrderBy(sr => sr.Name))
        {
            var exportObjectType = connectedSystem.ObjectTypes?.FirstOrDefault(ot => ot.Id == exportSyncRule.ConnectedSystemObjectTypeId);
            if (exportObjectType == null || exportObjectType.ObjectMatchingRules.Count == 0)
                continue;

            warnings.Add($"Export Synchronisation Rule '{exportSyncRule.Name}' has no Object Matching Rules of its own, so export matching " +
                "will not be attempted for it in advanced matching mode and provisioning will proceed as though no match existed. " +
                "Add Object Matching Rules to it if exported objects should join existing accounts.");
        }

        Log.Information("SwitchToAdvancedModeAsync: Copied matching rules to {Count} Synchronisation Rule(s), with {WarningCount} warning(s)",
            syncRulesUpdated, warnings.Count);
        return ObjectMatchingModeSwitchResult.ToAdvancedMode(syncRulesUpdated, warnings);
    }

    private async Task<ObjectMatchingModeSwitchResult> SwitchToSimpleModeAsync(ConnectedSystem connectedSystem)
    {
        var migrations = new List<ObjectTypeMatchingRuleMigration>();
        var objectTypesUpdated = 0;

        // Change-tracked because the clears below are persisted through UpdateSyncRuleAsync, which refuses a
        // detached rule; tracked removal also cascade-deletes the cleared rules rather than orphaning them.
        var syncRules = await GetSyncRulesAsync(connectedSystem.Id, includeDisabledSyncRules: true, withChangeTracking: true);
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
                else
                {
                    // The object type's existing rules take precedence, so the Synchronisation Rules' own rules
                    // are about to be cleared below without being migrated; that loss must be warned about (#1569).
                    migration.ObjectTypeRulesTookPrecedence = true;

                    Log.Warning("SwitchToSimpleModeAsync: Object type {ObjectType} already had matching rules; the rules on " +
                        "{SyncRuleCount} Synchronisation Rule(s) were discarded rather than migrated",
                        objectType.Name, migration.SyncRulesWithMatchingRules);
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

        // Deprovisioning impact (#809): the attribute values this system's Synchronisation Rules
        // contribute (by provenance) and the distinct Metaverse Objects holding them; what a
        // synchronised deprovisioning would recall or hand to a surviving contributor. Count-only.
        (preview.ContributedValueCount, preview.ContributedValueObjectCount) =
            await Application.Metaverse.GetContributedValueCountsByConnectedSystemAsync(connectedSystemId);

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
    /// Recorded on the delete Activity when an immediate deletion is issued against a system already fenced
    /// by a Synchronised Deprovisioning run: the finish-immediately exit (#809). Customer-facing wording; the
    /// audit trail must say what was and was not done for the objects the abandoned run never reached.
    /// </summary>
    public const string SynchronisedDeprovisioningAbandonedMessage =
        "A Synchronised Deprovisioning run was abandoned partway and the deletion completed immediately. " +
        "The remaining contributed attribute values were kept without provenance (they can no longer be recalled), " +
        "surviving contributors were not re-elected, and downstream systems were not corrected.";

    /// <summary>
    /// Evaluates a deletion request against a system already fenced (Status = Deleting), per the #809
    /// failed-run exit decision. Returns the result to answer with, or null when the request should proceed
    /// through the ordinary flow (re-queue for a deprovisioning retry; complete the deletion for
    /// finish-immediately). There is deliberately no un-fencing abort: a half-deprovisioned system never
    /// returns to service.
    /// </summary>
    /// <param name="existingDeletionTask">The persisted deletion task for the system, if one survives. A
    /// surviving task means the run is queued or executing, its checkpoint intact; a failed run's task row
    /// is removed at the worker's boundary, so no task means the retry must queue afresh.</param>
    /// <param name="synchronisedDeprovisioning">The mode of the incoming request.</param>
    /// <param name="connectedSystemId">The fenced system, for logging.</param>
    private static ConnectedSystemDeletionResult? EvaluateFencedDeletionRequest(
        DeleteConnectedSystemWorkerTask? existingDeletionTask,
        bool synchronisedDeprovisioning,
        int connectedSystemId)
    {
        if (synchronisedDeprovisioning)
        {
            // RETRY: re-issuing the deprovisioning delete resumes the run.
            if (existingDeletionTask == null)
                return null; // a failed run left no task; the caller queues a fresh one (completed batches deleted their objects, so the new run resumes from where the data stands).

            if (!existingDeletionTask.SynchronisedDeprovisioning)
            {
                Log.Warning("DeleteAsync: Connected System {Id} is fenced with an immediate deletion task {TaskId} queued; a deprovisioning retry cannot supersede it.",
                    connectedSystemId, existingDeletionTask.Id);
                return ConnectedSystemDeletionResult.Failed(
                    "An immediate deletion is already queued for this Connected System and will complete without deprovisioning; a Synchronised Deprovisioning request cannot supersede it.");
            }

            if (existingDeletionTask.Activity == null)
            {
                // Fast/hard: a persisted deletion task without its Activity is an integrity fault; attaching
                // to it would leave the caller with nothing to monitor.
                Log.Error("DeleteAsync: Connected System {Id} has deprovisioning task {TaskId} persisted with no Activity; refusing the retry.",
                    connectedSystemId, existingDeletionTask.Id);
                return ConnectedSystemDeletionResult.Failed(
                    "The queued Synchronised Deprovisioning task for this Connected System carries no Activity; investigate the task queue before retrying.");
            }

            // The run is already queued or executing; the retry attaches to it (checkpoint intact) rather
            // than queuing a second run against the same system.
            Log.Information("DeleteAsync: Connected System {Id} is fenced with deprovisioning task {TaskId} persisted; the retry attaches to it.",
                connectedSystemId, existingDeletionTask.Id);
            return ConnectedSystemDeletionResult.QueuedAsBackgroundJob(existingDeletionTask.Id, existingDeletionTask.Activity.Id);
        }

        // FINISH-IMMEDIATELY: the immediate delete on a fenced system abandons the remaining deprovisioning
        // work and completes the deletion. A run actively executing cannot be raced by a bulk delete, so
        // that one case refuses; a queued (not yet started) run is cancelled by the caller before proceeding.
        if (existingDeletionTask is { Status: WorkerTaskStatus.Processing })
        {
            Log.Warning("DeleteAsync: Connected System {Id} has deletion task {TaskId} currently executing; refusing the immediate deletion.",
                connectedSystemId, existingDeletionTask.Id);
            return ConnectedSystemDeletionResult.Failed(
                "A deletion task for this Connected System is currently executing; wait for it to complete or fail before requesting the immediate deletion.");
        }

        return null;
    }

    /// <summary>
    /// Deletes a Connected System and all its related data.
    /// Implements the queue-based deletion approach:
    /// 1. Sets status to Deleting (blocks new operations)
    /// 2. If sync is running, queues deletion to run after sync completes
    /// 3. Otherwise, executes deletion (sync or async based on CSO count)
    /// <para>
    /// A system already fenced (Status = Deleting) takes the failed-run exits instead (#809):
    /// deprovisioning mode RETRIES the run (attaching to the persisted task where one survives, its
    /// checkpoint intact, or queueing afresh), and immediate mode FINISHES the deletion immediately,
    /// abandoning the remaining deprovisioning work with the abandonment recorded on the Activity. There is
    /// no un-fencing abort: a half-deprovisioned system never returns to service.
    /// </para>
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to delete.</param>
    /// <param name="initiatedBy">The user who initiated the deletion.</param>
    /// <param name="deleteChangeHistory">Whether to delete change history for the deleted CSOs. Default: false (preserves audit trail).</param>
    /// <param name="changeReason">Optional reason for the deletion, recorded on the Activity.</param>
    /// <param name="synchronisedDeprovisioning">When true, the deletion runs as Synchronised Deprovisioning
    /// (#809): the system is fenced and the work ALWAYS queues to the worker (there is no synchronous
    /// small-system path), where every Connected System Object is processed through the synchronisation
    /// engine's obsoletion semantics before the deletion. False (the default) keeps the immediate deletion
    /// exactly as it is.</param>
    /// <returns>The result of the deletion request.</returns>
    public async Task<ConnectedSystemDeletionResult> DeleteAsync(int connectedSystemId, MetaverseObject? initiatedBy, bool deleteChangeHistory = false, string? changeReason = null, bool synchronisedDeprovisioning = false)
    {
        Log.Information("DeleteAsync: Starting deletion for Connected System {Id}, initiated by {User}, deleteChangeHistory={DeleteHistory}, synchronisedDeprovisioning={Deprovision}",
            connectedSystemId, initiatedBy?.NameOrId ?? "System", deleteChangeHistory, synchronisedDeprovisioning);

        // Get the Connected System (Core: only Name and Status are read, and Status is updated via the entity).
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
        {
            Log.Warning("DeleteAsync: Connected System {Id} not found", connectedSystemId);
            return ConnectedSystemDeletionResult.Failed($"Connected System with ID {connectedSystemId} not found.");
        }

        // A system already fenced (Status = Deleting) is a failed-run exit (#809): deprovisioning mode is
        // the RETRY, immediate mode is FINISH-IMMEDIATELY. There is no un-fencing abort.
        var alreadyFenced = connectedSystem.Status == ConnectedSystemStatus.Deleting;
        if (alreadyFenced)
        {
            var existingDeletionTask = await Application.Tasking.GetDeleteConnectedSystemWorkerTaskAsync(connectedSystemId);
            var fencedRefusalOrAttachment = EvaluateFencedDeletionRequest(existingDeletionTask, synchronisedDeprovisioning, connectedSystemId);
            if (fencedRefusalOrAttachment != null)
                return fencedRefusalOrAttachment;

            if (!synchronisedDeprovisioning && existingDeletionTask != null)
            {
                // Finish-immediately abandons the queued (not yet started) deprovisioning run: cancel its
                // task and Activity before completing the deletion through the ordinary flow below.
                Log.Information("DeleteAsync: Connected System {Id}: cancelling queued deletion task {TaskId}; the immediate deletion abandons the remaining deprovisioning work.",
                    connectedSystemId, existingDeletionTask.Id);
                await Application.Tasking.CancelWorkerTaskAsync(existingDeletionTask);
            }
        }
        else
        {
            // Set status to Deleting to block new operations
            connectedSystem.Status = ConnectedSystemStatus.Deleting;
            await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
            Log.Information("DeleteAsync: Set Connected System {Id} status to Deleting", connectedSystemId);
        }

        // The finish-immediately exit must be recorded on the Activity and must never un-fence on failure.
        var abandonsDeprovisioningRun = alreadyFenced && !synchronisedDeprovisioning;

        // Check for running sync operations
        var runningSyncTask = await Application.Repository.ConnectedSystems.GetRunningSyncTaskAsync(connectedSystemId);
        if (runningSyncTask != null)
        {
            // Queue deletion to run after sync completes
            Log.Information("DeleteAsync: Sync task {TaskId} is running for Connected System {CsId}. Queuing deletion.",
                runningSyncTask.Id, connectedSystemId);

            var deleteTask = initiatedBy != null
                ? DeleteConnectedSystemWorkerTask.ForUser(connectedSystemId, initiatedBy.Id, initiatedBy.NameOrId, evaluateMvoDeletionRules: true, deleteChangeHistory, synchronisedDeprovisioning)
                : new DeleteConnectedSystemWorkerTask(connectedSystemId, evaluateMvoDeletionRules: true, deleteChangeHistory, synchronisedDeprovisioning);
            deleteTask.AbandonsDeprovisioningRun = abandonsDeprovisioningRun;
            deleteTask.ChangeReason = changeReason;
            _ = await Application.Tasking.CreateWorkerTaskAsync(deleteTask);

            return ConnectedSystemDeletionResult.QueuedAfterSync(deleteTask.Id, deleteTask.Activity!.Id);
        }

        if (synchronisedDeprovisioning)
        {
            // Synchronised Deprovisioning ALWAYS queues (no synchronous small-system path): the run is
            // per-object synchronisation-engine work, checkpointed and resumable, and must execute on the
            // worker regardless of scale. The system is already fenced (Status = Deleting) above.
            Log.Information("DeleteAsync: Connected System {Id} deletion queued as Synchronised Deprovisioning.", connectedSystemId);

            var deprovisioningTask = initiatedBy != null
                ? DeleteConnectedSystemWorkerTask.ForUser(connectedSystemId, initiatedBy.Id, initiatedBy.NameOrId, evaluateMvoDeletionRules: true, deleteChangeHistory, synchronisedDeprovisioning: true)
                : new DeleteConnectedSystemWorkerTask(connectedSystemId, evaluateMvoDeletionRules: true, deleteChangeHistory, synchronisedDeprovisioning: true);
            deprovisioningTask.ChangeReason = changeReason;
            _ = await Application.Tasking.CreateWorkerTaskAsync(deprovisioningTask);

            return ConnectedSystemDeletionResult.QueuedAsBackgroundJob(deprovisioningTask.Id, deprovisioningTask.Activity!.Id);
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
            deleteTask.AbandonsDeprovisioningRun = abandonsDeprovisioningRun;
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
            TargetOperationType = ActivityTargetOperationType.Delete,
            // The finish-immediately exit (#809) must leave the abandonment on the audit trail.
            Message = abandonsDeprovisioningRun ? SynchronisedDeprovisioningAbandonedMessage : null
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

            if (alreadyFenced)
            {
                // The system was fenced by a deprovisioning run before this request: the fence must hold on
                // failure so a half-deprovisioned system never returns to service (#809). The deletion stays
                // retryable through the fenced-system exits.
                Log.Warning("DeleteAsync: Connected System {Id} deletion failed; keeping the Deleting fence (the system was part-way through Synchronised Deprovisioning).", connectedSystemId);
            }
            else
            {
                // Reset status so deletion can be retried
                connectedSystem.Status = ConnectedSystemStatus.Active;
                await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
            }

            return ConnectedSystemDeletionResult.Failed($"Failed to delete Connected System: {errorMessage}");
        }
    }

    /// <summary>
    /// Deletes a Connected System (initiated by API key). <paramref name="synchronisedDeprovisioning"/>
    /// carries the same semantics as the user-initiated overload: true always queues the worker-side
    /// Synchronised Deprovisioning run; false keeps the immediate deletion exactly as it is.
    /// </summary>
    public async Task<ConnectedSystemDeletionResult> DeleteAsync(int connectedSystemId, ApiKey initiatedByApiKey, bool deleteChangeHistory = false, string? changeReason = null, bool synchronisedDeprovisioning = false)
    {
        Log.Information("DeleteAsync: Starting deletion for Connected System {Id}, initiated by API key {ApiKeyName}, deleteChangeHistory={DeleteHistory}, synchronisedDeprovisioning={Deprovision}",
            connectedSystemId, initiatedByApiKey.Name, deleteChangeHistory, synchronisedDeprovisioning);

        // Get the Connected System (Core: only Name and Status are read, and Status is updated via the entity).
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
        {
            Log.Warning("DeleteAsync: Connected System {Id} not found", connectedSystemId);
            return ConnectedSystemDeletionResult.Failed($"Connected System with ID {connectedSystemId} not found.");
        }

        // A system already fenced (Status = Deleting) is a failed-run exit (#809): deprovisioning mode is
        // the RETRY, immediate mode is FINISH-IMMEDIATELY. There is no un-fencing abort.
        var alreadyFenced = connectedSystem.Status == ConnectedSystemStatus.Deleting;
        if (alreadyFenced)
        {
            var existingDeletionTask = await Application.Tasking.GetDeleteConnectedSystemWorkerTaskAsync(connectedSystemId);
            var fencedRefusalOrAttachment = EvaluateFencedDeletionRequest(existingDeletionTask, synchronisedDeprovisioning, connectedSystemId);
            if (fencedRefusalOrAttachment != null)
                return fencedRefusalOrAttachment;

            if (!synchronisedDeprovisioning && existingDeletionTask != null)
            {
                // Finish-immediately abandons the queued (not yet started) deprovisioning run: cancel its
                // task and Activity before completing the deletion through the ordinary flow below.
                Log.Information("DeleteAsync: Connected System {Id}: cancelling queued deletion task {TaskId}; the immediate deletion abandons the remaining deprovisioning work.",
                    connectedSystemId, existingDeletionTask.Id);
                await Application.Tasking.CancelWorkerTaskAsync(existingDeletionTask);
            }
        }
        else
        {
            // Set status to Deleting to block new operations
            connectedSystem.Status = ConnectedSystemStatus.Deleting;
            await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
            Log.Information("DeleteAsync: Set Connected System {Id} status to Deleting", connectedSystemId);
        }

        // The finish-immediately exit must be recorded on the Activity and must never un-fence on failure.
        var abandonsDeprovisioningRun = alreadyFenced && !synchronisedDeprovisioning;

        // Check for running sync operations
        var runningSyncTask = await Application.Repository.ConnectedSystems.GetRunningSyncTaskAsync(connectedSystemId);
        if (runningSyncTask != null)
        {
            Log.Information("DeleteAsync: Sync task {TaskId} is running for Connected System {CsId}. Queuing deletion.",
                runningSyncTask.Id, connectedSystemId);

            var deleteTask = DeleteConnectedSystemWorkerTask.ForApiKey(connectedSystemId, initiatedByApiKey.Id, initiatedByApiKey.Name, evaluateMvoDeletionRules: true, deleteChangeHistory, synchronisedDeprovisioning);
            deleteTask.AbandonsDeprovisioningRun = abandonsDeprovisioningRun;
            deleteTask.ChangeReason = changeReason;
            _ = await Application.Tasking.CreateWorkerTaskAsync(deleteTask);

            return ConnectedSystemDeletionResult.QueuedAfterSync(deleteTask.Id, deleteTask.Activity!.Id);
        }

        if (synchronisedDeprovisioning)
        {
            // Synchronised Deprovisioning ALWAYS queues (no synchronous small-system path); see the
            // user-initiated overload for the rationale. The system is already fenced above.
            Log.Information("DeleteAsync: Connected System {Id} deletion queued as Synchronised Deprovisioning.", connectedSystemId);

            var deprovisioningTask = DeleteConnectedSystemWorkerTask.ForApiKey(connectedSystemId, initiatedByApiKey.Id, initiatedByApiKey.Name, evaluateMvoDeletionRules: true, deleteChangeHistory, synchronisedDeprovisioning: true);
            deprovisioningTask.ChangeReason = changeReason;
            _ = await Application.Tasking.CreateWorkerTaskAsync(deprovisioningTask);

            return ConnectedSystemDeletionResult.QueuedAsBackgroundJob(deprovisioningTask.Id, deprovisioningTask.Activity!.Id);
        }

        // Get CSO count to determine sync vs async deletion
        var csoCount = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync(connectedSystemId);

        if (csoCount > BackgroundDeletionThreshold)
        {
            Log.Information("DeleteAsync: Connected System {Id} has {Count} CSOs (>{Threshold}). Queueing as background job.",
                connectedSystemId, csoCount, BackgroundDeletionThreshold);

            var deleteTask = DeleteConnectedSystemWorkerTask.ForApiKey(connectedSystemId, initiatedByApiKey.Id, initiatedByApiKey.Name, evaluateMvoDeletionRules: true, deleteChangeHistory);
            deleteTask.AbandonsDeprovisioningRun = abandonsDeprovisioningRun;
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
            TargetOperationType = ActivityTargetOperationType.Delete,
            // The finish-immediately exit (#809) must leave the abandonment on the audit trail.
            Message = abandonsDeprovisioningRun ? SynchronisedDeprovisioningAbandonedMessage : null
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

            if (alreadyFenced)
            {
                // The fence must hold on failure so a half-deprovisioned system never returns to service
                // (#809); the deletion stays retryable through the fenced-system exits.
                Log.Warning("DeleteAsync: Connected System {Id} deletion failed; keeping the Deleting fence (the system was part-way through Synchronised Deprovisioning).", connectedSystemId);
            }
            else
            {
                connectedSystem.Status = ConnectedSystemStatus.Active;
                await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
            }

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
    /// <summary>
    /// Whether a Connected System's settings are complete and well-formed: every required setting has a value, and
    /// every required-group and required-when constraint declared in the setting metadata is satisfied. Asked of the
    /// values alone, and never of the target system.
    /// </summary>
    /// <remarks>
    /// This is what <see cref="ConnectedSystem.SettingValuesValid"/> carries, and it is deliberately narrower than
    /// <see cref="ValidateConnectedSystemSettings"/>. That method also asks the Connector, whose own validation is a
    /// live probe: the LDAP Connector binds to the directory, the File Connector looks for the file. Persisting the
    /// answer to a live probe as a property of the configuration means an unreachable target marks stored settings
    /// invalid, and the portal gates the Schema, Partitions &amp; Containers and Matching tabs on this flag, so saving
    /// anything at all during a directory outage locked an administrator out of three tabs until somebody re-saved
    /// the Settings tab. It also put a network round trip on the path of every unrelated save.
    ///
    /// Whether the target answers is still reported, where it is actionable: the Settings tab and the settings-writing
    /// REST endpoint both call <see cref="ValidateConnectedSystemSettings"/> and surface what it finds.
    /// </remarks>
    public static bool AreSettingValuesComplete(ConnectedSystem connectedSystem)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        return ConnectorSettingValidator.Validate(connectedSystem.SettingValues).All(r => r.IsValid);
    }

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

            // some of what the settings say is about the Object Types selected for synchronisation (the SQL Connector's
            // Delta Import Mode, for one), so a connector that can judge that is shown the schema as it stands. no schema
            // yet is nothing selected, which is a valid answer, not a reason to skip the question.
            if (connector is IConnectorObjectTypeSelectionValidation selectionValidation)
                results.AddRange(selectionValidation.ValidateObjectTypeSelection(connectedSystem.SettingValues, connectedSystem.ObjectTypes ?? [], Log.Logger));
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }

        return results;
    }

    /// <summary>
    /// Refuses a schema selection the Connector says the settings cannot serve (#1424): selecting an Object Type
    /// that lacks what the configured Delta Import Mode needs, for example. Asked of the values and the schema
    /// alone, never of the target system, so it is safe on every save path. Connected Systems whose Connector
    /// cannot judge the selection, or that carry no settings to judge it against, are left alone.
    /// </summary>
    /// <param name="connectedSystem">Carries the Connector Definition and the setting values.</param>
    /// <param name="objectTypes">The schema as it will stand once the change is persisted.</param>
    /// <exception cref="InvalidSettingValuesException">The Connector refused the selection; the message is the Connector's own.</exception>
    private void ThrowIfObjectTypeSelectionInvalid(ConnectedSystem connectedSystem, IReadOnlyCollection<ConnectedSystemObjectType> objectTypes)
    {
        if (connectedSystem.ConnectorDefinition == null || connectedSystem.SettingValues is not { Count: > 0 })
            return;

        // connectors that hold connections or temporary files are disposable; the rest are not, and a null here is fine.
        var connector = CreateConnector(connectedSystem);
        using var disposableConnector = connector as IDisposable;

        if (connector is not IConnectorObjectTypeSelectionValidation selectionValidation)
            return;

        var problems = selectionValidation.ValidateObjectTypeSelection(connectedSystem.SettingValues, objectTypes, Log.Logger)
            .Where(result => !result.IsValid)
            .Select(result => result.ErrorMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToList();

        if (problems.Count > 0)
            throw new InvalidSettingValuesException(string.Join(" ", problems));
    }

    /// <summary>
    /// As <see cref="ThrowIfObjectTypeSelectionInvalid"/>, for a single Object Type being updated on its own (the
    /// REST API and PowerShell path). Only a selection can newly violate the settings, so a deselection is not
    /// judged; for a selection the persisted schema is loaded and this Object Type's pending state stands in for
    /// its persisted one, so the Connector judges the selection as it will be after the update.
    /// </summary>
    private async Task ThrowIfObjectTypeSelectionInvalidAsync(ConnectedSystemObjectType objectType)
    {
        if (!objectType.Selected)
            return;

        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(objectType.ConnectedSystemId);
        if (connectedSystem?.ConnectorDefinition == null || connectedSystem.SettingValues is not { Count: > 0 })
            return;

        var persisted = await Application.Repository.ConnectedSystems.GetObjectTypesAsync(objectType.ConnectedSystemId) ?? [];
        var objectTypes = persisted
            .Where(candidate => candidate.Id != objectType.Id)
            .Append(objectType)
            .ToList();

        ThrowIfObjectTypeSelectionInvalid(connectedSystem, objectTypes);
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
    /// Causes the associated Connector to be instantiated and the schema imported from the Connected System, in
    /// one call: retrieve, merge and persist. Additions and definition updates are persisted; removals are
    /// reported but deliberately retained (see issue #782), so nothing is deleted by a refresh. For a
    /// preview-then-decide flow, use <see cref="PreviewConnectedSystemSchemaRefreshAsync"/> followed by
    /// <see cref="ApplyConnectedSystemSchemaRefreshAsync(ConnectedSystem, SchemaRefreshResult, MetaverseObject?)"/>.
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
    /// Retrieves the Connected System's schema and merges it into the supplied instance <b>in memory only</b>,
    /// reporting what a refresh would change. Nothing is persisted and no Activity is recorded: a preview is a
    /// read, and the administrator decides what happens next. To persist exactly what this call merged, pass the
    /// same instance and result to
    /// <see cref="ApplyConnectedSystemSchemaRefreshAsync(ConnectedSystem, SchemaRefreshResult, MetaverseObject?)"/>;
    /// to discard, drop the instance and reload the Connected System.
    /// </summary>
    /// <param name="connectedSystem">The Connected System to preview a schema refresh for. Mutated in memory by
    /// the merge; callers who must keep an untouched instance should pass a freshly loaded one.</param>
    /// <returns>A result object describing what the refresh would change, including removals (which an apply
    /// would retain, not delete) and attribute definition changes.</returns>
    public async Task<SchemaRefreshResult> PreviewConnectedSystemSchemaRefreshAsync(ConnectedSystem connectedSystem)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        var connector = CreateConnector(connectedSystem);
        if (connector is not IConnectorSchema schemaConnector)
            throw new NotSupportedException($"The '{connectedSystem.ConnectorDefinition.Name}' connector does not support schema import.");

        var schema = await schemaConnector.GetSchemaAsync(connectedSystem.SettingValues, Log.Logger);
        var result = MergeSchemaIntoConnectedSystem(connectedSystem, schema);

        // Read while connected, exactly as the one-call import does, so an apply persists the same graph the
        // one-call path would have. Mutates the in-memory instance only.
        await DiscoverPasswordPolicyAsync(connector, connectedSystem, result);

        return result;
    }

    /// <summary>
    /// Persists a schema refresh previously retrieved and merged by
    /// <see cref="PreviewConnectedSystemSchemaRefreshAsync"/>, under an ImportSchema Activity. The pair is
    /// equivalent to <see cref="ImportConnectedSystemSchemaAsync(ConnectedSystem, MetaverseObject?)"/> with a
    /// decision point in the middle; the preview result completes the Activity so discovery warnings still reach
    /// the other surfaces.
    /// </summary>
    /// <param name="connectedSystem">The instance the preview merged into. Persisted as-is.</param>
    /// <param name="previewResult">The preview's result, used to complete the Activity.</param>
    /// <param name="initiatedBy">The user the change is attributed to.</param>
    public async Task ApplyConnectedSystemSchemaRefreshAsync(ConnectedSystem connectedSystem, SchemaRefreshResult previewResult, MetaverseObject? initiatedBy)
        => await ApplyConnectedSystemSchemaRefreshAsync(connectedSystem, previewResult, disableDependents: null, initiatedBy);

    /// <summary>
    /// As <see cref="ApplyConnectedSystemSchemaRefreshAsync(ConnectedSystem, SchemaRefreshResult, MetaverseObject?)"/>,
    /// attributed to an API key (the REST API and PowerShell path).
    /// </summary>
    public async Task ApplyConnectedSystemSchemaRefreshAsync(ConnectedSystem connectedSystem, SchemaRefreshResult previewResult, ApiKey initiatedByApiKey)
        => await ApplyConnectedSystemSchemaRefreshAsync(connectedSystem, previewResult, disableDependents: null, initiatedByApiKey);

    /// <summary>
    /// Applies a previewed schema refresh and then disables its dependents: the "Apply and Disable Dependents"
    /// option of the schema refresh decision (#1485). The schema is recorded exactly as the plain apply records
    /// it, then each Synchronisation Rule and Attribute Flow mapping the plan names is disabled with its
    /// recorded reason, under child Activities of the refresh so the history reads as one decision.
    /// </summary>
    /// <param name="connectedSystem">The instance the preview merged into. Persisted as-is.</param>
    /// <param name="previewResult">The preview's result, used to complete the refresh Activity.</param>
    /// <param name="disableDependents">What to disable, with per-item reasons; null applies the schema alone.</param>
    /// <param name="initiatedBy">The user the change is attributed to.</param>
    public async Task ApplyConnectedSystemSchemaRefreshAsync(ConnectedSystem connectedSystem, SchemaRefreshResult previewResult, SchemaRefreshDependents? disableDependents, MetaverseObject? initiatedBy)
    {
        var activity = await PersistSchemaRefreshUnderActivityAsync(connectedSystem, previewResult, initiatedBy, initiatedByApiKey: null);

        if (disableDependents is { HasAny: true })
            await DisableSchemaRefreshDependentsAsync(connectedSystem, disableDependents, activity, initiatedBy, initiatedByApiKey: null);
    }

    /// <summary>
    /// As <see cref="ApplyConnectedSystemSchemaRefreshAsync(ConnectedSystem, SchemaRefreshResult, SchemaRefreshDependents?, MetaverseObject?)"/>,
    /// attributed to an API key (the REST API and PowerShell path).
    /// </summary>
    public async Task ApplyConnectedSystemSchemaRefreshAsync(ConnectedSystem connectedSystem, SchemaRefreshResult previewResult, SchemaRefreshDependents? disableDependents, ApiKey initiatedByApiKey)
    {
        var activity = await PersistSchemaRefreshUnderActivityAsync(connectedSystem, previewResult, initiatedBy: null, initiatedByApiKey);

        if (disableDependents is { HasAny: true })
            await DisableSchemaRefreshDependentsAsync(connectedSystem, disableDependents, activity, initiatedBy: null, initiatedByApiKey);
    }

    /// <summary>
    /// Applies a previewed schema refresh and then removes its dependents: the "Apply and Remove" option of the
    /// schema refresh decision (#1485). The schema is recorded exactly as the plain apply records it; the
    /// Synchronisation Rules and mappings the plan names are deleted under child Activities of the refresh; and
    /// the dependent data is removed by a queued worker task, so a Connected System holding hundreds of
    /// thousands of objects never does that work in the request path. The task's ids are resolved from the
    /// preview's pre-refresh snapshot, because removed entries are no longer on the merged schema graph.
    /// </summary>
    /// <param name="connectedSystem">The instance the preview merged into. Persisted as-is.</param>
    /// <param name="previewResult">The preview's result: completes the refresh Activity and resolves the
    /// removed Object Type and attribute ids the data removal targets.</param>
    /// <param name="removeDependents">The configuration to delete, as previewed and confirmed.</param>
    /// <param name="initiatedBy">The user the change is attributed to.</param>
    /// <returns>The queued data-removal task's creation result, or null when the refresh removed no Object
    /// Types or attributes and there is therefore no data to remove.</returns>
    public async Task<WorkerTaskCreationResult?> ApplyConnectedSystemSchemaRefreshWithRemovalAsync(ConnectedSystem connectedSystem, SchemaRefreshResult previewResult, SchemaRefreshDependents removeDependents, MetaverseObject? initiatedBy)
    {
        ArgumentNullException.ThrowIfNull(removeDependents);

        var activity = await PersistSchemaRefreshUnderActivityAsync(connectedSystem, previewResult, initiatedBy, initiatedByApiKey: null);
        await RemoveSchemaRefreshDependentConfigurationAsync(connectedSystem, removeDependents, activity, initiatedBy, initiatedByApiKey: null);
        return await QueueSchemaRefreshRemovalTaskAsync(connectedSystem, previewResult, initiatedBy, initiatedByApiKey: null);
    }

    /// <summary>
    /// As <see cref="ApplyConnectedSystemSchemaRefreshWithRemovalAsync(ConnectedSystem, SchemaRefreshResult, SchemaRefreshDependents, MetaverseObject?)"/>,
    /// attributed to an API key (the REST API and PowerShell path).
    /// </summary>
    public async Task<WorkerTaskCreationResult?> ApplyConnectedSystemSchemaRefreshWithRemovalAsync(ConnectedSystem connectedSystem, SchemaRefreshResult previewResult, SchemaRefreshDependents removeDependents, ApiKey initiatedByApiKey)
    {
        ArgumentNullException.ThrowIfNull(removeDependents);

        var activity = await PersistSchemaRefreshUnderActivityAsync(connectedSystem, previewResult, initiatedBy: null, initiatedByApiKey);
        await RemoveSchemaRefreshDependentConfigurationAsync(connectedSystem, removeDependents, activity, initiatedBy: null, initiatedByApiKey);
        return await QueueSchemaRefreshRemovalTaskAsync(connectedSystem, previewResult, initiatedBy: null, initiatedByApiKey);
    }

    /// <summary>
    /// The shared middle of every schema refresh apply: records the refresh under an ImportSchema Activity and
    /// returns that Activity so the decision's follow-on work (disables, deletions) can parent itself under it.
    /// A failure discards the staged schema changes before recording it, exactly as the one-call import does.
    /// </summary>
    private async Task<Activity> PersistSchemaRefreshUnderActivityAsync(ConnectedSystem connectedSystem, SchemaRefreshResult previewResult, MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.ImportSchema,
            ConnectedSystemId = connectedSystem.Id
        };
        if (initiatedByApiKey != null)
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        else
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        try
        {
            if (initiatedByApiKey != null)
                await PersistConnectedSystemSchemaUpdateAsync(connectedSystem, initiatedByApiKey);
            else
                await PersistConnectedSystemSchemaUpdateAsync(connectedSystem, initiatedBy);
            await CaptureConnectedSystemConfigurationChangeAsync(activity, connectedSystem.Id);
            await CompleteSchemaImportActivityAsync(activity, previewResult);
        }
        catch (Exception ex)
        {
            // Discard staged schema changes before recording the failure: see the one-call import overloads.
            Application.Repository.ClearChangeTracker();
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }

        return activity;
    }

    /// <summary>
    /// Deletes the rules and mappings a destructive schema refresh invalidated, as the administrator chose on
    /// the refresh review (#1485). Deletion goes through the same audited paths a standalone delete uses
    /// (tombstone snapshot, attribute priority reconciliation), with each delete Activity parented under the
    /// refresh so the history reads as one decision. Mappings named by the plan that belong to a rule the plan
    /// also deletes are skipped: the database cascades them with their rule.
    /// </summary>
    private async Task RemoveSchemaRefreshDependentConfigurationAsync(
        ConnectedSystem connectedSystem,
        SchemaRefreshDependents dependents,
        Activity refreshActivity,
        MetaverseObject? initiatedBy,
        ApiKey? initiatedByApiKey)
    {
        if (!dependents.HasAny)
            return;

        var rules = await GetSyncRulesAsync(connectedSystem.Id, includeDisabledSyncRules: true);
        var rulesById = rules.ToDictionary(rule => rule.Id);
        var ruleIdsBeingDeleted = dependents.InvalidatedSyncRules.Select(entry => entry.SyncRuleId).ToHashSet();

        foreach (var entry in dependents.InvalidatedSyncRules.Where(e => rulesById.ContainsKey(e.SyncRuleId)))
        {
            var rule = rulesById[entry.SyncRuleId];
            // recallContributedValues: false keeps the schema refresh's Apply and Remove behaviour unchanged
            // (#1485): the rule deletes synchronously here, and the removal task's obsoletion pipeline is what
            // withdraws dependent data, recalling contributed values by their surviving system provenance.
            if (initiatedByApiKey != null)
                await DeleteSyncRuleAsync(rule, initiatedByApiKey, entry.Reason, refreshActivity.Id, recallContributedValues: false);
            else
                await DeleteSyncRuleAsync(rule, initiatedBy, entry.Reason, refreshActivity.Id, recallContributedValues: false);
        }

        foreach (var group in dependents.InvalidatedMappings
                     .Where(m => !ruleIdsBeingDeleted.Contains(m.SyncRuleId))
                     .GroupBy(m => m.SyncRuleId)
                     .Where(g => rulesById.ContainsKey(g.Key)))
        {
            var rule = rulesById[group.Key];
            var mappingsById = rule.AttributeFlowRules.ToDictionary(m => m.Id);
            foreach (var mapping in group.Where(e => mappingsById.ContainsKey(e.MappingId)).Select(e => mappingsById[e.MappingId]))
            {
                // The rule navigation gives the delete Activity its context (the rule's name) and the
                // post-delete configuration capture its id.
                mapping.SyncRule ??= rule;
                if (initiatedByApiKey != null)
                    await DeleteSyncRuleMappingAsync(mapping, initiatedByApiKey, refreshActivity.Id);
                else
                    await DeleteSyncRuleMappingAsync(mapping, initiatedBy, refreshActivity.Id);
            }
        }
    }

    /// <summary>
    /// Queues the data-removal half of "Apply and Remove" (#1485): a worker task carrying the pre-refresh ids
    /// of the Object Types and attributes the Connected System no longer reports. Returns null when the refresh
    /// removed neither, because there is then no data to remove and a queued no-op would only confuse the queue.
    /// </summary>
    private async Task<WorkerTaskCreationResult?> QueueSchemaRefreshRemovalTaskAsync(
        ConnectedSystem connectedSystem,
        SchemaRefreshResult previewResult,
        MetaverseObject? initiatedBy,
        ApiKey? initiatedByApiKey)
    {
        var (removedObjectTypeIds, removedAttributeIds) = ResolveRemovedSchemaIds(previewResult);
        if (removedObjectTypeIds.Count == 0 && removedAttributeIds.Count == 0)
            return null;

        var task = initiatedByApiKey != null
            ? SchemaRefreshRemovalWorkerTask.ForApiKey(connectedSystem.Id, removedObjectTypeIds, removedAttributeIds, initiatedByApiKey.Id, initiatedByApiKey.Name)
            : SchemaRefreshRemovalWorkerTask.ForUser(connectedSystem.Id, removedObjectTypeIds, removedAttributeIds,
                initiatedBy?.Id ?? Guid.Empty, initiatedBy?.CachedDisplayName ?? "Unknown");

        return await Application.Tasking.CreateWorkerTaskAsync(task);
    }

    /// <summary>
    /// Resolves what a refresh removed into the ids configuration and data reference, using the pre-refresh
    /// snapshot the merge captured: removed entries are no longer on the merged graph, and the schema rows
    /// themselves are retained on apply (see issue #782), so the pre-refresh ids remain valid to act on.
    /// Attributes of a wholly removed Object Type are deliberately not resolved; their values leave with their
    /// objects through the obsoletion pipeline.
    /// </summary>
    private static (List<int> RemovedObjectTypeIds, List<int> RemovedAttributeIds) ResolveRemovedSchemaIds(SchemaRefreshResult previewResult)
    {
        var preRefreshTypesByName = previewResult.PreRefreshSchema.ToDictionary(type => type.Name);

        var removedObjectTypeIds = previewResult.RemovedObjectTypes
            .Where(preRefreshTypesByName.ContainsKey)
            .Select(name => preRefreshTypesByName[name].Id)
            .ToList();

        var removedAttributeIds = previewResult.RemovedAttributes
            .Where(kvp => preRefreshTypesByName.ContainsKey(kvp.Key))
            .SelectMany(kvp =>
            {
                var attributeNames = kvp.Value.ToHashSet();
                return preRefreshTypesByName[kvp.Key].Attributes
                    .Where(attribute => attributeNames.Contains(attribute.Name))
                    .Select(attribute => attribute.Id);
            })
            .ToList();

        return (removedObjectTypeIds, removedAttributeIds);
    }

    /// <summary>
    /// Counts what the schema refresh decision's "Apply and Remove" option (#1485) would remove, so the
    /// administrator confirms with the numbers in front of them: Connected System Objects per removed Object
    /// Type (each would be marked Obsolete and flow through the standard deprovisioning pipeline) and stored
    /// values per removed attribute (each would be deleted). Uses the preview's pre-refresh snapshot to resolve
    /// ids, exactly as the removal itself does, so the preview and the action can never disagree on scope.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System the refresh was previewed against.</param>
    /// <param name="previewResult">The refresh preview whose removals are being counted.</param>
    public async Task<SchemaRefreshRemovalImpact> ComputeSchemaRefreshRemovalImpactAsync(int connectedSystemId, SchemaRefreshResult previewResult)
    {
        ArgumentNullException.ThrowIfNull(previewResult);

        var impact = new SchemaRefreshRemovalImpact();
        var preRefreshTypesByName = previewResult.PreRefreshSchema.ToDictionary(type => type.Name);

        foreach (var type in previewResult.RemovedObjectTypes.Where(preRefreshTypesByName.ContainsKey).Select(name => preRefreshTypesByName[name]))
        {
            impact.RemovedObjectTypes.Add(new SchemaRefreshRemovalTypeImpact
            {
                ObjectTypeId = type.Id,
                ObjectTypeName = type.Name,
                ConnectedSystemObjectCount = await Application.Repository.ConnectedSystems.GetLiveConnectedSystemObjectCountOfTypeAsync(connectedSystemId, type.Id)
            });
        }

        foreach (var kvp in previewResult.RemovedAttributes.Where(kvp => preRefreshTypesByName.ContainsKey(kvp.Key)))
        {
            var type = preRefreshTypesByName[kvp.Key];
            var attributeNames = kvp.Value.ToHashSet();
            foreach (var attribute in type.Attributes.Where(attribute => attributeNames.Contains(attribute.Name)))
            {
                impact.RemovedAttributes.Add(new SchemaRefreshRemovalAttributeImpact
                {
                    AttributeId = attribute.Id,
                    AttributeName = attribute.Name,
                    ObjectTypeName = type.Name,
                    StoredValueCount = await Application.Repository.ConnectedSystems.GetConnectedSystemAttributeValueCountAsync(connectedSystemId, attribute.Id)
                });
            }
        }

        return impact;
    }

    /// <summary>
    /// Executes a queued schema refresh removal (#1485), on the worker: marks every live Connected System
    /// Object of the removed Object Types Obsolete (they flow through disconnection, attribute recall, grace
    /// periods and Metaverse Deletion Rules on the next synchronisation run), deletes their Pending Exports
    /// exactly as import-detected deletions do, deletes every stored value of the removed attributes, and
    /// records one Run Profile Execution Item per obsoleted object on the task's Activity so the decision's
    /// history names every object it touched.
    /// </summary>
    /// <param name="task">The queued task, carrying the pre-refresh ids to act on and the Activity to report to.</param>
    public async Task<SchemaRefreshRemovalResult> ExecuteSchemaRefreshRemovalAsync(SchemaRefreshRemovalWorkerTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Activity == null)
            throw new InvalidDataException("ExecuteSchemaRefreshRemovalAsync: the task must carry the Activity it was queued under.");

        const int batchSize = 500;
        var result = new SchemaRefreshRemovalResult();
        var activity = task.Activity;

        // The schema rows of removed Object Types are retained on apply (issue #782), so their names resolve
        // from the persisted system; tolerate their absence rather than fail the whole removal over a label.
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(task.ConnectedSystemId);
        var typeNamesById = (connectedSystem?.ObjectTypes ?? []).ToDictionary(type => type.Id, type => type.Name);

        var idsByType = new Dictionary<int, List<Guid>>();
        foreach (var objectTypeId in task.RemovedObjectTypeIds)
            idsByType[objectTypeId] = await Application.Repository.ConnectedSystems.GetLiveConnectedSystemObjectIdsOfTypeAsync(task.ConnectedSystemId, objectTypeId);

        activity.ObjectsToProcess = idsByType.Values.Sum(ids => ids.Count);
        activity.ObjectsProcessed = 0;
        await Application.Repository.Activity.UpdateActivityAsync(activity);

        foreach (var (objectTypeId, csoIds) in idsByType)
        {
            var typeName = typeNamesById.GetValueOrDefault(objectTypeId, $"Removed Object Type {objectTypeId}");
            foreach (var batch in csoIds.Chunk(batchSize))
            {
                // Load before flipping the status: the display snapshots have to reflect the object as it was,
                // and the live-of-type query would no longer return it afterwards.
                var csos = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsByIdsNoTrackingAsync(task.ConnectedSystemId, batch);

                result.ConnectedSystemObjectsObsoleted += await Application.Repository.ConnectedSystems.ObsoleteConnectedSystemObjectsByIdsAsync(batch);
                result.PendingExportsRemoved += await Application.Repository.ConnectedSystems.DeletePendingExportsForConnectedSystemObjectsAsync(batch);

                var executionItems = csos.Select(cso =>
                {
                    var item = new ActivityRunProfileExecutionItem
                    {
                        Id = Guid.NewGuid(),
                        ObjectChangeType = ObjectChangeType.Deleted,
                        ConnectedSystemObjectId = cso.Id
                    };
                    item.SnapshotCsoDisplayFields(cso);
                    // The by-ids load does not include the Type navigation; the removed type's name is resolved above.
                    item.ObjectTypeSnapshot = typeName;
                    item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
                    {
                        OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected,
                        TargetEntityId = cso.Id,
                        TargetEntityDescription = cso.Name,
                        DetailMessage = $"Object Type '{typeName}' is no longer reported by the Connected System; marked Obsolete by the schema refresh removal.",
                        Ordinal = 0
                    });
                    item.OutcomeSummary = $"{ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected}:1";
                    return item;
                }).ToList();

                await Application.Activities.AddRunProfileExecutionItemsAsync(activity, executionItems);

                activity.ObjectsProcessed += batch.Length;
                await Application.Repository.Activity.UpdateActivityAsync(activity);
            }
        }

        if (task.RemovedAttributeIds.Count > 0)
            result.AttributeValuesRemoved = await Application.Repository.ConnectedSystems.DeleteConnectedSystemAttributeValuesByAttributeIdsAsync(task.ConnectedSystemId, task.RemovedAttributeIds);

        activity.TotalDeleted = result.ConnectedSystemObjectsObsoleted;
        activity.Message = $"Marked {result.ConnectedSystemObjectsObsoleted:N0} Connected System Object(s) Obsolete, removed {result.PendingExportsRemoved:N0} Pending Export(s) and deleted {result.AttributeValuesRemoved:N0} stored attribute value(s).";

        Log.Information(
            "ExecuteSchemaRefreshRemovalAsync: Connected System {ConnectedSystemId}: {ObsoletedCount} Connected System Object(s) marked Obsolete across {TypeCount} removed Object Type(s), {PendingExportCount} Pending Export(s) removed, {ValueCount} stored value(s) deleted across {AttributeCount} removed attribute(s).",
            task.ConnectedSystemId, result.ConnectedSystemObjectsObsoleted, task.RemovedObjectTypeIds.Count,
            result.PendingExportsRemoved, result.AttributeValuesRemoved, task.RemovedAttributeIds.Count);

        return result;
    }

    /// <summary>
    /// Disables the rules and mappings a destructive schema refresh invalidated, as the administrator chose on
    /// the refresh review. Deliberately targeted rather than routed through
    /// <see cref="CreateOrUpdateSyncRuleAsync(SyncRule, MetaverseObject?, Activity?, string?, Guid?)"/>: that
    /// path saves a whole rule and applies save-the-world semantics along the way (Simple Mode clears matching
    /// rules, provisioning switches initial passwords off), none of which a disable has asked for. Each change
    /// is audited as a child Activity of the refresh and captured into the rule's configuration history.
    /// </summary>
    private async Task DisableSchemaRefreshDependentsAsync(
        ConnectedSystem connectedSystem,
        SchemaRefreshDependents dependents,
        Activity refreshActivity,
        MetaverseObject? initiatedBy,
        ApiKey? initiatedByApiKey)
    {
        var rules = await GetSyncRulesAsync(connectedSystem.Id, includeDisabledSyncRules: true);
        var rulesById = rules.ToDictionary(rule => rule.Id);

        foreach (var entry in dependents.InvalidatedSyncRules.Where(e => rulesById.ContainsKey(e.SyncRuleId)))
        {
            var rule = rulesById[entry.SyncRuleId];
            rule.Enabled = false;
            rule.DisabledReason = entry.Reason;
            StampUpdated(rule, initiatedBy, initiatedByApiKey);
            await Application.Repository.ConnectedSystems.UpdateSyncRuleAsync(rule);
            await RecordSyncRuleDisableActivityAsync(rule, connectedSystem, refreshActivity, initiatedBy, initiatedByApiKey);
        }

        // Mappings are disabled through the bulk path in one write, then each affected rule's configuration
        // history is captured once, so a refresh disabling a dozen flows records one clean version per rule.
        var mappingsToDisable = new List<SyncRuleMapping>();
        var affectedRules = new List<SyncRule>();
        foreach (var group in dependents.InvalidatedMappings.GroupBy(m => m.SyncRuleId).Where(g => rulesById.ContainsKey(g.Key)))
        {
            var rule = rulesById[group.Key];
            var mappingsById = rule.AttributeFlowRules.ToDictionary(m => m.Id);
            var disabledAny = false;
            foreach (var entry in group.Where(e => mappingsById.ContainsKey(e.MappingId)))
            {
                var mapping = mappingsById[entry.MappingId];
                mapping.Enabled = false;
                mapping.DisabledReason = entry.Reason;
                StampUpdated(mapping, initiatedBy, initiatedByApiKey);
                mappingsToDisable.Add(mapping);
                disabledAny = true;
            }
            if (disabledAny)
                affectedRules.Add(rule);
        }

        if (mappingsToDisable.Count == 0)
            return;

        await Application.Repository.ConnectedSystems.UpdateSyncRuleMappingsAsync(mappingsToDisable);
        foreach (var rule in affectedRules)
            await RecordSyncRuleDisableActivityAsync(rule, connectedSystem, refreshActivity, initiatedBy, initiatedByApiKey);
    }

    private static void StampUpdated(IAuditable entity, MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey)
    {
        if (initiatedByApiKey != null)
            AuditHelper.SetUpdated(entity, initiatedByApiKey);
        else
            AuditHelper.SetUpdated(entity, initiatedBy);
    }

    /// <summary>
    /// Records one rule's disable (or its mappings' disables) as an audited Update Activity, parented under the
    /// refresh's ImportSchema Activity, and captures the rule's new configuration version.
    /// </summary>
    private async Task RecordSyncRuleDisableActivityAsync(
        SyncRule rule,
        ConnectedSystem connectedSystem,
        Activity refreshActivity,
        MetaverseObject? initiatedBy,
        ApiKey? initiatedByApiKey)
    {
        var activity = new Activity
        {
            TargetName = rule.Name,
            TargetContext = connectedSystem.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.Update,
            ParentActivityId = refreshActivity.Id
        };

        if (initiatedByApiKey != null)
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        else
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await CaptureSyncRuleConfigurationChangeAsync(activity, rule.Id);
        await Application.Activities.CompleteActivityAsync(activity);
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

        // Snapshot the pre-merge schema before the rebuild below: removed entries drop off the in-memory graph,
        // and dependent detection (#1485) needs their ids to resolve what a removal invalidates.
        result.PreRefreshSchema = (connectedSystem.ObjectTypes ?? []).Select(type => new SchemaRefreshPreRefreshType
        {
            Id = type.Id,
            Name = type.Name,
            Attributes = type.Attributes.Select(attribute => new SchemaRefreshPreRefreshAttribute
            {
                Id = attribute.Id,
                Name = attribute.Name
            }).ToList()
        }).ToList();

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

        // Declared reference targets are wired in a second pass after this loop, once every Object Type
        // instance exists in the graph: a reference may point at an Object Type declared after it (#1285).
        var declaredReferenceTargets = new List<(ConnectedSystemObjectTypeAttribute Attribute, string TargetName)>();

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
                        // Update existing attribute properties but preserve the ID. Definition changes (plurality
                        // and data type) are recorded on the result before being applied: they were applied
                        // silently for years, and a restated definition can invalidate an Attribute Flow mapping
                        // that was validated against the old one, so the administrator must get to see them.
                        existingAttribute.Description = schemaAttribute.Description;

                        if (existingAttribute.AttributePlurality != schemaAttribute.AttributePlurality)
                        {
                            result.AddChangedAttribute(schemaObjectType.Name, new SchemaAttributeDefinitionChange
                            {
                                AttributeName = existingAttribute.Name,
                                Aspect = SchemaAttributeChangeAspect.Plurality,
                                OldValue = existingAttribute.AttributePlurality.ToString(),
                                NewValue = schemaAttribute.AttributePlurality.ToString()
                            });
                        }
                        existingAttribute.AttributePlurality = schemaAttribute.AttributePlurality;

                        // A refresh restates what the Connector discovered and leaves what the administrator
                        // decided, which is why Selected and IsExternalId are absent from this block. A data
                        // type is normally discovered, so it belongs here; one an administrator chose does
                        // not, and overwriting it would silently undo the override. That matters more than it
                        // sounds: the mapping validator runs when a mapping is created rather than
                        // continuously, so a Synchronisation Rule validated against the chosen type would go
                        // on running against the reverted one, and the Attribute Flow, which switches on the
                        // source type, would write the value into the wrong column of the Metaverse Object.
                        // It would also sidestep the rule that an override is refused once values exist (#1354).
                        if (!existingAttribute.TypeSetByAdministrator)
                        {
                            if (existingAttribute.Type != schemaAttribute.Type)
                            {
                                result.AddChangedAttribute(schemaObjectType.Name, new SchemaAttributeDefinitionChange
                                {
                                    AttributeName = existingAttribute.Name,
                                    Aspect = SchemaAttributeChangeAspect.DataType,
                                    OldValue = existingAttribute.Type.ToString(),
                                    NewValue = schemaAttribute.Type.ToString()
                                });
                            }
                            existingAttribute.Type = schemaAttribute.Type;
                        }

                        existingAttribute.ClassName = schemaAttribute.ClassName;
                        existingAttribute.Writability = schemaAttribute.Writability;
                        existingAttribute.Required = schemaAttribute.Required;
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
                            Writability = schemaAttribute.Writability,
                            Required = schemaAttribute.Required
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
                        Writability = a.Writability,
                        Required = a.Required
                    }).ToList()
                };

                // All attributes in a new object type are considered "added"
                result.AddedAttributes[schemaObjectType.Name] = schemaObjectType.Attributes.Select(a => a.Name).ToList();
            }

            // Restate the declared reference target (connector-stated, like Writability): cleared here so a
            // withdrawn declaration cannot leave a stale target behind, and re-wired in the second pass below
            // when the schema still declares one (#1285).
            foreach (var schemaAttribute in schemaObjectType.Attributes)
            {
                var mergedAttribute = connectedSystemObjectType.Attributes.FirstOrDefault(a => a.Name == schemaAttribute.Name);
                if (mergedAttribute == null)
                    continue;

                mergedAttribute.ReferencedObjectType = null;
                mergedAttribute.ReferencedObjectTypeId = null;
                if (!string.IsNullOrWhiteSpace(schemaAttribute.ReferencesObjectTypeName))
                    declaredReferenceTargets.Add((mergedAttribute, schemaAttribute.ReferencesObjectTypeName.Trim()));
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

        // Second pass: wire each declared reference target to the merged Object Type instance it names. The
        // navigation carries the link because a brand-new target has no id until it is saved; EF assigns the
        // foreign key from the navigation for those, and the id is set directly where one already exists.
        // Case-insensitive to match the SQL Connector's own name handling. A declared target the schema does
        // not contain is a connector defect: reported as a discovery warning, never wired (#1285).
        foreach (var (attribute, targetName) in declaredReferenceTargets)
        {
            var target = connectedSystem.ObjectTypes.FirstOrDefault(ot => ot.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                result.DiscoveryWarnings.Add(
                    $"Attribute '{attribute.Name}' declares reference target Object Type '{targetName}', which the schema does not contain. The target was not recorded.");
                continue;
            }

            attribute.ReferencedObjectType = target;
            attribute.ReferencedObjectTypeId = target.Id > 0 ? target.Id : null;
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
    /// A Connected System's Password Synchronisation configuration (#1119), or null where it has never been
    /// configured, which is where every system starts.
    /// </summary>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<ConnectedSystemPasswordSynchronisation?> GetPasswordSynchronisationAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetPasswordSynchronisationAsync(connectedSystemId);
    }

    /// <summary>
    /// Where each of the named Connected Systems stands on Password Synchronisation (#1119, requirement 26), for
    /// a list that shows the state per row and lets an administrator sort and filter on it.
    /// </summary>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<Dictionary<int, PasswordSynchronisationState>> GetPasswordSynchronisationStatesAsync(IReadOnlyCollection<int> connectedSystemIds)
    {
        return await Application.Repository.ConnectedSystems.GetPasswordSynchronisationStatesAsync(connectedSystemIds);
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
    /// <para>
    /// A channel the Connected System's configuration forbids is a different matter, and is refused outright
    /// before anything is sent (#1119). It is a configuration fault rather than a transient one: trying again
    /// changes nothing until somebody either encrypts the connection or accepts an unencrypted one.
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
            // Asked once the channel exists, because what is being judged is the channel that opened rather than
            // the settings it was built from. The same rule governs the other two paths that write a password to
            // this system, so an administrator who turns the setting on gets one answer everywhere.
            if (PasswordChannelSecurity.RefusesChannel(connectedSystem, passwordConnector))
            {
                Log.Error("SetConnectedSystemObjectPasswordAsync: Connected System {ConnectedSystemId} requires a secure transport for passwords and the password channel is not encrypted; no password was sent",
                    connectedSystem.Id);
                return PasswordSetResult.Failed(PasswordSetFailureReason.ConfigurationFault,
                    PasswordChannelSecurity.RefusalMessage(connectedSystem));
            }

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

        // Container identity is global rather than per level, because a container can be matched at a level it did
        // not previously sit at: that is what a move is. Both of these are therefore collected across the whole
        // refresh and applied afterwards (#1318).
        var matchedContainers = new HashSet<ConnectedSystemContainer>();
        var containersToReparent = new List<(ConnectedSystemContainer Container, ConnectedSystemPartition Partition, ConnectedSystemContainer? NewParent)>();

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

                // Match containers recursively within this partition. Reparenting and removals are deliberately
                // deferred to passes of their own, once every partition has been matched; see the comment on
                // MatchContainersRecursive.
                existing.Containers ??= new HashSet<ConnectedSystemContainer>();
                MatchContainersRecursive(
                    existing,
                    parentContainer: null,
                    discovered.Containers,
                    result,
                    existingContainerLookup,
                    existingContainerStableIdLookup,
                    matchedContainers,
                    containersToReparent);
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

                // Record every container in the new partition as matched, for the same reason a new container
                // under an existing partition is (see MatchContainersRecursive): the removal pass below walks
                // every partition, this one included, and deletes anything it does not find in matchedContainers.
                // A container the directory has just reported is matched by definition. Without this, the first
                // retrieval on a Connected System discovered the whole hierarchy, reported it as added, and then
                // threw it away before the save, so nothing appeared until the button was pressed again (#1369).
                foreach (var newContainer in newPartition.Containers)
                    MarkContainerTreeMatched(newContainer, matchedContainers);

                // Count all new containers within the new partition
                CountAddedContainersRecursive(newPartition.Containers, result.AddedContainers);
            }
        }

        // Apply the moves now that every partition has been matched, so that a container is only ever detached from
        // its old home after everything that might claim it has had its say.
        foreach (var (container, partition, newParent) in containersToReparent)
            ReparentContainer(container, connectedSystem, partition, newParent);

        // Only now remove what the directory no longer holds. Running this last is what keeps a moved container:
        // by this point it sits under its new parent, so the pass walking its old parent no longer sees it.
        foreach (var partition in connectedSystem.Partitions.Where(p => p.Containers != null))
            RemoveUnmatchedContainers(partition.Containers!, matchedContainers, result);

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
    /// Recursively matches discovered containers against stored ones, adding those the directory has gained and
    /// noting those it has moved. Removes nothing and reparents nothing.
    /// </summary>
    /// <remarks>
    /// Matching, reparenting and removal are three passes rather than one because container identity is global
    /// while the hierarchy is walked level by level. A container resolves by its stable identifier wherever it now
    /// sits, so a moved one is matched at a level it did not previously belong to; a single pass that also removed
    /// per level therefore deleted it from its old parent, either before or after the level that claimed it
    /// depending only on the order the Connector happened to return its containers in. Splitting the passes is what
    /// makes the outcome independent of that order (#1318).
    /// </remarks>
    private static void MatchContainersRecursive(
        ConnectedSystemPartition partition,
        ConnectedSystemContainer? parentContainer,
        List<ConnectorContainer> discoveredContainers,
        HierarchyRefreshResult result,
        Dictionary<string, ConnectedSystemContainer> globalLookup,
        Dictionary<string, ConnectedSystemContainer> globalStableIdLookup,
        HashSet<ConnectedSystemContainer> matchedContainers,
        List<(ConnectedSystemContainer Container, ConnectedSystemPartition Partition, ConnectedSystemContainer? NewParent)> containersToReparent)
    {
        foreach (var discovered in discoveredContainers)
        {
            if (TryResolveExistingContainer(discovered, globalLookup, globalStableIdLookup, out var existing))
            {
                matchedContainers.Add(existing);

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

                // Check for move (a different parent, or none where there was one). Compared by reference rather
                // than by Distinguished Name: the parent's own name may be being rewritten in this same refresh.
                if (!ReferenceEquals(existing.ParentContainer, parentContainer))
                {
                    result.MovedContainers.Add(new HierarchyMoveItem
                    {
                        ExternalId = discovered.Id,
                        Name = discovered.Name,
                        OldParentExternalId = existing.ParentContainer?.ExternalId,
                        NewParentExternalId = parentContainer?.ExternalId
                    });

                    containersToReparent.Add((existing, partition, parentContainer));
                }

                // Recurse into children
                MatchContainersRecursive(
                    partition,
                    existing,
                    discovered.ChildContainers,
                    result,
                    globalLookup,
                    globalStableIdLookup,
                    matchedContainers,
                    containersToReparent);
            }
            else
            {
                // NEW container: build it and put it where the directory says it belongs.
                var newContainer = BuildConnectedSystemContainerTree(discovered);
                if (parentContainer != null)
                {
                    parentContainer.AddChildContainer(newContainer);
                }
                else
                {
                    newContainer.Partition = partition;
                    partition.Containers ??= [];
                    partition.Containers.Add(newContainer);
                }

                // Record it and its whole subtree as matched, or the removal pass deletes it again in this same
                // refresh: "not matched" is how that pass recognises a container that has left the directory. A
                // container created since the last refresh was once reported as added and then silently dropped, so
                // it never appeared on the Partitions and Containers tab to be selected.
                MarkContainerTreeMatched(newContainer, matchedContainers);

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
    }

    /// <summary>
    /// Marks a container and everything beneath it as present in the directory.
    /// </summary>
    private static void MarkContainerTreeMatched(ConnectedSystemContainer container, HashSet<ConnectedSystemContainer> matchedContainers)
    {
        matchedContainers.Add(container);

        foreach (var childContainer in container.ChildContainers)
            MarkContainerTreeMatched(childContainer, matchedContainers);
    }

    /// <summary>
    /// Moves a stored container to the parent the directory now reports for it, preserving everything the container
    /// carries: its selection, its exclusion, its scope and its own descendants.
    /// </summary>
    /// <remarks>
    /// Both sides of the relationship are maintained explicitly, navigation and foreign key alike. Only top-level
    /// containers carry a partition and only nested ones carry a parent container, so a move between those two
    /// shapes has to clear one pair as it sets the other; leaving a stale foreign key behind would have the row
    /// claim two homes. The detach searches the whole Connected System rather than the partition being merged,
    /// because a container's old home is wherever it was, not wherever it is going.
    /// </remarks>
    private static void ReparentContainer(
        ConnectedSystemContainer container,
        ConnectedSystem connectedSystem,
        ConnectedSystemPartition partition,
        ConnectedSystemContainer? newParent)
    {
        DetachContainerFromItsCurrentHome(container, connectedSystem);

        if (newParent != null)
        {
            newParent.AddChildContainer(container);
            container.ParentContainerId = newParent.Id;
            container.Partition = null;
            container.PartitionId = null;
        }
        else
        {
            container.ParentContainer = null;
            container.ParentContainerId = null;
            container.Partition = partition;
            container.PartitionId = partition.Id;
            partition.Containers ??= [];
            partition.Containers.Add(container);
        }
    }

    /// <summary>
    /// Removes a container from whichever collection currently holds it.
    /// </summary>
    /// <remarks>
    /// The parent navigation answers this on a graph EF Core has fixed up, but it is not relied on alone: the portal
    /// loads the Connected System in one scope and saves it in another, and a navigation that was never included
    /// reads as null. Falling back to a search of the stored hierarchy costs one walk per moved container, which is
    /// nothing against the cost of leaving a container in two collections at once.
    /// </remarks>
    private static void DetachContainerFromItsCurrentHome(ConnectedSystemContainer container, ConnectedSystem connectedSystem)
    {
        if (container.ParentContainer != null && container.ParentContainer.ChildContainers.Remove(container))
            return;

        foreach (var partition in connectedSystem.Partitions ?? [])
        {
            if (partition.Containers?.Remove(container) == true)
                return;

            if (partition.Containers != null && RemoveFromDescendants(partition.Containers, container))
                return;
        }
    }

    private static bool RemoveFromDescendants(IEnumerable<ConnectedSystemContainer> containers, ConnectedSystemContainer container)
    {
        foreach (var candidate in containers)
        {
            if (candidate.ChildContainers.Remove(container))
                return true;

            if (RemoveFromDescendants(candidate.ChildContainers, container))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Removes every container the directory no longer holds, walking the hierarchy after the moves have settled.
    /// </summary>
    private static void RemoveUnmatchedContainers(
        HashSet<ConnectedSystemContainer> containers,
        HashSet<ConnectedSystemContainer> matchedContainers,
        HierarchyRefreshResult result)
    {
        foreach (var container in containers.ToList())
        {
            if (matchedContainers.Contains(container))
            {
                RemoveUnmatchedContainers(container.ChildContainers, matchedContainers, result);
            }
            else
            {
                // Anything still beneath this container is genuinely gone too: a descendant that moved elsewhere
                // has already been detached from it by the reparenting pass.
                CollectRemovedContainerRecursive(container, result);
                containers.Remove(container);
            }
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

        // Each of the things below that can go partly right writes a warning here rather than straight onto the
        // Activity, which only has room for one message: a hierarchy that both failed to enumerate and failed to
        // count used to report whichever happened to run last.
        var warnings = new List<string>();

        // Counted against the same directory state that produced the hierarchy, and before the merge, because the
        // Connector answers in terms of its own partitions. Applied after the merge, when JIM's own Containers
        // exist to hang the figures on.
        var objectCounts = await CountContainerObjectsAsync(connectedSystem, partitionsConnector, partitions, warnings);
        if (partitions.Count == 0)
        {
            // Zero partitions almost always means the connector could not enumerate them (connection,
            // authentication, or scope problem) rather than a genuinely empty directory. Warn the admin;
            // MergeHierarchy deliberately leaves the existing hierarchy untouched in this case (#876).
            warnings.Add("The hierarchy refresh retrieved no partitions from the Connected System, so the existing hierarchy was left unchanged. This usually indicates a connection, authentication, or scope problem rather than an empty directory; check the Connected System's settings and connectivity, then try again.");
        }

        // A partition whose count was cut short has its figures discarded, so say so. Blank counts otherwise look
        // exactly like a Connected System that cannot count at all, and only one of those is worth acting on.
        var incompleteCounts = ContainerObjectCounts.DescribeIncompleteCounts(partitions
            .Where(partition => objectCounts.ContainsKey(partition.Id))
            .Select(partition => (partition.Name, objectCounts[partition.Id])));

        if (incompleteCounts != null)
            warnings.Add(incompleteCounts);

        if (warnings.Count > 0)
            activity.WarningMessage = string.Join(" ", warnings);

        // Merge discovered partitions with existing ones, preserving user selections
        var result = MergeHierarchy(connectedSystem, partitions);

        ApplyContainerObjectCounts(connectedSystem, objectCounts);

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

    /// <summary>
    /// What an administrator is told when the count threw rather than merely stopping short. Held as a constant so
    /// that a Connected System whose every partition fails says it once instead of once per partition.
    /// </summary>
    private const string CountFailedWarning =
        "The hierarchy was retrieved, but the objects in each Container could not be counted. The Containers are correct; their object counts are not shown.";

    /// <summary>
    /// Asks the Connector how many objects each Container holds, one partition at a time (#1276).
    /// </summary>
    /// <remarks>
    /// Folded into the hierarchy retrieval rather than offered as a second action, so the Containers and their
    /// figures always describe the same moment. A Connector that cannot answer returns nothing and the tab simply
    /// shows no counts.
    ///
    /// A failure here must not fail the refresh. The hierarchy is what an administrator asked for and it has
    /// already been retrieved; losing it because a supplementary count timed out would be a poor trade. The
    /// Activity carries the warning instead.
    /// </remarks>
    /// <returns>Direct counts per Container identifier, keyed by partition external id. Empty when nothing counted.</returns>
    private static async Task<Dictionary<string, ConnectorContainerObjectCountResult>> CountContainerObjectsAsync(
        ConnectedSystem connectedSystem,
        IConnectorPartitions partitionsConnector,
        List<ConnectorPartition> partitions,
        List<string> warnings)
    {
        var countsByPartition = new Dictionary<string, ConnectorContainerObjectCountResult>(StringComparer.OrdinalIgnoreCase);
        if (partitionsConnector is not IConnectorContainerObjectCounts countingConnector)
            return countsByPartition;

        // A count across Object Types JIM will never import is not a number anyone can act on, so nothing is
        // counted until the administrator has said what they are managing. The Schema tab sits before Partitions
        // and Containers, so by the time anyone is choosing Containers this is normally already answered.
        var objectTypeNames = (connectedSystem.ObjectTypes ?? [])
            .Where(objectType => objectType.Selected)
            .Select(objectType => objectType.Name)
            .ToList();

        if (objectTypeNames.Count == 0)
            return countsByPartition;

        foreach (var partition in partitions)
        {
            try
            {
                countsByPartition[partition.Id] = await countingConnector.GetContainerObjectCountsAsync(
                    connectedSystem.SettingValues, partition, objectTypeNames, Log.Logger, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "CountContainerObjectsAsync: could not count objects in partition {Partition} of {ConnectedSystem}",
                    LogSanitiser.Sanitise(partition.Name), connectedSystem.Id);

                if (!warnings.Contains(CountFailedWarning))
                    warnings.Add(CountFailedWarning);
            }
        }

        return countsByPartition;
    }

    /// <summary>
    /// Hangs the Connector's direct counts on JIM's own Containers, and rolls up each one's subtree total.
    /// </summary>
    /// <remarks>
    /// A partition the Connector reported no counts for is left uncounted rather than zeroed: "not counted" and
    /// "counted, and empty" are different statements, and only one of them says a Container holds nothing. An
    /// incomplete count is discarded entirely for the same reason, because figures short of the truth read as whole
    /// and understate what deselecting a Container costs.
    /// </remarks>
    private static void ApplyContainerObjectCounts(
        ConnectedSystem connectedSystem,
        Dictionary<string, ConnectorContainerObjectCountResult> countsByPartition)
    {
        foreach (var partition in connectedSystem.Partitions ?? [])
        {
            var counted = countsByPartition.TryGetValue(partition.ExternalId, out var result) && result.Complete
                ? result.DirectCountsByContainerIdentifier
                : null;

            ContainerObjectCounts.Apply(partition, counted);
        }
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
    /// <summary>
    /// The names of a Connected System's Object Types, keyed by id: a lightweight projection for resolving
    /// a Reference attribute's declared target name (#1285) without loading a navigation into an entity
    /// graph a mutating path might attach.
    /// </summary>
    public async Task<Dictionary<int, string>> GetObjectTypeNamesAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetObjectTypeNamesAsync(connectedSystemId);
    }

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
    /// Sets exactly which auxiliary classes an Object Type carries, merging in what is new to the set and
    /// withdrawing what has left it.
    /// </summary>
    /// <remarks>
    /// The whole set rather than one class at a time, so that the portal, the REST API and PowerShell reach the
    /// same state through the same validation. An empty set withdraws every selection.
    /// </remarks>
    /// <param name="objectTypeId">The Object Type the classes are merged into.</param>
    /// <param name="extensionObjectTypeIds">The Object Types of the auxiliary classes it should carry.</param>
    public async Task<AuxiliaryClassSelectionResult> SetObjectTypeExtensionsAsync(
        int objectTypeId,
        IEnumerable<int> extensionObjectTypeIds)
    {
        var objectType = await GetObjectTypeAsync(objectTypeId);
        if (objectType == null)
            return AuxiliaryClassSelectionResult.Refused($"Object Type {objectTypeId} does not exist.");

        if (!objectType.ManagesClassMembership())
            return AuxiliaryClassSelectionResult.Refused(
                $"'{objectType.Name}' belongs to a Connected System that does not let JIM compose class membership, so it has nowhere to write an auxiliary class.");

        if (objectType.IsAuxiliary())
            return AuxiliaryClassSelectionResult.Refused(
                $"'{objectType.Name}' is itself an auxiliary class, and an auxiliary class cannot carry another.");

        var wanted = extensionObjectTypeIds.ToHashSet();
        var schema = await GetObjectTypesAsync(objectType.ConnectedSystemId);

        foreach (var wantedId in wanted)
        {
            var candidate = schema.FirstOrDefault(type => type.Id == wantedId);
            if (candidate == null)
                return AuxiliaryClassSelectionResult.Refused(
                    $"Object Type {wantedId} is not part of the same Connected System as '{objectType.Name}'.");

            if (!candidate.IsAuxiliary())
                return AuxiliaryClassSelectionResult.Refused(
                    $"'{candidate.Name}' is not an auxiliary class, so it cannot be merged into '{objectType.Name}'.");
        }

        var current = (await GetObjectTypeExtensionsAsync(objectType.ConnectedSystemId))
            .Where(extension => extension.BaseObjectTypeId == objectTypeId)
            .Select(extension => extension.ExtensionObjectTypeId)
            .ToHashSet();

        foreach (var toAdd in wanted.Except(current))
            await Application.Repository.ConnectedSystems.AddObjectTypeExtensionAsync(objectTypeId, toAdd);

        foreach (var toRemove in current.Except(wanted))
            await Application.Repository.ConnectedSystems.RemoveObjectTypeExtensionAsync(objectTypeId, toRemove);

        return AuxiliaryClassSelectionResult.Applied();
    }

    /// <summary>
    /// Names the structural Object Type to use as the carrier when creating objects of a type that cannot stand
    /// alone, or clears it when passed null.
    /// </summary>
    public async Task<AuxiliaryClassSelectionResult> SetStructuralCarrierObjectTypeAsync(int objectTypeId, int? carrierObjectTypeId)
    {
        var objectType = await GetObjectTypeAsync(objectTypeId);
        if (objectType == null)
            return AuxiliaryClassSelectionResult.Refused($"Object Type {objectTypeId} does not exist.");

        if (carrierObjectTypeId != null)
        {
            if (!objectType.IsAuxiliary())
                return AuxiliaryClassSelectionResult.Refused(
                    $"'{objectType.Name}' is not an auxiliary class, so it already states what its objects are and needs no carrier.");

            var schema = await GetObjectTypesAsync(objectType.ConnectedSystemId);
            var carrier = schema.FirstOrDefault(type => type.Id == carrierObjectTypeId);
            if (carrier == null)
                return AuxiliaryClassSelectionResult.Refused(
                    $"Object Type {carrierObjectTypeId} is not part of the same Connected System as '{objectType.Name}'.");

            if (!carrier.IsStructural())
                return AuxiliaryClassSelectionResult.Refused(
                    $"'{carrier.Name}' is not a structural class, so an object cannot be created as one.");
        }

        await Application.Repository.ConnectedSystems.SetStructuralCarrierObjectTypeAsync(objectTypeId, carrierObjectTypeId);
        return AuxiliaryClassSelectionResult.Applied();
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
        using var connectorDisposable = connector as IDisposable;

        var runner = new AuxiliaryClassDiscoveryRunner(Application, Log.Logger);
        return await runner.RunAsync(connectedSystem, workerTask.Scope, workerTask.SampleSizePerObjectType,
            activity, connector, progress, cancellationToken);
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

        await ThrowIfObjectTypeSelectionInvalidAsync(objectType);

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

        await ThrowIfObjectTypeSelectionInvalidAsync(objectType);

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

    /// <summary>
    /// Gets a window of one attribute's values on a Connected System Object addressed by absolute offset and
    /// count, for a virtualised (infinite-scroll) multi-valued attribute on the object's detail page. Ordered by
    /// value id, and shares its query core with <see cref="GetAttributeValuesPagedAsync"/>. Pass
    /// <paramref name="includeTotalCount"/> as false to skip counting the whole match set when the caller
    /// already knows the total; the returned total is then null rather than zero.
    /// </summary>
    /// <param name="connectedSystemObjectId">The Connected System Object whose values are wanted.</param>
    /// <param name="attributeName">The attribute whose values are wanted.</param>
    /// <param name="offset">The zero-based index of the first value wanted; negative values read as zero.</param>
    /// <param name="count">How many values are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="searchText">Optional case-insensitive search over the stored value, the unresolved
    /// reference and the referenced object's own values.</param>
    /// <param name="includeTotalCount">Whether to count the whole match set alongside the window; counting is the
    /// expensive half of a window read, so callers that already hold the total pass false and receive a null total.</param>
    public async Task<RangeResultSet<ConnectedSystemObjectAttributeValue>> GetAttributeValuesRangeAsync(
        Guid connectedSystemObjectId,
        string attributeName,
        int offset,
        int count,
        string? searchText = null,
        bool includeTotalCount = true)
    {
        return await Application.Repository.ConnectedSystems.GetAttributeValuesRangeAsync(
            connectedSystemObjectId, attributeName, offset, count, searchText, includeTotalCount);
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
    /// Gets a window of Connected System Object headers addressed by absolute offset and count, for virtualised
    /// (infinite-scroll) list views. Shares its query, filters and projection with
    /// <see cref="GetConnectedSystemObjectHeadersAsync"/>. Pass <paramref name="includeTotalCount"/> as false to
    /// skip counting the whole match set when the caller already knows the total; the returned total is then null
    /// rather than zero.
    /// </summary>
    public async Task<RangeResultSet<ConnectedSystemObjectHeader>> GetConnectedSystemObjectHeadersRangeAsync(
        int connectedSystemId,
        int offset,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        IEnumerable<ConnectedSystemObjectStatus>? statusFilter = null,
        IEnumerable<int>? objectTypeFilter = null,
        IEnumerable<ConnectedSystemObjectJoinType>? joinTypeFilter = null,
        bool includeTotalCount = true)
    {
        using var span = Diagnostics.Diagnostics.Database.StartSpan("Cso.GetHeadersRange")
            .SetTag("connectedSystemId", connectedSystemId)
            .SetTag("offset", offset)
            .SetTag("count", count)
            .SetTag("hasSearch", !string.IsNullOrWhiteSpace(searchQuery))
            .SetTag("sortBy", sortBy ?? "default")
            .SetTag("includeTotalCount", includeTotalCount);
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            connectedSystemId, offset, count, searchQuery, sortBy, sortDescending, statusFilter,
            objectTypeFilter, joinTypeFilter, includeTotalCount);
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
    /// Streams the joined Connected System Objects of one type in a Connected System, with the attribute values
    /// and type loaded that Synchronisation Rule scope evaluation reads, for previewing what a destructive
    /// Synchronisation Rule toggle would do to the objects the rule stands over (#1115).
    /// </summary>
    public IAsyncEnumerable<ConnectedSystemObject> StreamJoinedConnectedSystemObjects(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return Application.Repository.ConnectedSystems.StreamJoinedConnectedSystemObjects(connectedSystemId, connectedSystemObjectTypeId);
    }

    /// <summary>
    /// Streams every Connected System Object of one type in a Connected System, joined or not, with the attribute
    /// values and type loaded that Scoping Criteria evaluation reads, for previewing what a change to a
    /// Synchronisation Rule's scope would do (#1436). The unjoined objects are what a widened scope would newly
    /// project, so unlike the destructive-toggle walk this one cannot be reduced to the joined population.
    /// </summary>
    public IAsyncEnumerable<ConnectedSystemObject> StreamConnectedSystemObjectsOfType(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return Application.Repository.ConnectedSystems.StreamConnectedSystemObjectsOfType(connectedSystemId, connectedSystemObjectTypeId);
    }

    /// <summary>
    /// Returns the count of Connected System Objects of one type in a Connected System, joined or not: the
    /// population a Scoping Criteria change preview walks, counted set-based for the dispatch decision (#1436).
    /// </summary>
    public async Task<int> GetConnectedSystemObjectCountOfTypeAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountOfTypeAsync(connectedSystemId, connectedSystemObjectTypeId);
    }

    /// <summary>
    /// The identifiers of a Connected System's live, unjoined objects of one type: the population a
    /// synchronisation would put to Object Matching on its next run, and therefore the only population an Object
    /// Matching change can move (#1457).
    /// </summary>
    public async Task<List<Guid>> GetUnjoinedConnectedSystemObjectIdsOfTypeAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetUnjoinedConnectedSystemObjectIdsOfTypeAsync(connectedSystemId, connectedSystemObjectTypeId);
    }

    /// <summary>
    /// How many live, unjoined objects of one type a Connected System holds; the set-based count behind an Object
    /// Matching preview's cost estimate.
    /// </summary>
    public async Task<int> GetUnjoinedConnectedSystemObjectCountOfTypeAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetUnjoinedConnectedSystemObjectCountOfTypeAsync(connectedSystemId, connectedSystemObjectTypeId);
    }

    /// <summary>
    /// The identifiers of a Connected System's live objects of one type, joined or not: the population that stops
    /// being imported when the type is deselected (#1475).
    /// </summary>
    public async Task<List<Guid>> GetLiveConnectedSystemObjectIdsOfTypeAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetLiveConnectedSystemObjectIdsOfTypeAsync(connectedSystemId, connectedSystemObjectTypeId);
    }

    /// <summary>
    /// The identifiers of a Connected System's live objects of one type that hold a value for one attribute: the
    /// population whose values freeze when that attribute is deselected (#1475).
    /// </summary>
    public async Task<List<Guid>> GetLiveConnectedSystemObjectIdsHoldingAttributeAsync(int connectedSystemId,
        int connectedSystemObjectTypeId, int attributeId)
    {
        return await Application.Repository.ConnectedSystems.GetLiveConnectedSystemObjectIdsHoldingAttributeAsync(
            connectedSystemId, connectedSystemObjectTypeId, attributeId);
    }

    /// <summary>
    /// The identifiers of a Connected System's obsolete objects of one type that are still joined: the population
    /// whose fate changes when Remove Contributed Attributes On Obsoletion is toggled (#1475).
    /// </summary>
    public async Task<List<Guid>> GetObsoleteJoinedConnectedSystemObjectIdsOfTypeAsync(int connectedSystemId,
        int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetObsoleteJoinedConnectedSystemObjectIdsOfTypeAsync(
            connectedSystemId, connectedSystemObjectTypeId);
    }

    /// <summary>
    /// Connected System Objects by identifier, without change tracking: the batched read behind a population that
    /// was resolved to identifiers first.
    /// </summary>
    public async Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByIdsNoTrackingAsync(int connectedSystemId, IEnumerable<Guid> connectedSystemObjectIds)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsByIdsNoTrackingAsync(connectedSystemId, connectedSystemObjectIds);
    }

    /// <summary>
    /// Returns the count of joined Connected System Objects of one type in a Connected System: the population a
    /// destructive Synchronisation Rule toggle preview walks, counted set-based for the dispatch decision (#1115).
    /// </summary>
    public async Task<int> GetJoinedConnectedSystemObjectCountAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetJoinedConnectedSystemObjectCountAsync(connectedSystemId, connectedSystemObjectTypeId);
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
    /// Returns the count of Connected System Objects for a particular Connected System, where the status is Obsolete.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the Obsolete object count for.</param>
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
            if (rpei == null)
                continue;

            rpei.ConnectedSystemObjectId = cso.Id;
            try
            {
                ProcessConnectedSystemObjectAttributeValueChanges(cso, rpei, changeTrackingEnabled);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Synchronisation integrity boundary (#1386): a change record that cannot be built for
                // one object is that object's error, reported on its Run Profile Execution Item; letting
                // it escape would fail the whole Activity and abandon every object still to be processed,
                // with nothing recorded against the object at fault. Cancellation still propagates: an
                // aborting run must not grind on through this path.
                RecordChangeRecordFailureOnRpei(cso, rpei, ex, nameof(LinkUpdateChangeRecords));

                // Mirror the normal completion: the caller snapshotted these lists for persistence before
                // this ran, and leaving them populated would make the object look like it still holds
                // unapplied work.
                cso.PendingAttributeValueAdditions = new List<ConnectedSystemObjectAttributeValue>();
                cso.PendingAttributeValueRemovals = new List<ConnectedSystemObjectAttributeValue>();
            }
        }
    }

    /// <summary>
    /// Records a change-record construction failure on the object's Run Profile Execution Item, so the
    /// error is visible against the object it belongs to and the run continues (#1386).
    /// </summary>
    private static void RecordChangeRecordFailureOnRpei(
        ConnectedSystemObject cso,
        ActivityRunProfileExecutionItem rpei,
        Exception ex,
        string callerName)
    {
        Log.Error(ex, "{Caller}: could not build the change record for Connected System Object {CsoId}; " +
            "the object is errored on its Run Profile Execution Item and the run continues.", callerName, cso.Id);
        rpei.ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError;
        rpei.ErrorMessage = ex.Message;
        rpei.ErrorStackTrace = ex.ToString();
    }

    /// <summary>
    /// Finds the single attribute value holding a Connected System Object's External ID, throwing a
    /// diagnostic exception when the object holds more than one.
    /// </summary>
    /// <remarks>
    /// The <see cref="ConnectedSystemObject.ExternalIdAttributeValue"/> property answers the same
    /// question via SingleOrDefault, whose duplicate failure names neither the object nor the values
    /// (#1386: "Sequence contains more than one matching element", with the whole run dead behind it).
    /// The change-record paths use this instead so the per-object error the caller records is
    /// actionable. The property itself stays strict; it is the read on the sync path that must name
    /// what it found.
    /// </remarks>
    private static ConnectedSystemObjectAttributeValue? GetSingleExternalIdAttributeValue(ConnectedSystemObject cso)
    {
        var externalIdValues = cso.AttributeValues
            .Where(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == cso.ExternalIdAttributeId)
            .ToList();

        if (externalIdValues.Count > 1)
        {
            throw new InvalidOperationException(
                $"Connected System Object {cso.Id} holds {externalIdValues.Count} values for its External ID attribute " +
                $"(attribute id {cso.ExternalIdAttributeId}): {string.Join(", ", externalIdValues.Select(v => v.ToStringNoName() ?? "(empty)"))}. " +
                "An object must hold exactly one External ID value; a duplicate means an earlier write stored the anchor " +
                "in a slot a later one did not recognise. The object needs its duplicate value removed before its change " +
                "history can be recorded.");
        }

        return externalIdValues.Count == 1 ? externalIdValues[0] : null;
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

        if (changeTrackingEnabled)
        {
            for (int i = 0; i < connectedSystemObjects.Count; i++)
            {
                var cso = connectedSystemObjects[i];
                var executionItem = rpeis[i];

                try
                {
                    // Name only, never NameOrId: the external id is captured separately alongside it.
                    // The guarded read throws a diagnostic error on a duplicated anchor, recorded per
                    // object below rather than escaping the run (#1386).
                    var externalId = GetSingleExternalIdAttributeValue(cso)?.ToStringNoName();
                    var finalAttributeValues = cso.AttributeValues
                        .Where(av => av.Attribute != null && av.Attribute.Type != AttributeDataType.NotSet)
                        .ToList();

                    var change = new ConnectedSystemObjectChange
                    {
                        ConnectedSystemId = cso.ConnectedSystemId,
                        ChangeType = ObjectChangeType.Deleted,
                        ChangeTime = DateTime.UtcNow,
                        DeletedObjectType = cso.Type,
                        DeletedObjectExternalId = externalId,
                        DeletedObjectDisplayName = cso.Name,
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Same synchronisation integrity boundary as LinkUpdateChangeRecords (#1386): one
                    // object's failed deletion change record must not abandon the rest of the batch.
                    // The FK-nulling loop below still runs for this RPEI; the CSOs are about to be
                    // deleted, and a live FK would violate the constraint at persistence.
                    RecordChangeRecordFailureOnRpei(cso, executionItem, ex, nameof(LinkDeleteChangeRecords));
                }
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
                // Use ToStringNoName() to match the format used in deletion changes. The guarded read
                // throws a diagnostic error on a duplicated anchor, which the caller records per object (#1386).
                DeletedObjectExternalId = GetSingleExternalIdAttributeValue(connectedSystemObject)?.ToStringNoName()
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

        var partitions = await Application.Repository.ConnectedSystems.GetConnectedSystemPartitionsAsync(connectedSystem);

        foreach (var partition in partitions)
            ContainerObjectCounts.RecalculateSubtreeTotals(partition);

        return partitions;
    }

    public async Task<ConnectedSystemPartition?> GetConnectedSystemPartitionAsync(int id, bool withChangeTracking = false)
    {
        var partition = await Application.Repository.ConnectedSystems.GetConnectedSystemPartitionAsync(id, withChangeTracking);
        if (partition != null)
            ContainerObjectCounts.RecalculateSubtreeTotals(partition);

        return partition;
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
    /// Names the given Containers, for a surface holding their ids and needing to render them. Ids that no longer
    /// resolve are absent from the result rather than faked, so a caller can say plainly that a Container has
    /// gone rather than inventing a name for it.
    /// </summary>
    public async Task<List<ConnectedSystemContainerSummary>> GetConnectedSystemContainerSummariesAsync(IReadOnlyCollection<int> containerIds)
    {
        ArgumentNullException.ThrowIfNull(containerIds);
        return await Application.Repository.ConnectedSystems.GetConnectedSystemContainerSummariesAsync(containerIds);
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
    /// A Connected System's Container Scope as canonical Advanced Mode text, or null where the Connected System
    /// does not exist.
    /// </summary>
    public async Task<string?> GetContainerScopeTextAsync(int connectedSystemId)
    {
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);

        return connectedSystem == null
            ? null
            : ContainerScopeText.Project(connectedSystem.Partitions ?? []);
    }

    /// <summary>
    /// Replaces a Connected System's whole Container Scope with the statements in a piece of text, recording the
    /// change with an Activity and a versioned configuration snapshot.
    /// </summary>
    /// <remarks>
    /// The whole scope is one configuration change and is saved as one, exactly as the portal's tree is: an
    /// administrator restating a hierarchy has made a single decision about what JIM manages, and recording it as
    /// a Container's worth of separate changes would leave a change history nobody can read a decision out of.
    /// </remarks>
    /// <returns>Null where the Connected System does not exist.</returns>
    public async Task<ContainerScopeTextApplyResult?> ApplyContainerScopeTextAsync(
        int connectedSystemId,
        string? text,
        MetaverseObject? initiatedBy) =>
        await ApplyContainerScopeTextAsync(connectedSystemId, text,
            connectedSystem => UpdateConnectedSystemAsync(connectedSystem, initiatedBy));

    /// <summary>
    /// Replaces a Connected System's whole Container Scope with the statements in a piece of text (initiated by API
    /// key), recording the change with an Activity and a versioned configuration snapshot.
    /// </summary>
    /// <returns>Null where the Connected System does not exist.</returns>
    public async Task<ContainerScopeTextApplyResult?> ApplyContainerScopeTextAsync(
        int connectedSystemId,
        string? text,
        ApiKey initiatedByApiKey) =>
        await ApplyContainerScopeTextAsync(connectedSystemId, text,
            connectedSystem => UpdateConnectedSystemAsync(connectedSystem, initiatedByApiKey));

    private async Task<ContainerScopeTextApplyResult?> ApplyContainerScopeTextAsync(
        int connectedSystemId,
        string? text,
        Func<ConnectedSystem, Task> persistAsync)
    {
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(
            connectedSystemId, withChangeTracking: true);

        if (connectedSystem == null)
            return null;

        var partitions = connectedSystem.Partitions ?? [];
        var errors = ContainerScopeText.Apply(text, partitions);

        if (errors.Count > 0)
            return new ContainerScopeTextApplyResult { Errors = errors, Text = ContainerScopeText.Project(partitions) };

        await persistAsync(connectedSystem);

        return new ContainerScopeTextApplyResult { Errors = [], Text = ContainerScopeText.Project(partitions) };
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
    /// The rejection message for a second Attribute Flow targeting an attribute the Synchronisation Rule already
    /// flows to (#1532). Names the sanctioned alternatives, so the administrator is told how to express the
    /// intent rather than merely refused.
    /// </summary>
    private static string BuildDuplicateMappingTargetMessage(string? syncRuleName, string targetAttributeName)
    {
        var ruleDescription = string.IsNullOrWhiteSpace(syncRuleName)
            ? "This Synchronisation Rule"
            : $"Synchronisation Rule '{syncRuleName}'";
        return $"{ruleDescription} already has an Attribute Flow targeting '{targetAttributeName}'; only one mapping per " +
               "target attribute is supported, and a disabled mapping still counts. To fall back between source attributes " +
               "within one rule, use a single expression mapping (for example attribute_1 ?? attribute_2). To arbitrate " +
               "between sources with Attribute Priority, define the second flow on a separate, differently-scoped " +
               "Synchronisation Rule.";
    }

    /// <summary>
    /// Validates that no other mapping on the same Synchronisation Rule already targets this mapping's target
    /// attribute (#1532). The engine evaluates one mapping per target attribute, so a same-rule duplicate is
    /// representable in configuration but never honoured: the lower-priority mapping silently never contributes.
    /// Refusing the configuration is the honest answer. Disabled mappings count as duplicates too, because a
    /// disabled duplicate re-enabled later would recreate the trap.
    /// </summary>
    /// <param name="mapping">The mapping being created or updated; excluded from the collision check by its id.</param>
    /// <exception cref="ArgumentException">Another mapping on the rule already targets the same attribute.</exception>
    private async Task ValidateNoDuplicateMappingTargetAsync(SyncRuleMapping mapping)
    {
        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId;
        if (syncRuleId == 0)
            return; // a rule still being composed has no persisted mappings; the whole-rule save path validates its collection

        var targetMetaverseAttributeId = mapping.TargetMetaverseAttributeId ?? mapping.TargetMetaverseAttribute?.Id;
        var targetConnectedSystemAttributeId = mapping.TargetConnectedSystemAttributeId ?? mapping.TargetConnectedSystemAttribute?.Id;
        if (targetMetaverseAttributeId == null && targetConnectedSystemAttributeId == null)
            return; // no target yet; the model's own validation owns that problem

        var existingMappings = await Application.Repository.ConnectedSystems.GetSyncRuleMappingsAsync(syncRuleId);
        var duplicate = existingMappings.FirstOrDefault(existing =>
            existing.Id != mapping.Id &&
            ((targetMetaverseAttributeId != null &&
              (existing.TargetMetaverseAttributeId ?? existing.TargetMetaverseAttribute?.Id) == targetMetaverseAttributeId) ||
             (targetConnectedSystemAttributeId != null &&
              (existing.TargetConnectedSystemAttributeId ?? existing.TargetConnectedSystemAttribute?.Id) == targetConnectedSystemAttributeId)));
        if (duplicate == null)
            return;

        var targetAttributeName = mapping.TargetMetaverseAttribute?.Name
            ?? mapping.TargetConnectedSystemAttribute?.Name
            ?? duplicate.TargetMetaverseAttribute?.Name
            ?? duplicate.TargetConnectedSystemAttribute?.Name
            ?? $"ID {targetMetaverseAttributeId ?? targetConnectedSystemAttributeId}";
        var message = BuildDuplicateMappingTargetMessage(mapping.SyncRule?.Name, targetAttributeName);
        Log.Warning("ValidateNoDuplicateMappingTargetAsync: rejecting mapping; {Message}", LogSanitiser.Sanitise(message));
        throw new ArgumentException(message);
    }

    /// <summary>
    /// The whole-rule-save sibling of <see cref="ValidateNoDuplicateMappingTargetAsync"/> (#1532): rejects a
    /// Synchronisation Rule whose Attribute Flow collection targets the same attribute twice, since the portal
    /// composes mappings in-memory and saves the whole rule. The collection is validated rather than the
    /// database, because the save replaces the collection.
    /// </summary>
    /// <exception cref="ArgumentException">Two mappings on the rule target the same attribute.</exception>
    private static void ValidateNoDuplicateMappingTargets(SyncRule syncRule)
    {
        var duplicateTargetGroup = syncRule.AttributeFlowRules
            .Select(mapping => new
            {
                Mapping = mapping,
                MetaverseTargetId = mapping.TargetMetaverseAttributeId ?? mapping.TargetMetaverseAttribute?.Id,
                ConnectedSystemTargetId = mapping.TargetConnectedSystemAttributeId ?? mapping.TargetConnectedSystemAttribute?.Id
            })
            .Where(candidate => candidate.MetaverseTargetId != null || candidate.ConnectedSystemTargetId != null)
            .GroupBy(candidate => (candidate.MetaverseTargetId, candidate.ConnectedSystemTargetId))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTargetGroup == null)
            return;

        var targetAttributeName = duplicateTargetGroup
            .Select(candidate => candidate.Mapping.TargetMetaverseAttribute?.Name ?? candidate.Mapping.TargetConnectedSystemAttribute?.Name)
            .FirstOrDefault(name => name != null)
            ?? $"ID {duplicateTargetGroup.Key.MetaverseTargetId ?? duplicateTargetGroup.Key.ConnectedSystemTargetId}";
        var message = BuildDuplicateMappingTargetMessage(syncRule.Name, targetAttributeName);
        Log.Warning("CreateOrUpdateSyncRuleAsync: rejecting Synchronisation Rule; {Message}", LogSanitiser.Sanitise(message));
        throw new ArgumentException(message);
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
        await ValidateNoDuplicateMappingTargetAsync(mapping);

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

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId;
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
        await ValidateNoDuplicateMappingTargetAsync(mapping);

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

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId;
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
        await ValidateNoDuplicateMappingTargetAsync(mapping);

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

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId;
        AuditHelper.SetUpdated(mapping, initiatedBy);
        await Application.Repository.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping);

        await CaptureSyncRuleConfigurationChangeAsync(activity, syncRuleId);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Changes the settings on an existing Attribute Flow, leaving what it reads and writes alone.
    /// </summary>
    /// <param name="mappingId">The mapping to change.</param>
    /// <param name="settings">The settings to change; anything not named is left as it is.</param>
    /// <param name="initiatedBy">The user who initiated the change.</param>
    /// <returns>The updated mapping, or null when no mapping has that id.</returns>
    /// <exception cref="ArgumentException">The update names a setting that does not apply to this mapping.</exception>
    public async Task<SyncRuleMapping?> UpdateSyncRuleMappingSettingsAsync(int mappingId, SyncRuleMappingSettingsUpdate settings, MetaverseObject? initiatedBy)
    {
        return await UpdateSyncRuleMappingSettingsCoreAsync(mappingId, settings, initiatedBy, null);
    }

    /// <summary>
    /// Changes the settings on an existing Attribute Flow (initiated by API key).
    /// </summary>
    /// <param name="mappingId">The mapping to change.</param>
    /// <param name="settings">The settings to change; anything not named is left as it is.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the change.</param>
    /// <returns>The updated mapping, or null when no mapping has that id.</returns>
    /// <exception cref="ArgumentException">The update names a setting that does not apply to this mapping.</exception>
    public async Task<SyncRuleMapping?> UpdateSyncRuleMappingSettingsAsync(int mappingId, SyncRuleMappingSettingsUpdate settings, ApiKey initiatedByApiKey)
    {
        return await UpdateSyncRuleMappingSettingsCoreAsync(mappingId, settings, null, initiatedByApiKey);
    }

    /// <summary>
    /// Loads the mapping tracked, applies the settings, and records the change as an audited Update Activity.
    /// </summary>
    /// <remarks>
    /// The mapping is loaded here rather than accepted from the caller, because it must be tracked for the save
    /// to do anything at all: JIM.Web runs the context NoTracking, so a mapping the caller loaded and mutated
    /// would save nothing while reporting success.
    /// </remarks>
    private async Task<SyncRuleMapping?> UpdateSyncRuleMappingSettingsCoreAsync(
        int mappingId, SyncRuleMappingSettingsUpdate settings, MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.HasChanges)
            throw new ArgumentException("No settings were supplied to change.", nameof(settings));

        var mapping = await Application.Repository.ConnectedSystems.GetSyncRuleMappingForUpdateAsync(mappingId);
        if (mapping == null)
            return null;

        // Validation before the Activity, so a rejected update leaves no trace of an Update that never happened.
        ApplySyncRuleMappingSettings(mapping, settings);
        ValidateMappingTypeCompatibility(mapping);
        ValidateMappingWritability(mapping);

        Log.Debug("UpdateSyncRuleMappingSettingsAsync() called for mapping {Id}", mapping.Id);

        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
        var activity = new Activity
        {
            TargetName = $"{Activity.SyncRuleMappingTargetNamePrefix}{targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            SyncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId,
            TargetOperationType = ActivityTargetOperationType.Update
        };

        if (initiatedByApiKey != null)
        {
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
            AuditHelper.SetUpdated(mapping, initiatedByApiKey);
        }
        else
        {
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
            AuditHelper.SetUpdated(mapping, initiatedBy);
        }

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId;
        await Application.Repository.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping);

        await CaptureSyncRuleConfigurationChangeAsync(activity, syncRuleId);
        await Application.Activities.CompleteActivityAsync(activity);

        return mapping;
    }

    /// <summary>
    /// Applies a settings update to a mapping, refusing any setting that does not apply to it.
    /// </summary>
    /// <remarks>
    /// A setting that cannot apply is refused rather than ignored. Silently dropping "Null is a value" on an
    /// export mapping would leave an administrator believing an authoritative-null contribution had been
    /// configured, and it is exactly the kind of misconfiguration that only shows up as missing data later.
    /// </remarks>
    private static void ApplySyncRuleMappingSettings(SyncRuleMapping mapping, SyncRuleMappingSettingsUpdate settings)
    {
        var isImport = mapping.TargetMetaverseAttributeId.HasValue;
        var expressionSources = mapping.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Expression)).ToList();

        if ((settings.Expression != null || settings.MissingInputBehaviour.HasValue) && expressionSources.Count == 0)
            throw new ArgumentException("This Attribute Flow has no Expression source, so Expression settings do not apply to it. " +
                "Delete the mapping and create it with an Expression source instead.");

        if (settings.Expression != null && expressionSources.Count > 1)
            throw new ArgumentException("This Attribute Flow has more than one Expression source, so it is ambiguous which Expression to replace.");

        if (!isImport && (settings.NullIsValue.HasValue || settings.InboundValueProcessing.HasValue || settings.CaseNormalisation.HasValue))
            throw new ArgumentException("Null is a value, inbound value processing and case normalisation apply to import mappings only.");

        if (isImport && settings.InitialExportOnly.HasValue)
            throw new ArgumentException("Initial Export Only applies to export mappings only.");

        if (settings.Expression != null)
            expressionSources[0].Expression = settings.Expression;

        if (settings.MissingInputBehaviour.HasValue)
            foreach (var source in expressionSources)
                source.MissingInputBehaviour = settings.MissingInputBehaviour.Value;

        if (settings.NullIsValue.HasValue)
            mapping.NullIsValue = settings.NullIsValue.Value;

        if (settings.InboundValueProcessing.HasValue)
            mapping.InboundValueProcessing = settings.InboundValueProcessing.Value;

        if (settings.CaseNormalisation.HasValue)
            mapping.CaseNormalisation = settings.CaseNormalisation.Value;

        if (settings.InitialExportOnly.HasValue)
            mapping.InitialExportOnly = settings.InitialExportOnly.Value;

        // Enabled applies to both directions (#1485), which is why it is absent from the direction guards
        // above. Re-enabling clears the recorded reason: it describes why the mapping is off, and re-enabled
        // it would be a stale claim about a state that no longer holds.
        if (settings.Enabled.HasValue)
        {
            mapping.Enabled = settings.Enabled.Value;
            if (mapping.Enabled)
                mapping.DisabledReason = null;
        }
    }

    /// <summary>
    /// Deletes a Synchronisation Rule mapping, with the recall-or-keep choice for the Metaverse attribute
    /// values it contributed (#1537). See <see cref="DeleteSyncRuleMappingCoreAsync"/> for the semantics.
    /// </summary>
    /// <param name="mapping">The mapping to delete.</param>
    /// <param name="initiatedBy">The user who initiated the deletion.</param>
    /// <param name="parentActivityId">An optional parent Activity when the deletion is part of a larger decision.</param>
    /// <param name="keepContributedValues">True to keep the values the mapping contributed (their provenance is
    /// severed before the row deletion, permanently exempting them from the orphan recall); false (the default
    /// on every surface) to leave them to be recalled at the next Full Synchronisation of the contributing system.</param>
    public Task<SyncRuleMappingDeletionResult> DeleteSyncRuleMappingAsync(SyncRuleMapping mapping, MetaverseObject? initiatedBy, Guid? parentActivityId = null, bool keepContributedValues = false)
        => DeleteSyncRuleMappingCoreAsync(mapping, initiatedBy, initiatedByApiKey: null, parentActivityId, keepContributedValues);

    /// <summary>
    /// Deletes a Synchronisation Rule Mapping (initiated by API key), with the recall-or-keep choice for the
    /// Metaverse attribute values it contributed (#1537). See <see cref="DeleteSyncRuleMappingCoreAsync"/>.
    /// </summary>
    /// <param name="mapping">The mapping to delete.</param>
    /// <param name="initiatedByApiKey">The API key that initiated the deletion.</param>
    /// <param name="parentActivityId">An optional parent Activity when the deletion is part of a larger decision.</param>
    /// <param name="keepContributedValues">See the user-initiated overload above.</param>
    public Task<SyncRuleMappingDeletionResult> DeleteSyncRuleMappingAsync(SyncRuleMapping mapping, ApiKey initiatedByApiKey, Guid? parentActivityId = null, bool keepContributedValues = false)
        => DeleteSyncRuleMappingCoreAsync(mapping, initiatedBy: null, initiatedByApiKey, parentActivityId, keepContributedValues);

    /// <summary>
    /// The shared heart of an Attribute Flow mapping deletion (#1537). The default (recall) is exactly the
    /// long-shipped behaviour: nothing queues, the deletion stamps the configuration watermark, and the orphan
    /// recall withdraws the mapping's contributed values at the next Full Synchronisation of the contributing
    /// system. Choosing keep severs the values' Synchronisation Rule provenance BEFORE the row is deleted
    /// (null-provenance values are never recalled, so the exemption is permanent) and records the choice on
    /// the deletion Activity. Only meaningful for import mappings: an export mapping, or one with no target
    /// Metaverse attribute, has contributed nothing and there is nothing to sever.
    /// </summary>
    private async Task<SyncRuleMappingDeletionResult> DeleteSyncRuleMappingCoreAsync(
        SyncRuleMapping mapping, MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey, Guid? parentActivityId, bool keepContributedValues)
    {
        if (mapping == null)
            throw new ArgumentNullException(nameof(mapping));

        Log.Debug("DeleteSyncRuleMappingAsync() called for mapping {Id}", mapping.Id);

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId;
        // The scalar FK, deliberately: every caller of the direct delete hands over a persisted mapping (the
        // REST handler, PowerShell and the schema refresh all load from the database), so the scalar is always
        // populated, and it is what the pre-choice code keyed the priority reconcile on. Editor-built mappings
        // with only the navigation set travel the staged-removal save path instead.
        var targetMetaverseAttributeId = mapping.TargetMetaverseAttributeId;

        // Quantify the mapping's contributed values (count queries only). Tolerate a null summary from stubbed
        // repositories: it means nothing is known to be contributed, so there is nothing to keep or recall.
        var contributedValuesSummary = syncRuleId > 0 && targetMetaverseAttributeId.HasValue
            ? await Application.Repository.Metaverse.GetContributedValuesSummaryAsync(syncRuleId, targetMetaverseAttributeId.Value)
            : null;
        var result = new SyncRuleMappingDeletionResult
        {
            AffectedValueCount = contributedValuesSummary?.TotalValues ?? 0,
            AffectedObjectCount = contributedValuesSummary?.TotalObjects ?? 0
        };

        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
        var activity = new Activity
        {
            TargetName = $"{Activity.SyncRuleMappingTargetNamePrefix}{targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            SyncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId,
            TargetOperationType = ActivityTargetOperationType.Delete,
            // A deletion performed as part of a larger decision (a schema refresh's Apply and Remove, #1485)
            // parents itself under that decision's Activity so the history reads as one action.
            ParentActivityId = parentActivityId
        };

        // A keep chosen while values were present must be auditable at the moment of choice (#1537), mirroring
        // the rule-level deletion's wording.
        if (keepContributedValues && result.AffectedValueCount > 0)
        {
            activity.Message = $"Contributed attribute values were kept: {result.AffectedValueCount:N0} value(s) across " +
                $"{result.AffectedObjectCount:N0} Metaverse Object(s) remain in place with no Synchronisation Rule provenance.";
        }

        if (initiatedByApiKey != null)
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        else
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        // Sever BEFORE the row deletion. Nothing in the deletion itself touches value provenance (it keys on
        // the rule, which survives), but severing first means a failure between the two steps cannot leave the
        // mapping gone with the keep unhonoured and the values still eligible for recall.
        if (keepContributedValues && result.AffectedValueCount > 0 && targetMetaverseAttributeId.HasValue)
        {
            // affected values imply an import target, but CodeQL cannot see that; capture the value once.
            var severedAttributeId = targetMetaverseAttributeId.Value;
            var severedCount = await Application.Repository.Metaverse.SeverContributedValueProvenanceAsync(syncRuleId, severedAttributeId);
            result.ContributedValuesKept = true;
            Log.Information(
                "DeleteSyncRuleMappingAsync: keep chosen for mapping {MappingId} (Synchronisation Rule {SyncRuleId}, Metaverse attribute {AttributeId}); " +
                "severed provenance on {SeveredCount} value(s) across {ObjectCount} Metaverse Object(s).",
                mapping.Id, syncRuleId, severedAttributeId, severedCount, result.AffectedObjectCount);
        }

        // Capture the import mapping's attribute scope before deletion so the remaining contributors can be re-densified.
        var metaverseObjectTypeId = mapping.SyncRule?.MetaverseObjectTypeId;

        await Application.Repository.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping);

        if (metaverseObjectTypeId.HasValue && targetMetaverseAttributeId.HasValue)
            await ReconcileAttributePriorityAsync(metaverseObjectTypeId.Value, targetMetaverseAttributeId.Value);

        await CaptureSyncRuleConfigurationChangeAsync(activity, syncRuleId);
        await Application.Activities.CompleteActivityAsync(activity);
        return result;
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
    /// Gets every attribute data flow, in both directions, for the system-wide Data Flow view (#1199): one flow per
    /// Synchronisation Rule mapping, filtered by the supplied query. Import flows are stamped with how many
    /// contributors their target Metaverse Attribute has, so the caller can tell a shared attribute from a
    /// single-source one without asking again per row.
    /// </summary>
    /// <param name="query">The filters to apply. All are optional and combine with AND.</param>
    public async Task<IList<DataFlowHeader>> GetDataFlowsAsync(DataFlowQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var flows = await Application.Repository.ConnectedSystems.GetDataFlowHeadersAsync(query);

        // Contributor counts are taken across the WHOLE configuration, not the filtered set: filtering the view to
        // one Connected System must not make an attribute that system shares with another look like a sole
        // contributor, which would invert what the count is for. Counted per Metaverse Object Type present in the
        // results, which is a handful of queries at configuration scale.
        var importFlows = flows
            .Where(f => f.Direction == SyncRuleDirection.Import && f.TargetMetaverseAttributeId.HasValue)
            .ToList();

        var countsByObjectType = new Dictionary<int, Dictionary<int, int>>();
        foreach (var objectTypeId in importFlows.Select(f => f.MetaverseObjectTypeId).Distinct())
            countsByObjectType[objectTypeId] = await GetAttributeContributorCountsAsync(objectTypeId);

        foreach (var flow in importFlows)
        {
            flow.ContributorCount = countsByObjectType[flow.MetaverseObjectTypeId]
                .TryGetValue(flow.TargetMetaverseAttributeId!.Value, out var count) ? count : 0;
        }

        // Applied here rather than in the query because it depends on the counts above, which the query cannot see:
        // it reads one flow at a time and the count spans every rule contributing to the same attribute.
        if (query.MultipleContributorsOnly)
            flows = flows.Where(f => f.HasMultipleContributors).ToList();

        return flows;
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
    /// Reconciles a Metaverse attribute's contributor list to a dense 1..N after its contributor set changes (#91),
    /// so no gap or stale number is ever left behind. Called whenever a contribution is added, removed or retargeted,
    /// by both the granular mapping methods and the whole-rule save path (#1199), which is what keeps the list dense
    /// across every route an administrator can take. A sole remaining contributor is reset to the int.MaxValue
    /// sentinel (priority is meaningless with one source, matching the invariant that explicit priorities exist only
    /// when an attribute has more than one contributor); zero contributors is a no-op. Order-preserving, so no
    /// resolution outcome changes. Must be called after the change is persisted, so the query returns the resulting
    /// contributor set.
    /// </summary>
    /// <param name="metaverseObjectTypeId">The object type that scopes the attribute's priority list.</param>
    /// <param name="metaverseAttributeId">The target Metaverse attribute whose contributor list changed.</param>
    /// <param name="arrivingMappingIds">Mappings joining this attribute's list from elsewhere (a retargeted mapping),
    /// which must land at the bottom. Omit where nothing is arriving, such as a deletion.</param>
    private async Task ReconcileAttributePriorityAsync(int metaverseObjectTypeId, int metaverseAttributeId, IReadOnlySet<int>? arrivingMappingIds = null)
    {
        var contributors = await Application.Repository.ConnectedSystems
            .GetImportSyncRuleMappingsForMetaverseAttributeAsync(metaverseObjectTypeId, metaverseAttributeId);

        if (contributors.Count == 0)
            return;

        // An arriving contribution must land last, and its stored priority cannot put it there. The query orders by
        // (Priority asc, Id asc), so while every contributor is still at the sentinel the tie-break is the mapping id,
        // and a retargeted mapping is by definition older than at least some incumbents: it would take the top of its
        // new attribute's list and silently start winning resolution. Ordering arrivals last is what makes the
        // safe-addition promise hold for a retarget as well as for a genuine insert (whose id happens to be highest
        // anyway). OrderBy is stable, so the existing contributors keep their relative order.
        if (arrivingMappingIds is { Count: > 0 })
            contributors = contributors.OrderBy(m => arrivingMappingIds.Contains(m.Id) ? 1 : 0).ToList();

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
            // An unsaved mapping carries only the navigation (the scalar is still default); a persisted one
            // always carries the scalar, which is required (#1550).
            .Select(m => m.SyncRuleId != 0 ? m.SyncRuleId : m.SyncRule?.Id ?? 0)
            .Where(id => id != 0)
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
    /// Gets a window of Pending Export headers addressed by absolute offset and count, for virtualised
    /// (infinite-scroll) list views. Shares its query, filters and projection with
    /// <see cref="GetPendingExportHeadersAsync"/>. Pass <paramref name="includeTotalCount"/> as false to skip
    /// counting the whole match set when the caller already knows the total; the returned total is then null
    /// rather than zero.
    /// </summary>
    public async Task<RangeResultSet<PendingExportHeader>> GetPendingExportHeadersRangeAsync(
        int connectedSystemId,
        int offset,
        int count,
        IEnumerable<PendingExportStatus>? statusFilters = null,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        bool includeTotalCount = true)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportHeadersRangeAsync(
            connectedSystemId, offset, count, statusFilters, searchQuery, sortBy, sortDescending, includeTotalCount);
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
        var result = await Application.Repository.ConnectedSystems.GetPendingExportDetailAsync(id);
        if (result == null)
            return null;

        result.UnresolvedReferences = await DescribeUnresolvedReferencesAsync(result.PendingExport);
        return result;
    }

    /// <summary>
    /// Explains each reference change on a Pending Export that has not been written yet (issue #1398), against
    /// the target's current state rather than anything recorded at export time, so the explanation is always
    /// current: the referenced Metaverse Object has a Connected System Object in the target with an anchor
    /// (resolvable on the next export run), one without an anchor (waiting on its own export), or none at all
    /// (cannot resolve as things stand). Costs nothing when the export has no unresolved references.
    /// </summary>
    private async Task<List<PendingExportUnresolvedReference>> DescribeUnresolvedReferencesAsync(PendingExport pendingExport)
    {
        var unresolved = pendingExport.AttributeValueChanges
            .Where(c => !string.IsNullOrEmpty(c.UnresolvedReferenceValue) && Guid.TryParse(c.UnresolvedReferenceValue, out _))
            .Select(c => (Change: c, MvoId: Guid.Parse(c.UnresolvedReferenceValue!)))
            .ToList();

        if (unresolved.Count == 0)
            return [];

        var mvoIds = unresolved.Select(u => u.MvoId).Distinct().ToList();
        var csosByMvo = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(mvoIds, pendingExport.ConnectedSystemId);
        var names = await Application.Repository.Metaverse.GetMetaverseObjectDisplayNamesAsync(mvoIds);

        return unresolved.Select(u => new PendingExportUnresolvedReference
        {
            AttributeChangeId = u.Change.Id,
            AttributeName = u.Change.Attribute?.Name ?? $"attribute {u.Change.AttributeId}",
            ReferencedMetaverseObjectId = u.MvoId,
            ReferencedMetaverseObjectDisplayName = names.TryGetValue(u.MvoId, out var name) ? name : null,
            Reason = ClassifyUnresolvedReference(csosByMvo.TryGetValue(u.MvoId, out var cso) ? cso : null)
        }).ToList();
    }

    /// <summary>
    /// The one classification the export's deferred pass and this detail share (issue #1398): no object in the
    /// target means the reference cannot resolve as things stand; an object without an anchor is waiting; an
    /// object with an anchor resolves on the next run. The anchor is read the way export resolution reads it,
    /// preferring the secondary external id (a DN) over the primary.
    /// </summary>
    internal static UnresolvedReferenceReason ClassifyUnresolvedReference(ConnectedSystemObject? referencedCso)
    {
        if (referencedCso == null)
            return UnresolvedReferenceReason.NotInTargetSystem;

        var anchor = referencedCso.AttributeValues.FirstOrDefault(av => av.Attribute?.IsSecondaryExternalId == true)
                     ?? referencedCso.AttributeValues.FirstOrDefault(av => av.Attribute?.IsExternalId == true);

        return anchor?.ToReferenceValueString() != null
            ? UnresolvedReferenceReason.Resolvable
            : UnresolvedReferenceReason.AwaitingAnchor;
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
    /// Gets a window of one attribute's changes on a Pending Export addressed by absolute offset and count, for
    /// a virtualised (infinite-scroll) multi-valued attribute. Ordered by change id, and shares its query core
    /// with <see cref="GetPendingExportAttributeChangesPagedAsync"/>. Pass
    /// <paramref name="includeTotalCount"/> as false to skip counting the whole match set when the caller
    /// already knows the total; the returned total is then null rather than zero.
    /// </summary>
    /// <param name="pendingExportId">The unique identifier of the Pending Export.</param>
    /// <param name="attributeName">The name of the attribute to retrieve changes for.</param>
    /// <param name="offset">The zero-based index of the first change wanted; negative values read as zero.</param>
    /// <param name="count">How many changes are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="searchText">Optional case-insensitive search over the stored value and the unresolved
    /// reference.</param>
    /// <param name="includeTotalCount">Whether to count the whole match set alongside the window; counting is the
    /// expensive half of a window read, so callers that already hold the total pass false and receive a null total.</param>
    public async Task<RangeResultSet<PendingExportAttributeValueChange>> GetPendingExportAttributeChangesRangeAsync(
        Guid pendingExportId,
        string attributeName,
        int offset,
        int count,
        string? searchText = null,
        bool includeTotalCount = true)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, attributeName, offset, count, searchText, includeTotalCount);
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
    /// Gets a window of a Pending Export's attribute value changes addressed by absolute offset and count, for
    /// the virtualised (infinite-scroll) Pending Export grid on the Connected System Object detail page. Ordered
    /// by attribute name, and shares its query core with
    /// <see cref="GetAllPendingExportChangesPagedAsync"/>. Pass <paramref name="includeTotalCount"/> as false to
    /// skip counting the whole match set when the caller already knows the total; the returned total is then
    /// null rather than zero.
    /// </summary>
    /// <param name="pendingExportId">The unique identifier of the Pending Export.</param>
    /// <param name="offset">The zero-based index of the first change wanted; negative values read as zero.</param>
    /// <param name="count">How many changes are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="searchText">Optional search text to filter changes by value or attribute name.</param>
    /// <param name="includeTotalCount">Whether to count the whole match set alongside the window; counting is the
    /// expensive half of a window read, so callers that already hold the total pass false and receive a null total.</param>
    public async Task<RangeResultSet<PendingExportAttributeValueChange>> GetAllPendingExportChangesRangeAsync(
        Guid pendingExportId,
        int offset,
        int count,
        string? searchText = null,
        bool includeTotalCount = true)
    {
        return await Application.Repository.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset, count, searchText, includeTotalCount);
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
    /// Gets a window of deleted Connected System Object changes addressed by absolute offset and count, for the
    /// virtualised (infinite-scroll) Deleted Objects list. Shares its filters with
    /// <see cref="GetDeletedCsoChangesAsync"/>, ordered by deletion time newest first. Pass
    /// <paramref name="includeTotalCount"/> as false to skip counting the whole match set when the caller already
    /// knows the total; the returned total is then null rather than zero.
    /// </summary>
    public async Task<RangeResultSet<ConnectedSystemObjectChange>> GetDeletedCsoChangesRangeAsync(
        int offset,
        int count,
        int? connectedSystemId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? externalIdSearch = null,
        bool includeTotalCount = true)
    {
        return await Application.Repository.ConnectedSystems.GetDeletedCsoChangesRangeAsync(
            offset, count, connectedSystemId, fromDate, toDate, externalIdSearch, includeTotalCount);
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
    /// <param name="withChangeTracking">Track the returned rules for mutation on this JimApplication instance;
    /// required by callers that go on to save them, since <c>UpdateSyncRuleAsync</c> refuses a detached rule.</param>
    public async Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabledSyncRules, bool withChangeTracking = false)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRulesAsync(connectedSystemId, includeDisabledSyncRules, withChangeTracking);
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

    /// <param name="previewActivityId">
    /// The Configuration Change Preview this change was made after reading, where one was run. Recorded on the
    /// Activity so "previewed, then applied" is auditable rather than a claim (#827).
    /// </param>
    /// <param name="mappingRemovalChoices">
    /// The staged Attribute Flow mapping removals this save performs, each with its recall-or-keep choice
    /// (#1537): the named mappings are deleted properly (rows and sources removed), and kept ones have their
    /// contributed values' provenance severed before the row deletion. Only valid when updating an existing rule.
    /// </param>
    public async Task<bool> CreateOrUpdateSyncRuleAsync(SyncRule syncRule, MetaverseObject? initiatedBy, Activity? parentActivity = null, string? changeReason = null, Guid? previewActivityId = null, IReadOnlyCollection<SyncRuleMappingRemovalChoice>? mappingRemovalChoices = null)
    {
        // validate the Synchronisation Rule
        if (syncRule == null)
            throw new NullReferenceException(nameof(syncRule));

        Log.Verbose($"CreateOrUpdateSyncRuleAsync() called for: {syncRule}");

        if (!syncRule.IsValid())
            return false;

        // reject removal choices that cannot describe a real staged removal (#1537); see the validator for why
        // each is refused rather than ignored.
        ValidateMappingRemovalChoices(syncRule, mappingRemovalChoices);

        // reject any scoping criterion whose comparison operator is invalid for its attribute's data type
        // (for example "Starts With" on a DateTime). Hard-fail rather than persist a criterion the evaluator
        // can never satisfy, which would silently drop objects out of scope.
        ValidateScopingCriteriaOperators(syncRule);

        // reject two Attribute Flows targeting the same attribute (#1532): the engine evaluates one mapping per
        // target attribute, so the second would be representable but silently never honoured.
        ValidateNoDuplicateMappingTargets(syncRule);

        // The disabled reason describes why the rule is off (#1485); saving an enabled rule clears it, or a
        // re-enabled rule would carry a stale claim about a state that no longer holds.
        if (syncRule.Enabled)
            syncRule.DisabledReason = null;

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

        // Only a newly created account has never had a password, so a rule that creates none cannot deliver an
        // initial password. Switching the setting off with the provisioning it depended on keeps the stored
        // configuration honest, rather than leaving one that reads as on and can never run.
        //
        // The REST API states the same rule by refusing an enabled initial-password configuration on such a
        // Synchronisation Rule outright. That is right for a request whose whole subject is the initial
        // password, and wrong here: this path saves a whole rule, and an administrator turning provisioning off
        // has not asked about passwords at all. The portal removes the Initial Password tab the moment
        // provisioning goes off, so refusing would also leave nothing on screen to correct.
        //
        // Left above the parked-account comparison below on purpose: the comparison must see the settings as
        // they will be stored, so that switching the feature off releases the accounts parked against it.
        if (syncRule.InitialPassword is { Enabled: true } &&
            !(syncRule.Direction == SyncRuleDirection.Export && syncRule.ProvisionToConnectedSystem == true))
        {
            Log.Information("CreateOrUpdateSyncRuleAsync: Switching the initial password off for Synchronisation Rule {Id}, " +
                "which no longer provisions to its Connected System and so creates no account to give one to", syncRule.Id);
            syncRule.InitialPassword.Enabled = false;
        }


        // Capture the attribute priority state the database holds before the save, and reset any retargeted mapping
        // to the safe-addition sentinel, so the reconcile below can tell what this save actually changed (#1199).
        var previousImportTargets = await CaptureImportPriorityStateBeforeSaveAsync(syncRule);

        // Get Connected System name for activity context (Core: only .Name is read).
        var connectedSystemForContext = syncRule.ConnectedSystem ??
            (syncRule.ConnectedSystemId > 0 ? await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(syncRule.ConnectedSystemId) : null);

        // every crud operation must be tracked via an Activity
        var activity = new Activity
        {
            TargetName = syncRule.Name,
            TargetContext = connectedSystemForContext?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            ParentActivityId = parentActivity?.Id,
            PreviewActivityId = previewActivityId
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
            // Staged mapping removals (#1537): sever kept values' provenance BEFORE anything flushes. The
            // required owner foreign key (#1550) deletes a severed mapping's row at the first SaveChanges, so
            // this must run while every named row is still readable; the keep choices are recorded on the
            // Activity once it exists.
            var keepMessages = await ApplyStagedMappingRemovalChoicesAsync(syncRule, mappingRemovalChoices);

            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
            if (keepMessages.Count > 0)
                activity.Message = string.Join(" ", keepMessages);

            // Read before the write, or there is nothing left to compare the new configuration against.
            var previousInitialPassword = await Application.Repository.ConnectedSystems.GetSyncRuleInitialPasswordAsync(syncRule.Id);

            await Application.Repository.ConnectedSystems.UpdateSyncRuleAsync(syncRule);
            await ReleaseParkedInitialPasswordsIfDeliveryChangedAsync(syncRule, previousInitialPassword);
        }

        // The contributor set may have changed, so bring each affected attribute's priority list back to a dense
        // 1..N. Runs after the write so the query sees the resulting contributors, and before the change capture
        // so the snapshot records the priorities as they end up (#1199).
        await ReconcileAttributePriorityAfterRuleSaveAsync(syncRule, previousImportTargets);

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
    /// <param name="previewActivityId">
    /// The Configuration Change Preview this change was made after reading, where one was run. Recorded on the
    /// Activity so "previewed, then applied" is auditable rather than a claim (#827).
    /// </param>
    /// <param name="mappingRemovalChoices">
    /// The staged Attribute Flow mapping removals this save performs, each with its recall-or-keep choice
    /// (#1537); see the user-initiated overload above.
    /// </param>
    public async Task<bool> CreateOrUpdateSyncRuleAsync(SyncRule syncRule, ApiKey initiatedByApiKey, Activity? parentActivity = null, string? changeReason = null, Guid? previewActivityId = null, IReadOnlyCollection<SyncRuleMappingRemovalChoice>? mappingRemovalChoices = null)
    {
        if (syncRule == null)
            throw new NullReferenceException(nameof(syncRule));

        Log.Verbose($"CreateOrUpdateSyncRuleAsync() called for: {syncRule} (API key initiated)");

        if (!syncRule.IsValid())
            return false;

        // reject removal choices that cannot describe a real staged removal (#1537); see the validator for why
        // each is refused rather than ignored.
        ValidateMappingRemovalChoices(syncRule, mappingRemovalChoices);

        // reject any scoping criterion whose comparison operator is invalid for its attribute's data type
        // (for example "Starts With" on a DateTime). Hard-fail rather than persist a criterion the evaluator
        // can never satisfy, which would silently drop objects out of scope.
        ValidateScopingCriteriaOperators(syncRule);

        // reject two Attribute Flows targeting the same attribute (#1532): the engine evaluates one mapping per
        // target attribute, so the second would be representable but silently never honoured.
        ValidateNoDuplicateMappingTargets(syncRule);

        // The disabled reason describes why the rule is off (#1485); saving an enabled rule clears it, or a
        // re-enabled rule would carry a stale claim about a state that no longer holds.
        if (syncRule.Enabled)
            syncRule.DisabledReason = null;

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

        // Capture the attribute priority state the database holds before the save, and reset any retargeted mapping
        // to the safe-addition sentinel, so the reconcile below can tell what this save actually changed (#1199).
        var previousImportTargets = await CaptureImportPriorityStateBeforeSaveAsync(syncRule);

        // Get Connected System name for activity context (Core: only .Name is read).
        var connectedSystemForContext = syncRule.ConnectedSystem ??
            (syncRule.ConnectedSystemId > 0 ? await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(syncRule.ConnectedSystemId) : null);

        var activity = new Activity
        {
            TargetName = syncRule.Name,
            TargetContext = connectedSystemForContext?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            ParentActivityId = parentActivity?.Id,
            PreviewActivityId = previewActivityId
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

            // Staged mapping removals (#1537): sever kept values' provenance BEFORE anything flushes. The
            // required owner foreign key (#1550) deletes a severed mapping's row at the first SaveChanges, so
            // this must run while every named row is still readable; the keep choices are recorded on the
            // Activity once it exists.
            var keepMessages = await ApplyStagedMappingRemovalChoicesAsync(syncRule, mappingRemovalChoices);

            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
            if (keepMessages.Count > 0)
                activity.Message = string.Join(" ", keepMessages);

            await Application.Repository.ConnectedSystems.UpdateSyncRuleAsync(syncRule);
        }

        // The contributor set may have changed, so bring each affected attribute's priority list back to a dense
        // 1..N. Runs after the write so the query sees the resulting contributors, and before the change capture
        // so the snapshot records the priorities as they end up (#1199).
        await ReconcileAttributePriorityAfterRuleSaveAsync(syncRule, previousImportTargets);

        await CaptureConfigurationChangeAsync(activity, syncRule, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
        return true;
    }

    /// <summary>
    /// Quantifies the Metaverse attribute values a Synchronisation Rule currently contributes (#1537),
    /// optionally scoped to one target Metaverse Attribute (an Attribute Flow mapping's slice). Count queries
    /// only, no value rows are materialised, so deletion surfaces can state the impact responsively on large
    /// estates before the administrator chooses to recall or keep the values.
    /// </summary>
    /// <param name="syncRuleId">The Synchronisation Rule whose contributions are being quantified.</param>
    /// <param name="metaverseAttributeId">Optional: limit the summary to one target Metaverse Attribute
    /// (the mapping-deletion case); null summarises every attribute the rule contributes to.</param>
    public Task<ContributedValuesSummary> GetSyncRuleContributedValuesSummaryAsync(int syncRuleId, int? metaverseAttributeId = null)
        => Application.Repository.Metaverse.GetContributedValuesSummaryAsync(syncRuleId, metaverseAttributeId);

    public Task<SyncRuleDeletionResult> DeleteSyncRuleAsync(SyncRule syncRule, MetaverseObject? initiatedBy, string? changeReason = null, Guid? parentActivityId = null, bool recallContributedValues = true)
        => DeleteSyncRuleInternalAsync(syncRule, initiatedBy, initiatedByApiKey: null, changeReason, parentActivityId, recallContributedValues);

    /// <summary>
    /// Deletes a Synchronisation Rule (initiated by API key).
    /// </summary>
    public Task<SyncRuleDeletionResult> DeleteSyncRuleAsync(SyncRule syncRule, ApiKey initiatedByApiKey, string? changeReason = null, Guid? parentActivityId = null, bool recallContributedValues = true)
        => DeleteSyncRuleInternalAsync(syncRule, initiatedBy: null, initiatedByApiKey, changeReason, parentActivityId, recallContributedValues);

    /// <summary>
    /// Deletes a Synchronisation Rule with the recall-or-keep choice for its contributed Metaverse attribute
    /// values (#1537). When recall is chosen (the default on every surface) and the rule still contributes
    /// values, the rule is disabled immediately and a <see cref="DeleteSyncRuleWorkerTask"/> is queued: the
    /// worker withdraws the values by provenance (re-electing surviving contributors and staging Pending
    /// Exports) and deletes the rule as its final step, and the returned result carries the queued Activity id.
    /// Keep, or a rule with no contributed values, deletes synchronously exactly as before (the ON DELETE SET
    /// NULL foreign key produces the keep end state); a keep chosen with values present is recorded on the
    /// deletion Activity so the choice is auditable.
    /// </summary>
    private async Task<SyncRuleDeletionResult> DeleteSyncRuleInternalAsync(
        SyncRule syncRule,
        MetaverseObject? initiatedBy,
        ApiKey? initiatedByApiKey,
        string? changeReason,
        Guid? parentActivityId,
        bool recallContributedValues)
    {
        // Quantify the rule's contributed values (count queries only). Tolerate a null summary from stubbed
        // repositories: it means nothing is known to be contributed, which is the synchronous path.
        var contributedValuesSummary = syncRule.Id > 0
            ? await Application.Repository.Metaverse.GetContributedValuesSummaryAsync(syncRule.Id)
            : null;
        var result = new SyncRuleDeletionResult
        {
            AffectedValueCount = contributedValuesSummary?.TotalValues ?? 0,
            AffectedObjectCount = contributedValuesSummary?.TotalObjects ?? 0
        };

        if (recallContributedValues && result.AffectedValueCount > 0)
        {
            // Recall chosen and there is something to recall: disable the rule immediately (it stops being
            // evaluated; #1538's dormant-contributor behaviour retains its values in the meantime) and queue
            // the recall-then-delete task. The rule is deliberately NOT deleted here: deletion's ON DELETE SET
            // NULL would sever the very provenance the recall selects on.
            syncRule.Enabled = false;
            syncRule.DisabledReason = "Deletion in progress: contributed attribute values are being recalled.";
            StampUpdated(syncRule, initiatedBy, initiatedByApiKey);
            await Application.Repository.ConnectedSystems.UpdateSyncRuleAsync(syncRule);

            DeleteSyncRuleWorkerTask recallTask;
            if (initiatedByApiKey != null)
                recallTask = DeleteSyncRuleWorkerTask.ForApiKey(syncRule.Id, initiatedByApiKey.Id, initiatedByApiKey.Name);
            else if (initiatedBy != null)
                recallTask = DeleteSyncRuleWorkerTask.ForUser(syncRule.Id, initiatedBy.Id, initiatedBy.NameOrId);
            else
            {
                // An internal caller with no principal: attribute the task to the system rather than queueing
                // it with NotSet, which the worker's dispatch refuses (the task would sit stuck with the rule
                // left disabled and its Activity never completed).
                recallTask = new DeleteSyncRuleWorkerTask(syncRule.Id)
                {
                    InitiatedByType = ActivityInitiatorType.System,
                    InitiatedByName = "System"
                };
            }
            recallTask.ChangeReason = changeReason;
            _ = await Application.Tasking.CreateWorkerTaskAsync(recallTask);

            Log.Information(
                "DeleteSyncRuleAsync: Synchronisation Rule {SyncRuleId} contributes {ValueCount} value(s) across {ObjectCount} object(s); disabled the rule and queued recall task {TaskId} (Activity {ActivityId}).",
                syncRule.Id, result.AffectedValueCount, result.AffectedObjectCount, recallTask.Id, recallTask.Activity.Id);

            result.RecallQueued = true;
            result.RecallActivityId = recallTask.Activity.Id;
            return result;
        }

        // Keep chosen, or nothing contributed: synchronous delete exactly as before.
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
            ConnectedSystemId = syncRule.ConnectedSystemId,
            // A deletion performed as part of a larger decision (a schema refresh's Apply and Remove, #1485)
            // parents itself under that decision's Activity so the history reads as one action.
            ParentActivityId = parentActivityId
        };

        // A keep chosen while values were present must be auditable at the moment of choice (#1537): the
        // values remain in place with their Synchronisation Rule provenance nulled, and nothing ever recalls
        // them.
        if (!recallContributedValues && result.AffectedValueCount > 0)
        {
            activity.Message = $"Contributed attribute values were kept: {result.AffectedValueCount:N0} value(s) across " +
                $"{result.AffectedObjectCount:N0} Metaverse Object(s) remain in place with no Synchronisation Rule provenance.";
        }

        if (initiatedByApiKey != null)
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        else
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await DeleteSyncRuleCoreAsync(syncRule, activity, changeReason);
        return result;
    }

    /// <summary>
    /// The synchronous heart of a Synchronisation Rule deletion, shared by the direct delete paths and the
    /// recall task's final step: captures the configuration tombstone, deletes the rule, re-densifies each
    /// affected attribute's priority list, and completes the given Activity.
    /// </summary>
    private async Task DeleteSyncRuleCoreAsync(SyncRule syncRule, Activity activity, string? changeReason)
    {
        // Capture the attributes this import rule contributes to before deletion, so each can be re-densified after.
        var affectedAttributeIds = GetContributingImportAttributeIds(syncRule);

        await CaptureConfigurationDeletionAsync(activity, syncRule, changeReason);
        await Application.Repository.ConnectedSystems.DeleteSyncRuleAsync(syncRule);

        foreach (var attributeId in affectedAttributeIds)
            await ReconcileAttributePriorityAsync(syncRule.MetaverseObjectTypeId, attributeId);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// The Metaverse attribute a mapping targets, read from the navigation property in preference to the scalar FK.
    /// A whole-rule save arrives straight from the portal's editor, which binds the target to the navigation
    /// (<see cref="SyncRuleMapping.TargetMetaverseAttribute"/>); EF only fixes the FK up at SaveChanges, so before
    /// the write the scalar is still null on a new mapping and stale on a retargeted one. Null for export mappings.
    /// </summary>
    private static int? GetTargetMetaverseAttributeId(SyncRuleMapping mapping) =>
        mapping.TargetMetaverseAttribute?.Id ?? mapping.TargetMetaverseAttributeId;

    /// <summary>
    /// Validates the staged mapping removal choices a whole-rule save carries (#1537), refusing shapes that
    /// cannot describe a real staged removal. Each is a caller defect, not a tolerable input: a choice on a
    /// rule being created has no persisted mappings to remove, a duplicate makes the intent ambiguous, and a
    /// choice naming a mapping still present on the rule claims a removal the save will not perform; honouring
    /// its keep would sever live values out from under a mapping that still exists. Fast, hard failure over
    /// silent damage.
    /// </summary>
    private static void ValidateMappingRemovalChoices(SyncRule syncRule, IReadOnlyCollection<SyncRuleMappingRemovalChoice>? mappingRemovalChoices)
    {
        if (mappingRemovalChoices == null || mappingRemovalChoices.Count == 0)
            return;

        if (syncRule.Id == 0)
            throw new ArgumentException("Mapping removal choices apply to updates only: a Synchronisation Rule being created has no persisted Attribute Flow mappings to remove.");

        if (mappingRemovalChoices.Select(c => c.MappingId).Distinct().Count() != mappingRemovalChoices.Count)
            throw new ArgumentException("Mapping removal choices contain a duplicate mapping id, making the intent ambiguous.");

        var stagedMappingIds = syncRule.AttributeFlowRules.Where(m => m.Id != 0).Select(m => m.Id).ToHashSet();
        var stillPresent = mappingRemovalChoices.FirstOrDefault(c => stagedMappingIds.Contains(c.MappingId));
        if (stillPresent != null)
            throw new ArgumentException($"Mapping removal choice for mapping {stillPresent.MappingId} names a mapping still present on the Synchronisation Rule; a choice may only describe a staged removal.");
    }

    /// <summary>
    /// Applies a whole-rule save's staged Attribute Flow mapping removals (#1537): the portal's editor removes
    /// mappings from <see cref="SyncRule.AttributeFlowRules"/> in memory, and each removal's recall-or-keep
    /// choice arrives here alongside the save. Per named mapping, resolved against the DATABASE state (the
    /// staged collection no longer holds it): where keep was chosen and the mapping's (rule, target attribute)
    /// pair contributed values, their Synchronisation Rule provenance is severed FIRST (permanently exempting
    /// them from the orphan recall) and the choice is recorded on the save's Activity; then the mapping row and
    /// its sources are deleted properly. Without a keep, provenance stays intact and the shipped orphan recall
    /// withdraws the values at the next Full Synchronisation of the contributing system.
    /// <para>
    /// A staged removal NOT named in the choices still deletes its row: the mapping's Synchronisation Rule
    /// foreign key is required (#1550), so severing the relationship deletes the orphaned row at save. What the
    /// carrier adds on top is the recall-or-keep choice and its audit trail; an uncarried removal always takes
    /// the safe default (recall).
    /// </para>
    /// </summary>
    private async Task<List<string>> ApplyStagedMappingRemovalChoicesAsync(SyncRule syncRule, IReadOnlyCollection<SyncRuleMappingRemovalChoice>? mappingRemovalChoices)
    {
        if (mappingRemovalChoices == null || mappingRemovalChoices.Count == 0)
            return [];

        // Read every named mapping BEFORE acting on any of them. The mapping's Synchronisation Rule foreign key
        // is required (#1550), so on a tracking context the very first SaveChanges after the editor severed a
        // mapping deletes its row; a read-then-act interleaving would find the second mapping already gone.
        // This is also why the caller invokes this method before anything else in the save flushes.
        var removedMappings = new List<(SyncRuleMappingRemovalChoice Choice, SyncRuleMapping Mapping)>();
        foreach (var choice in mappingRemovalChoices)
        {
            // The database state is authoritative: the staged collection no longer holds the mapping, and only
            // the persisted row can say which Metaverse attribute it targeted.
            var removedMapping = await Application.Repository.ConnectedSystems.GetSyncRuleMappingAsync(choice.MappingId)
                ?? throw new ArgumentException($"Mapping removal choice for mapping {choice.MappingId} names a mapping that does not exist; it may already have been deleted, in which case its choice was made then.");
            // The owner is read from the persisted foreign key: a mapping owned by a DIFFERENT rule is a caller
            // defect. On a tracking context the staged removal may have severed the loaded instance's rule
            // navigation, which is why the scalar is the authority here.
            if (removedMapping.SyncRuleId != syncRule.Id)
                throw new ArgumentException($"Mapping removal choice for mapping {choice.MappingId} names a mapping that does not belong to Synchronisation Rule {syncRule.Id}.");

            removedMappings.Add((choice, removedMapping));
        }

        // The rows themselves are NOT deleted here: the required foreign key guarantees the save's own flush
        // deletes every severed row (and cascades its sources), and the rule save advances the configuration
        // watermark, so the #1536 orphan recall re-evaluates every object at the next Full Synchronisation.
        var keepMessages = new List<string>();
        foreach (var (choice, removedMapping) in removedMappings)
        {
            var targetMetaverseAttributeId = GetTargetMetaverseAttributeId(removedMapping);
            if (!choice.KeepContributedValues || !targetMetaverseAttributeId.HasValue)
                continue;

            // Sever BEFORE the rows are deleted by the save, mirroring the direct delete path. #1535 made
            // duplicate targets within a rule impossible, so (rule id, target attribute id) identifies exactly
            // this mapping's contributions.
            var severedAttributeId = targetMetaverseAttributeId.Value;
            var summary = await Application.Repository.Metaverse.GetContributedValuesSummaryAsync(syncRule.Id, severedAttributeId);
            if (summary.TotalValues == 0)
                continue;

            await Application.Repository.Metaverse.SeverContributedValueProvenanceAsync(syncRule.Id, severedAttributeId);
            var attributeName = removedMapping.TargetMetaverseAttribute?.Name ?? $"attribute {severedAttributeId}";
            keepMessages.Add($"Contributed attribute values were kept for the removed {attributeName} Attribute Flow: " +
                $"{summary.TotalValues:N0} value(s) across {summary.TotalObjects:N0} Metaverse Object(s) remain in place " +
                "with no Synchronisation Rule provenance.");
            Log.Information(
                "ApplyStagedMappingRemovalChoicesAsync: keep chosen for staged removal of mapping {MappingId} (Synchronisation Rule {SyncRuleId}, " +
                "Metaverse attribute {AttributeId}); severed provenance on {ValueCount} value(s) across {ObjectCount} Metaverse Object(s).",
                choice.MappingId, syncRule.Id, severedAttributeId, summary.TotalValues, summary.TotalObjects);
        }

        return keepMessages;
    }

    /// <summary>
    /// Captures, before a whole-rule save, which Metaverse attribute each of the rule's import mappings targets in
    /// the database, and resets any retargeted mapping to the safe-addition sentinel (#1199).
    /// <para>
    /// The portal never calls the granular create/delete mapping methods: its Attribute Flow editor mutates
    /// <see cref="SyncRule.AttributeFlowRules"/> in memory and saves the whole rule, so without this the
    /// safe-addition default and the dense-list invariant applied only to API callers.
    /// </para>
    /// <para>
    /// The sentinel reset is the subtle half. Retargeting moves a mapping between two attributes' priority lists,
    /// and priority numbers are only meaningful within one list: a mapping sitting at priority 1 for its old
    /// attribute would be renumbered straight to the top of its new attribute's list and silently start winning
    /// resolution there. Resetting it makes it arrive last, like any other newly-added contribution. It happens
    /// before the write so the new value rides the same SaveChanges rather than costing a second one.
    /// </para>
    /// </summary>
    /// <param name="syncRule">The rule about to be saved.</param>
    /// <returns>Target Metaverse attribute id keyed by mapping id, as the database holds it. Empty for an export
    /// rule or a rule being created, neither of which has import priority state to compare against.</returns>
    private async Task<Dictionary<int, int>> CaptureImportPriorityStateBeforeSaveAsync(SyncRule syncRule)
    {
        if (syncRule.Direction != SyncRuleDirection.Import || syncRule.Id == 0)
            return [];

        var previousTargets = await Application.Repository.ConnectedSystems
            .GetImportMappingTargetMetaverseAttributesAsync(syncRule.Id);

        foreach (var mapping in syncRule.AttributeFlowRules
                     .Where(m => GetTargetMetaverseAttributeId(m) is int target &&
                                 previousTargets.TryGetValue(m.Id, out var previous) &&
                                 previous != target))
            mapping.Priority = int.MaxValue;

        return previousTargets;
    }

    /// <summary>
    /// After a whole-rule save, reconciles the priority list of every Metaverse attribute whose contributor set this
    /// save changed (#1199), so adding, removing or retargeting an Attribute Flow in the portal maintains the same
    /// dense 1..N invariant the granular API paths maintain.
    /// <para>
    /// Only genuinely affected attributes are reconciled: an unrelated edit (renaming the rule, changing a scoping
    /// criterion) touches no contributor set and issues no queries at all. An attribute is affected when a mapping
    /// now targets it that did not before, or no longer targets it and did.
    /// </para>
    /// </summary>
    /// <param name="syncRule">The just-saved rule. Its mappings now carry database-assigned ids.</param>
    /// <param name="previousTargets">The pre-save state from <see cref="CaptureImportPriorityStateBeforeSaveAsync"/>.</param>
    private async Task ReconcileAttributePriorityAfterRuleSaveAsync(SyncRule syncRule, Dictionary<int, int> previousTargets)
    {
        if (syncRule.Direction != SyncRuleDirection.Import)
            return;

        var currentTargets = syncRule.AttributeFlowRules
            .Where(m => GetTargetMetaverseAttributeId(m).HasValue)
            .ToDictionary(m => m.Id, m => GetTargetMetaverseAttributeId(m)!.Value);

        var affectedAttributeIds = new HashSet<int>();
        var arrivalsByAttribute = new Dictionary<int, HashSet<int>>();

        // Attributes that gained a contribution: a mapping targeting something it did not target before. These are the
        // arrivals, which have to land at the bottom of the attribute's list.
        foreach (var current in currentTargets
                     .Where(c => !previousTargets.TryGetValue(c.Key, out var previous) || previous != c.Value))
        {
            affectedAttributeIds.Add(current.Value);
            if (!arrivalsByAttribute.TryGetValue(current.Value, out var arrivals))
            {
                arrivals = [];
                arrivalsByAttribute[current.Value] = arrivals;
            }

            arrivals.Add(current.Key);
        }

        // Attributes that lost one: a mapping deleted outright, or retargeted away.
        foreach (var previous in previousTargets
                     .Where(p => !currentTargets.TryGetValue(p.Key, out var current) || current != p.Value))
            affectedAttributeIds.Add(previous.Value);

        foreach (var attributeId in affectedAttributeIds)
            await ReconcileAttributePriorityAsync(syncRule.MetaverseObjectTypeId, attributeId,
                arrivalsByAttribute.GetValueOrDefault(attributeId));
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
    /// Refuses an Object Matching Rule that could never match anything, before it is stored.
    /// </summary>
    /// <remarks>
    /// <see cref="ObjectMatchingRule.IsValid"/> described what a workable rule looks like and nothing called it,
    /// so the portal was able to store Simple mode rules with no Metaverse Object Type (#1458). The matching engine
    /// skips such a rule and moves to the next one, so a Connected System whose only rules are malformed matches
    /// nothing at all: every account that should have joined an existing identity projects a new one instead, and
    /// nothing reports it. A hard refusal here is what the Synchronisation Integrity rules ask for; the alternative
    /// is discovering the duplicate identities by hand, months later.
    /// </remarks>
    /// <exception cref="InvalidDataException">The rule cannot work, with the reason.</exception>
    private static void EnsureObjectMatchingRuleIsWorkable(ObjectMatchingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var invalidity = rule.DescribeInvalidity();
        if (invalidity != null)
            throw new InvalidDataException(invalidity);
    }

    /// <summary>
    /// Refuses a new Object Matching Rule whose scope the Connected System's matching mode would never consult.
    /// </summary>
    /// <remarks>
    /// The engine only reads the scope the mode names (type-scoped rules in simple mode, Synchronisation Rule
    /// scoped rules in advanced mode), so a rule of the other scope is silently inert: synchronisation joins
    /// nothing and nothing reports why (#1569). Creation is the only guarded operation: mode switches deliberately
    /// retain rules of the outgoing scope so a later switch back restores them, and those retained rules must stay
    /// editable and deletable.
    /// </remarks>
    /// <exception cref="InvalidDataException">The rule's scope and the system's mode disagree, with the remedy.</exception>
    private async Task EnsureObjectMatchingRuleScopeMatchesModeAsync(ObjectMatchingRule rule)
    {
        var connectedSystem = await ResolveObjectMatchingRuleConnectedSystemAsync(rule);

        // An unresolvable owner means the referenced parent does not exist; storage fails on the foreign key
        // regardless, so there is no mode to disagree with here.
        if (connectedSystem == null)
            return;

        var mismatch = ObjectMatchingRule.DescribeScopeMismatch(
            connectedSystem.ObjectMatchingRuleMode,
            ruleIsSyncRuleScoped: rule.SyncRuleId.HasValue || rule.SyncRule != null,
            connectedSystem.Name);
        if (mismatch != null)
            throw new InvalidDataException(mismatch);
    }

    /// <summary>
    /// Resolves the Connected System that owns an Object Matching Rule, preferring loaded navigations and falling
    /// back to repository lookups, whichever parent (Synchronisation Rule or Connected System Object Type) the
    /// rule carries.
    /// </summary>
    private async Task<ConnectedSystem?> ResolveObjectMatchingRuleConnectedSystemAsync(ObjectMatchingRule rule)
    {
        if (rule.SyncRule?.ConnectedSystem != null)
            return rule.SyncRule.ConnectedSystem;

        if (rule.SyncRule != null)
            return await GetConnectedSystemCoreAsync(rule.SyncRule.ConnectedSystemId);

        if (rule.SyncRuleId.HasValue)
        {
            var syncRule = await GetSyncRuleAsync(rule.SyncRuleId.Value);
            if (syncRule != null)
                return syncRule.ConnectedSystem ?? await GetConnectedSystemCoreAsync(syncRule.ConnectedSystemId);
        }

        if (rule.ConnectedSystemObjectType?.ConnectedSystem != null)
            return rule.ConnectedSystemObjectType.ConnectedSystem;

        if (rule.ConnectedSystemObjectType != null)
            return await GetConnectedSystemCoreAsync(rule.ConnectedSystemObjectType.ConnectedSystemId);

        if (rule.ConnectedSystemObjectTypeId.HasValue)
        {
            var objectType = await Application.Repository.ConnectedSystems.GetObjectTypeAsync(rule.ConnectedSystemObjectTypeId.Value);
            if (objectType != null)
                return await GetConnectedSystemCoreAsync(objectType.ConnectedSystemId);
        }

        return null;
    }

    /// <summary>
    /// Creates a new Object Matching Rule for a Connected System Object Type.
    /// </summary>
    public async Task CreateObjectMatchingRuleAsync(ObjectMatchingRule rule, MetaverseObject? initiatedBy)
    {
        EnsureObjectMatchingRuleIsWorkable(rule);
        await EnsureObjectMatchingRuleScopeMatchesModeAsync(rule);

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
        EnsureObjectMatchingRuleIsWorkable(rule);
        await EnsureObjectMatchingRuleScopeMatchesModeAsync(rule);

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
        EnsureObjectMatchingRuleIsWorkable(rule);

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
        EnsureObjectMatchingRuleIsWorkable(rule);

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
