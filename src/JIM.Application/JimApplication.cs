using JIM.Application.Servers;
using JIM.Data;
using JIM.Models.Core;
using Serilog;

namespace JIM.Application
{
    public class JimApplication : IDisposable
    {
        public ConnectedSystemServer ConnectedSystems { get; }
        public MetaverseServer Metaverse { get; }
        public SecurityServer Security { get; }
        public ServiceSettingsServer ServiceSettings { get; }
        internal IRepository Repository { get; }

        public JimApplication(IRepository dataRepository)
        {
            ConnectedSystems = new ConnectedSystemServer(this);
            Metaverse = new MetaverseServer(this);
            Security = new SecurityServer(this);
            ServiceSettings = new ServiceSettingsServer(this);
            Repository = dataRepository;
            Log.Verbose("The JIM Application has started.");
        }

        /// <summary>
        /// Ensures that JIM is fully deployed and seeded, i.e. database migrations have been performed
        /// and data needed to run the service has been created.
        /// Only the primary JIM application instance should run this task on startup. Secondary app instances
        /// must not run it, or conflicts are likely to occur. 
        /// </summary>
        public async Task InitialiseDatabaseAsync()
        {
            await Repository.InitialiseDatabaseAsync();
        }

        /// <summary>
        /// Stores SSO information in the database so the user can view it in the interface.
        /// Also ensures there is always a user with the admin role assignment.
        /// </summary>
        public async Task InitialiseSSOAsync(string nameIdAttribute, string initialAdminNameIdValue)
        {
            Log.Information($"InitialiseSSOAsync: nameId: {nameIdAttribute}, initialAdminNameId: {initialAdminNameIdValue}");

            var nameIdMetaverseAttribute = Repository.Metaverse.GetMetaverseAttribute(nameIdAttribute);
            if (nameIdMetaverseAttribute == null)
                throw new Exception("Unsupported SSO NameID attribute. Please specify one that exists.");

            var serviceSettings = ServiceSettings.GetServiceSettings();
            if (serviceSettings == null)
                throw new Exception("ServiceSettings do not exist. Application is not properly initialised. Are you sure the application is ready?");

            if (serviceSettings.SSONameIDAttribute == null || serviceSettings.SSONameIDAttribute.Id != nameIdMetaverseAttribute.Id)
            {
                serviceSettings.SSONameIDAttribute = nameIdMetaverseAttribute;
                await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
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
                if (!Security.IsObjectInRole(user, Constants.BuiltInRoles.Administrators))
                    await Security.AddObjectToRole(user, Constants.BuiltInRoles.Administrators);
            }
            else
            {
                // no matching user found, create them in stub form; just enough to sign-in and then token claim values will suppliment, i.e. display name
                user = new MetaverseObject { Type = objectType };
                user.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    MetaverseObject = user,
                    Attribute = nameIdMetaverseAttribute,
                    StringValue = initialAdminNameIdValue
                });

                await Metaverse.CreateMetaverseObjectAsync(user);
                await Security.AddObjectToRole(user, Constants.BuiltInRoles.Administrators);
                Log.Verbose($"InitialiseSSOAsync: Created {initialAdminNameIdValue} metaverse object user with the {Constants.BuiltInRoles.Administrators} role.");
            }
        }

        /// <summary>
        /// Indicates to secondary JimApplication clients (i.e. not the master) if the application is ready to be used.
        /// Do not progress with client initialisation if JimApplication is not ready.
        /// </summary>
        public bool IsApplicationReady()
        {
            var serviceSettings = ServiceSettings.GetServiceSettings();
            if (serviceSettings == null || serviceSettings.IsServiceInMaintenanceMode)
                return false;

            return true;
        }

        public void Dispose()
        {
            if (Repository != null)
                Repository.Dispose();
        }
    }
}