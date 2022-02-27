using JIM.Data;
using JIM.Models.Core;

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
            ConnectedSystems = new ConnectedSystemRepository(this);
            Metaverse = new MetaverseRepository(this);
            ServiceSettings = new ServiceSettingsRepository(this);
            Security = new SecurityRepository(this);
        }

        public async Task SeedDatabaseAsync()
        {
            // seeding requirements:
            // - service settings
            // - metaverse attributes
            // - object types, with attributes

            using var db = new JimDbContext();
            if (!db.ServiceSettings.Any())
                db.ServiceSettings.Add(new ServiceSettings());

            // HOW ARE WE GOING TO ENFORCE FIXED LIST VALUES?
            // This will be important even in the sync-delivery phase where we need to provide simple config values that map to directory attributes, i.e. grouptype/scope
            // Would also like a way to make it easy to configure userAccountControl using checkboxes or choose-flag-to-enable/disable helpers
            // Maybe attributes can register helpers that have UI and API aspects?

            // generic attributes
            var accountNameAttribute = SeedAttribute(db, Constants.BuiltInAttributes.AccountName, AttributePlurality.SingleValued, AttributeDataType.String);
            var descriptionAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Description, AttributePlurality.SingleValued, AttributeDataType.String);
            var displayNameAttribute = SeedAttribute(db, Constants.BuiltInAttributes.DisplayName, AttributePlurality.SingleValued, AttributeDataType.String);
            var distinguishedNameAttribute = SeedAttribute(db, Constants.BuiltInAttributes.DistinguishedName, AttributePlurality.SingleValued, AttributeDataType.String);
            var emailAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Email, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute1 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute1, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute10 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute10, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute11 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute11, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute12 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute12, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute13 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute13, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute14 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute14, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute15 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute15, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute2 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute2, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute3 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute3, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute4 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute4, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute5 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute5, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute6 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute6, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute7 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute7, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute8 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute8, AttributePlurality.SingleValued, AttributeDataType.String);
            var extensionAttribute1Attribute9 = SeedAttribute(db, Constants.BuiltInAttributes.ExtensionAttribute9, AttributePlurality.SingleValued, AttributeDataType.String);
            var hideFromAddressListsAttribute = SeedAttribute(db, Constants.BuiltInAttributes.HideFromAddressLists, AttributePlurality.SingleValued, AttributeDataType.Bool);
            var infoAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Info, AttributePlurality.SingleValued, AttributeDataType.String);
            var mailNicknameAttribute = SeedAttribute(db, Constants.BuiltInAttributes.MailNickname, AttributePlurality.SingleValued, AttributeDataType.String);
            var objectGuidAttribute = SeedAttribute(db, Constants.BuiltInAttributes.ObjectGUID, AttributePlurality.SingleValued, AttributeDataType.Guid);
            var objectSidAttribute = SeedAttribute(db, Constants.BuiltInAttributes.ObjectSid, AttributePlurality.SingleValued, AttributeDataType.Binary);

            // user-specific attributes
            var accountExpiresAttribute = SeedAttribute(db, Constants.BuiltInAttributes.AccountExpires, AttributePlurality.SingleValued, AttributeDataType.DateTime);
            var altSecurityIdentitiesAttribute = SeedAttribute(db, Constants.BuiltInAttributes.AltSecurityIdentities, AttributePlurality.MultiValued, AttributeDataType.String);
            var commonNameAttribute = SeedAttribute(db, Constants.BuiltInAttributes.CommonName, AttributePlurality.SingleValued, AttributeDataType.String);
            var companyAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Company, AttributePlurality.SingleValued, AttributeDataType.String);
            var countryAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Country, AttributePlurality.SingleValued, AttributeDataType.String);
            var countryCodeAttribute = SeedAttribute(db, Constants.BuiltInAttributes.CountryCode, AttributePlurality.SingleValued, AttributeDataType.String);
            var departmentAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Department, AttributePlurality.SingleValued, AttributeDataType.String);
            var employeeIdAttribute = SeedAttribute(db, Constants.BuiltInAttributes.EmployeeID, AttributePlurality.SingleValued, AttributeDataType.String);
            var employeeTypeAttribute = SeedAttribute(db, Constants.BuiltInAttributes.EmployeeType, AttributePlurality.SingleValued, AttributeDataType.String);
            var facsimileTelephoneNumberAttribute = SeedAttribute(db, Constants.BuiltInAttributes.FacsimileTelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var firstNameAttribute = SeedAttribute(db, Constants.BuiltInAttributes.FirstName, AttributePlurality.SingleValued, AttributeDataType.String);
            var homeDirectoryAttribute = SeedAttribute(db, Constants.BuiltInAttributes.HomeDirectory, AttributePlurality.SingleValued, AttributeDataType.String);
            var homeDriveAttribute = SeedAttribute(db, Constants.BuiltInAttributes.HomeDrive, AttributePlurality.SingleValued, AttributeDataType.String);
            var homePhoneAttribute = SeedAttribute(db, Constants.BuiltInAttributes.HomePhone, AttributePlurality.SingleValued, AttributeDataType.String);
            var ipPhoneAttribute = SeedAttribute(db, Constants.BuiltInAttributes.IpPhone, AttributePlurality.SingleValued, AttributeDataType.String);
            var jobTitleAttribute = SeedAttribute(db, Constants.BuiltInAttributes.JobTitle, AttributePlurality.SingleValued, AttributeDataType.String);
            var lastNameAttribute = SeedAttribute(db, Constants.BuiltInAttributes.LastName, AttributePlurality.SingleValued, AttributeDataType.String);
            var localityAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Locality, AttributePlurality.SingleValued, AttributeDataType.String);
            var managerAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Manager, AttributePlurality.SingleValued, AttributeDataType.Reference);
            var mobileNumberAttribute = SeedAttribute(db, Constants.BuiltInAttributes.MobileNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var officeAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Office, AttributePlurality.SingleValued, AttributeDataType.String);
            var organisationAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Organisation, AttributePlurality.SingleValued, AttributeDataType.String);
            var otherFacsimileTelephoneNumbersAttribute = SeedAttribute(db, Constants.BuiltInAttributes.OtherFacsimileTelephoneNumbers, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherIpPhonesAttribute = SeedAttribute(db, Constants.BuiltInAttributes.OtherIpPhones, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherMobilesAttribute = SeedAttribute(db, Constants.BuiltInAttributes.OtherMobiles, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherPagersAttribute = SeedAttribute(db, Constants.BuiltInAttributes.OtherPagers, AttributePlurality.MultiValued, AttributeDataType.String);
            var otherTelephonesAttribute = SeedAttribute(db, Constants.BuiltInAttributes.OtherTelephones, AttributePlurality.MultiValued, AttributeDataType.String);
            var pagerAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Pager, AttributePlurality.SingleValued, AttributeDataType.String);
            var physicalDeliveryOfficeNameAttribute = SeedAttribute(db, Constants.BuiltInAttributes.PhysicalDeliveryOfficeName, AttributePlurality.SingleValued, AttributeDataType.String);
            var postalAddressesAttribute = SeedAttribute(db, Constants.BuiltInAttributes.PostalAddresses, AttributePlurality.MultiValued, AttributeDataType.String);
            var postalCodeAttribute = SeedAttribute(db, Constants.BuiltInAttributes.PostalCode, AttributePlurality.SingleValued, AttributeDataType.String);
            var postOFficeBoxesAttribute = SeedAttribute(db, Constants.BuiltInAttributes.PostOfficeBoxes, AttributePlurality.MultiValued, AttributeDataType.String);
            var pronounsAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Pronouns, AttributePlurality.SingleValued, AttributeDataType.String);
            var proxyAddressesAttribute = SeedAttribute(db, Constants.BuiltInAttributes.ProxyAddresses, AttributePlurality.MultiValued, AttributeDataType.String);
            var scriptPathAttribute = SeedAttribute(db, Constants.BuiltInAttributes.ScriptPath, AttributePlurality.SingleValued, AttributeDataType.String);
            var sidHistoryAttribute = SeedAttribute(db, Constants.BuiltInAttributes.SidHistory, AttributePlurality.MultiValued, AttributeDataType.Binary);
            var stateOrProvinceAttribute = SeedAttribute(db, Constants.BuiltInAttributes.StateOrProvince, AttributePlurality.SingleValued, AttributeDataType.String);
            var statusAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Status, AttributePlurality.SingleValued, AttributeDataType.String);
            var streetAddressAttribute = SeedAttribute(db, Constants.BuiltInAttributes.StreetAddress, AttributePlurality.SingleValued, AttributeDataType.String);
            var teamAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Team, AttributePlurality.SingleValued, AttributeDataType.String);
            var telephoneNumberAttribute = SeedAttribute(db, Constants.BuiltInAttributes.TelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String);
            var urlsAttribute = SeedAttribute(db, Constants.BuiltInAttributes.Urls, AttributePlurality.MultiValued, AttributeDataType.String);
            var userAccountControlAttribute = SeedAttribute(db, Constants.BuiltInAttributes.UserAccountControl, AttributePlurality.SingleValued, AttributeDataType.Number);
            var userCertificatesAttribute = SeedAttribute(db, Constants.BuiltInAttributes.UserCertificates, AttributePlurality.MultiValued, AttributeDataType.Binary);
            var userPrincipalNameAttribute = SeedAttribute(db, Constants.BuiltInAttributes.UserPrincipalName, AttributePlurality.SingleValued, AttributeDataType.String);
            var userSharedFolderAttribute = SeedAttribute(db, Constants.BuiltInAttributes.UserSharedFolder, AttributePlurality.SingleValued, AttributeDataType.String);
            var webPageAttribute = SeedAttribute(db, Constants.BuiltInAttributes.WebPage, AttributePlurality.SingleValued, AttributeDataType.String);

            // group-specific attributes
            var groupScopeAttribute = SeedAttribute(db, Constants.BuiltInAttributes.GroupScope, AttributePlurality.SingleValued, AttributeDataType.String);
            var groupTypeAttribute = SeedAttribute(db, Constants.BuiltInAttributes.GroupType, AttributePlurality.SingleValued, AttributeDataType.String);
            var managedByAttribute = SeedAttribute(db, Constants.BuiltInAttributes.ManagedBy, AttributePlurality.SingleValued, AttributeDataType.Reference);
            var staticMembersAttribute = SeedAttribute(db, Constants.BuiltInAttributes.StaticMembers, AttributePlurality.MultiValued, AttributeDataType.Reference);

            // create the user object type and attribute mappings
            var userObjectType = db.MetaverseObjectTypes.SingleOrDefault(q => q.Name == Constants.BuiltInObjectTypes.User);
            if (userObjectType == null)
            {
                userObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User, BuiltIn = true };
                db.MetaverseObjectTypes.Add(userObjectType);
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
            var groupObjectType = db.MetaverseObjectTypes.SingleOrDefault(q => q.Name == Constants.BuiltInObjectTypes.Group);
            if (groupObjectType == null)
            {
                groupObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.Group, BuiltIn = true };
                db.MetaverseObjectTypes.Add(groupObjectType);
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

            await db.SaveChangesAsync();
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
            }
            return attribute;
        }

        private static void AddAttributeToObjectType(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute)
        {
            if (!metaverseObjectType.Attributes.Any(q => q.Name == metaverseAttribute.Name))
                metaverseObjectType.Attributes.Add(metaverseAttribute);
        }
    }
}