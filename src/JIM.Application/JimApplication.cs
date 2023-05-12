using JIM.Application.Search;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Models.Core;
using Serilog;

namespace JIM.Application
{
    public class JimApplication
    {
        internal IRepository Repository { get; }
        internal SeedingServer Seeding { get; }
        public ConnectedSystemServer ConnectedSystems { get; }
        public DataGenerationServer DataGeneration { get; }
        public HistoryServer History { get; }
        public MetaverseServer Metaverse { get; }
        public SearchServer Search { get; }
        public SecurityServer Security { get; }
        public ServiceSettingsServer ServiceSettings { get; }
        public TaskingServer Tasking { get; }

        public JimApplication(IRepository dataRepository)
        {
            ConnectedSystems = new ConnectedSystemServer(this);
            DataGeneration = new DataGenerationServer(this);
            History = new HistoryServer(this);
            Metaverse = new MetaverseServer(this);
            Repository = dataRepository;
            Search = new SearchServer(this);
            Security = new SecurityServer(this);
            Seeding = new SeedingServer(this);
            ServiceSettings = new ServiceSettingsServer(this);
            Tasking = new TaskingServer(this);
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
            await Seeding.SeedAsync();
            await Repository.InitialisationCompleteAsync();
        }

        /// <summary>
        /// Stores SSO information in the database so the user can view it in the interface.
        /// Also ensures there is always a user with the admin role assignment.
        /// </summary>
        public async Task InitialiseSSOAsync(
            string ssoAuthority,
            string ssoClientId,
            string ssoSecret,
            string uniqueIdentifierClaimType, 
            string uniqueIdentifierMetaverseAttributeName, 
            string initialAdminUniqueIdentifierClaimValue)
        {
            Log.Debug($"InitialiseSSOAsync: uniqueIdentifierClaimType: {uniqueIdentifierClaimType}, initialAdminUniqueIdentifierClaimValue: {initialAdminUniqueIdentifierClaimValue}");

            var uniqueIdentifierMetaverseAttribute = await Repository.Metaverse.GetMetaverseAttributeAsync(uniqueIdentifierMetaverseAttributeName);
            if (uniqueIdentifierMetaverseAttribute == null)
                throw new Exception("Unsupported SSO unique identifier Metaverse Attribute Name. Please specify one that exists.");

            var serviceSettings = await ServiceSettings.GetServiceSettingsAsync();
            if (serviceSettings == null)
                throw new Exception("ServiceSettings do not exist. Application is not properly initialised. Are you sure the application is ready?");

            // SSO AUTHENTICATION PROPERTIES
            // The variables that enable authentication via SSO with our Identity Provider are mastered in the docker-compose configuration file.
            // we want to mirror these into the database via the ServiceSettings object to make life easy for administrators, so they don't have to access the hosting environment
            // to check the values and can do so through JIM Web.
            // We will need to update the database if the configuration file values change.
            if (string.IsNullOrEmpty(serviceSettings.SSOAuthority) || serviceSettings.SSOAuthority != ssoAuthority)
            {
                serviceSettings.SSOAuthority = ssoAuthority;
                await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
                Log.Information($"InitialiseSSOAsync: Updated ServiceSettings.SSOAuthority to: {ssoAuthority}");
            }

            if (string.IsNullOrEmpty(serviceSettings.SSOClientId) || serviceSettings.SSOClientId != ssoClientId)
            {
                serviceSettings.SSOClientId = ssoClientId;
                await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
                Log.Information($"InitialiseSSOAsync: Updated ServiceSettings.SSOClientId to: {ssoClientId}");
            }
            
            if (string.IsNullOrEmpty(serviceSettings.SSOSecret) || serviceSettings.SSOSecret != ssoSecret)
            {
                serviceSettings.SSOSecret = ssoSecret;
                await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);

                // don't print the secret to logs!
                Log.Information($"InitialiseSSOAsync: Updated ServiceSettings.SSOSecret");
            }

            // INBOUND CLAIM MAPPING:
            // We want to make it easy for IDP and JIM teams to enable SSO. We don't want them to have to add JIM-specific claims to the OIDC ID token if possible.
            // we want to allow an IDP team to setup the relying party for JIM using their standard integration approach, where possible.
            // this will provide the slickest integration experience.
            if (string.IsNullOrEmpty(serviceSettings.SSOUniqueIdentifierClaimType) || serviceSettings.SSOUniqueIdentifierClaimType != uniqueIdentifierClaimType)
            {
                serviceSettings.SSOUniqueIdentifierClaimType = uniqueIdentifierClaimType;
                Log.Information($"InitialiseSSOAsync: Updating ServiceSettings.SSOUniqueIdentifierClaimType to: {uniqueIdentifierClaimType}");
                await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
            }

            if (serviceSettings.SSOUniqueIdentifierMetaverseAttribute == null || serviceSettings.SSOUniqueIdentifierMetaverseAttribute.Id != uniqueIdentifierMetaverseAttribute.Id)
            {
                serviceSettings.SSOUniqueIdentifierMetaverseAttribute = uniqueIdentifierMetaverseAttribute;
                Log.Information($"InitialiseSSOAsync: Updating ServiceSettings.SSOUniqueIdentifierMetaverseAttribute to: {uniqueIdentifierMetaverseAttribute.Name}");
                await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
            }

            // check for a matching user, if not create, and check admin role assignment
            // get user by attribute = get metaverse object by attribute value
            var objectType = await Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.Users, false);
            if (objectType == null)
                throw new Exception($"{Constants.BuiltInObjectTypes.Users} object type could not be found. Something went wrong with db seeding.");

            var user = await Repository.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(objectType, uniqueIdentifierMetaverseAttribute, initialAdminUniqueIdentifierClaimValue);
            if (user != null)
            {
                // we have a matching user, do they have the Administrators role?
                if (!await Security.IsObjectInRoleAsync(user, Constants.BuiltInRoles.Administrators))
                    await Security.AddObjectToRoleAsync(user, Constants.BuiltInRoles.Administrators);
            }
            else
            {
                // no matching user found, create them in stub form; just enough to sign-in
                user = new MetaverseObject { Type = objectType };
                user.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    MetaverseObject = user,
                    Attribute = uniqueIdentifierMetaverseAttribute,
                    StringValue = initialAdminUniqueIdentifierClaimValue
                });

                var typeAttribute = await Repository.Metaverse.GetMetaverseAttributeAsync(Constants.BuiltInAttributes.Type);
                if (typeAttribute == null)
                    throw new Exception($"Couldn't get essential attribute: {Constants.BuiltInAttributes.Type}");

                user.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    MetaverseObject = user,
                    Attribute = typeAttribute,
                    StringValue = "Person"
                });

                Log.Information($"InitialiseSSOAsync: Creating metaverse object user ({initialAdminUniqueIdentifierClaimValue}) and assigning the {Constants.BuiltInRoles.Administrators} role.");
                await Metaverse.CreateMetaverseObjectAsync(user);
                await Security.AddObjectToRoleAsync(user, Constants.BuiltInRoles.Administrators);
            }
        }

        /// <summary>
        /// Indicates to secondary JimApplication clients (i.e. not the master) if the application is ready to be used.
        /// Do not progress with client initialisation if JimApplication is not ready.
        /// </summary>
        public async Task<bool> IsApplicationReadyAsync()
        {
            Log.Verbose("JIM.Application: IsApplicationReadyAsync()");
            var serviceSettings = await ServiceSettings.GetServiceSettingsAsync();
            if (serviceSettings == null || serviceSettings.IsServiceInMaintenanceMode)
                return false;

            return true;
        }
    }
}