using JIM.Data;
using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Diagnostics;

namespace JIM.PostgresData
{
    public class PostgresDataRepository : IRepository
    {
        public IConnectedSystemRepository ConnectedSystems { get; }
        public IMetaverseRepository Metaverse { get; }
        public IServiceSettingsRepository ServiceSettings { get; }
        public ISecurityRepository Security { get; }

        public PostgresDataRepository()
        {
            // needed to enable DateTime.Now assignments to work. Without it, the database will
            // throw errors when trying to set dates. This is a .NET/Postgres type mapping issue.
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            ConnectedSystems = new ConnectedSystemRepository(this);
            Metaverse = new MetaverseRepository(this);
            ServiceSettings = new ServiceSettingsRepository(this);
            Security = new SecurityRepository(this);
        }

        public async Task InitialiseDatabaseAsync()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            using var db = new JimDbContext();
            await MigrateDatabaseAsync(db);
            await SeedDatabaseAsync(db);

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

        private static async Task MigrateDatabaseAsync(JimDbContext databaseContext)
        {
            if (databaseContext.Database.GetPendingMigrations().Any())
                await databaseContext.Database.MigrateAsync();
        }

        private static async Task SeedDatabaseAsync(JimDbContext databaseContext)
        {
            // seeding requirements:
            // - service settings
            // - metaverse attributes
            // - object types, with attributes

            if (!databaseContext.ServiceSettings.Any())
            {
                await databaseContext.ServiceSettings.AddAsync(new ServiceSettings());
                Log.Information("SeedDatabaseAsync: Created ServiceSettings");
            }

            // HOW ARE WE GOING TO ENFORCE FIXED LIST VALUES?
            // This will be important even in the sync-delivery phase where we need to provide simple config values that map to directory attributes, i.e. grouptype/scope
            // Would also like a way to make it easy to configure userAccountControl using checkboxes or choose-flag-to-enable/disable helpers
            // Maybe attributes can register helpers that have UI and API aspects?

            // generic attributes
            var accountNameAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.AccountName, AttributePlurality.SingleValued, AttributeDataType.String);
            var descriptionAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Description, AttributePlurality.SingleValued, AttributeDataType.String);
            var displayNameAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.DisplayName, AttributePlurality.SingleValued, AttributeDataType.String);
            var distinguishedNameAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.DistinguishedName, AttributePlurality.SingleValued, AttributeDataType.String);
            var emailAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Email, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute1 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute1, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute10 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute10, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute11 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute11, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute12 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute12, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute13 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute13, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute14 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute14, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute15 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute15, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute2 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute2, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute3 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute3, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute4 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute4, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute5 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute5, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute6 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute6, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute7 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute7, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute8 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute8, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute9 = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute9, AttributePlurality.SingleValued, AttributeDataType.String);
            var hideFromAddressListsAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.HideFromAddressLists, AttributePlurality.SingleValued, AttributeDataType.Bool);
            var infoAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Info, AttributePlurality.SingleValued, AttributeDataType.String);
            var mailNicknameAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.MailNickname, AttributePlurality.SingleValued, AttributeDataType.String);
            var objectGuidAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ObjectGUID, AttributePlurality.SingleValued, AttributeDataType.Guid);
            var objectSidAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ObjectSid, AttributePlurality.SingleValued, AttributeDataType.Binary);

            // user-specific attributes
            var accountExpiresAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.AccountExpires, AttributePlurality.SingleValued, AttributeDataType.DateTime);
            var altSecurityIdentitiesAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.AltSecurityIdentities, AttributePlurality.MultiValued, AttributeDataType.String);
            var commonNameAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.CommonName, AttributePlurality.SingleValued, AttributeDataType.String);
            var companyAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Company, AttributePlurality.SingleValued, AttributeDataType.String);
            var countryAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Country, AttributePlurality.SingleValued, AttributeDataType.String);
            var countryCodeAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.CountryCode, AttributePlurality.SingleValued, AttributeDataType.String);
            var departmentAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Department, AttributePlurality.SingleValued, AttributeDataType.String);
            var employeeIdAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.EmployeeID, AttributePlurality.SingleValued, AttributeDataType.String);
            var employeeTypeAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.EmployeeType, AttributePlurality.SingleValued, AttributeDataType.String);
            var facsimileTelephoneNumberAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.FacsimileTelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var firstNameAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.FirstName, AttributePlurality.SingleValued, AttributeDataType.String);
            var homeDirectoryAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.HomeDirectory, AttributePlurality.SingleValued, AttributeDataType.String);
            var homeDriveAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.HomeDrive, AttributePlurality.SingleValued, AttributeDataType.String);
            var homePhoneAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.HomePhone, AttributePlurality.SingleValued, AttributeDataType.String);
            var ipPhoneAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.IpPhone, AttributePlurality.SingleValued, AttributeDataType.String);
            var jobTitleAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.JobTitle, AttributePlurality.SingleValued, AttributeDataType.String);
            var lastNameAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.LastName, AttributePlurality.SingleValued, AttributeDataType.String);
            var localityAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Locality, AttributePlurality.SingleValued, AttributeDataType.String);
            var managerAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Manager, AttributePlurality.SingleValued, AttributeDataType.Reference);
            var mobileNumberAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.MobileNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var officeAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Office, AttributePlurality.SingleValued, AttributeDataType.String);
            var organisationAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Organisation, AttributePlurality.SingleValued, AttributeDataType.String);
            var otherFacsimileTelephoneNumbersAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.OtherFacsimileTelephoneNumbers, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherIpPhonesAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.OtherIpPhones, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherMobilesAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.OtherMobiles, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherPagersAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.OtherPagers, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherTelephonesAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.OtherTelephones, AttributePlurality.MultiValued, AttributeDataType.String);
            var pagerAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Pager, AttributePlurality.SingleValued, AttributeDataType.String);
            var physicalDeliveryOfficeNameAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.PhysicalDeliveryOfficeName, AttributePlurality.SingleValued, AttributeDataType.String);
            var postalAddressesAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.PostalAddresses, AttributePlurality.MultiValued, AttributeDataType.String);
            var postalCodeAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.PostalCode, AttributePlurality.SingleValued, AttributeDataType.String);
            var postOFficeBoxesAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.PostOfficeBoxes, AttributePlurality.MultiValued, AttributeDataType.String);
            var pronounsAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Pronouns, AttributePlurality.SingleValued, AttributeDataType.String);
            var proxyAddressesAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ProxyAddresses, AttributePlurality.MultiValued, AttributeDataType.String);
            var scriptPathAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ScriptPath, AttributePlurality.SingleValued, AttributeDataType.String);
            var sidHistoryAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.SidHistory, AttributePlurality.MultiValued, AttributeDataType.Binary);
            var stateOrProvinceAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.StateOrProvince, AttributePlurality.SingleValued, AttributeDataType.String);
            var statusAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Status, AttributePlurality.SingleValued, AttributeDataType.String);
            var streetAddressAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.StreetAddress, AttributePlurality.SingleValued, AttributeDataType.String);
            var teamAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Team, AttributePlurality.SingleValued, AttributeDataType.String);
            var telephoneNumberAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.TelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var urlsAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.Urls, AttributePlurality.MultiValued, AttributeDataType.String);
            var userAccountControlAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.UserAccountControl, AttributePlurality.SingleValued, AttributeDataType.Number);
            var userCertificatesAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.UserCertificates, AttributePlurality.MultiValued, AttributeDataType.Binary);
            var userPrincipalNameAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.UserPrincipalName, AttributePlurality.SingleValued, AttributeDataType.String);
            var userSharedFolderAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.UserSharedFolder, AttributePlurality.SingleValued, AttributeDataType.String);
            var webPageAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.WebPage, AttributePlurality.SingleValued, AttributeDataType.String);

            // group-specific attributes
            var groupScopeAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.GroupScope, AttributePlurality.SingleValued, AttributeDataType.String);
            var groupTypeAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.GroupType, AttributePlurality.SingleValued, AttributeDataType.String);
            var managedByAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.ManagedBy, AttributePlurality.SingleValued, AttributeDataType.Reference);
            var staticMembersAttribute = await SeedAttributeAsync(databaseContext, Constants.BuiltInAttributes.StaticMembers, AttributePlurality.MultiValued, AttributeDataType.Reference);

            // create the user object type and attribute mappings
            var userObjectType = databaseContext.MetaverseObjectTypes.SingleOrDefault(q => q.Name == Constants.BuiltInObjectTypes.User);
            if (userObjectType == null)
            {
                userObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User, BuiltIn = true };
                await databaseContext.MetaverseObjectTypes.AddAsync(userObjectType);
                Log.Information("SeedDatabaseAsync: MetaverseObjectType User");
            }

            AddAttributeToObjectType(userObjectType, accountExpiresAttribute);
            AddAttributeToObjectType(userObjectType, accountNameAttribute);
            AddAttributeToObjectType(userObjectType, altSecurityIdentitiesAttribute);
            AddAttributeToObjectType(userObjectType, commonNameAttribute);
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
            var groupObjectType = databaseContext.MetaverseObjectTypes.SingleOrDefault(q => q.Name == Constants.BuiltInObjectTypes.Group);
            if (groupObjectType == null)
            {
                groupObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.Group, BuiltIn = true };
                await databaseContext.MetaverseObjectTypes.AddAsync(groupObjectType);
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

            await databaseContext.SaveChangesAsync();
        }

        private static async Task<MetaverseAttribute> SeedAttributeAsync(JimDbContext jimDbContext, string name, AttributePlurality attributePlurality, AttributeDataType attributeDataType)
        {
            var attribute = jimDbContext.MetaverseAttributes.SingleOrDefault(q => q.Name == name);
            if (attribute == null)
            {
                attribute = new MetaverseAttribute
                {
                    Name = name,
                    AttributePlurality = attributePlurality,
                    Type = attributeDataType,
                    BuiltIn = true
                };
                await jimDbContext.MetaverseAttributes.AddAsync(attribute);
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
    }
}
