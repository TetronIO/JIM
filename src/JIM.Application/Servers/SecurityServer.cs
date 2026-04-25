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
}