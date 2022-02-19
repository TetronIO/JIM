using Serilog;
using TIM.Application.Servers;
using TIM.Data;
using TIM.Models.Core;

namespace TIM.Application
{
    public class TimApplication
    {
        public ConnectedSystemServer ConnectedSystems { get; }
        public MetaverseServer Metaverse { get; }
        internal IRepository Repository { get; }

        public TimApplication(IRepository dataRepository)
        {
            ConnectedSystems = new ConnectedSystemServer(this);
            Metaverse = new MetaverseServer(this);
            Repository = dataRepository;
            Log.Information("The TIM Application has started.");
        }

        // stores SSO information in the database so the user can view it in the interface
        // also ensures there is always a user with the admin role assignment
        public async Task InitialiseSSOAsync(string nameIdAttribute, string initialAdminNameId)
        {
            Log.Information($"InitialiseSSOAsync: nameId: {nameIdAttribute} initialAdminNameId: {initialAdminNameId}");

            var metaverseAttribute = Repository.Metaverse.GetMetaverseAttribute(nameIdAttribute);
            if (metaverseAttribute == null)
                throw new Exception("Unsupported SSO NameID attribute. Please specify one that exists.");

            var serviceSettings = Repository.ServiceSettings.GetServiceSettings();
            if (serviceSettings.SSONameIDAttribute.Id != metaverseAttribute.Id)
            {
                serviceSettings.SSONameIDAttribute = metaverseAttribute;
                await Repository.ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
                Log.Verbose($"InitialiseSSOAsync: Updated ServiceSettings SSONameIDAttribute to {nameIdAttribute}");
            }

            // check for a matching user, if not create, and check admin role assignment
            // get user by attribute = get metaverse object by attribute value

            var objectType = Repository.Metaverse.GetMetaverseObjectType(BuiltInObjectTypeNames.User.ToString());
            if (objectType == null)
                throw new Exception($"{BuiltInObjectTypeNames.User} object type could not be found. Something went wrong with db seeding.");

            var user = Repository.Metaverse.GetMetaverseObjectByTypeAndAttribute(objectType, metaverseAttribute, initialAdminNameId);
            if (user != null)
            {
                // we have a matching user, do they have the Administrators role?
                if (!user.Roles.Exists(r => r.Name == BuiltInRoleNames.Administrators.ToString()))
                {
                    // user does not have the role, add it and update the user
                    var role = Repository.Security.GetRole(BuiltInRoleNames.Administrators.ToString());
                    if (role == null)
                        throw new Exception($"{BuiltInRoleNames.Administrators} built-in role could not be found. Something went wrong with db seeding.");

                    user.Roles.Add(role);
                    await Repository.Metaverse.UpdateMetaverseObjectAsync(user);
                    Log.Verbose($"InitialiseSSOAsync: Added {BuiltInRoleNames.Administrators} role to {initialAdminNameId}.");
                }
                else
                {
                    Log.Verbose($"InitialiseSSOAsync: {initialAdminNameId} already had the {BuiltInRoleNames.Administrators} role.");
                }
            }
            else
            {
                // no matching user found, create them.
                var role = Repository.Security.GetRole(BuiltInRoleNames.Administrators.ToString());
                if (role == null)
                    throw new Exception($"{BuiltInRoleNames.Administrators} built-in role could not be found. Something went wrong with db seeding.");

                user = new MetaverseObject(objectType);
                user.AttributeValues.Add(new MetaverseObjectAttributeValue(user, metaverseAttribute) { StringValue = initialAdminNameId });
                user.Roles.Add(role);
                await Repository.Metaverse.CreateMetaverseObjectAsync(user);
                Log.Verbose($"InitialiseSSOAsync: Created {initialAdminNameId} metaverse object user with the {BuiltInRoleNames.Administrators} role.");
            }
        }
    }
}