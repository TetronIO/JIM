// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Security;
namespace JIM.Data.Repositories;

public interface ISecurityRepository
{
    public Task<List<Role>> GetRolesAsync();

    public Task<Role?> GetRoleAsync(string roleName);

    public Task<Role?> GetRoleByIdAsync(int roleId);

    public Task<bool> IsObjectInRoleAsync(Guid userId, string roleName);

    public Task<List<Role>> GetMetaverseObjectRolesAsync(Guid metaverseObjectId);

    public Task AddObjectToRoleAsync(Guid userId, string roleName);

    public Task AddObjectToRoleByIdAsync(Guid objectId, int roleId);

    public Task RemoveObjectFromRoleAsync(Guid objectId, int roleId);

    public Task<List<MetaverseObject>> GetRoleMembersAsync(int roleId);
}