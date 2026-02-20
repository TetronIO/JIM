using JIM.Models.Core;
using JIM.Models.Security;
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

    public async Task<Role?> GetRoleAsync(string roleName)
    {
        return await Application.Repository.Security.GetRoleAsync(roleName);
    }

    public async Task<List<Role>> GetMetaverseObjectRolesAsync(MetaverseObject metaverseObject)
    {
        return await Application.Repository.Security.GetMetaverseObjectRolesAsync(metaverseObject.Id);
    }

    public async Task<bool> IsObjectInRoleAsync(MetaverseObject metaverseObject, string roleName)
    { 
        return await Application.Repository.Security.IsObjectInRoleAsync(metaverseObject.Id, roleName);
    }

    public async Task AddObjectToRoleAsync(MetaverseObject metaverseObject, string roleName)
    {
        await Application.Repository.Security.AddObjectToRoleAsync(metaverseObject.Id, roleName);
    }
}