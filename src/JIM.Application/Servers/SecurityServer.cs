// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Utilities;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Models.Security.DTOs;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

public class SecurityServer
{
    private JimApplication Application { get; }

    internal SecurityServer(JimApplication application)
    {
        Application = application;
    }

    public async Task<List<Role>> GetRolesAsync()
    {
        return await Application.Repository.Security.GetRolesAsync();
    }

    public async Task<List<RoleHeader>> GetRoleHeadersAsync()
    {
        return await Application.Repository.Security.GetRoleHeadersAsync();
    }

    public async Task<Role?> GetRoleAsync(string roleName)
    {
        return await Application.Repository.Security.GetRoleAsync(roleName);
    }

    public async Task<Role?> GetRoleByIdAsync(int roleId)
    {
        return await Application.Repository.Security.GetRoleByIdAsync(roleId);
    }

    public async Task<List<Role>> GetMetaverseObjectRolesAsync(MetaverseObject metaverseObject)
    {
        return await Application.Repository.Security.GetMetaverseObjectRolesAsync(metaverseObject.Id);
    }

    public async Task<List<Role>> GetMetaverseObjectRolesAsync(Guid metaverseObjectId)
    {
        return await Application.Repository.Security.GetMetaverseObjectRolesAsync(metaverseObjectId);
    }

    public async Task<bool> IsObjectInRoleAsync(MetaverseObject metaverseObject, string roleName)
    {
        return await Application.Repository.Security.IsObjectInRoleAsync(metaverseObject.Id, roleName);
    }

    public async Task AddObjectToRoleAsync(MetaverseObject metaverseObject, string roleName)
    {
        await Application.Repository.Security.AddObjectToRoleAsync(metaverseObject.Id, roleName);
    }

    public async Task AddObjectToRoleByIdAsync(Guid objectId, int roleId)
    {
        await Application.Repository.Security.AddObjectToRoleByIdAsync(objectId, roleId);
    }

    public async Task RemoveObjectFromRoleAsync(Guid objectId, int roleId)
    {
        await Application.Repository.Security.RemoveObjectFromRoleAsync(objectId, roleId);
    }

    public async Task<List<MetaverseObject>> GetRoleMembersAsync(int roleId)
    {
        return await Application.Repository.Security.GetRoleMembersAsync(roleId);
    }

    #region API Keys

    /// <summary>
    /// Gets all API keys.
    /// </summary>
    public async Task<List<ApiKey>> GetApiKeysAsync()
    {
        return await Application.Repository.ApiKeys.GetAllAsync();
    }

    /// <summary>
    /// Gets an API key by its ID.
    /// </summary>
    public async Task<ApiKey?> GetApiKeyAsync(Guid id)
    {
        return await Application.Repository.ApiKeys.GetByIdAsync(id);
    }

    /// <summary>
    /// Gets an API key by its hash. Used for authentication.
    /// </summary>
    public async Task<ApiKey?> GetApiKeyByHashAsync(string keyHash)
    {
        return await Application.Repository.ApiKeys.GetByHashAsync(keyHash);
    }

    /// <summary>
    /// Creates a new API key.
    /// </summary>
    public async Task<ApiKey> CreateApiKeyAsync(ApiKey apiKey, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        return await CreateApiKeyCoreAsync(apiKey, changeReason,
            activity => CreateApiKeyActivityAsync(activity, initiatedBy),
            key => SetApiKeyCreated(key, initiatedBy));
    }

    /// <summary>
    /// Creates a new API key. API-key initiator overload.
    /// </summary>
    public async Task<ApiKey> CreateApiKeyAsync(ApiKey apiKey, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        return await CreateApiKeyCoreAsync(apiKey, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey),
            key => AuditHelper.SetCreated(key, initiatedByApiKey));
    }

    private async Task<ApiKey> CreateApiKeyCoreAsync(ApiKey apiKey, string? changeReason,
        Func<Activity, Task> createActivityAsync, Action<ApiKey> setCreated)
    {
        var activity = new Activity
        {
            TargetName = apiKey.Name,
            TargetType = ActivityTargetType.ApiKey,
            TargetOperationType = ActivityTargetOperationType.Create,
            Message = $"Creating API key '{apiKey.Name}'"
        };
        await createActivityAsync(activity);

        try
        {
            setCreated(apiKey);
            Log.Information("Creating API key '{Name}' (ID: {Id})", LogSanitiser.Sanitise(apiKey.Name), apiKey.Id);
            var result = await Application.Repository.ApiKeys.CreateAsync(apiKey);

            await CaptureConfigurationChangeAsync(activity, result.Id, changeReason);
            activity.Message = $"Created API key '{apiKey.Name}'";
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
    /// Updates an existing API key.
    /// </summary>
    public async Task<ApiKey> UpdateApiKeyAsync(ApiKey apiKey, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        return await UpdateApiKeyCoreAsync(apiKey, changeReason,
            activity => CreateApiKeyActivityAsync(activity, initiatedBy),
            key => SetApiKeyUpdated(key, initiatedBy));
    }

    /// <summary>
    /// Updates an existing API key. API-key initiator overload.
    /// </summary>
    public async Task<ApiKey> UpdateApiKeyAsync(ApiKey apiKey, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        return await UpdateApiKeyCoreAsync(apiKey, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey),
            key => AuditHelper.SetUpdated(key, initiatedByApiKey));
    }

    private async Task<ApiKey> UpdateApiKeyCoreAsync(ApiKey apiKey, string? changeReason,
        Func<Activity, Task> createActivityAsync, Action<ApiKey> setUpdated)
    {
        var activity = new Activity
        {
            TargetName = apiKey.Name,
            TargetType = ActivityTargetType.ApiKey,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Updating API key '{apiKey.Name}'"
        };
        await createActivityAsync(activity);

        try
        {
            setUpdated(apiKey);
            Log.Information("Updating API key '{Name}' (ID: {Id})", LogSanitiser.Sanitise(apiKey.Name), apiKey.Id);
            var result = await Application.Repository.ApiKeys.UpdateAsync(apiKey);

            await CaptureConfigurationChangeAsync(activity, result.Id, changeReason);
            activity.Message = $"Updated API key '{apiKey.Name}'";
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
    /// Deletes an API key.
    /// </summary>
    public async Task DeleteApiKeyAsync(Guid id, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        await DeleteApiKeyCoreAsync(id, changeReason,
            activity => CreateApiKeyActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Deletes an API key. API-key initiator overload.
    /// </summary>
    public async Task DeleteApiKeyAsync(Guid id, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        await DeleteApiKeyCoreAsync(id, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task DeleteApiKeyCoreAsync(Guid id, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        var apiKey = await Application.Repository.ApiKeys.GetByIdAsync(id);
        var apiKeyName = apiKey?.Name ?? $"Unknown (ID: {id})";

        var activity = new Activity
        {
            TargetName = apiKeyName,
            TargetType = ActivityTargetType.ApiKey,
            TargetOperationType = ActivityTargetOperationType.Delete,
            Message = $"Deleting API key '{apiKeyName}'"
        };
        await createActivityAsync(activity);

        try
        {
            if (apiKey != null)
            {
                Log.Information("Deleting API key '{Name}' (ID: {Id})", LogSanitiser.Sanitise(apiKey.Name), id);
                await CaptureConfigurationDeletionAsync(activity, apiKey, changeReason);
            }

            await Application.Repository.ApiKeys.DeleteAsync(id);

            activity.Message = $"Deleted API key '{apiKeyName}'";
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Records usage of an API key (updates last used timestamp and IP). Deliberately records no Activity and
    /// captures no configuration snapshot: usage tracking is operational state, not a configuration change.
    /// </summary>
    public async Task RecordApiKeyUsageAsync(Guid id, string? ipAddress)
    {
        await Application.Repository.ApiKeys.RecordUsageAsync(id, ipAddress);
    }

    /// <summary>
    /// Creates the audit Activity for an API Key change attributed to <paramref name="initiatedBy"/>, or to the
    /// System principal when no user is supplied. Unlike the Trusted Certificate and other Phase 4 configuration
    /// types, API Keys are genuinely created with no principal available in one supported path (the JIM.Web
    /// bootstrap creates the infrastructure key before any session exists), so the null case must fall back to a
    /// system-attributed Activity rather than attempt (and fail) to attribute it to an absent user.
    /// </summary>
    private Task CreateApiKeyActivityAsync(Activity activity, MetaverseObject? initiatedBy) =>
        initiatedBy == null
            ? Application.Activities.CreateSystemActivityAsync(activity)
            : Application.Activities.CreateActivityAsync(activity, initiatedBy);

    /// <summary>
    /// Stamps Created audit fields for an API Key, falling back to system attribution when no user is supplied. See
    /// <see cref="CreateApiKeyActivityAsync"/> for why the fallback is needed here.
    /// </summary>
    private static void SetApiKeyCreated(ApiKey apiKey, MetaverseObject? initiatedBy)
    {
        if (initiatedBy == null)
            AuditHelper.SetCreatedBySystem(apiKey);
        else
            AuditHelper.SetCreated(apiKey, initiatedBy);
    }

    /// <summary>
    /// Stamps LastUpdated audit fields for an API Key, falling back to system attribution when no user is supplied.
    /// See <see cref="CreateApiKeyActivityAsync"/> for why the fallback is needed here.
    /// </summary>
    private static void SetApiKeyUpdated(ApiKey apiKey, MetaverseObject? initiatedBy)
    {
        if (initiatedBy == null)
            AuditHelper.SetUpdatedBySystem(apiKey);
        else
            AuditHelper.SetUpdated(apiKey, initiatedBy);
    }

    /// <summary>
    /// Captures a versioned, metadata-only configuration snapshot of an API Key onto its audit Activity via the
    /// shared ConfigurationChangeCaptureService (which owns the toggle, dedupe-guard, versioning and best-effort
    /// behaviours). The key is reloaded so the snapshot reflects persisted truth, including its Role assignments;
    /// call it after the change has been persisted.
    /// </summary>
    private async Task CaptureConfigurationChangeAsync(Activity activity, Guid apiKeyId, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.ApiKey, apiKeyId,
            async hashKey =>
            {
                var persisted = await Application.Repository.ApiKeys.GetByIdAsync(apiKeyId);
                return persisted == null ? null : Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"API Key {apiKeyId}");
    }

    /// <summary>
    /// Captures a tombstone snapshot of an API Key onto its delete Activity, before the key is removed. Matching the
    /// other configuration types' deletion behaviour, this does not set <see cref="Activity.ApiKeyId"/> or a
    /// version: the key is deleted before the Activity completes, so the Activity is left unlinked and the snapshot
    /// is surfaced via the Activity itself rather than the object's history.
    /// </summary>
    private async Task CaptureConfigurationDeletionAsync(Activity activity, ApiKey apiKey, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureDeletionAsync(activity, changeReason,
            hashKey => Task.FromResult<ConfigurationSnapshot?>(Application.ConfigurationSnapshots.CreateSnapshot(apiKey, hashKey)),
            $"API Key {apiKey.Id}");
    }

    #endregion
}