using JIM.Data;
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

            if (!Database.ServiceSettings.Any())
            {
                Database.ServiceSettings.Add(new ServiceSettings());
                Log.Information("SeedDatabaseAsync: Created ServiceSettings");
            }

            // HOW ARE WE GOING TO ENFORCE FIXED LIST VALUES?
            // This will be important even in the sync-delivery phase where we need to provide simple config values that map to directory attributes, i.e. grouptype/scope
            // Would also like a way to make it easy to configure userAccountControl using checkboxes or choose-flag-to-enable/disable helpers
            // Maybe attributes can register helpers that have UI and API aspects?

            // generic attributes
            var accountNameAttribute = SeedAttribute(Constants.BuiltInAttributes.AccountName, AttributePlurality.SingleValued, AttributeDataType.String);
            var descriptionAttribute = SeedAttribute(Constants.BuiltInAttributes.Description, AttributePlurality.SingleValued, AttributeDataType.String);
            var displayNameAttribute = SeedAttribute(Constants.BuiltInAttributes.DisplayName, AttributePlurality.SingleValued, AttributeDataType.String);
            var distinguishedNameAttribute = SeedAttribute(Constants.BuiltInAttributes.DistinguishedName, AttributePlurality.SingleValued, AttributeDataType.String);
            var emailAttribute = SeedAttribute(Constants.BuiltInAttributes.Email, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute1 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute1, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute10 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute10, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute11 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute11, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute12 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute12, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute13 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute13, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute14 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute14, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute15 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute15, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute2 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute2, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute3 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute3, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute4 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute4, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute5 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute5, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute6 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute6, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute7 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute7, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute8 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute8, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute9 = SeedAttribute(Constants.BuiltInAttributes.ExtensionAttribute9, AttributePlurality.SingleValued, AttributeDataType.String);
            var hideFromAddressListsAttribute = SeedAttribute(Constants.BuiltInAttributes.HideFromAddressLists, AttributePlurality.SingleValued, AttributeDataType.Bool);
            var infoAttribute = SeedAttribute(Constants.BuiltInAttributes.Info, AttributePlurality.SingleValued, AttributeDataType.String);
            var mailNicknameAttribute = SeedAttribute(Constants.BuiltInAttributes.MailNickname, AttributePlurality.SingleValued, AttributeDataType.String);
            var objectGuidAttribute = SeedAttribute(Constants.BuiltInAttributes.ObjectGUID, AttributePlurality.SingleValued, AttributeDataType.Guid);
            var objectSidAttribute = SeedAttribute(Constants.BuiltInAttributes.ObjectSid, AttributePlurality.SingleValued, AttributeDataType.Binary);

            // user-specific attributes
            var accountExpiresAttribute = SeedAttribute(Constants.BuiltInAttributes.AccountExpires, AttributePlurality.SingleValued, AttributeDataType.DateTime);
            var altSecurityIdentitiesAttribute = SeedAttribute(Constants.BuiltInAttributes.AltSecurityIdentities, AttributePlurality.MultiValued, AttributeDataType.String);
            var commonNameAttribute = SeedAttribute(Constants.BuiltInAttributes.CommonName, AttributePlurality.SingleValued, AttributeDataType.String);
            var companyAttribute = SeedAttribute(Constants.BuiltInAttributes.Company, AttributePlurality.SingleValued, AttributeDataType.String);
            var countryAttribute = SeedAttribute(Constants.BuiltInAttributes.Country, AttributePlurality.SingleValued, AttributeDataType.String);
            var countryCodeAttribute = SeedAttribute(Constants.BuiltInAttributes.CountryCode, AttributePlurality.SingleValued, AttributeDataType.String);
            var departmentAttribute = SeedAttribute(Constants.BuiltInAttributes.Department, AttributePlurality.SingleValued, AttributeDataType.String);
            var employeeIdAttribute = SeedAttribute(Constants.BuiltInAttributes.EmployeeID, AttributePlurality.SingleValued, AttributeDataType.String);
            var employeeTypeAttribute = SeedAttribute(Constants.BuiltInAttributes.EmployeeType, AttributePlurality.SingleValued, AttributeDataType.String);
            var facsimileTelephoneNumberAttribute = SeedAttribute(Constants.BuiltInAttributes.FacsimileTelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var firstNameAttribute = SeedAttribute(Constants.BuiltInAttributes.FirstName, AttributePlurality.SingleValued, AttributeDataType.String);
            var homeDirectoryAttribute = SeedAttribute(Constants.BuiltInAttributes.HomeDirectory, AttributePlurality.SingleValued, AttributeDataType.String);
            var homeDriveAttribute = SeedAttribute(Constants.BuiltInAttributes.HomeDrive, AttributePlurality.SingleValued, AttributeDataType.String);
            var homePhoneAttribute = SeedAttribute(Constants.BuiltInAttributes.HomePhone, AttributePlurality.SingleValued, AttributeDataType.String);
            var ipPhoneAttribute = SeedAttribute(Constants.BuiltInAttributes.IpPhone, AttributePlurality.SingleValued, AttributeDataType.String);
            var jobTitleAttribute = SeedAttribute(Constants.BuiltInAttributes.JobTitle, AttributePlurality.SingleValued, AttributeDataType.String);
            var lastNameAttribute = SeedAttribute(Constants.BuiltInAttributes.LastName, AttributePlurality.SingleValued, AttributeDataType.String);
            var localityAttribute = SeedAttribute(Constants.BuiltInAttributes.Locality, AttributePlurality.SingleValued, AttributeDataType.String);
            var managerAttribute = SeedAttribute(Constants.BuiltInAttributes.Manager, AttributePlurality.SingleValued, AttributeDataType.Reference);
            var mobileNumberAttribute = SeedAttribute(Constants.BuiltInAttributes.MobileNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var officeAttribute = SeedAttribute(Constants.BuiltInAttributes.Office, AttributePlurality.SingleValued, AttributeDataType.String);
            var organisationAttribute = SeedAttribute(Constants.BuiltInAttributes.Organisation, AttributePlurality.SingleValued, AttributeDataType.String);
            var otherFacsimileTelephoneNumbersAttribute = SeedAttribute(Constants.BuiltInAttributes.OtherFacsimileTelephoneNumbers, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherIpPhonesAttribute = SeedAttribute(Constants.BuiltInAttributes.OtherIpPhones, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherMobilesAttribute = SeedAttribute(Constants.BuiltInAttributes.OtherMobiles, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherPagersAttribute = SeedAttribute(Constants.BuiltInAttributes.OtherPagers, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherTelephonesAttribute = SeedAttribute(Constants.BuiltInAttributes.OtherTelephones, AttributePlurality.MultiValued, AttributeDataType.String);
            var pagerAttribute = SeedAttribute(Constants.BuiltInAttributes.Pager, AttributePlurality.SingleValued, AttributeDataType.String);
            var physicalDeliveryOfficeNameAttribute = SeedAttribute(Constants.BuiltInAttributes.PhysicalDeliveryOfficeName, AttributePlurality.SingleValued, AttributeDataType.String);
            var postalAddressesAttribute = SeedAttribute(Constants.BuiltInAttributes.PostalAddresses, AttributePlurality.MultiValued, AttributeDataType.String);
            var postalCodeAttribute = SeedAttribute(Constants.BuiltInAttributes.PostalCode, AttributePlurality.SingleValued, AttributeDataType.String);
            var postOFficeBoxesAttribute = SeedAttribute(Constants.BuiltInAttributes.PostOfficeBoxes, AttributePlurality.MultiValued, AttributeDataType.String);
            var pronounsAttribute = SeedAttribute(Constants.BuiltInAttributes.Pronouns, AttributePlurality.SingleValued, AttributeDataType.String);
            var proxyAddressesAttribute = SeedAttribute(Constants.BuiltInAttributes.ProxyAddresses, AttributePlurality.MultiValued, AttributeDataType.String);
            var scriptPathAttribute = SeedAttribute(Constants.BuiltInAttributes.ScriptPath, AttributePlurality.SingleValued, AttributeDataType.String);
            var sidHistoryAttribute = SeedAttribute(Constants.BuiltInAttributes.SidHistory, AttributePlurality.MultiValued, AttributeDataType.Binary);
            var stateOrProvinceAttribute = SeedAttribute(Constants.BuiltInAttributes.StateOrProvince, AttributePlurality.SingleValued, AttributeDataType.String);
            var statusAttribute = SeedAttribute(Constants.BuiltInAttributes.Status, AttributePlurality.SingleValued, AttributeDataType.String);
            var streetAddressAttribute = SeedAttribute(Constants.BuiltInAttributes.StreetAddress, AttributePlurality.SingleValued, AttributeDataType.String);
            var teamAttribute = SeedAttribute(Constants.BuiltInAttributes.Team, AttributePlurality.SingleValued, AttributeDataType.String);
            var telephoneNumberAttribute = SeedAttribute(Constants.BuiltInAttributes.TelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var urlsAttribute = SeedAttribute(Constants.BuiltInAttributes.Urls, AttributePlurality.MultiValued, AttributeDataType.String);
            var userAccountControlAttribute = SeedAttribute(Constants.BuiltInAttributes.UserAccountControl, AttributePlurality.SingleValued, AttributeDataType.Number);
            var userCertificatesAttribute = SeedAttribute(Constants.BuiltInAttributes.UserCertificates, AttributePlurality.MultiValued, AttributeDataType.Binary);
            var userPrincipalNameAttribute = SeedAttribute(Constants.BuiltInAttributes.UserPrincipalName, AttributePlurality.SingleValued, AttributeDataType.String);
            var userSharedFolderAttribute = SeedAttribute(Constants.BuiltInAttributes.UserSharedFolder, AttributePlurality.SingleValued, AttributeDataType.String);
            var webPageAttribute = SeedAttribute(Constants.BuiltInAttributes.WebPage, AttributePlurality.SingleValued, AttributeDataType.String);

            // group-specific attributes
            var groupScopeAttribute = SeedAttribute(Constants.BuiltInAttributes.GroupScope, AttributePlurality.SingleValued, AttributeDataType.String);
            var groupTypeAttribute = SeedAttribute(Constants.BuiltInAttributes.GroupType, AttributePlurality.SingleValued, AttributeDataType.String);
            var managedByAttribute = SeedAttribute(Constants.BuiltInAttributes.ManagedBy, AttributePlurality.SingleValued, AttributeDataType.Reference);
            var staticMembersAttribute = SeedAttribute(Constants.BuiltInAttributes.StaticMembers, AttributePlurality.MultiValued, AttributeDataType.Reference);

            // create the user object type and attribute mappings
            var userObjectType = Database.MetaverseObjectTypes.SingleOrDefault(q => q.Name == Constants.BuiltInObjectTypes.User);
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
            var groupObjectType = Database.MetaverseObjectTypes.SingleOrDefault(q => q.Name == Constants.BuiltInObjectTypes.Group);
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
            Database.Roles.Add(new Role { 
                BuiltIn = true,
                Name = "Administrators"
            });

            await Database.SaveChangesAsync();
        }

        private MetaverseAttribute SeedAttribute(string name, AttributePlurality attributePlurality, AttributeDataType attributeDataType)
        {
            var attribute = Database.MetaverseAttributes.SingleOrDefault(q => q.Name == name);
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
