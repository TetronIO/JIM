using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
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
            await Application.Tasking.CreateWorkerTaskAsync(deleteTask);

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
            await Application.Tasking.CreateWorkerTaskAsync(deleteTask);

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
            return new LdapConnector().ValidateSettingValues(connectedSystem.SettingValues, Log.Logger);

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
    #endregion

    #region Connected System Schema
    /// <summary>
    /// Causes the associated Connector to be instantiated and the schema imported from the connected system.
    /// Changes will be persisted, even if they are destructive, i.e. an attribute is removed.
    /// </summary>
    /// <returns>Nothing, the ConnectedSystem passed in will be updated though with the new schema.</returns>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task ImportConnectedSystemSchemaAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy)
    {
        ValidateConnectedSystemParameter(connectedSystem);

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
            schema = await new LdapConnector().GetSchemaAsync(connectedSystem.SettingValues, Log.Logger);
        else if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.FileConnectorName)
            schema = await new FileConnector().GetSchemaAsync(connectedSystem.SettingValues, Log.Logger);
        else
            throw new NotImplementedException("Support for that connector definition has not been implemented yet.");

        // this could potentially be a good point to check for data-loss if persisted and return a report object
        // that the user could use to decide if they need to take corrective steps, i.e. adjust attribute flow on sync rules.

        // super destructive at this point. this is for MVP only. will result in all prior  user object type and attribute selections to be lost!
        // todo: work out dependent changes required, i.e. sync rules will rely on connected system object type attributes. if they get removed from the schema
        // then we need to break any sync rule attribute flow relationships. this could be done gracefully to allow the user the opportunity to revise them, 
        // i.e. instead of just deleting the attribute flow and the user not knowing what they've lost, perhaps disable the attribute flow and leave a copy of the cs attrib name in place, 
        // so they can see it's not valid anymore and have information that will enable them to work out what to do about it.
        schema.ObjectTypes = schema.ObjectTypes.OrderBy(q => q.Name).ToList();
        connectedSystem.ObjectTypes = new List<ConnectedSystemObjectType>(); 
        foreach (var objectType in schema.ObjectTypes)
        {
            objectType.Attributes = objectType.Attributes.OrderBy(a => a.Name).ToList();
            var connectedSystemObjectType = new ConnectedSystemObjectType
            {
                Name = objectType.Name,
                Attributes = objectType.Attributes.Select(a => new ConnectedSystemObjectTypeAttribute
                {
                    Name = a.Name,
                    Description = a.Description,
                    AttributePlurality = a.AttributePlurality,
                    Type = a.Type,
                    ClassName = a.ClassName
                }).ToList()
            };

            // if there's an External Id attribute recommendation from the connector, use that. otherwise the user will have to pick one.
            var attribute = connectedSystemObjectType.Attributes.SingleOrDefault(a => objectType.RecommendedExternalIdAttribute != null && a.Name == objectType.RecommendedExternalIdAttribute.Name);
            if (attribute != null)
                attribute.IsExternalId = true;
            //else
            //   Log.Error($"A recommended External Id attribute '{objectType.RecommendedExternalIdAttribute.Name}' was not found in the objects list of attributes.");

            // if the connector supports it (requires it), take the secondary external id from the schema and mark the attribute as such
            if (connectedSystem.ConnectorDefinition.SupportsSecondaryExternalId && objectType.RecommendedSecondaryExternalIdAttribute != null)
            {
                var secondaryExternalIdAttribute = connectedSystemObjectType.Attributes.SingleOrDefault(a => a.Name == objectType.RecommendedSecondaryExternalIdAttribute.Name);
                if (secondaryExternalIdAttribute != null)
                    secondaryExternalIdAttribute.IsSecondaryExternalId = true;
                else
                    Log.Error($"Recommended Secondary External Id attribute '{objectType.RecommendedSecondaryExternalIdAttribute.Name}' was not found in the objects list of attributes!");
            }

            connectedSystem.ObjectTypes.Add(connectedSystemObjectType);
        }

        await UpdateConnectedSystemAsync(connectedSystem, initiatedBy, activity);

        // finish the activity
        await Application.Activities.CompleteActivityAsync(activity);
    }
    #endregion

    #region Connected System Hierarchy
    /// <summary>
    /// Causes the associated Connector to be instantiated and the hierarchy (partitions and containers) to be imported from the connected system.
    /// You will need update the ConnectedSystem after if happy with the changes, to persist them.
    /// </summary>
    /// <returns>Nothing, the ConnectedSystem passed in will be updated though with the new hierarchy.</returns>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task ImportConnectedSystemHierarchyAsync(ConnectedSystem connectedSystem, MetaverseObject? initiatedBy)
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
            partitions = await new LdapConnector().GetPartitionsAsync(connectedSystem.SettingValues, Log.Logger);
            if (partitions.Count == 0)
            {
                // todo: report to the user we attempted to retrieve partitions, but got none back
            }
        }
        else
        {
            throw new NotImplementedException("Support for that connector definition has not been implemented yet.");
        }

        // this point could potentially be a good point to check for data-loss if persisted and return a report object
        // that the user could decide whether or not to take action against, i.e. cancel or persist.

        connectedSystem.Partitions = new List<ConnectedSystemPartition>(); // super destructive at this point. this is for mvp only. this causes all user partition/OU selections to be lost!
        foreach (var partition in partitions)
        {
            connectedSystem.Partitions.Add(new ConnectedSystemPartition
            {
                Name = partition.Name,
                ExternalId = partition.Id,
                Containers = partition.Containers.Select(BuildConnectedSystemContainerTree).ToHashSet()
            });
        }

        // for now though, we will just persist and let the user select containers later
        // pass in this user-initiated activity, so that sub-operations can be associated with it, i.e. the partition persisting operation
        await UpdateConnectedSystemAsync(connectedSystem, initiatedBy, activity);

        // finish the activity
        await Application.Activities.CompleteActivityAsync(activity);
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
            TargetName = objectType.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = objectType.ConnectedSystemId
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
            TargetName = attribute.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemId = attribute.ConnectedSystemObjectType?.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        await Application.Repository.ConnectedSystems.UpdateAttributeAsync(attribute);

        await Application.Activities.CompleteActivityAsync(activity);
    }
    #endregion

    #region Connected System Objects
    /// <summary>
    /// Deletes a Connected System Object, and it's attribute values from a Connected System.
    /// Also prepares a Connected System Object Change for persistence with the activityRunProfileExecutionItem by the caller.  
    /// </summary>
    public async Task DeleteConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
    {
        await Application.Repository.ConnectedSystems.DeleteConnectedSystemObjectAsync(connectedSystemObject);
        
        // create a Change Object for this deletion
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemObject.ConnectedSystemId,
            ConnectedSystemObject = connectedSystemObject,
            ChangeType = ObjectChangeType.Delete,
            ChangeTime = DateTime.UtcNow,
            DeletedObjectType = connectedSystemObject.Type,
            DeletedObjectExternalIdAttributeValue = connectedSystemObject.ExternalIdAttributeValue,
            ActivityRunProfileExecutionItem = activityRunProfileExecutionItem
        };

        // the change object will be persisted with the activity run profile execution item further up the stack.
        // we just need to associate the change with the execution item.
        activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;
    }
    
    public async Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeStringAsync(connectedSystemId, connectedSystemObjectTypeId);
    }
    
    public async Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeIntAsync(connectedSystemId, connectedSystemObjectTypeId);
    }
    
    public async Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeGuidAsync(connectedSystemId, connectedSystemObjectTypeId);
    }
    
    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, id);
    }

    public async Task<PagedResultSet<ConnectedSystemObjectHeader>> GetConnectedSystemObjectHeadersAsync(int connectedSystemId, int page = 1, int pageSize = 20)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(connectedSystemId, page, pageSize);
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

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue);
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, int attributeValue)
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
    /// </summary>
    public async Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, List<ActivityRunProfileExecutionItem> activityRunProfileExecutionItems)
    {
        // add a change object to the relevant activity run profile execution item for each cso to be updated.
        // the change objects will be persisted later, further up the call stack, when the activity gets persisted.
        foreach (var cso in connectedSystemObjects)
        {
            var activityRunProfileExecutionItem = activityRunProfileExecutionItems.SingleOrDefault(q => q.ConnectedSystemObject != null && q.ConnectedSystemObject.Id == cso.Id) ?? 
                                                  throw new InvalidDataException($"Couldn't find an ActivityRunProfileExecutionItem referencing CSO {cso.Id}! It should have been created before now.");
            
            ProcessConnectedSystemObjectAttributeValueChanges(cso, activityRunProfileExecutionItem);
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
            ChangeType = ObjectChangeType.Create,
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
            ChangeType = ObjectChangeType.Update,
            ChangeTime = DateTime.UtcNow,
            ActivityRunProfileExecutionItem = activityRunProfileExecutionItem
        };

        // the change object will be persisted with the activity run profile execution item further up the stack.
        // we just need to associate the change with the detail item.
        // unsure if this is the right approach. should we persist the change here and just associate with the detail item?
        activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;

        // make sure the CSO is linked to the activity run profile execution item
        activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;

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
            TargetType = ActivityTargetType.SyncRule,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

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
            TargetType = ActivityTargetType.SyncRule,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

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

    public async Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, MetaverseObject? initiatedBy)
    {
        if (connectedSystemRunProfile == null)
            return;

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystemRunProfile.Name,
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

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
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
        
        
        // every crud operation must be tracked via an Activity
        var activity = new Activity
        {
            TargetName = syncRule.Name,
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

    public async Task DeleteSyncRuleAsync(SyncRule syncRule, MetaverseObject? initiatedBy)
    {
        // every crud operation must be tracked via an Activity
        var activity = new Activity
        {
            TargetName = syncRule.Name,
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
    /// Creates a new object matching rule for a Connected System Object Type.
    /// </summary>
    public async Task CreateObjectMatchingRuleAsync(ObjectMatchingRule rule, MetaverseObject? initiatedBy)
    {
        var activity = new Activity
        {
            TargetName = $"Rule for {rule.ConnectedSystemObjectType?.Name ?? "Object Type"}",
            TargetType = ActivityTargetType.ObjectMatchingRule,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
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
