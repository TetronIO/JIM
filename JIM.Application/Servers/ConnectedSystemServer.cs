using JIM.Connectors.File;
using JIM.Connectors.LDAP;
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
using Serilog;

namespace JIM.Application.Servers;

public class ConnectedSystemServer
{
    private JimApplication Application { get; }

    internal ConnectedSystemServer(JimApplication application)
    {
        Application = application;
    }

    #region Connector Definitions
    public async Task<IList<ConnectorDefinitionHeader>> GetConnectorDefinitionHeadersAsync()
    {
        return await Application.Repository.ConnectedSystems.GetConnectorDefinitionHeadersAsync();
    }

    public async Task<ConnectorDefinition?> GetConnectorDefinitionAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetConnectorDefinitionAsync(id);
    }

    public async Task<ConnectorDefinition?> GetConnectorDefinitionAsync(string name)
    {
        return await Application.Repository.ConnectedSystems.GetConnectorDefinitionAsync(name);
    }

    public async Task CreateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition)
    {
        await Application.Repository.ConnectedSystems.CreateConnectorDefinitionAsync(connectorDefinition);
    }

    public async Task UpdateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition)
    {
        await Application.Repository.ConnectedSystems.UpdateConnectorDefinitionAsync(connectorDefinition);
    }

    public async Task DeleteConnectorDefinitionAsync(ConnectorDefinition connectorDefinition)
    {
        await Application.Repository.ConnectedSystems.DeleteConnectorDefinitionAsync(connectorDefinition);
    }

    public async Task CreateConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile)
    {
        await Application.Repository.ConnectedSystems.CreateConnectorDefinitionFileAsync(connectorDefinitionFile);
    }

    public async Task DeleteConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile)
    {
        await Application.Repository.ConnectedSystems.DeleteConnectorDefinitionFileAsync(connectorDefinitionFile);
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

    public async Task<ConnectedSystem?> GetConnectedSystemAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(id);
    }

    public async Task<ConnectedSystemHeader?> GetConnectedSystemHeaderAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemHeaderAsync(id);
    }

    public int GetConnectedSystemCount()
    {
        return Application.Repository.ConnectedSystems.GetConnectedSystemCount();
    }
        
    public async Task CreateConnectedSystemAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (connectedSystem.ConnectorDefinition == null)
            throw new ArgumentException("connectedSystem.ConnectorDefinition is null!");

        if (connectedSystem.ConnectorDefinition.Settings == null || connectedSystem.ConnectorDefinition.Settings.Count == 0)
            throw new ArgumentException("connectedSystem.ConnectorDefinition has no settings. Cannot construct a valid connectedSystem object!");

        if (!AreRunProfilesValid(connectedSystem))
            throw new ArgumentException("connectedSystem.RunProfiles has some of a run type that is not supported by the Connector.");

        // create the connected system setting value objects from the connected system definition settings
        foreach (var connectedSystemDefinitionSetting in connectedSystem.ConnectorDefinition.Settings)
        {
            var settingValue = new ConnectedSystemSettingValue {
                Setting = connectedSystemDefinitionSetting
            };

            if (connectedSystemDefinitionSetting is { Type: ConnectedSystemSettingType.CheckBox, DefaultCheckboxValue: not null })
                settingValue.CheckboxValue = connectedSystemDefinitionSetting.DefaultCheckboxValue.Value;

            // Apply default string values for String, DropDown, and File settings
            if ((connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.String ||
                 connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.DropDown ||
                 connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.File) &&
                !string.IsNullOrEmpty(connectedSystemDefinitionSetting.DefaultStringValue))
                settingValue.StringValue = connectedSystemDefinitionSetting.DefaultStringValue.Trim();

            if (connectedSystemDefinitionSetting is { Type: ConnectedSystemSettingType.Integer, DefaultIntValue: not null })
                settingValue.IntValue = connectedSystemDefinitionSetting.DefaultIntValue.Value;

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
        await Application.Repository.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Creates a new Connected System (initiated by API key).
    /// </summary>
    public async Task CreateConnectedSystemAsync(ConnectedSystem connectedSystem, ApiKey initiatedByApiKey)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (connectedSystem.ConnectorDefinition == null)
            throw new ArgumentException("connectedSystem.ConnectorDefinition is null!");

        if (connectedSystem.ConnectorDefinition.Settings == null || connectedSystem.ConnectorDefinition.Settings.Count == 0)
            throw new ArgumentException("connectedSystem.ConnectorDefinition has no settings. Cannot construct a valid connectedSystem object!");

        if (!AreRunProfilesValid(connectedSystem))
            throw new ArgumentException("connectedSystem.RunProfiles has some of a run type that is not supported by the Connector.");

        // create the connected system setting value objects from the connected system definition settings
        foreach (var connectedSystemDefinitionSetting in connectedSystem.ConnectorDefinition.Settings)
        {
            var settingValue = new ConnectedSystemSettingValue {
                Setting = connectedSystemDefinitionSetting
            };

            if (connectedSystemDefinitionSetting is { Type: ConnectedSystemSettingType.CheckBox, DefaultCheckboxValue: not null })
                settingValue.CheckboxValue = connectedSystemDefinitionSetting.DefaultCheckboxValue.Value;

            // Apply default string values for String, DropDown, and File settings
            if ((connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.String ||
                 connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.DropDown ||
                 connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.File) &&
                !string.IsNullOrEmpty(connectedSystemDefinitionSetting.DefaultStringValue))
                settingValue.StringValue = connectedSystemDefinitionSetting.DefaultStringValue.Trim();

            if (connectedSystemDefinitionSetting is { Type: ConnectedSystemSettingType.Integer, DefaultIntValue: not null })
                settingValue.IntValue = connectedSystemDefinitionSetting.DefaultIntValue.Value;

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
        await Application.Repository.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy, Activity? parentActivity = null)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (!AreRunProfilesValid(connectedSystem))
            throw new ArgumentException("connectedSystem.RunProfiles has some of a run type that is not supported by the Connector.");

        Log.Verbose($"UpdateConnectedSystemAsync() called for {connectedSystem}");

        // are the settings valid?
        var validationResults = ValidateConnectedSystemSettings(connectedSystem);
        connectedSystem.SettingValuesValid = validationResults.All(q => q.IsValid);

        connectedSystem.LastUpdated = DateTime.UtcNow;

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ParentActivityId = parentActivity?.Id,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
            
        SanitiseConnectedSystemUserInput(connectedSystem);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing Connected System (initiated by API key).
    /// </summary>
    public async Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem, ApiKey initiatedByApiKey, Activity? parentActivity = null)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (!AreRunProfilesValid(connectedSystem))
            throw new ArgumentException("connectedSystem.RunProfiles has some of a run type that is not supported by the Connector.");

        Log.Verbose($"UpdateConnectedSystemAsync() called for {connectedSystem} (API key initiated)");

        // are the settings valid?
        var validationResults = ValidateConnectedSystemSettings(connectedSystem);
        connectedSystem.SettingValuesValid = validationResults.All(q => q.IsValid);

        connectedSystem.LastUpdated = DateTime.UtcNow;

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ParentActivityId = parentActivity?.Id,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        SanitiseConnectedSystemUserInput(connectedSystem);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);

        await Application.Activities.CompleteActivityAsync(activity);
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
    /// Switches the object matching rule mode for a Connected System.
    /// When switching to Advanced Mode (SyncRule), copies matching rules from
    /// Connected System object types to all import sync rules.
    /// When switching to Simple Mode (ConnectedSystem), analyses sync rule matching rules,
    /// selects the most common configuration per object type, and clears sync rule rules.
    /// </summary>
    /// <param name="connectedSystem">The Connected System to update</param>
    /// <param name="newMode">The new object matching rule mode</param>
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
            // Switching to Advanced Mode - copy matching rules to import sync rules
            result = await SwitchToAdvancedModeAsync(connectedSystem, initiatedBy);
        }
        else
        {
            // Switching to Simple Mode - migrate rules from sync rules to object types
            result = await SwitchToSimpleModeAsync(connectedSystem, initiatedBy);
        }

        if (!result.Success)
            return result;

        // Update the Connected System mode
        connectedSystem.ObjectMatchingRuleMode = newMode;
        connectedSystem.LastUpdated = DateTime.UtcNow;

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
            // Find matching rules for the sync rule's object type
            var objectType = connectedSystem.ObjectTypes?.FirstOrDefault(ot => ot.Id == syncRule.ConnectedSystemObjectTypeId);
            if (objectType == null || objectType.ObjectMatchingRules.Count == 0)
                continue;

            // Only copy if sync rule doesn't already have matching rules
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
                        MetaverseAttributeId = s.MetaverseAttributeId,
                        Expression = s.Expression
                    }).ToList()
                };
                syncRule.ObjectMatchingRules.Add(newRule);
            }

            await CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
            syncRulesUpdated++;
        }

        Log.Information("SwitchToAdvancedModeAsync: Copied matching rules to {Count} sync rule(s)", syncRulesUpdated);
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

        // Group sync rules by object type
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

            // Get sync rules that have matching rules defined
            var syncRulesWithRules = objectTypeGroup
                .Where(sr => sr.ObjectMatchingRules.Count > 0)
                .ToList();

            if (syncRulesWithRules.Count > 0)
            {
                // Create a signature for each sync rule's matching rules configuration
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

                // Get the sync rule with the most common configuration
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
                            TargetMetaverseAttributeId = sourceRule.TargetMetaverseAttributeId,
                            CaseSensitive = sourceRule.CaseSensitive,
                            Sources = sourceRule.Sources.Select(s => new ObjectMatchingRuleSource
                            {
                                Order = s.Order,
                                ConnectedSystemAttributeId = s.ConnectedSystemAttributeId,
                                MetaverseAttributeId = s.MetaverseAttributeId,
                                Expression = s.Expression
                            }).ToList()
                        };
                        objectType.ObjectMatchingRules.Add(newRule);
                    }

                    migration.MatchingRulesSet = sourceSyncRule.ObjectMatchingRules.Count;
                    objectTypesUpdated++;

                    Log.Information("SwitchToSimpleModeAsync: Set {Count} matching rule(s) on object type {ObjectType} " +
                        "(selected from {SyncRuleCount} sync rules with {UniqueConfigs} unique configuration(s))",
                        migration.MatchingRulesSet, objectType.Name, migration.SyncRulesWithMatchingRules,
                        migration.UniqueSyncRuleConfigurations);
                }
            }

            // Clear matching rules from all sync rules for this object type
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
                    .Select(s => $"{s.ConnectedSystemAttributeId}:{s.MetaverseAttributeId}:{s.Expression}")
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
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
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
            preview.Warnings.Add($"{preview.SyncRuleCount} sync rule(s) will be permanently deleted.");

        if (preview.JoinedMvoCount > 0)
            preview.Warnings.Add($"{preview.JoinedMvoCount} Metaverse Object(s) are joined to CSOs in this system. They will be disconnected.");

        if (preview.PendingExportCount > 0)
            preview.Warnings.Add($"{preview.PendingExportCount} pending export(s) will be deleted.");

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
    /// <returns>The result of the deletion request.</returns>
    public async Task<ConnectedSystemDeletionResult> DeleteAsync(int connectedSystemId, MetaverseObject? initiatedBy)
    {
        Log.Information("DeleteAsync: Starting deletion for Connected System {Id}, initiated by {User}",
            connectedSystemId, initiatedBy?.DisplayName ?? "System");

        // Get the Connected System
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
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
                ? new DeleteConnectedSystemWorkerTask(connectedSystemId, initiatedBy, evaluateMvoDeletionRules: true)
                : new DeleteConnectedSystemWorkerTask(connectedSystemId, evaluateMvoDeletionRules: true);
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
                ? new DeleteConnectedSystemWorkerTask(connectedSystemId, initiatedBy, evaluateMvoDeletionRules: true)
                : new DeleteConnectedSystemWorkerTask(connectedSystemId, evaluateMvoDeletionRules: true);
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
            // Mark orphaned MVOs for deletion before deleting the Connected System
            // This sets LastConnectorDisconnectedDate so housekeeping will delete them after grace period
            await Application.Metaverse.MarkOrphanedMvosForDeletionAsync(connectedSystemId);

            // Perform the deletion
            await Application.Repository.ConnectedSystems.DeleteConnectedSystemAsync(connectedSystemId);

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
    /// Executes the deletion of a Connected System. Called by the worker service for background deletions.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to delete.</param>
    /// <param name="evaluateMvoDeletionRules">Whether to mark orphaned MVOs for deletion before deleting the Connected System.</param>
    public async Task ExecuteDeletionAsync(int connectedSystemId, bool evaluateMvoDeletionRules = true)
    {
        Log.Information("ExecuteDeletionAsync: Starting for Connected System {Id}, EvaluateMvoDeletionRules={EvaluateMvo}",
            connectedSystemId, evaluateMvoDeletionRules);

        if (evaluateMvoDeletionRules)
        {
            // Mark orphaned MVOs for deletion before deleting the Connected System
            // This sets LastConnectorDisconnectedDate so housekeeping will delete them after grace period
            await Application.Metaverse.MarkOrphanedMvosForDeletionAsync(connectedSystemId);
        }

        await Application.Repository.ConnectedSystems.DeleteConnectedSystemAsync(connectedSystemId);

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
                Required = connectorSetting.Required
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

        // work out what connector we need to instantiate, so that we can use its internal validation method
        // 100% expecting this to be something we need to centralise/improve later as we develop the connector definition system
        // especially when we need to support uploaded connectors, not just built-in ones

        if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.LdapConnectorName)
            return CreateConfiguredLdapConnector().ValidateSettingValues(connectedSystem.SettingValues, Log.Logger);

        if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.FileConnectorName)
            return new FileConnector().ValidateSettingValues(connectedSystem.SettingValues, Log.Logger);

        // todo: support custom connectors.

        throw new NotImplementedException("Support for that connector definition has not been implemented yet.");
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

    /// <summary>
    /// Creates and configures an LDAP connector with credential protection and certificate provider.
    /// </summary>
    private LdapConnector CreateConfiguredLdapConnector()
    {
        var connector = new LdapConnector();

        // Set up credential protection for decrypting passwords
        if (Application.CredentialProtection != null)
            connector.SetCredentialProtection(Application.CredentialProtection);

        // Set up certificate provider for SSL/TLS validation
        connector.SetCertificateProvider(Application.Certificates);

        return connector;
    }
    #endregion

    #region Connected System Schema
    /// <summary>
    /// Causes the associated Connector to be instantiated and the schema imported from the connected system.
    /// Changes will be persisted, even if they are destructive, i.e. an attribute is removed.
    /// </summary>
    /// <returns>A result object containing details about what changed during the schema refresh.</returns>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<SchemaRefreshResult> ImportConnectedSystemSchemaAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        var result = new SchemaRefreshResult { Success = true };

        // every operation that results, either directly or indirectly in a data change requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.ImportSchema,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        // work out what connector we need to instantiate, so that we can use its internal validation method
        // 100% expecting this to be something we need to centralise/improve later as we develop the connector definition system
        // especially when we need to support uploaded connectors, not just built-in ones

        ConnectorSchema schema;
        if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.LdapConnectorName)
            schema = await CreateConfiguredLdapConnector().GetSchemaAsync(connectedSystem.SettingValues, Log.Logger);
        else if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.FileConnectorName)
            schema = await new FileConnector().GetSchemaAsync(connectedSystem.SettingValues, Log.Logger);
        else
            throw new NotImplementedException("Support for that connector definition has not been implemented yet.");

        // Merge the new schema with the existing one, preserving IDs for attributes that are referenced by sync rules
        // This prevents FK constraint violations when attributes are used in sync rule mappings
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

                connectedSystemObjectType.Attributes = new List<ConnectedSystemObjectTypeAttribute>();

                // Track removed attributes for this object type
                var removedAttributeNames = existingAttributeNames.Except(newAttributeNames).ToList();
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
                            ClassName = schemaAttribute.ClassName
                        });
                    }
                }

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
                        ClassName = a.ClassName
                    }).ToList()
                };

                // All attributes in a new object type are considered "added"
                result.AddedAttributes[schemaObjectType.Name] = schemaObjectType.Attributes.Select(a => a.Name).ToList();
            }

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

        // Set totals
        result.TotalObjectTypes = connectedSystem.ObjectTypes.Count;
        result.TotalAttributes = connectedSystem.ObjectTypes.Sum(ot => ot.Attributes?.Count ?? 0);

        await UpdateConnectedSystemAsync(connectedSystem, initiatedBy, activity);

        // finish the activity
        await Application.Activities.CompleteActivityAsync(activity);

        return result;
    }

    /// <summary>
    /// Imports a Connected System schema (initiated by API key).
    /// </summary>
    public async Task<SchemaRefreshResult> ImportConnectedSystemSchemaAsync(ConnectedSystem connectedSystem, ApiKey initiatedByApiKey)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        var result = new SchemaRefreshResult { Success = true };

        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.ImportSchema,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        ConnectorSchema schema;
        if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.LdapConnectorName)
            schema = await CreateConfiguredLdapConnector().GetSchemaAsync(connectedSystem.SettingValues, Log.Logger);
        else if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.FileConnectorName)
            schema = await new FileConnector().GetSchemaAsync(connectedSystem.SettingValues, Log.Logger);
        else
            throw new NotImplementedException("Support for that connector definition has not been implemented yet.");

        schema.ObjectTypes = schema.ObjectTypes.OrderBy(q => q.Name).ToList();

        var existingObjectTypes = connectedSystem.ObjectTypes?.ToList() ?? new List<ConnectedSystemObjectType>();
        var existingObjectTypeNames = existingObjectTypes.Select(ot => ot.Name).ToHashSet();
        var newObjectTypeNames = schema.ObjectTypes.Select(ot => ot.Name).ToHashSet();

        connectedSystem.ObjectTypes = new List<ConnectedSystemObjectType>();

        foreach (var removedObjectTypeName in existingObjectTypeNames.Except(newObjectTypeNames))
        {
            result.RemovedObjectTypes.Add(removedObjectTypeName);
        }

        foreach (var schemaObjectType in schema.ObjectTypes)
        {
            schemaObjectType.Attributes = schemaObjectType.Attributes.OrderBy(a => a.Name).ToList();

            var existingObjectType = existingObjectTypes.FirstOrDefault(ot => ot.Name == schemaObjectType.Name);

            ConnectedSystemObjectType connectedSystemObjectType;
            if (existingObjectType != null)
            {
                result.UpdatedObjectTypes.Add(schemaObjectType.Name);
                connectedSystemObjectType = existingObjectType;
                var existingAttributes = existingObjectType.Attributes?.ToList() ?? new List<ConnectedSystemObjectTypeAttribute>();
                var existingAttributeNames = existingAttributes.Select(a => a.Name).ToHashSet();
                var newAttributeNames = schemaObjectType.Attributes.Select(a => a.Name).ToHashSet();

                connectedSystemObjectType.Attributes = new List<ConnectedSystemObjectTypeAttribute>();

                var removedAttributeNames = existingAttributeNames.Except(newAttributeNames).ToList();
                if (removedAttributeNames.Count > 0)
                {
                    result.RemovedAttributes[schemaObjectType.Name] = removedAttributeNames;
                }

                var addedAttributeNames = new List<string>();

                foreach (var schemaAttribute in schemaObjectType.Attributes)
                {
                    var existingAttribute = existingAttributes.FirstOrDefault(a => a.Name == schemaAttribute.Name);
                    if (existingAttribute != null)
                    {
                        existingAttribute.Description = schemaAttribute.Description;
                        existingAttribute.AttributePlurality = schemaAttribute.AttributePlurality;
                        existingAttribute.Type = schemaAttribute.Type;
                        existingAttribute.ClassName = schemaAttribute.ClassName;
                        connectedSystemObjectType.Attributes.Add(existingAttribute);
                    }
                    else
                    {
                        addedAttributeNames.Add(schemaAttribute.Name);
                        connectedSystemObjectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                        {
                            Name = schemaAttribute.Name,
                            Description = schemaAttribute.Description,
                            AttributePlurality = schemaAttribute.AttributePlurality,
                            Type = schemaAttribute.Type,
                            ClassName = schemaAttribute.ClassName
                        });
                    }
                }

                if (addedAttributeNames.Count > 0)
                {
                    result.AddedAttributes[schemaObjectType.Name] = addedAttributeNames;
                }
            }
            else
            {
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
                        ClassName = a.ClassName
                    }).ToList()
                };

                result.AddedAttributes[schemaObjectType.Name] = schemaObjectType.Attributes.Select(a => a.Name).ToList();
            }

            // if there's an External Id attribute recommendation from the connector, use that
            // External ID attributes are automatically selected and locked to ensure the system always has the required anchor attributes.
            var attribute = connectedSystemObjectType.Attributes.SingleOrDefault(a => schemaObjectType.RecommendedExternalIdAttribute != null && a.Name == schemaObjectType.RecommendedExternalIdAttribute.Name);
            if (attribute != null)
            {
                attribute.IsExternalId = true;
                attribute.Selected = true;
                attribute.SelectionLocked = true;
            }

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

        result.TotalObjectTypes = connectedSystem.ObjectTypes.Count;
        result.TotalAttributes = connectedSystem.ObjectTypes.Sum(ot => ot.Attributes?.Count ?? 0);

        await UpdateConnectedSystemAsync(connectedSystem, initiatedByApiKey, activity);

        await Application.Activities.CompleteActivityAsync(activity);

        return result;
    }
    #endregion

    #region Connected System Hierarchy
    /// <summary>
    /// Causes the associated Connector to be instantiated and the hierarchy (partitions and containers) to be imported from the connected system.
    /// You will need update the ConnectedSystem after if happy with the changes, to persist them.
    /// </summary>
    /// <returns>A result object describing what changed during the hierarchy refresh.</returns>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task<HierarchyRefreshResult> ImportConnectedSystemHierarchyAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        // work out what connector we need to instantiate, so that we can use its internal validation method
        // 100% expecting this to be something we need to centralise/improve later as we develop the connector definition system
        // especially when we need to support uploaded connectors, not just built-in ones

        // every operation that results, either directly or indirectly in a data change requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.ImportHierarchy,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        List<ConnectorPartition> partitions;
        if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.LdapConnectorName)
        {
            partitions = await CreateConfiguredLdapConnector().GetPartitionsAsync(connectedSystem.SettingValues, Log.Logger);
            if (partitions.Count == 0)
            {
                // todo: report to the user we attempted to retrieve partitions, but got none back
            }
        }
        else
        {
            throw new NotImplementedException("Support for that connector definition has not been implemented yet.");
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

        // Persist the changes
        await UpdateConnectedSystemAsync(connectedSystem, initiatedBy, activity);

        // finish the activity
        await Application.Activities.CompleteActivityAsync(activity);

        return result;
    }

    /// <summary>
    /// Import the hierarchy (partitions and containers) from the connected system (initiated by API key).
    /// </summary>
    /// <param name="connectedSystem">The connected system to import hierarchy for.</param>
    /// <param name="initiatedByApiKey">The API key that initiated this operation.</param>
    /// <returns>A result object describing what changed during the hierarchy refresh.</returns>
    public async Task<HierarchyRefreshResult> ImportConnectedSystemHierarchyAsync(ConnectedSystem connectedSystem, ApiKey initiatedByApiKey)
    {
        ValidateConnectedSystemParameter(connectedSystem);
        ArgumentNullException.ThrowIfNull(initiatedByApiKey);

        // every operation that results, either directly or indirectly in a data change requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.ImportHierarchy,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        List<ConnectorPartition> partitions;
        if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.LdapConnectorName)
        {
            partitions = await CreateConfiguredLdapConnector().GetPartitionsAsync(connectedSystem.SettingValues, Log.Logger);
            if (partitions.Count == 0)
            {
                // todo: report to the user we attempted to retrieve partitions, but got none back
            }
        }
        else
        {
            throw new NotImplementedException("Support for that connector definition has not been implemented yet.");
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

        // Persist the changes
        await UpdateConnectedSystemAsync(connectedSystem, initiatedByApiKey, activity);

        // finish the activity
        await Application.Activities.CompleteActivityAsync(activity);

        return result;
    }

    /// <summary>
    /// Adds newly created containers to the hierarchy and auto-selects them if their parent is selected.
    /// Uses the connector's interface methods to parse container identifiers without connector-specific
    /// knowledge in the application layer.
    /// </summary>
    /// <param name="connectedSystem">The connected system to update.</param>
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

                // Determine if any ancestor container is already selected.
                // If so, this new container is already implicitly included via the ancestor's subtree search,
                // so we should NOT select it separately (that would cause duplicate imports).
                var hasSelectedAncestor = IsAnyAncestorSelected(parentContainer);

                // Only auto-select if:
                // 1. It's a top-level container in a selected partition, OR
                // 2. No ancestor is selected (meaning this branch wasn't previously covered)
                // In practice, if parent is selected, we do NOT select the child - it's already covered by subtree.
                var shouldSelect = !hasSelectedAncestor && (parentContainer == null && partition.Selected);

                // Create the new container using connector's method to extract display name
                var containerName = containerCreator.GetContainerDisplayName(containerExternalId);
                var newContainer = new ConnectedSystemContainer
                {
                    ExternalId = containerExternalId,
                    Name = containerName,
                    Selected = shouldSelect,
                    Partition = partition
                };

                if (parentContainer != null)
                {
                    parentContainer.AddChildContainer(newContainer);
                }
                else
                {
                    // Top-level container in partition
                    partition.Containers ??= new HashSet<ConnectedSystemContainer>();
                    partition.Containers.Add(newContainer);
                }

                containersAdded++;
                if (hasSelectedAncestor)
                {
                    Log.Information("RefreshAndAutoSelectContainersAsync: Added container {ContainerExternalId}, Selected: False (ancestor already selected, implicitly included via subtree)",
                        containerExternalId);
                }
                else
                {
                    Log.Information("RefreshAndAutoSelectContainersAsync: Added container {ContainerExternalId}, Selected: {Selected}",
                        containerExternalId, shouldSelect);
                }
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
                await UpdateConnectedSystemAsync(connectedSystem, initiatedByApiKey, activity);
            else
                await UpdateConnectedSystemAsync(connectedSystem, initiatedByUser, activity);

            activity.Message = $"Auto-selected {containersAdded} container(s) created during export";
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
    /// <param name="connectedSystem">The connected system to merge hierarchy into.</param>
    /// <param name="discoveredPartitions">The partitions discovered from the connector.</param>
    /// <returns>A result object describing what changed during the merge.</returns>
    private static HierarchyRefreshResult MergeHierarchy(ConnectedSystem connectedSystem, List<ConnectorPartition> discoveredPartitions)
    {
        var result = new HierarchyRefreshResult { Success = true };

        // Build lookup of existing items by ExternalId for efficient matching
        var existingPartitionLookup = (connectedSystem.Partitions ?? new List<ConnectedSystemPartition>())
            .ToDictionary(p => p.ExternalId, StringComparer.OrdinalIgnoreCase);
        var existingContainerLookup = BuildContainerLookup(connectedSystem.Partitions);

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
                    existingContainerLookup);
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
    /// Recursively merges discovered containers with existing ones.
    /// </summary>
    private static void MergeContainersRecursive(
        HashSet<ConnectedSystemContainer> existingContainers,
        List<ConnectorContainer> discoveredContainers,
        string? parentExternalId,
        HierarchyRefreshResult result,
        Dictionary<string, ConnectedSystemContainer> globalLookup)
    {
        var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var discovered in discoveredContainers)
        {
            if (globalLookup.TryGetValue(discovered.Id, out var existing))
            {
                matchedIds.Add(discovered.Id);

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
                    globalLookup);
            }
            else
            {
                // NEW container - add it
                var newContainer = BuildConnectedSystemContainerTree(discovered);
                existingContainers.Add(newContainer);

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
    #endregion

    /// <summary>
    /// Checks if any ancestor container in the hierarchy is selected.
    /// Used to determine if a new child container is already implicitly included via a parent's subtree search.
    /// </summary>
    private static bool IsAnyAncestorSelected(ConnectedSystemContainer? container)
    {
        var current = container;
        while (current != null)
        {
            if (current.Selected)
                return true;
            current = current.ParentContainer;
        }
        return false;
    }

    private static ConnectedSystemContainer BuildConnectedSystemContainerTree(ConnectorContainer connectorContainer)
    {
        var connectedSystemContainer = new ConnectedSystemContainer
        {
            ExternalId = connectorContainer.Id,
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

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Bulk updates multiple Connected System Attributes with a single parent activity.
    /// </summary>
    /// <param name="connectedSystem">The connected system containing the attributes.</param>
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
            await Application.Repository.ConnectedSystems.UpdateAttributesAsync(updated);

        if (errors.Count > 0)
            await Application.Activities.CompleteActivityWithWarningAsync(activity);
        else
            await Application.Activities.CompleteActivityAsync(activity);

        return (activity, updated, errors);
    }

    /// <summary>
    /// Bulk updates multiple Connected System Attributes with a single parent activity (initiated by API key).
    /// </summary>
    /// <param name="connectedSystem">The connected system containing the attributes.</param>
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
            await Application.Repository.ConnectedSystems.UpdateAttributesAsync(updated);

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
        // Capture the external ID value string representation BEFORE deletion.
        // We cannot reference the attribute value entity after deletion because it gets cascade deleted with the CSO.
        var externalIdDisplayValue = connectedSystemObject.ExternalIdAttributeValue?.ToString();

        await Application.Repository.ConnectedSystems.DeleteConnectedSystemObjectAsync(connectedSystemObject);

        // Create a Change Object for this deletion.
        // Note: ConnectedSystemObject and DeletedObjectExternalIdAttributeValue are intentionally NOT set
        // because the CSO and its attribute values have been cascade deleted from the database.
        // The DeletedObjectType field preserves the object type information.
        // TODO: Consider adding a DeletedObjectExternalIdValue (string) field to store the external ID value
        // without requiring a FK reference to the deleted attribute value entity.
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemObject.ConnectedSystemId,
            // ConnectedSystemObject is null for DELETE operations (CSO no longer exists)
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = DateTime.UtcNow,
            DeletedObjectType = connectedSystemObject.Type,
            // DeletedObjectExternalIdAttributeValue cannot be set - the attribute value is cascade deleted with the CSO
            ActivityRunProfileExecutionItem = activityRunProfileExecutionItem
        };

        // Log the external ID for audit purposes since we can't persist it via FK
        if (!string.IsNullOrEmpty(externalIdDisplayValue))
        {
            Log.Debug("DeleteConnectedSystemObjectAsync: Deleted CSO with external ID: {ExternalId}", externalIdDisplayValue);
        }

        // The change object will be persisted with the activity run profile execution item further up the stack.
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

        // Capture external ID values before deletion
        var externalIdValues = connectedSystemObjects
            .Select(cso => cso.ExternalIdAttributeValue?.ToString())
            .ToList();

        // Batch delete from database
        await Application.Repository.ConnectedSystems.DeleteConnectedSystemObjectsAsync(connectedSystemObjects);

        // Create change objects for each deletion
        for (int i = 0; i < connectedSystemObjects.Count; i++)
        {
            var cso = connectedSystemObjects[i];
            var executionItem = activityRunProfileExecutionItems[i];
            var externalIdValue = externalIdValues[i];

            var change = new ConnectedSystemObjectChange
            {
                ConnectedSystemId = cso.ConnectedSystemId,
                ChangeType = ObjectChangeType.Deleted,
                ChangeTime = DateTime.UtcNow,
                DeletedObjectType = cso.Type,
                ActivityRunProfileExecutionItem = executionItem
            };

            executionItem.ConnectedSystemObjectChange = change;
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

    public async Task<PagedResultSet<ConnectedSystemObjectHeader>> GetConnectedSystemObjectHeadersAsync(
        int connectedSystemId,
        int page = 1,
        int pageSize = 20,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        IEnumerable<ConnectedSystemObjectStatus>? statusFilter = null)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(
            connectedSystemId, page, pageSize, searchQuery, sortBy, sortDescending, statusFilter);
    }
    
    /// <summary>
    /// Retrieves a page's worth of Connected System Objects for a specific system.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="page">Which page to return results for, i.e. 1-n.</param>
    /// <param name="pageSize">How many Connected System Objects to return in this page of result. By default it's 100.</param>
    /// <param name="returnAttributes">Controls whether ConnectedSystemObject.AttributeValues[n].Attribute is populated. By default, it isn't for performance reasons.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public async Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(int connectedSystemId, int page = 1, int pageSize = 100, bool returnAttributes = false)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsAsync(connectedSystemId, page, pageSize, returnAttributes);
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
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue);
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, int attributeValue)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue);
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, long attributeValue)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue);
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, Guid attributeValue)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue);
    }

    /// <summary>
    /// Gets a Connected System Object by its secondary external ID attribute value.
    /// Used to find PendingProvisioning CSOs during import reconciliation.
    /// </summary>
    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(int connectedSystemId, int objectTypeId, string secondaryExternalIdValue)
    {
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
    /// Returns the count of CSOs in a connected system that are joined to a specific MVO.
    /// Used during sync to check if an MVO already has a join in this connected system (1:1 constraint).
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
    /// Returns the count of Connected System Objects joined to a specific Metaverse Object.
    /// Used to determine if an MVO has any remaining connectors before deletion.
    /// </summary>
    /// <param name="metaverseObjectId">The MVO ID to count joined CSOs for.</param>
    public async Task<int> GetConnectedSystemObjectCountByMetaverseObjectIdAsync(Guid metaverseObjectId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(metaverseObjectId);
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
    public async Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, List<ActivityRunProfileExecutionItem> activityRunProfileExecutionItems)
    {
        // bulk persist csos creates
        await Application.Repository.ConnectedSystems.CreateConnectedSystemObjectsAsync(connectedSystemObjects);

        // add a Change Object to the relevant Activity Run Profile Execution Item for each cso.
        // they will be persisted further up the call stack, when the activity gets persisted.
        foreach (var cso in connectedSystemObjects)
        {
            var activityRunProfileExecutionItem = activityRunProfileExecutionItems.SingleOrDefault(q => q.ConnectedSystemObject != null && q.ConnectedSystemObject.Id == cso.Id) ??
                                                  throw new InvalidDataException($"Couldn't find an ActivityRunProfileExecutionItem referencing CSO {cso.Id}! It should have been created before now.");

            // Explicitly set the FK now that the CSO has been persisted and has an ID.
            // This ensures the FK is properly tracked when the execution item is saved later.
            activityRunProfileExecutionItem.ConnectedSystemObjectId = cso.Id;

            AddConnectedSystemObjectChange(cso, activityRunProfileExecutionItem);
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
        // add a change object to the relevant activity run profile execution item for each cso to be updated.
        // the change objects will be persisted later, further up the call stack, when the activity gets persisted.
        foreach (var cso in connectedSystemObjects)
        {
            // Find the RPEI for this CSO - may be null if no attribute changes occurred (CSO was added to update list
            // for reference resolution purposes only)
            var activityRunProfileExecutionItem = activityRunProfileExecutionItems.FirstOrDefault(q => q.ConnectedSystemObject != null && q.ConnectedSystemObject.Id == cso.Id);

            if (activityRunProfileExecutionItem != null)
            {
                // Explicitly set the FK to ensure it's properly tracked when the execution item is saved.
                activityRunProfileExecutionItem.ConnectedSystemObjectId = cso.Id;

                ProcessConnectedSystemObjectAttributeValueChanges(cso, activityRunProfileExecutionItem);
            }
            // If no RPEI exists, CSO was added to update list for reference resolution but had no changes - skip change tracking
        }

        // bulk persist csos updates
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemObjectsAsync(connectedSystemObjects);
    }

    /// <summary>
    /// Adds a Change Object to a Run Profile Execution Item for a CSO that's being created.
    /// </summary>
    private static void AddConnectedSystemObjectChange(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
    {
        // now populate the Connected System Object Change Object with the cso attribute values.
        // create a change object we can add attribute changes to.
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemObject.ConnectedSystemId,
            ConnectedSystemObject = connectedSystemObject,
            ChangeType = ObjectChangeType.Added,
            ChangeTime = DateTime.UtcNow,
            ActivityRunProfileExecutionItem = activityRunProfileExecutionItem,
            ActivityRunProfileExecutionItemId = activityRunProfileExecutionItem.Id
        };
        activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;
        
        foreach (var attributeValue in connectedSystemObject.AttributeValues)
            AddChangeAttributeValueObject(change, attributeValue, ValueChangeType.Add);
    }

    /// <summary>
    /// Adds a Change object to the Run Profile Execution Item for a CSO that's being updated.
    /// </summary>
    private static void ProcessConnectedSystemObjectAttributeValueChanges(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
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

        // create a change object we can track attribute changes with
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemObject.ConnectedSystem.Id,
            ConnectedSystemObject = connectedSystemObject,
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = DateTime.UtcNow,
            ActivityRunProfileExecutionItem = activityRunProfileExecutionItem
        };

        // the change object will be persisted with the activity run profile execution item further up the stack.
        // we just need to associate the change with the detail item.
        // unsure if this is the right approach. should we persist the change here and just associate with the detail item?
        activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;

        // make sure the CSO is linked to the activity run profile execution item
        activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
        activityRunProfileExecutionItem.ConnectedSystemObjectId = connectedSystemObject.Id;

        // persist new attribute values from addition list and create change object
        foreach (var pendingAttributeValueAddition in connectedSystemObject.PendingAttributeValueAdditions)
        {
            connectedSystemObject.AttributeValues.Add(pendingAttributeValueAddition);
                
            // trigger auditing of this change
            AddChangeAttributeValueObject(change, pendingAttributeValueAddition, ValueChangeType.Add);
        }

        // delete attribute values to be removed and create change
        foreach (var pendingAttributeValueRemoval in connectedSystemObject.PendingAttributeValueRemovals)
        {
            // this will cause a cascade delete of the attribute value object
            connectedSystemObject.AttributeValues.RemoveAll(av => av.Id == pendingAttributeValueRemoval.Id);

            // trigger auditing of this change
            AddChangeAttributeValueObject(change, pendingAttributeValueRemoval, ValueChangeType.Remove);
        }
        
        // we can now reset the pending attribute value lists
        connectedSystemObject.PendingAttributeValueAdditions = new List<ConnectedSystemObjectAttributeValue>();
        connectedSystemObject.PendingAttributeValueRemovals = new List<ConnectedSystemObjectAttributeValue>();
    }

    /// <summary>
    /// Causes all the connected system objects and their dependencies to be deleted for a connected system.
    /// This includes: pending exports, CSO attribute values, change history, and disconnects CSOs from MVOs.
    /// Once performed, an admin must then re-synchronise all connectors to re-calculate any metaverse and connected system object changes.
    /// </summary>
    /// <remarks>
    /// Only intended to be called by JIM.Service, i.e. this action should always be queued.
    /// That's why this method is lightweight and doesn't create its own activity.
    /// Uses the shared DeleteAllConnectedSystemObjectsAndDependenciesAsync method with deleteChangeHistory=true
    /// to remove all CSO-related data including change history (since objects will be re-imported).
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier for the connected system to clear.</param>
    /// <exception cref="InvalidOperationException">Thrown if the Connected System is being deleted.</exception>
    public async Task ClearConnectedSystemObjectsAsync(int connectedSystemId)
    {
        Log.Information("ClearConnectedSystemObjectsAsync: Starting for Connected System {Id}", connectedSystemId);

        // Check for concurrency - don't clear if system is being deleted
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
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
        // deleteChangeHistory=true because we're clearing for re-import - the old change history is no longer relevant.
        await Application.Repository.ConnectedSystems.DeleteAllConnectedSystemObjectsAndDependenciesAsync(connectedSystemId, deleteChangeHistory: true);

        Log.Information("ClearConnectedSystemObjectsAsync: Completed for Connected System {Id}", connectedSystemId);

        // todo: think about returning a status to the UI. perhaps return the job id and allow the job status to be polled/streamed?
    }
        
    /// <summary>
    /// Creates the necessary attribute change audit item for when a CSO is created, updated, or deleted, and adds it to the change object.
    /// </summary>
    /// <param name="connectedSystemObjectChange">The ConnectedSystemObjectChange that's associated with a ActivityRunProfileExecutionItem (the audit object for a sync run).</param>
    /// <param name="connectedSystemObjectAttributeValue">The attribute and value pair for the new value.</param>
    /// <param name="valueChangeType">The type of change, i.e. CREATE/UPDATE/DELETE.</param>
    private static void AddChangeAttributeValueObject(ConnectedSystemObjectChange connectedSystemObjectChange, ConnectedSystemObjectAttributeValue connectedSystemObjectAttributeValue, ValueChangeType valueChangeType)
    {
        var attributeChange = connectedSystemObjectChange.AttributeChanges.SingleOrDefault(ac => ac.Attribute.Id == connectedSystemObjectAttributeValue.Attribute.Id);
        if (attributeChange == null)
        {
            // create the attribute change object that provides an audit trail of changes to a cso's attributes
            attributeChange = new ConnectedSystemObjectChangeAttribute
            {
                Attribute = connectedSystemObjectAttributeValue.Attribute,
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
            case AttributeDataType.Reference when connectedSystemObjectAttributeValue.ReferenceValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, connectedSystemObjectAttributeValue.ReferenceValue));
                break;
            case AttributeDataType.Reference when connectedSystemObjectAttributeValue.UnresolvedReferenceValue != null:
                // we do not log changes for unresolved references. only resolved references get change tracked.
                break;
            case AttributeDataType.NotSet:
            default:
                throw new InvalidDataException($"AddChangeAttributeValueObject:  Invalid removal attribute '{connectedSystemObjectAttributeValue.Attribute.Name}' of type '{connectedSystemObjectAttributeValue.Attribute.Type}' or null attribute value.");
        }
    }

    public async Task<bool> IsObjectTypeAttributeBeingReferencedAsync(ConnectedSystemObjectTypeAttribute connectedSystemObjectTypeAttribute)
    {
        return await Application.Repository.ConnectedSystems.IsObjectTypeAttributeBeingReferencedAsync(connectedSystemObjectTypeAttribute);
    }
    #endregion

    #region Connected System Partitions
    public async Task CreateConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition)
    {
        if (connectedSystemPartition == null)
            throw new ArgumentNullException(nameof(connectedSystemPartition));

        await Application.Repository.ConnectedSystems.CreateConnectedSystemPartitionAsync(connectedSystemPartition);
    }

    public async Task<IList<ConnectedSystemPartition>> GetConnectedSystemPartitionsAsync(ConnectedSystem connectedSystem)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        return await Application.Repository.ConnectedSystems.GetConnectedSystemPartitionsAsync(connectedSystem);
    }

    public async Task<ConnectedSystemPartition?> GetConnectedSystemPartitionAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemPartitionAsync(id);
    }

    public async Task UpdateConnectedSystemPartitionAsync(ConnectedSystemPartition partition)
    {
        if (partition == null)
            throw new ArgumentNullException(nameof(partition));

        await Application.Repository.ConnectedSystems.UpdateConnectedSystemPartitionAsync(partition);
    }

    public async Task DeleteConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition)
    {
        if (connectedSystemPartition == null)
            throw new ArgumentNullException(nameof(connectedSystemPartition));

        await Application.Repository.ConnectedSystems.DeleteConnectedSystemPartitionAsync(connectedSystemPartition);
    }
    #endregion

    #region Connected System Containers
    /// <summary>
    /// Used to create a top-level container (optionally with children), when the connector does not implement Partitions.
    /// If the connector implements Partitions, then use CreateConnectedSystemPartitionAsync and add the container to that.
    /// </summary>
    public async Task CreateConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer)
    {
        if (connectedSystemContainer == null)
            throw new ArgumentNullException(nameof(connectedSystemContainer));

        await Application.Repository.ConnectedSystems.CreateConnectedSystemContainerAsync(connectedSystemContainer);
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

    public async Task UpdateConnectedSystemContainerAsync(ConnectedSystemContainer container)
    {
        if (container == null)
            throw new ArgumentNullException(nameof(container));

        await Application.Repository.ConnectedSystems.UpdateConnectedSystemContainerAsync(container);
    }

    public async Task DeleteConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer)
    {
        if (connectedSystemContainer == null)
            throw new ArgumentNullException(nameof(connectedSystemContainer));


        await Application.Repository.ConnectedSystems.DeleteConnectedSystemContainerAsync(connectedSystemContainer);
    }
    #endregion

    #region Sync Rule Mappings
    /// <summary>
    /// Gets all mappings for a sync rule.
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    public async Task<List<SyncRuleMapping>> GetSyncRuleMappingsAsync(int syncRuleId)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRuleMappingsAsync(syncRuleId);
    }

    /// <summary>
    /// Gets a specific sync rule mapping by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the mapping.</param>
    public async Task<SyncRuleMapping?> GetSyncRuleMappingAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRuleMappingAsync(id);
    }

    /// <summary>
    /// Creates a new sync rule mapping.
    /// </summary>
    /// <param name="mapping">The mapping to create.</param>
    /// <param name="initiatedBy">The user who initiated the creation.</param>
    public async Task CreateSyncRuleMappingAsync(SyncRuleMapping mapping, MetaverseObject? initiatedBy)
    {
        if (mapping == null)
            throw new ArgumentNullException(nameof(mapping));

        Log.Debug("CreateSyncRuleMappingAsync() called for sync rule {SyncRuleId}", mapping.SyncRule?.Id);

        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
        var activity = new Activity
        {
            TargetName = $"Mapping to {targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SyncRule,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await Application.Repository.ConnectedSystems.CreateSyncRuleMappingAsync(mapping);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Creates a new sync rule mapping (initiated by API key).
    /// </summary>
    public async Task CreateSyncRuleMappingAsync(SyncRuleMapping mapping, ApiKey initiatedByApiKey)
    {
        if (mapping == null)
            throw new ArgumentNullException(nameof(mapping));

        Log.Debug("CreateSyncRuleMappingAsync() called for sync rule {SyncRuleId} (API key initiated)", mapping.SyncRule?.Id);

        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
        var activity = new Activity
        {
            TargetName = $"Mapping to {targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SyncRule,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        await Application.Repository.ConnectedSystems.CreateSyncRuleMappingAsync(mapping);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing sync rule mapping.
    /// </summary>
    /// <param name="mapping">The mapping to update.</param>
    /// <param name="initiatedBy">The user who initiated the update.</param>
    public async Task UpdateSyncRuleMappingAsync(SyncRuleMapping mapping, MetaverseObject? initiatedBy)
    {
        if (mapping == null)
            throw new ArgumentNullException(nameof(mapping));

        Log.Debug("UpdateSyncRuleMappingAsync() called for mapping {Id}", mapping.Id);

        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
        var activity = new Activity
        {
            TargetName = $"Mapping to {targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SyncRule,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await Application.Repository.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes a sync rule mapping.
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
            TargetName = $"Mapping to {targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SyncRule,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await Application.Repository.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping);

        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes a Sync Rule Mapping (initiated by API key).
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
            TargetName = $"Mapping to {targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SyncRule,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);

        await Application.Repository.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping);

        await Application.Activities.CompleteActivityAsync(activity);
    }
    #endregion

    #region Connected System Run Profiles
    public async Task CreateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, MetaverseObject? initiatedBy)
    {
        if (connectedSystemRunProfile == null)
            throw new ArgumentNullException(nameof(connectedSystemRunProfile));

        // need to get the connected system, so we can validate the run profile
        var connectedSystem = await GetConnectedSystemAsync(connectedSystemRunProfile.ConnectedSystemId) ?? throw new ArgumentException("No such Connected System found!");
        if (!IsRunProfileValid(connectedSystem, connectedSystemRunProfile))
            throw new ArgumentException("Run profile is not valid for the Connector!");

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
        await Application.Repository.ConnectedSystems.CreateConnectedSystemRunProfileAsync(connectedSystemRunProfile);

        // now the run profile has been persisted, associated it with the activity and complete it.
        activity.ConnectedSystemRunProfileId = connectedSystemRunProfile.Id;
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Creates a Connected System Run Profile (initiated by API key).
    /// </summary>
    public async Task CreateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, ApiKey initiatedByApiKey)
    {
        if (connectedSystemRunProfile == null)
            throw new ArgumentNullException(nameof(connectedSystemRunProfile));

        var connectedSystem = await GetConnectedSystemAsync(connectedSystemRunProfile.ConnectedSystemId) ?? throw new ArgumentException("No such Connected System found!");
        if (!IsRunProfileValid(connectedSystem, connectedSystemRunProfile))
            throw new ArgumentException("Run profile is not valid for the Connector!");

        var activity = new Activity
        {
            TargetName = connectedSystemRunProfile.Name,
            TargetContext = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Create,
            ConnectedSystemId = connectedSystemRunProfile.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        await Application.Repository.ConnectedSystems.CreateConnectedSystemRunProfileAsync(connectedSystemRunProfile);

        activity.ConnectedSystemRunProfileId = connectedSystemRunProfile.Id;
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, MetaverseObject? initiatedBy)
    {
        if (connectedSystemRunProfile == null)
            return;

        // Get connected system name for activity context
        var connectedSystem = await GetConnectedSystemAsync(connectedSystemRunProfile.ConnectedSystemId);

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
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task UpdateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, MetaverseObject? initiatedBy)
    {
        if (connectedSystemRunProfile == null)
            throw new ArgumentNullException(nameof(connectedSystemRunProfile));

        // Get connected system name for activity context
        var connectedSystem = await GetConnectedSystemAsync(connectedSystemRunProfile.ConnectedSystemId);

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
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(connectedSystemRunProfile);
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
    /// Checks if any run profile types are not supported by the connectors capabilities.
    /// </summary>
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
    /// Checks if any run profile types are not supported by the connectors capabilities.
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
    /// Used to efficiently create pending exports during sync export evaluation.
    /// </summary>
    /// <param name="pendingExports">The Pending Exports to create.</param>
    public async Task CreatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
    {
        await Application.Repository.ConnectedSystems.CreatePendingExportsAsync(pendingExports);
    }

    /// <summary>
    /// Deletes multiple Pending Export objects in a single batch operation.
    /// Used to efficiently remove confirmed pending exports during sync.
    /// </summary>
    /// <param name="pendingExports">The Pending Exports to delete.</param>
    public async Task DeletePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
    {
        await Application.Repository.ConnectedSystems.DeletePendingExportsAsync(pendingExports);
    }

    /// <summary>
    /// Updates multiple Pending Export objects in a single batch operation.
    /// Used to efficiently update pending exports during sync.
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
    /// Retrieves the Pending Export for a specific Connected System Object.
    /// </summary>
    /// <param name="connectedSystemObjectId">The unique identifier of the Connected System Object.</param>
    /// <returns>The PendingExport for the CSO, or null if none exists.</returns>
    public async Task<PendingExport?> GetPendingExportForObjectAsync(Guid connectedSystemObjectId)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportByConnectedSystemObjectIdAsync(connectedSystemObjectId);
    }
    #endregion

    #region Sync Rules
    public async Task<List<SyncRule>> GetSyncRulesAsync()
    {
        return await Application.Repository.ConnectedSystems.GetSyncRulesAsync();
    }

    /// <summary>
    /// Retrieves all the sync rules for a given Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="includeDisabledSyncRules">Controls whether to return sync rules that are disabled</param>
    public async Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabledSyncRules)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRulesAsync(connectedSystemId, includeDisabledSyncRules);
    }

    public async Task<IList<SyncRuleHeader>> GetSyncRuleHeadersAsync()
    {
        return await Application.Repository.ConnectedSystems.GetSyncRuleHeadersAsync();
    }

    public async Task<SyncRule?> GetSyncRuleAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRuleAsync(id);
    }

    public async Task<bool> CreateOrUpdateSyncRuleAsync(SyncRule syncRule, MetaverseObject? initiatedBy, Activity? parentActivity = null)
    {
        // validate the sync rule
        if (syncRule == null)
            throw new NullReferenceException(nameof(syncRule));

        Log.Verbose($"CreateOrUpdateSyncRuleAsync() called for: {syncRule}");
        
        if (!syncRule.IsValid())
            return false;
        
        // remove any mutually-exclusive property combinations
        if (syncRule.Direction == SyncRuleDirection.Import)
        {
            // import rule cannot have these properties:
            syncRule.ProvisionToConnectedSystem = null;
            // Note: ObjectScopingCriteriaGroups IS valid for import rules - evaluates CSO attributes

            // In Simple Mode, matching rules are defined on the Connected System, not sync rules
            // Clear any matching rules that may have been provided
            if (syncRule.ConnectedSystemId > 0)
            {
                var connectedSystem = syncRule.ConnectedSystem ??
                    await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(syncRule.ConnectedSystemId);

                if (connectedSystem?.ObjectMatchingRuleMode == ObjectMatchingRuleMode.ConnectedSystem)
                {
                    if (syncRule.ObjectMatchingRules.Count > 0)
                    {
                        Log.Warning("CreateOrUpdateSyncRuleAsync: Clearing {Count} matching rules from sync rule {Id} " +
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
        
        
        // Get connected system name for activity context
        var connectedSystemForContext = syncRule.ConnectedSystem ??
            (syncRule.ConnectedSystemId > 0 ? await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(syncRule.ConnectedSystemId) : null);

        // every crud operation must be tracked via an Activity
        var activity = new Activity
        {
            TargetName = syncRule.Name,
            TargetContext = connectedSystemForContext?.Name,
            TargetType = ActivityTargetType.SyncRule,
            ParentActivityId = parentActivity?.Id
        };

        if (syncRule.Id == 0)
        {
            // new sync rule - create
            activity.TargetOperationType = ActivityTargetOperationType.Create;
            syncRule.CreatedBy = initiatedBy;
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
            await Application.Repository.ConnectedSystems.CreateSyncRuleAsync(syncRule);
        }
        else
        {
            // existing sync rule - update
            activity.TargetOperationType = ActivityTargetOperationType.Update;
            syncRule.LastUpdated = DateTime.UtcNow;
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
            await Application.Repository.ConnectedSystems.UpdateSyncRuleAsync(syncRule);
        }

        await Application.Activities.CompleteActivityAsync(activity);
        return true;
    }

    /// <summary>
    /// Creates or updates a Sync Rule (initiated by API key).
    /// </summary>
    public async Task<bool> CreateOrUpdateSyncRuleAsync(SyncRule syncRule, ApiKey initiatedByApiKey, Activity? parentActivity = null)
    {
        if (syncRule == null)
            throw new NullReferenceException(nameof(syncRule));

        Log.Verbose($"CreateOrUpdateSyncRuleAsync() called for: {syncRule} (API key initiated)");

        if (!syncRule.IsValid())
            return false;

        if (syncRule.Direction == SyncRuleDirection.Import)
        {
            syncRule.ProvisionToConnectedSystem = null;

            if (syncRule.ConnectedSystemId > 0)
            {
                var connectedSystem = syncRule.ConnectedSystem ??
                    await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(syncRule.ConnectedSystemId);

                if (connectedSystem?.ObjectMatchingRuleMode == ObjectMatchingRuleMode.ConnectedSystem)
                {
                    if (syncRule.ObjectMatchingRules.Count > 0)
                    {
                        Log.Warning("CreateOrUpdateSyncRuleAsync: Clearing {Count} matching rules from sync rule {Id} " +
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

        // Get connected system name for activity context
        var connectedSystemForContext = syncRule.ConnectedSystem ??
            (syncRule.ConnectedSystemId > 0 ? await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(syncRule.ConnectedSystemId) : null);

        var activity = new Activity
        {
            TargetName = syncRule.Name,
            TargetContext = connectedSystemForContext?.Name,
            TargetType = ActivityTargetType.SyncRule,
            ParentActivityId = parentActivity?.Id
        };

        if (syncRule.Id == 0)
        {
            activity.TargetOperationType = ActivityTargetOperationType.Create;
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
            await Application.Repository.ConnectedSystems.CreateSyncRuleAsync(syncRule);
        }
        else
        {
            activity.TargetOperationType = ActivityTargetOperationType.Update;
            syncRule.LastUpdated = DateTime.UtcNow;
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
            await Application.Repository.ConnectedSystems.UpdateSyncRuleAsync(syncRule);
        }

        await Application.Activities.CompleteActivityAsync(activity);
        return true;
    }

    public async Task DeleteSyncRuleAsync(SyncRule syncRule, MetaverseObject? initiatedBy)
    {
        // Get connected system name for activity context
        var connectedSystem = syncRule.ConnectedSystem ??
            (syncRule.ConnectedSystemId > 0 ? await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(syncRule.ConnectedSystemId) : null);

        // every crud operation must be tracked via an Activity
        var activity = new Activity
        {
            TargetName = syncRule.Name,
            TargetContext = connectedSystem?.Name,
            TargetType = ActivityTargetType.SyncRule,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        await Application.Repository.ConnectedSystems.DeleteSyncRuleAsync(syncRule);
        await Application.Activities.CompleteActivityAsync(activity);
    }
    #endregion

    #region Object Matching Rules
    /// <summary>
    /// Gets the target context for an ObjectMatchingRule activity.
    /// Returns Connected System name for Mode A (rules on ConnectedSystemObjectType) or Sync Rule name for Mode B.
    /// </summary>
    private async Task<string?> GetObjectMatchingRuleContextAsync(ObjectMatchingRule rule)
    {
        // Mode B: Rule is on a SyncRule - show the Sync Rule name
        if (rule.SyncRule != null)
            return rule.SyncRule.Name;

        // Mode A: Rule is on a ConnectedSystemObjectType - show the Connected System name
        // First check if navigation property is loaded
        if (rule.ConnectedSystemObjectType?.ConnectedSystem != null)
            return rule.ConnectedSystemObjectType.ConnectedSystem.Name;

        // Navigation property not loaded - fetch the Connected System
        if (rule.ConnectedSystemObjectType != null)
        {
            var connectedSystem = await GetConnectedSystemAsync(rule.ConnectedSystemObjectType.ConnectedSystemId);
            return connectedSystem?.Name;
        }

        if (rule.ConnectedSystemObjectTypeId.HasValue)
        {
            var objectType = await Application.Repository.ConnectedSystems.GetObjectTypeAsync(rule.ConnectedSystemObjectTypeId.Value);
            if (objectType != null)
            {
                var connectedSystem = await GetConnectedSystemAsync(objectType.ConnectedSystemId);
                return connectedSystem?.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a new object matching rule for a Connected System Object Type.
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
        await Application.Repository.ConnectedSystems.CreateObjectMatchingRuleAsync(rule);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Creates a new object matching rule (initiated by API key).
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
        await Application.Repository.ConnectedSystems.CreateObjectMatchingRuleAsync(rule);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Updates an existing object matching rule.
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
        await Application.Repository.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Deletes an object matching rule and its sources.
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
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Gets an object matching rule by ID.
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
