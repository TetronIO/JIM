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

    public async Task<List<MetaverseObject>> GetRoleMembersAsync(int roleId)
    {
        return await Application.Repository.Security.GetRoleMembersAsync(roleId);
    }

    #region Roles

    /// <summary>
    /// Creates a new Role definition. Used by seeding today (<see cref="SeedingServer.SeedBuiltInRolesAsync"/>);
    /// admin-editable Role creation arrives with #612 and will reuse this method.
    /// </summary>
    public async Task<Role> CreateRoleAsync(Role role, MetaverseObject? initiatedBy = null, string? changeReason = null, Guid? parentActivityId = null)
    {
        return await CreateRoleCoreAsync(role, changeReason, parentActivityId,
            activity => CreateRoleActivityAsync(activity, initiatedBy),
            r => SetRoleCreated(r, initiatedBy));
    }

    /// <summary>
    /// Creates a new Role definition. API-key initiator overload.
    /// </summary>
    public async Task<Role> CreateRoleAsync(Role role, ApiKey initiatedByApiKey, string? changeReason = null, Guid? parentActivityId = null)
    {
        return await CreateRoleCoreAsync(role, changeReason, parentActivityId,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey),
            r => AuditHelper.SetCreated(r, initiatedByApiKey));
    }

    private async Task<Role> CreateRoleCoreAsync(Role role, string? changeReason, Guid? parentActivityId,
        Func<Activity, Task> createActivityAsync, Action<Role> setCreated)
    {
        var activity = new Activity
        {
            TargetName = role.Name,
            TargetType = ActivityTargetType.Role,
            TargetOperationType = ActivityTargetOperationType.Create,
            Message = $"Creating Role '{role.Name}'",
            ParentActivityId = parentActivityId
        };
        await createActivityAsync(activity);

        try
        {
            setCreated(role);
            Log.Information("Creating Role '{Name}'", LogSanitiser.Sanitise(role.Name));
            var result = await Application.Repository.Security.CreateRoleAsync(role);

            await CaptureRoleConfigurationChangeAsync(activity, result.Id, changeReason);
            activity.Message = $"Created Role '{role.Name}'";
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
    /// Adds a Metaverse Object as a static member of a Role, identified by the Role's name. The auditable question
    /// for a membership change is "who was in this Role and when", so the Activity targets the Role, not the member.
    /// </summary>
    public async Task AddObjectToRoleAsync(MetaverseObject metaverseObject, string roleName, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        await AddObjectToRoleCoreAsync(metaverseObject, roleName, changeReason,
            activity => CreateRoleActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Adds a Metaverse Object as a static member of a Role, identified by the Role's name. API-key initiator overload.
    /// </summary>
    public async Task AddObjectToRoleAsync(MetaverseObject metaverseObject, string roleName, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        await AddObjectToRoleCoreAsync(metaverseObject, roleName, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task AddObjectToRoleCoreAsync(MetaverseObject metaverseObject, string roleName, string? changeReason,
        Func<Activity, Task> createActivityAsync)
    {
        var memberName = metaverseObject.DisplayName ?? $"Unknown (ID: {metaverseObject.Id})";

        var activity = new Activity
        {
            TargetName = roleName,
            TargetType = ActivityTargetType.Role,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Adding '{memberName}' to Role '{roleName}'"
        };
        await createActivityAsync(activity);

        try
        {
            Log.Information("Adding '{MemberName}' to Role '{RoleName}'", LogSanitiser.Sanitise(memberName), LogSanitiser.Sanitise(roleName));
            await Application.Repository.Security.AddObjectToRoleAsync(metaverseObject.Id, roleName);

            // The membership mutator is keyed by name, but configuration-change capture is int-keyed, so the Role
            // must be resolved by name after the change to recover its id.
            var role = await Application.Repository.Security.GetRoleAsync(roleName);
            if (role != null)
                await CaptureRoleConfigurationChangeAsync(activity, role.Id, changeReason);

            activity.Message = $"Added '{memberName}' to Role '{roleName}'";
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Adds a Metaverse Object as a static member of a Role, identified by the Role's id.
    /// </summary>
    public async Task AddObjectToRoleByIdAsync(Guid objectId, int roleId, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        await AddObjectToRoleByIdCoreAsync(objectId, roleId, changeReason,
            activity => CreateRoleActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Adds a Metaverse Object as a static member of a Role, identified by the Role's id. API-key initiator overload.
    /// </summary>
    public async Task AddObjectToRoleByIdAsync(Guid objectId, int roleId, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        await AddObjectToRoleByIdCoreAsync(objectId, roleId, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task AddObjectToRoleByIdCoreAsync(Guid objectId, int roleId, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        var roleName = await ResolveRoleNameAsync(roleId);
        var memberName = await ResolveMemberNameAsync(objectId);

        var activity = new Activity
        {
            TargetName = roleName,
            TargetType = ActivityTargetType.Role,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Adding '{memberName}' to Role '{roleName}'"
        };
        await createActivityAsync(activity);

        try
        {
            Log.Information("Adding '{MemberName}' to Role '{RoleName}' (ID: {RoleId})", LogSanitiser.Sanitise(memberName), LogSanitiser.Sanitise(roleName), roleId);
            await Application.Repository.Security.AddObjectToRoleByIdAsync(objectId, roleId);

            await CaptureRoleConfigurationChangeAsync(activity, roleId, changeReason);
            activity.Message = $"Added '{memberName}' to Role '{roleName}'";
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Removes a Metaverse Object as a static member of a Role.
    /// </summary>
    public async Task RemoveObjectFromRoleAsync(Guid objectId, int roleId, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        await RemoveObjectFromRoleCoreAsync(objectId, roleId, changeReason,
            activity => CreateRoleActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Removes a Metaverse Object as a static member of a Role. API-key initiator overload.
    /// </summary>
    public async Task RemoveObjectFromRoleAsync(Guid objectId, int roleId, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        await RemoveObjectFromRoleCoreAsync(objectId, roleId, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task RemoveObjectFromRoleCoreAsync(Guid objectId, int roleId, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        var roleName = await ResolveRoleNameAsync(roleId);
        var memberName = await ResolveMemberNameAsync(objectId);

        var activity = new Activity
        {
            TargetName = roleName,
            TargetType = ActivityTargetType.Role,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Removing '{memberName}' from Role '{roleName}'"
        };
        await createActivityAsync(activity);

        try
        {
            Log.Information("Removing '{MemberName}' from Role '{RoleName}' (ID: {RoleId})", LogSanitiser.Sanitise(memberName), LogSanitiser.Sanitise(roleName), roleId);
            await Application.Repository.Security.RemoveObjectFromRoleAsync(objectId, roleId);

            await CaptureRoleConfigurationChangeAsync(activity, roleId, changeReason);
            activity.Message = $"Removed '{memberName}' from Role '{roleName}'";
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Resolves a Role's name for use in an Activity's target/message, falling back to a placeholder when the Role
    /// cannot be found (the subsequent repository mutation call still surfaces the real failure via its own
    /// exception, which fails the Activity).
    /// </summary>
    private async Task<string> ResolveRoleNameAsync(int roleId)
    {
        var role = await Application.Repository.Security.GetRoleByIdAsync(roleId);
        return role?.Name ?? $"Unknown (ID: {roleId})";
    }

    /// <summary>
    /// Resolves a Metaverse Object's display name for use in a Role membership Activity's message, falling back to a
    /// placeholder when the object cannot be found. See <see cref="ResolveRoleNameAsync"/> for why a fallback (not a
    /// thrown exception) is used here.
    /// </summary>
    private async Task<string> ResolveMemberNameAsync(Guid objectId)
    {
        var member = await Application.Metaverse.GetMetaverseObjectAsync(objectId);
        return member?.DisplayName ?? $"Unknown (ID: {objectId})";
    }

    /// <summary>
    /// Creates the audit Activity for a Role change attributed to <paramref name="initiatedBy"/>, or to the System
    /// principal when no user is supplied. Role creation happens exclusively through seeding today, and a membership
    /// change can originate from the AuthServer initial-admin bootstrap/retention path, neither of which has a
    /// distinct initiator to attribute the change to; the same rationale as <c>CreateApiKeyActivityAsync</c> below.
    /// </summary>
    private Task CreateRoleActivityAsync(Activity activity, MetaverseObject? initiatedBy) =>
        initiatedBy == null
            ? Application.Activities.CreateSystemActivityAsync(activity)
            : Application.Activities.CreateActivityAsync(activity, initiatedBy);

    /// <summary>
    /// Stamps Created audit fields for a Role, falling back to system attribution when no user is supplied. See
    /// <see cref="CreateRoleActivityAsync"/> for why the fallback is needed here.
    /// </summary>
    private static void SetRoleCreated(Role role, MetaverseObject? initiatedBy)
    {
        if (initiatedBy == null)
            AuditHelper.SetCreatedBySystem(role);
        else
            AuditHelper.SetCreated(role, initiatedBy);
    }

    /// <summary>
    /// Captures a versioned, metadata-only configuration snapshot of a Role (its definition and static membership)
    /// onto its audit Activity via the shared ConfigurationChangeCaptureService. The Role is reloaded so the snapshot
    /// reflects persisted truth, including current membership; call it after the change has been persisted. Shared
    /// by Role definition creation and Role membership changes, since both are configuration changes to the same
    /// Role.
    /// </summary>
    private async Task CaptureRoleConfigurationChangeAsync(Activity activity, int roleId, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.Role, roleId,
            async hashKey =>
            {
                var persisted = await Application.Repository.Security.GetRoleByIdAsync(roleId);
                return persisted == null ? null : Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"Role {roleId}");
    }

    #endregion

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