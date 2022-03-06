using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Diagnostics;

namespace JIM.PostgresData
{
    public class PostgresDataRepository : IRepository, IDisposable
    {
        public IConnectedSystemRepository ConnectedSystems { get; }
        public IMetaverseRepository Metaverse { get; }
        public IServiceSettingsRepository ServiceSettings { get; }
        public ISecurityRepository Security { get; }
        public IDataGenerationRepository DataGeneration { get; }

        internal JimDbContext Database { get; }

        public PostgresDataRepository()
        {
            // needed to enable DateTime.Now assignments to work. Without it, the database will
            // throw errors when trying to set dates. This is a .NET/Postgres type mapping issue.
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            ConnectedSystems = new ConnectedSystemRepository(this);
            Metaverse = new MetaverseRepository(this);
            ServiceSettings = new ServiceSettingsRepository(this);
            Security = new SecurityRepository(this);
            DataGeneration = new DataGenerationRepository(this);
            Database = new JimDbContext();
        }

        public async Task InitialiseDatabaseAsync()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            await MigrateDatabaseAsync();
            await SeedDatabaseAsync();

            // when the database is created, it's done so in maintenance mode
            // now we're all done, take it out of maintenance mode to open the app up to requests
            var serviceSettings = ServiceSettings.GetServiceSettings();
            if (serviceSettings == null)
                throw new Exception("ServiceSettings is null. Something has gone wrong with seeding.");

            serviceSettings.IsServiceInMaintenanceMode = false;
            await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);

            stopwatch.Stop();
            Log.Verbose($"InitialiseDatabaseAsync: Completed in: {stopwatch.Elapsed}");
        }

        private async Task MigrateDatabaseAsync()
        {
            if (Database.Database.GetPendingMigrations().Any())
                await Database.Database.MigrateAsync();
        }

        private async Task SeedDatabaseAsync()
        {
            // seeding requirements:
            // - service settings
            // - metaverse attributes
            // - object types, with attributes
            // - roles

            if (!await Database.ServiceSettings.AnyAsync())
            {
                Database.ServiceSettings.Add(new ServiceSettings());
                Log.Information("SeedDatabaseAsync: Created ServiceSettings");
            }

            // HOW ARE WE GOING TO ENFORCE FIXED LIST VALUES?
            // This will be important even in the sync-delivery phase where we need to provide simple config values that map to directory attributes, i.e. grouptype/scope
            // Would also like a way to make it easy to configure userAccountControl using checkboxes or choose-flag-to-enable/disable helpers
            // Maybe attributes can register helpers that have UI and API aspects?

            // generic attributes
            var accountNameAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.AccountName, AttributePlurality.SingleValued, AttributeDataType.String);
            var descriptionAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Description, AttributePlurality.SingleValued, AttributeDataType.String);
            var displayNameAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.DisplayName, AttributePlurality.SingleValued, AttributeDataType.String);
            var distinguishedNameAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.DistinguishedName, AttributePlurality.SingleValued, AttributeDataType.String);
            var emailAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Email, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute1 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute1, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute10 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute10, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute11 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute11, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute12 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute12, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute13 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute13, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute14 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute14, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute15 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute15, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute2 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute2, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute3 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute3, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute4 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute4, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute5 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute5, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute6 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute6, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute7 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute7, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute8 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute8, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute9 = await SeedAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute9, AttributePlurality.SingleValued, AttributeDataType.String);
            var hideFromAddressListsAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.HideFromAddressLists, AttributePlurality.SingleValued, AttributeDataType.Bool);
            var infoAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Info, AttributePlurality.SingleValued, AttributeDataType.String);
            var mailNicknameAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.MailNickname, AttributePlurality.SingleValued, AttributeDataType.String);
            var objectGuidAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.ObjectGUID, AttributePlurality.SingleValued, AttributeDataType.Guid);
            var objectSidAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.ObjectSid, AttributePlurality.SingleValued, AttributeDataType.Binary);

            // user-specific attributes
            var accountExpiresAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.AccountExpires, AttributePlurality.SingleValued, AttributeDataType.DateTime);
            var altSecurityIdentitiesAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.AltSecurityIdentities, AttributePlurality.MultiValued, AttributeDataType.String);
            var commonNameAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.CommonName, AttributePlurality.SingleValued, AttributeDataType.String);
            var companyAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Company, AttributePlurality.SingleValued, AttributeDataType.String);
            var countryAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Country, AttributePlurality.SingleValued, AttributeDataType.String);
            var countryCodeAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.CountryCode, AttributePlurality.SingleValued, AttributeDataType.String);
            var departmentAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Department, AttributePlurality.SingleValued, AttributeDataType.String);
            var employeeIdAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.EmployeeID, AttributePlurality.SingleValued, AttributeDataType.String);
            var employeeTypeAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.EmployeeType, AttributePlurality.SingleValued, AttributeDataType.String);
            var facsimileTelephoneNumberAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.FacsimileTelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var firstNameAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.FirstName, AttributePlurality.SingleValued, AttributeDataType.String);
            var homeDirectoryAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.HomeDirectory, AttributePlurality.SingleValued, AttributeDataType.String);
            var homeDriveAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.HomeDrive, AttributePlurality.SingleValued, AttributeDataType.String);
            var homePhoneAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.HomePhone, AttributePlurality.SingleValued, AttributeDataType.String);
            var ipPhoneAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.IpPhone, AttributePlurality.SingleValued, AttributeDataType.String);
            var jobTitleAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.JobTitle, AttributePlurality.SingleValued, AttributeDataType.String);
            var lastNameAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.LastName, AttributePlurality.SingleValued, AttributeDataType.String);
            var localityAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Locality, AttributePlurality.SingleValued, AttributeDataType.String);
            var managerAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Manager, AttributePlurality.SingleValued, AttributeDataType.Reference);
            var mobileNumberAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.MobileNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var officeAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Office, AttributePlurality.SingleValued, AttributeDataType.String);
            var organisationAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Organisation, AttributePlurality.SingleValued, AttributeDataType.String);
            var otherFacsimileTelephoneNumbersAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.OtherFacsimileTelephoneNumbers, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherIpPhonesAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.OtherIpPhones, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherMobilesAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.OtherMobiles, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherPagersAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.OtherPagers, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherTelephonesAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.OtherTelephones, AttributePlurality.MultiValued, AttributeDataType.String);
            var pagerAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Pager, AttributePlurality.SingleValued, AttributeDataType.String);
            var physicalDeliveryOfficeNameAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.PhysicalDeliveryOfficeName, AttributePlurality.SingleValued, AttributeDataType.String);
            var postalAddressesAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.PostalAddresses, AttributePlurality.MultiValued, AttributeDataType.String);
            var postalCodeAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.PostalCode, AttributePlurality.SingleValued, AttributeDataType.String);
            var postOFficeBoxesAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.PostOfficeBoxes, AttributePlurality.MultiValued, AttributeDataType.String);
            var pronounsAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Pronouns, AttributePlurality.SingleValued, AttributeDataType.String);
            var proxyAddressesAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.ProxyAddresses, AttributePlurality.MultiValued, AttributeDataType.String);
            var scriptPathAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.ScriptPath, AttributePlurality.SingleValued, AttributeDataType.String);
            var sidHistoryAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.SidHistory, AttributePlurality.MultiValued, AttributeDataType.Binary);
            var stateOrProvinceAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.StateOrProvince, AttributePlurality.SingleValued, AttributeDataType.String);
            var statusAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Status, AttributePlurality.SingleValued, AttributeDataType.String);
            var streetAddressAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.StreetAddress, AttributePlurality.SingleValued, AttributeDataType.String);
            var teamAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Team, AttributePlurality.SingleValued, AttributeDataType.String);
            var telephoneNumberAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.TelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var urlsAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.Urls, AttributePlurality.MultiValued, AttributeDataType.String);
            var userAccountControlAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.UserAccountControl, AttributePlurality.SingleValued, AttributeDataType.Number);
            var userCertificatesAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.UserCertificates, AttributePlurality.MultiValued, AttributeDataType.Binary);
            var userPrincipalNameAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.UserPrincipalName, AttributePlurality.SingleValued, AttributeDataType.String);
            var userSharedFolderAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.UserSharedFolder, AttributePlurality.SingleValued, AttributeDataType.String);
            var webPageAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.WebPage, AttributePlurality.SingleValued, AttributeDataType.String);

            // group-specific attributes
            var groupScopeAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.GroupScope, AttributePlurality.SingleValued, AttributeDataType.String);
            var groupTypeAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.GroupType, AttributePlurality.SingleValued, AttributeDataType.String);
            var managedByAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.ManagedBy, AttributePlurality.SingleValued, AttributeDataType.Reference);
            var staticMembersAttribute = await SeedAttributeAsync(Constants.BuiltInAttributes.StaticMembers, AttributePlurality.MultiValued, AttributeDataType.Reference);

            // create the user object type and attribute mappings
            var userObjectType = await Database.MetaverseObjectTypes.SingleOrDefaultAsync(q => q.Name == Constants.BuiltInObjectTypes.User);
            if (userObjectType == null)
            {
                userObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User, BuiltIn = true };
                Database.MetaverseObjectTypes.Add(userObjectType);
                Log.Information("SeedDatabaseAsync: MetaverseObjectType User");
            }

            AddAttributeToObjectType(userObjectType, accountExpiresAttribute);
            AddAttributeToObjectType(userObjectType, accountNameAttribute);
            AddAttributeToObjectType(userObjectType, altSecurityIdentitiesAttribute);
            AddAttributeToObjectType(userObjectType, commonNameAttribute);
            AddAttributeToObjectType(userObjectType, companyAttribute);
            AddAttributeToObjectType(userObjectType, countryAttribute);
            AddAttributeToObjectType(userObjectType, countryCodeAttribute);
            AddAttributeToObjectType(userObjectType, departmentAttribute);
            AddAttributeToObjectType(userObjectType, descriptionAttribute);
            AddAttributeToObjectType(userObjectType, displayNameAttribute);
            AddAttributeToObjectType(userObjectType, distinguishedNameAttribute);
            AddAttributeToObjectType(userObjectType, emailAttribute);
            AddAttributeToObjectType(userObjectType, employeeIdAttribute);
            AddAttributeToObjectType(userObjectType, employeeTypeAttribute);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute1);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute10);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute11);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute12);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute13);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute14);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute15);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute2);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute3);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute4);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute5);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute6);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute7);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute8);
            AddAttributeToObjectType(userObjectType, extensionAttribute1Attribute9);
            AddAttributeToObjectType(userObjectType, facsimileTelephoneNumberAttribute);
            AddAttributeToObjectType(userObjectType, firstNameAttribute);
            AddAttributeToObjectType(userObjectType, hideFromAddressListsAttribute);
            AddAttributeToObjectType(userObjectType, homeDirectoryAttribute);
            AddAttributeToObjectType(userObjectType, homeDriveAttribute);
            AddAttributeToObjectType(userObjectType, homePhoneAttribute);
            AddAttributeToObjectType(userObjectType, infoAttribute);
            AddAttributeToObjectType(userObjectType, ipPhoneAttribute);
            AddAttributeToObjectType(userObjectType, jobTitleAttribute);
            AddAttributeToObjectType(userObjectType, lastNameAttribute);
            AddAttributeToObjectType(userObjectType, localityAttribute);
            AddAttributeToObjectType(userObjectType, mailNicknameAttribute);
            AddAttributeToObjectType(userObjectType, managerAttribute);
            AddAttributeToObjectType(userObjectType, mobileNumberAttribute);
            AddAttributeToObjectType(userObjectType, objectGuidAttribute);
            AddAttributeToObjectType(userObjectType, objectSidAttribute);
            AddAttributeToObjectType(userObjectType, officeAttribute);
            AddAttributeToObjectType(userObjectType, organisationAttribute);
            AddAttributeToObjectType(userObjectType, otherFacsimileTelephoneNumbersAttribute);
            AddAttributeToObjectType(userObjectType, otherIpPhonesAttribute);
            AddAttributeToObjectType(userObjectType, otherMobilesAttribute);
            AddAttributeToObjectType(userObjectType, otherPagersAttribute);
            AddAttributeToObjectType(userObjectType, otherTelephonesAttribute);
            AddAttributeToObjectType(userObjectType, pagerAttribute);
            AddAttributeToObjectType(userObjectType, physicalDeliveryOfficeNameAttribute);
            AddAttributeToObjectType(userObjectType, postalAddressesAttribute);
            AddAttributeToObjectType(userObjectType, postalCodeAttribute);
            AddAttributeToObjectType(userObjectType, postOFficeBoxesAttribute);
            AddAttributeToObjectType(userObjectType, pronounsAttribute);
            AddAttributeToObjectType(userObjectType, proxyAddressesAttribute);
            AddAttributeToObjectType(userObjectType, scriptPathAttribute);
            AddAttributeToObjectType(userObjectType, sidHistoryAttribute);
            AddAttributeToObjectType(userObjectType, stateOrProvinceAttribute);
            AddAttributeToObjectType(userObjectType, statusAttribute);
            AddAttributeToObjectType(userObjectType, streetAddressAttribute);
            AddAttributeToObjectType(userObjectType, teamAttribute);
            AddAttributeToObjectType(userObjectType, telephoneNumberAttribute);
            AddAttributeToObjectType(userObjectType, urlsAttribute);
            AddAttributeToObjectType(userObjectType, userAccountControlAttribute);
            AddAttributeToObjectType(userObjectType, userCertificatesAttribute);
            AddAttributeToObjectType(userObjectType, userPrincipalNameAttribute);
            AddAttributeToObjectType(userObjectType, userSharedFolderAttribute);
            AddAttributeToObjectType(userObjectType, webPageAttribute);

            // create the group object type and attribute mappings
            var groupObjectType = await Database.MetaverseObjectTypes.SingleOrDefaultAsync(q => q.Name == Constants.BuiltInObjectTypes.Group);
            if (groupObjectType == null)
            {
                groupObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.Group, BuiltIn = true };
                Database.MetaverseObjectTypes.Add(groupObjectType);
                Log.Information("SeedDatabaseAsync: MetaverseObjectType Group");
            }

            AddAttributeToObjectType(groupObjectType, accountNameAttribute);
            AddAttributeToObjectType(groupObjectType, descriptionAttribute);
            AddAttributeToObjectType(groupObjectType, displayNameAttribute);
            AddAttributeToObjectType(groupObjectType, distinguishedNameAttribute);
            AddAttributeToObjectType(groupObjectType, emailAttribute);
            AddAttributeToObjectType(groupObjectType, groupScopeAttribute);
            AddAttributeToObjectType(groupObjectType, groupTypeAttribute);
            AddAttributeToObjectType(groupObjectType, hideFromAddressListsAttribute);
            AddAttributeToObjectType(groupObjectType, infoAttribute);
            AddAttributeToObjectType(groupObjectType, mailNicknameAttribute);
            AddAttributeToObjectType(groupObjectType, managedByAttribute);
            AddAttributeToObjectType(groupObjectType, objectGuidAttribute);
            AddAttributeToObjectType(groupObjectType, objectSidAttribute);
            AddAttributeToObjectType(groupObjectType, proxyAddressesAttribute);
            AddAttributeToObjectType(groupObjectType, staticMembersAttribute);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute1);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute10);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute11);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute12);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute13);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute14);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute15);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute2);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute3);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute4);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute5);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute6);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute7);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute8);
            AddAttributeToObjectType(groupObjectType, extensionAttribute1Attribute9);

            // create the built-in roles
            var administratorsRole = await Database.Roles.SingleOrDefaultAsync(q => q.Name == Constants.BuiltInRoles.Administrators);
            if (administratorsRole == null)
            {
                Database.Roles.Add(new Role
                {
                    BuiltIn = true,
                    Name = Constants.BuiltInRoles.Administrators
                });
                Log.Information($"SeedDatabaseAsync: Role: {Constants.BuiltInRoles.Administrators}");
            }

            await Database.SaveChangesAsync();
        }

        private async Task<MetaverseAttribute> SeedAttributeAsync(string name, AttributePlurality attributePlurality, AttributeDataType attributeDataType)
        {
            var attribute = await Database.MetaverseAttributes.SingleOrDefaultAsync(q => q.Name == name);
            if (attribute == null)
            {
                attribute = new MetaverseAttribute
                {
                    Name = name,
                    AttributePlurality = attributePlurality,
                    Type = attributeDataType,
                    BuiltIn = true
                };
                Database.MetaverseAttributes.Add(attribute);
                Log.Verbose($"SeedAttributeAsync: Created {name}");
            }
            return attribute;
        }

        private static void AddAttributeToObjectType(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute)
        {
            if (!metaverseObjectType.Attributes.Any(q => q.Name == metaverseAttribute.Name))
            {
                metaverseObjectType.Attributes.Add(metaverseAttribute);
                Log.Verbose($"AddAttributeToObjectType: {metaverseObjectType.Name} - Added {metaverseAttribute.Name}");
            }
        }

        public void Dispose()
        {
            if (Database != null)
                Database.Dispose();
        }
    }
}
