// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Models.Security.DTOs;
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
    public async Task<ApiKey> CreateApiKeyAsync(ApiKey apiKey)
    {
        return await Application.Repository.ApiKeys.CreateAsync(apiKey);
    }

    /// <summary>
    /// Updates an existing API key.
    /// </summary>
    public async Task<ApiKey> UpdateApiKeyAsync(ApiKey apiKey)
    {
        return await Application.Repository.ApiKeys.UpdateAsync(apiKey);
    }

    /// <summary>
    /// Deletes an API key.
    /// </summary>
    public async Task DeleteApiKeyAsync(Guid id)
    {
        await Application.Repository.ApiKeys.DeleteAsync(id);
    }

    /// <summary>
    /// Records usage of an API key (updates last used timestamp and IP).
    /// </summary>
    public async Task RecordApiKeyUsageAsync(Guid id, string? ipAddress)
    {
        await Application.Repository.ApiKeys.RecordUsageAsync(id, ipAddress);
    }

    #endregion
}