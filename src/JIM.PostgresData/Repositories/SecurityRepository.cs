// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Models.Security.DTOs;
using Microsoft.EntityFrameworkCore;
namespace JIM.PostgresData.Repositories;

public class SecurityRepository : ISecurityRepository
{
    private PostgresDataRepository Repository { get; }

    internal SecurityRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    public async Task<List<Role>> GetRolesAsync()
    {
        return await Repository.Database.Roles.OrderBy(q => q.Name).ToListAsync();
    }

    public async Task<List<RoleHeader>> GetRoleHeadersAsync()
    {
        return await Repository.Database.Roles
            .OrderBy(r => r.Name)
            .Select(r => new RoleHeader
            {
                Id = r.Id,
                Name = r.Name,
                BuiltIn = r.BuiltIn,
                Created = r.Created,
                StaticMemberCount = r.StaticMembers.Count
            })
            .ToListAsync();
    }

    public async Task<Role?> GetRoleAsync(string roleName)
    {
        return await Repository.Database.Roles.SingleOrDefaultAsync(q => q.Name == roleName);
    }

    public async Task<Role?> GetRoleByIdAsync(int roleId)
    {
        return await Repository.Database.Roles
            .Include(q => q.StaticMembers)
            .SingleOrDefaultAsync(q => q.Id == roleId);
    }

    public async Task<List<Role>> GetMetaverseObjectRolesAsync(Guid metaverseObjectId)
    {
        return await Repository.Database.Roles
            .Include(q => q.StaticMembers)
            .Where(q => q.StaticMembers.Any(sm => sm.Id == metaverseObjectId))
            .ToListAsync();
    }

    public async Task<bool> IsObjectInRoleAsync(Guid userId, string roleName)
    {
        return await Repository.Database.Roles.AnyAsync(q => q.Name == roleName && q.StaticMembers.Any(sm => sm.Id == userId));
    }

    public async Task AddObjectToRoleAsync(Guid objectId, string roleName)
    {
        var dbRole = await Repository.Database.Roles.Include(role => role.StaticMembers).AsTracking().SingleOrDefaultAsync(r => r.Name == roleName);
        if (dbRole == null)
            throw new ArgumentException($"No such role found: {roleName}");

        var dbObject = await Repository.Database.MetaverseObjects.AsTracking().SingleOrDefaultAsync(mo => mo.Id == objectId);
        if (dbObject == null)
            throw new ArgumentException($"No such object found: {objectId}");

        if (dbRole.StaticMembers.Any(sm => sm.Id == dbObject.Id))
            throw new ArgumentException($"Object is already in that role");

        dbRole.StaticMembers.Add(dbObject);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task AddObjectToRoleByIdAsync(Guid objectId, int roleId)
    {
        var dbRole = await Repository.Database.Roles.Include(role => role.StaticMembers).AsTracking().SingleOrDefaultAsync(r => r.Id == roleId);
        if (dbRole == null)
            throw new ArgumentException($"No such role found: {roleId}");

        var dbObject = await Repository.Database.MetaverseObjects.AsTracking().SingleOrDefaultAsync(mo => mo.Id == objectId);
        if (dbObject == null)
            throw new ArgumentException($"No such object found: {objectId}");

        if (dbRole.StaticMembers.Any(sm => sm.Id == dbObject.Id))
            throw new ArgumentException($"Object is already in that role");

        dbRole.StaticMembers.Add(dbObject);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task RemoveObjectFromRoleAsync(Guid objectId, int roleId)
    {
        var dbRole = await Repository.Database.Roles.Include(role => role.StaticMembers).AsTracking().SingleOrDefaultAsync(r => r.Id == roleId);
        if (dbRole == null)
            throw new ArgumentException($"No such role found: {roleId}");

        var member = dbRole.StaticMembers.SingleOrDefault(sm => sm.Id == objectId);
        if (member == null)
            throw new ArgumentException($"Object is not a member of this role");

        dbRole.StaticMembers.Remove(member);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task<List<MetaverseObject>> GetRoleMembersAsync(int roleId)
    {
        return await Repository.Database.MetaverseObjects
            .Include(mo => mo.Type)
            .Where(mo => mo.Roles.Any(r => r.Id == roleId))
            .OrderBy(mo => mo.CachedDisplayName)
            .ToListAsync();
    }
}
