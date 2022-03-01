using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Security;
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
            // - roles

            if (!databaseContext.ServiceSettings.Any())
            {
                databaseContext.ServiceSettings.Add(new ServiceSettings());
                Log.Information("SeedDatabaseAsync: Created ServiceSettings");
            }

            // HOW ARE WE GOING TO ENFORCE FIXED LIST VALUES?
            // This will be important even in the sync-delivery phase where we need to provide simple config values that map to directory attributes, i.e. grouptype/scope
            // Would also like a way to make it easy to configure userAccountControl using checkboxes or choose-flag-to-enable/disable helpers
            // Maybe attributes can register helpers that have UI and API aspects?

            // generic attributes
            var accountNameAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.AccountName, AttributePlurality.SingleValued, AttributeDataType.String);
            var descriptionAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Description, AttributePlurality.SingleValued, AttributeDataType.String);
            var displayNameAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.DisplayName, AttributePlurality.SingleValued, AttributeDataType.String);
            var distinguishedNameAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.DistinguishedName, AttributePlurality.SingleValued, AttributeDataType.String);
            var emailAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Email, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute1 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute1, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute10 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute10, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute11 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute11, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute12 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute12, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute13 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute13, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute14 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute14, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute15 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute15, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute2 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute2, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute3 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute3, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute4 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute4, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute5 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute5, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute6 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute6, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute7 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute7, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute8 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute8, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute9 = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ExtensionAttribute9, AttributePlurality.SingleValued, AttributeDataType.String);
            var hideFromAddressListsAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.HideFromAddressLists, AttributePlurality.SingleValued, AttributeDataType.Bool);
            var infoAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Info, AttributePlurality.SingleValued, AttributeDataType.String);
            var mailNicknameAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.MailNickname, AttributePlurality.SingleValued, AttributeDataType.String);
            var objectGuidAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ObjectGUID, AttributePlurality.SingleValued, AttributeDataType.Guid);
            var objectSidAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ObjectSid, AttributePlurality.SingleValued, AttributeDataType.Binary);

            // user-specific attributes
            var accountExpiresAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.AccountExpires, AttributePlurality.SingleValued, AttributeDataType.DateTime);
            var altSecurityIdentitiesAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.AltSecurityIdentities, AttributePlurality.MultiValued, AttributeDataType.String);
            var commonNameAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.CommonName, AttributePlurality.SingleValued, AttributeDataType.String);
            var companyAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Company, AttributePlurality.SingleValued, AttributeDataType.String);
            var countryAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Country, AttributePlurality.SingleValued, AttributeDataType.String);
            var countryCodeAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.CountryCode, AttributePlurality.SingleValued, AttributeDataType.String);
            var departmentAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Department, AttributePlurality.SingleValued, AttributeDataType.String);
            var employeeIdAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.EmployeeID, AttributePlurality.SingleValued, AttributeDataType.String);
            var employeeTypeAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.EmployeeType, AttributePlurality.SingleValued, AttributeDataType.String);
            var facsimileTelephoneNumberAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.FacsimileTelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var firstNameAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.FirstName, AttributePlurality.SingleValued, AttributeDataType.String);
            var homeDirectoryAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.HomeDirectory, AttributePlurality.SingleValued, AttributeDataType.String);
            var homeDriveAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.HomeDrive, AttributePlurality.SingleValued, AttributeDataType.String);
            var homePhoneAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.HomePhone, AttributePlurality.SingleValued, AttributeDataType.String);
            var ipPhoneAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.IpPhone, AttributePlurality.SingleValued, AttributeDataType.String);
            var jobTitleAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.JobTitle, AttributePlurality.SingleValued, AttributeDataType.String);
            var lastNameAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.LastName, AttributePlurality.SingleValued, AttributeDataType.String);
            var localityAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Locality, AttributePlurality.SingleValued, AttributeDataType.String);
            var managerAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Manager, AttributePlurality.SingleValued, AttributeDataType.Reference);
            var mobileNumberAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.MobileNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var officeAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Office, AttributePlurality.SingleValued, AttributeDataType.String);
            var organisationAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Organisation, AttributePlurality.SingleValued, AttributeDataType.String);
            var otherFacsimileTelephoneNumbersAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.OtherFacsimileTelephoneNumbers, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherIpPhonesAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.OtherIpPhones, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherMobilesAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.OtherMobiles, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherPagersAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.OtherPagers, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherTelephonesAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.OtherTelephones, AttributePlurality.MultiValued, AttributeDataType.String);
            var pagerAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Pager, AttributePlurality.SingleValued, AttributeDataType.String);
            var physicalDeliveryOfficeNameAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.PhysicalDeliveryOfficeName, AttributePlurality.SingleValued, AttributeDataType.String);
            var postalAddressesAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.PostalAddresses, AttributePlurality.MultiValued, AttributeDataType.String);
            var postalCodeAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.PostalCode, AttributePlurality.SingleValued, AttributeDataType.String);
            var postOFficeBoxesAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.PostOfficeBoxes, AttributePlurality.MultiValued, AttributeDataType.String);
            var pronounsAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Pronouns, AttributePlurality.SingleValued, AttributeDataType.String);
            var proxyAddressesAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ProxyAddresses, AttributePlurality.MultiValued, AttributeDataType.String);
            var scriptPathAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ScriptPath, AttributePlurality.SingleValued, AttributeDataType.String);
            var sidHistoryAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.SidHistory, AttributePlurality.MultiValued, AttributeDataType.Binary);
            var stateOrProvinceAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.StateOrProvince, AttributePlurality.SingleValued, AttributeDataType.String);
            var statusAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Status, AttributePlurality.SingleValued, AttributeDataType.String);
            var streetAddressAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.StreetAddress, AttributePlurality.SingleValued, AttributeDataType.String);
            var teamAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Team, AttributePlurality.SingleValued, AttributeDataType.String);
            var telephoneNumberAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.TelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var urlsAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.Urls, AttributePlurality.MultiValued, AttributeDataType.String);
            var userAccountControlAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.UserAccountControl, AttributePlurality.SingleValued, AttributeDataType.Number);
            var userCertificatesAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.UserCertificates, AttributePlurality.MultiValued, AttributeDataType.Binary);
            var userPrincipalNameAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.UserPrincipalName, AttributePlurality.SingleValued, AttributeDataType.String);
            var userSharedFolderAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.UserSharedFolder, AttributePlurality.SingleValued, AttributeDataType.String);
            var webPageAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.WebPage, AttributePlurality.SingleValued, AttributeDataType.String);

            // group-specific attributes
            var groupScopeAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.GroupScope, AttributePlurality.SingleValued, AttributeDataType.String);
            var groupTypeAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.GroupType, AttributePlurality.SingleValued, AttributeDataType.String);
            var managedByAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.ManagedBy, AttributePlurality.SingleValued, AttributeDataType.Reference);
            var staticMembersAttribute = SeedAttribute(databaseContext, Constants.BuiltInAttributes.StaticMembers, AttributePlurality.MultiValued, AttributeDataType.Reference);

            // create the user object type and attribute mappings
            var userObjectType = databaseContext.MetaverseObjectTypes.SingleOrDefault(q => q.Name == Constants.BuiltInObjectTypes.User);
            if (userObjectType == null)
            {
                userObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User, BuiltIn = true };
                databaseContext.MetaverseObjectTypes.Add(userObjectType);
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
            var groupObjectType = databaseContext.MetaverseObjectTypes.SingleOrDefault(q => q.Name == Constants.BuiltInObjectTypes.Group);
            if (groupObjectType == null)
            {
                groupObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.Group, BuiltIn = true };
                databaseContext.MetaverseObjectTypes.Add(groupObjectType);
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
            databaseContext.Roles.Add(new Role { 
                BuiltIn = true,
                Name = "Administrators"
            });

            await databaseContext.SaveChangesAsync();
        }

        private static MetaverseAttribute SeedAttribute(JimDbContext jimDbContext, string name, AttributePlurality attributePlurality, AttributeDataType attributeDataType)
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
                jimDbContext.MetaverseAttributes.Add(attribute);
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
