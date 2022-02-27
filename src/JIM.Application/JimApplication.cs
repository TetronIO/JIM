using JIM.Application.Servers;
using JIM.Data;
using JIM.Models.Core;
using Serilog;

namespace JIM.Application
{
    public class JimApplication
    {
        public ConnectedSystemServer ConnectedSystems { get; }
        public MetaverseServer Metaverse { get; }
        public SecurityServer Security { get; }
        internal IRepository Repository { get; }

        public JimApplication(IRepository dataRepository)
        {
            ConnectedSystems = new ConnectedSystemServer(this);
            Metaverse = new MetaverseServer(this);
            Security = new SecurityServer(this);
            Repository = dataRepository;
            Log.Information("The JIM Application has started.");
            
        }

        /// <summary>
        /// Ensures that JIM is initialised, i.e. all seed data has been created.
        /// Only one client of JIM should call this; the first one.
        /// </summary>
        public async Task Initialise()
        {
            await Repository.SeedDatabaseAsync();
        }

        // stores SSO information in the database so the user can view it in the interface
        // also ensures there is always a user with the admin role assignment
        // todo: work out if there's any consideration needed wrt sequencing... api & web, or just one?
        public async Task InitialiseSSOAsync(string nameIdAttribute, string initialAdminNameIdValue)
        {
            Log.Information($"InitialiseSSOAsync: nameId: {nameIdAttribute}, initialAdminNameId: {initialAdminNameIdValue}");

            var nameIdMetaverseAttribute = Repository.Metaverse.GetMetaverseAttribute(nameIdAttribute);
            if (nameIdMetaverseAttribute == null)
                throw new Exception("Unsupported SSO NameID attribute. Please specify one that exists.");

            var serviceSettings = Repository.ServiceSettings.GetServiceSettings();
            if (serviceSettings.SSONameIDAttribute.Id != nameIdMetaverseAttribute.Id)
            {
                serviceSettings.SSONameIDAttribute = nameIdMetaverseAttribute;
                await Repository.ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
                Log.Verbose($"InitialiseSSOAsync: Updated ServiceSettings SSONameIDAttribute to: {nameIdAttribute}");
            }

            // check for a matching user, if not create, and check admin role assignment
            // get user by attribute = get metaverse object by attribute value
            var objectType = Metaverse.GetMetaverseObjectType(BuiltInObjectTypeNames.User.ToString());
            if (objectType == null)
                throw new Exception($"{BuiltInObjectTypeNames.User} object type could not be found. Something went wrong with db seeding.");

            var user = Repository.Metaverse.GetMetaverseObjectByTypeAndAttribute(objectType, nameIdMetaverseAttribute, initialAdminNameIdValue);
            if (user != null)
            {
                // we have a matching user, do they have the Administrators role?
                if (!Security.IsUserInRole(user, BuiltInRoleNames.Administrators.ToString()))
                    await Security.AddUserToRoleAsync(user, BuiltInRoleNames.Administrators.ToString());
            }
            else
            {
                // no matching user found, create them in stub form; just enough to sign-in and then token claim values will suppliment, i.e. display name
                user = new MetaverseObject { Id = Guid.NewGuid(), Type = objectType };
                user.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Id = Guid.NewGuid(),
                    MetaverseObject = user,
                    Attribute = nameIdMetaverseAttribute,
                    StringValue = initialAdminNameIdValue
                });

                await Metaverse.CreateMetaverseObjectAsync(user);
                await Security.AddUserToRoleAsync(user, BuiltInRoleNames.Administrators.ToString());
                Log.Verbose($"InitialiseSSOAsync: Created {initialAdminNameIdValue} metaverse object user with the {BuiltInRoleNames.Administrators} role.");
            }
        }
    }
}