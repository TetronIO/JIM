using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Interfaces;
using JIM.Models.Search;
using JIM.Models.Security;
using JIM.Models.Staging;
using Serilog;
using System.Diagnostics;

namespace JIM.Application.Servers
{
    internal class SeedingServer
    {
        #region accessors
        private JimApplication Application { get; }
        #endregion

        #region constructors
        internal SeedingServer(JimApplication application)
        {
            Application = application;
        }
        #endregion

        internal async Task SeedAsync()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            // has seeding already happened? don't run it twice!
            if (await Application.ServiceSettings.ServiceSettingsExistAsync())
            {
                Log.Information("SeedAsync: ServiceSettings already exists so believe seeding has already been performed. Stopping.");
                return;
            }

            await Application.ServiceSettings.CreateServiceSettingsAsync(new ServiceSettings());

            // get attributes, if they don't exist, prepare object in list for bulk submission via seeding method
            // create object types as needed
            // if attributes don't exist on type, prepare type attributes and submit in bulk via seeding method

            var attributesToCreate = new List<MetaverseAttribute>();
            var objectTypesToCreate = new List<MetaverseObjectType>();
            var predefinedSearchesToCreate = new List<PredefinedSearch>();
            var rolesToCreate = new List<Role>();
            var exampleDataSetsToCreate = new List<ExampleDataSet>();
            var dataGenerationTemplatesToCreate = new List<DataGenerationTemplate>();
            var connectorDefinitions = new List<ConnectorDefinition>();

            #region MetaverseAttributes
            // generic attributes
            var accountNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.AccountName, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var descriptionAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Description, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var displayNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.DisplayName, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var distinguishedNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.DistinguishedName, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var emailAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Email, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var endDateAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EndDate, AttributePlurality.SingleValued, AttributeDataType.DateTime, attributesToCreate);
            var extensionAttribute1Attribute1 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute1, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute10 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute10, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute11 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute11, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute12 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute12, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute13 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute13, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute14 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute14, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute15 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute15, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute2 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute2, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute3 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute3, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute4 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute4, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute5 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute5, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute6 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute6, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute7 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute7, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute8 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute8, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var extensionAttribute1Attribute9 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute9, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var hideFromAddressListsAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HideFromAddressLists, AttributePlurality.SingleValued, AttributeDataType.Bool, attributesToCreate);
            var infoAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Info, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var mailNicknameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.MailNickname, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var objectGuidAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ObjectGUID, AttributePlurality.SingleValued, AttributeDataType.Guid, attributesToCreate);
            var objectSidAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ObjectSid, AttributePlurality.SingleValued, AttributeDataType.Binary, attributesToCreate);
            var startDateAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.StartDate, AttributePlurality.SingleValued, AttributeDataType.DateTime, attributesToCreate);
            var typeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Type, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);

            // user-specific attributes
            var accountExpiresAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.AccountExpires, AttributePlurality.SingleValued, AttributeDataType.DateTime, attributesToCreate);
            var altSecurityIdentitiesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.AltSecurityIdentities, AttributePlurality.MultiValued, AttributeDataType.String, attributesToCreate);
            var commonNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.CommonName, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var companyAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Company, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var countryAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Country, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var countryCodeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.CountryCode, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var departmentAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Department, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var employeeIdAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeID, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var employeeTypeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeType, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var facsimileTelephoneNumberAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.FacsimileTelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var firstNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.FirstName, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var homeDirectoryAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HomeDirectory, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var homeDriveAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HomeDrive, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var homePhoneAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HomePhone, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var ipPhoneAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.IpPhone, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var jobTitleAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.JobTitle, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var lastNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.LastName, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var localityAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Locality, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var managerAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Manager, AttributePlurality.SingleValued, AttributeDataType.Reference, attributesToCreate);
            var mobileNumberAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.MobileNumber, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var objectIdentifierAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ObjectIdentifier, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var officeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Office, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var organisationAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Organisation, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var otherFacsimileTelephoneNumbersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherFacsimileTelephoneNumbers, AttributePlurality.MultiValued, AttributeDataType.String, attributesToCreate);
            var otherIpPhonesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherIpPhones, AttributePlurality.MultiValued, AttributeDataType.String, attributesToCreate);
            var otherMobilesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherMobiles, AttributePlurality.MultiValued, AttributeDataType.String, attributesToCreate);
            var otherPagersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherPagers, AttributePlurality.MultiValued, AttributeDataType.String, attributesToCreate);
            var otherTelephonesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherTelephones, AttributePlurality.MultiValued, AttributeDataType.String, attributesToCreate);
            var pagerAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Pager, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var physicalDeliveryOfficeNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PhysicalDeliveryOfficeName, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var postalAddressesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PostalAddresses, AttributePlurality.MultiValued, AttributeDataType.String, attributesToCreate);
            var postalCodeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PostalCode, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var postOFficeBoxesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PostOfficeBoxes, AttributePlurality.MultiValued, AttributeDataType.String, attributesToCreate);
            var pronounsAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Pronouns, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var proxyAddressesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ProxyAddresses, AttributePlurality.MultiValued, AttributeDataType.String, attributesToCreate);
            var scriptPathAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ScriptPath, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var sidHistoryAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.SidHistory, AttributePlurality.MultiValued, AttributeDataType.Binary, attributesToCreate);
            var stateOrProvinceAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.StateOrProvince, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var statusAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Status, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var streetAddressAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.StreetAddress, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var teamAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Team, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var telephoneNumberAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.TelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var urlsAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Urls, AttributePlurality.MultiValued, AttributeDataType.String, attributesToCreate);
            var userAccountControlAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserAccountControl, AttributePlurality.SingleValued, AttributeDataType.Number, attributesToCreate);
            var userCertificatesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserCertificates, AttributePlurality.MultiValued, AttributeDataType.Binary, attributesToCreate);
            var userPrincipalNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserPrincipalName, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var userSharedFolderAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserSharedFolder, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var webPageAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.WebPage, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);

            // group-specific attributes
            var groupScopeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.GroupScope, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var groupTypeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.GroupType, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var managedByAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ManagedBy, AttributePlurality.SingleValued, AttributeDataType.Reference, attributesToCreate);
            var ownersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Owners, AttributePlurality.MultiValued, AttributeDataType.Reference, attributesToCreate);
            var staticMembersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.StaticMembers, AttributePlurality.MultiValued, AttributeDataType.Reference, attributesToCreate);
            #endregion

            #region MetaverseObjectTypes
            // prepare the user object type and attribute mappings
            var userObjectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.Users, true);
            if (userObjectType == null)
            {
                userObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.Users, BuiltIn = true };
                objectTypesToCreate.Add(userObjectType);
                Log.Information("SeedAsync: Preparing MetaverseObjectType User");
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
            AddAttributeToObjectType(userObjectType, objectIdentifierAttribute);
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
            AddAttributeToObjectType(userObjectType, typeAttribute);
            AddAttributeToObjectType(userObjectType, urlsAttribute);
            AddAttributeToObjectType(userObjectType, userAccountControlAttribute);
            AddAttributeToObjectType(userObjectType, userCertificatesAttribute);
            AddAttributeToObjectType(userObjectType, userPrincipalNameAttribute);
            AddAttributeToObjectType(userObjectType, userSharedFolderAttribute);
            AddAttributeToObjectType(userObjectType, webPageAttribute);
            AddAttributeToObjectType(userObjectType, startDateAttribute);
            AddAttributeToObjectType(userObjectType, endDateAttribute);

            // create the group object type and attribute mappings
            var groupObjectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.Groups, true);
            if (groupObjectType == null)
            {
                groupObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.Groups, BuiltIn = true };
                objectTypesToCreate.Add(groupObjectType);
                Log.Information("SeedAsync: Preparing MetaverseObjectType Group");
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
            AddAttributeToObjectType(groupObjectType, ownersAttribute);
            AddAttributeToObjectType(groupObjectType, objectGuidAttribute);
            AddAttributeToObjectType(groupObjectType, objectSidAttribute);
            AddAttributeToObjectType(groupObjectType, proxyAddressesAttribute);
            AddAttributeToObjectType(groupObjectType, staticMembersAttribute);
            AddAttributeToObjectType(groupObjectType, statusAttribute);
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
            #endregion

            #region PredefinedSearches
            var usersPredefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync("users");
            if (usersPredefinedSearch == null)
            {
                usersPredefinedSearch = new PredefinedSearch
                {
                    Name = "Users",
                    Uri = "users",
                    IsDefaultForMetaverseObjectType = true,
                    BuiltIn = true,
                    MetaverseObjectType = userObjectType
                };

                usersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = displayNameAttribute, Position = 0 });
                usersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = jobTitleAttribute, Position = 1 });
                usersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = departmentAttribute, Position = 2 });
                usersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = companyAttribute, Position = 3 });
                usersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = emailAttribute, Position = 4 });
                usersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = statusAttribute, Position = 5 });
                
                predefinedSearchesToCreate.Add(usersPredefinedSearch);
                Log.Information("SeedAsync: Preparing User default PredefinedSearch");
            }

            var peopleUsersPredefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync("people");
            if (peopleUsersPredefinedSearch == null)
            {
                peopleUsersPredefinedSearch = new PredefinedSearch
                {
                    Name = "People",
                    Uri = "people",
                    BuiltIn = true,
                    MetaverseObjectType = userObjectType
                };

                peopleUsersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = displayNameAttribute, Position = 0 });
                peopleUsersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = jobTitleAttribute, Position = 1 });
                peopleUsersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = departmentAttribute, Position = 2 });
                peopleUsersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = companyAttribute, Position = 3 });
                peopleUsersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = emailAttribute, Position = 4 });
                peopleUsersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = statusAttribute, Position = 5 });

                peopleUsersPredefinedSearch.CriteriaGroups.Add(new PredefinedSearchCriteriaGroup
                {
                    Type = PredefinedSearchGroupType.All,
                    Criteria = new List<PredefinedSearchCriteria> {
                        new PredefinedSearchCriteria {
                            ComparisonType = PredefinedSearchComparisonType.Equals,
                            MetaverseAttribute = typeAttribute,
                            StringValue = "Person"
                        }
                    }
                });

                predefinedSearchesToCreate.Add(peopleUsersPredefinedSearch);
                Log.Information("SeedAsync: Preparing People PredefinedSearch");
            }

            var servicePrincipleUsersPredefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync("service-principals");
            if (servicePrincipleUsersPredefinedSearch == null)
            {
                servicePrincipleUsersPredefinedSearch = new PredefinedSearch
                {
                    Name = "Service Principals",
                    Uri = "service-principals",
                    BuiltIn = true,
                    MetaverseObjectType = userObjectType
                };

                servicePrincipleUsersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = displayNameAttribute, Position = 0 });
                servicePrincipleUsersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = accountNameAttribute, Position = 1 });
                servicePrincipleUsersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = emailAttribute, Position = 2 });
                servicePrincipleUsersPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = statusAttribute, Position = 3 });

                servicePrincipleUsersPredefinedSearch.CriteriaGroups.Add(new PredefinedSearchCriteriaGroup
                {
                    Type = PredefinedSearchGroupType.All,
                    Criteria = new List<PredefinedSearchCriteria> {
                        new PredefinedSearchCriteria {
                            ComparisonType = PredefinedSearchComparisonType.Equals,
                            MetaverseAttribute = typeAttribute,
                            StringValue = "service principal"
                        }
                    }
                });

                predefinedSearchesToCreate.Add(servicePrincipleUsersPredefinedSearch);
                Log.Information("SeedAsync: Preparing Service Principals PredefinedSearch");
            }

            var groupsPredefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync("groups");
            if (groupsPredefinedSearch == null)
            {
                groupsPredefinedSearch = new PredefinedSearch
                {
                    Name = "Groups",
                    Uri = "groups",
                    IsDefaultForMetaverseObjectType = true,
                    BuiltIn = true,
                    MetaverseObjectType = groupObjectType
                };

                groupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = displayNameAttribute, Position = 0 });
                groupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = groupTypeAttribute, Position = 1 });
                groupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = groupScopeAttribute, Position = 2 });
                groupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = emailAttribute, Position = 3 });
                groupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = statusAttribute, Position = 4 });
                
                predefinedSearchesToCreate.Add(groupsPredefinedSearch);
                Log.Information("SeedAsync: Preparing Group default PredefinedSearch");
            }

            var securityGroupsPredefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync("security");
            if (securityGroupsPredefinedSearch == null)
            {
                securityGroupsPredefinedSearch = new PredefinedSearch
                {
                    Name = "Security Groups",
                    Uri = "security-groups",
                    BuiltIn = true,
                    MetaverseObjectType = groupObjectType
                };

                securityGroupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = displayNameAttribute, Position = 0 });
                securityGroupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = groupTypeAttribute, Position = 1 });
                securityGroupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = groupScopeAttribute, Position = 2 });
                securityGroupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = statusAttribute, Position = 3 });

                securityGroupsPredefinedSearch.CriteriaGroups.Add(new PredefinedSearchCriteriaGroup {
                    Type = PredefinedSearchGroupType.All,
                    Criteria = new List<PredefinedSearchCriteria> {
                        new PredefinedSearchCriteria {
                            ComparisonType = PredefinedSearchComparisonType.Equals,
                            MetaverseAttribute = groupTypeAttribute,
                            StringValue = "Security" 
                        } 
                    }
                });

                predefinedSearchesToCreate.Add(securityGroupsPredefinedSearch);
                Log.Information("SeedAsync: Preparing Security Groups PredefinedSearch");
            }

            var distributionGroupsPredefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync("distribution");
            if (distributionGroupsPredefinedSearch == null)
            {
                distributionGroupsPredefinedSearch = new PredefinedSearch
                {
                    Name = "Distribution Groups",
                    Uri = "distribution-groups",
                    BuiltIn = true,
                    MetaverseObjectType = groupObjectType
                };

                distributionGroupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = displayNameAttribute, Position = 0 });
                distributionGroupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = groupTypeAttribute, Position = 1 });
                distributionGroupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = groupScopeAttribute, Position = 2 });
                distributionGroupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = emailAttribute, Position = 3 });
                distributionGroupsPredefinedSearch.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = statusAttribute, Position = 4 });

                distributionGroupsPredefinedSearch.CriteriaGroups.Add(new PredefinedSearchCriteriaGroup
                {
                    Type = PredefinedSearchGroupType.All,
                    Criteria = new List<PredefinedSearchCriteria> {
                        new PredefinedSearchCriteria {
                            ComparisonType = PredefinedSearchComparisonType.Equals,
                            MetaverseAttribute = groupTypeAttribute,
                            StringValue = "Distribution"
                        }
                    }
                });

                predefinedSearchesToCreate.Add(distributionGroupsPredefinedSearch);
                Log.Information("SeedAsync: Preparing Distribution Groups PredefinedSearch");
            }
            #endregion

            #region Roles
            // create the built-in roles
            var administratorsRole = await Application.Security.GetRoleAsync(Constants.BuiltInRoles.Administrators);
            if (administratorsRole == null)
            {
                administratorsRole = new Role
                {
                    BuiltIn = true,
                    Name = Constants.BuiltInRoles.Administrators
                };
                rolesToCreate.Add(administratorsRole);
                Log.Information($"SeedAsync: Preparing Role: {Constants.BuiltInRoles.Administrators}");
            }
            #endregion

            #region ExampleDataSets
            var companiesEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.Companies, "en", Properties.Resources.Companies_en);
            if (companiesEnDataSet != null)
                exampleDataSetsToCreate.Add(companiesEnDataSet);

            var departmentsEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.Departments, "en", Properties.Resources.Departments_en);
            if (departmentsEnDataSet != null)
                exampleDataSetsToCreate.Add(departmentsEnDataSet);

            var teamsEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.Teams, "en", Properties.Resources.Teams_en);
            if (teamsEnDataSet != null)
                exampleDataSetsToCreate.Add(teamsEnDataSet);

            var jobTitlesEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.JobTitles, "en", Properties.Resources.JobTitles_en);
            if (jobTitlesEnDataSet != null)
                exampleDataSetsToCreate.Add(jobTitlesEnDataSet);

            var firstnamesMaleEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.FirstnamesMale, "en", Properties.Resources.FirstnamesMale_en);
            if (firstnamesMaleEnDataSet != null)
                exampleDataSetsToCreate.Add(firstnamesMaleEnDataSet);

            var firstnamesFemaleEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.FirstnamesFemale, "en", Properties.Resources.FirstnamesFemale_en);
            if (firstnamesFemaleEnDataSet != null)
                exampleDataSetsToCreate.Add(firstnamesFemaleEnDataSet);

            var lastnamesEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.Lastnames, "en", Properties.Resources.Lastnames_en);
            if (lastnamesEnDataSet != null)
                exampleDataSetsToCreate.Add(lastnamesEnDataSet);

            var adjectivesEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.Adjectives, "en", Properties.Resources.Adjectives_en);
            if (adjectivesEnDataSet != null)
                exampleDataSetsToCreate.Add(adjectivesEnDataSet);

            var coloursEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.Colours, "en", Properties.Resources.Colours_en);
            if (coloursEnDataSet != null)
                exampleDataSetsToCreate.Add(coloursEnDataSet);

            var groupNameEndingsEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.GroupNameEndings, "en", Properties.Resources.GroupNameEndings_en);
            if (groupNameEndingsEnDataSet != null)
                exampleDataSetsToCreate.Add(groupNameEndingsEnDataSet);

            var wordsEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.Words, "en", Properties.Resources.Words_en);
            if (wordsEnDataSet != null)
                exampleDataSetsToCreate.Add(wordsEnDataSet);

            var userStatusesEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.UserStatuses, "en", Properties.Resources.UserStatuses_en);
            if (userStatusesEnDataSet != null)
                exampleDataSetsToCreate.Add(userStatusesEnDataSet);

            var groupStatusesEnDataSet = await PrepareExampleDataSetAsync(Constants.BuiltInExampleDataSets.GroupStatuses, "en", Properties.Resources.GroupStatuses_en);
            if (groupStatusesEnDataSet != null)
                exampleDataSetsToCreate.Add(groupStatusesEnDataSet);
            #endregion

            #region DataGenerationTemplates
            var template = await PrepareUsersAndGroupsDataGenerationTemplateAsync(userObjectType, groupObjectType, exampleDataSetsToCreate, attributesToCreate);
            if (template != null)
                dataGenerationTemplatesToCreate.Add(template);
            #endregion

            #region Connector Definitions
            var ldapConnector = new LdapConnector();
            var ldapConnectorDefinition = await PrepareConnectorDefinitionAsync(ldapConnector);
            if (ldapConnectorDefinition != null)
                connectorDefinitions.Add(ldapConnectorDefinition);
            #endregion

                // submit all the preparations to the repository for creation
                await Application.Repository.Seeding.SeedDataAsync(
                attributesToCreate, 
                objectTypesToCreate, 
                predefinedSearchesToCreate,
                rolesToCreate, 
                exampleDataSetsToCreate, 
                dataGenerationTemplatesToCreate,
                connectorDefinitions);
            stopwatch.Stop();
            Log.Verbose($"SeedAsync: Completed in: {stopwatch.Elapsed}");
        }

        #region private methods
        private async Task<MetaverseAttribute> GetOrPrepareMetaverseAttributeAsync(string name, AttributePlurality attributePlurality, AttributeDataType attributeDataType, List<MetaverseAttribute> attributeList)
        {
            var attribute = await Application.Metaverse.GetMetaverseAttributeAsync(name);
            if (attribute == null)
            {
                attribute = new MetaverseAttribute
                {
                    Name = name,
                    AttributePlurality = attributePlurality,
                    Type = attributeDataType,
                    BuiltIn = true
                };
                attributeList.Add(attribute);
                Log.Verbose($"GetOrPrepareMetaverseAttributeAsync: Prepared {name}");
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

        private async Task<ExampleDataSet?> PrepareExampleDataSetAsync(string name, string culture, string resourceValues)
        {
            var changes = false;
            var exampleDataSet = await Application.Repository.DataGeneration.GetExampleDataSetAsync(name, culture);
            if (exampleDataSet == null)
            {
                exampleDataSet = new ExampleDataSet()
                {
                    Name = name,
                    Culture = culture,
                    BuiltIn = true
                };
                changes = true;
            }

            // check if the dataset has all the necesary values
            var rawValues = resourceValues.Split(Environment.NewLine).ToList();
            foreach (var rawValue in rawValues)
            {
                if (!exampleDataSet.Values.Any(q => q.StringValue == rawValue))
                {
                    exampleDataSet.Values.Add(new ExampleDataSetValue { StringValue = rawValue.Trim() });
                    if (!changes)
                        changes = true;
                }
            }

            if (changes)
                return exampleDataSet;
            else
                return null;
        }

        private async Task<DataGenerationTemplate?> PrepareUsersAndGroupsDataGenerationTemplateAsync(MetaverseObjectType userType, MetaverseObjectType groupType, List<ExampleDataSet> dataSets, List<MetaverseAttribute> metaverseAttributes)
        {
            var templateName = "Users & Groups";

            // does a template exist already?
            var template = await Application.Repository.DataGeneration.GetTemplateAsync(templateName, false);
            if (template != null)
                return null;

            template = new DataGenerationTemplate { Name = templateName, BuiltIn = true };
            AddUsersToDataGenerationTemplate(template, userType, dataSets, metaverseAttributes);
            AddGroupsToDataGenerationTemplate(template, groupType, userType, dataSets, metaverseAttributes);
            return template;
        }

        /// <summary>
        /// Prepare the built-in connectors for seeding.
        /// </summary>
        private async Task<ConnectorDefinition?> PrepareConnectorDefinitionAsync(IConnector connector)
        {
            var connectorCapabilities = (IConnectorCapabilities)connector;
            if (connectorCapabilities == null)
                throw new ArgumentException("connector does not implement IConnectorCapabilities");

            var connectorSettings = (IConnectorSettings)connector;
            if (connectorSettings == null)
                throw new ArgumentException("connector does not implement IConnectorSettings");

            var connectorDefinition = await Application.ConnectedSystems.GetConnectorDefinitionAsync(connector.Name);
            if (connectorDefinition != null)
                return null;

            connectorDefinition = new ConnectorDefinition
            {
                Name = connector.Name,
                Description = connector.Description,
                Url = connector.Url,
                BuiltIn = true,
                SupportsFullImport = connectorCapabilities.SupportsFullImport,
                SupportsDeltaImport = connectorCapabilities.SupportsDeltaImport,
                SupportsExport = connectorCapabilities.SupportsExport,
                SupportsPartitions = connectorCapabilities.SupportsPartitions,
                SupportsPartitionContainers = connectorCapabilities.SupportsPartitionContainers
            };

            Application.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(connectorSettings, connectorDefinition);
            return connectorDefinition;
        }

        private static void AddUsersToDataGenerationTemplate(DataGenerationTemplate template, MetaverseObjectType userType, List<ExampleDataSet> dataSets, List<MetaverseAttribute> metaverseAttributes)
        {
            var userDataGenerationObjectType = new DataGenerationObjectType
            {
                MetaverseObjectType = userType,
                ObjectsToCreate = 10000
            };
            template.ObjectTypes.Add(userDataGenerationObjectType);            

            // do we have all the attribute definitions?
            var firstnamesMaleDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.FirstnamesMale);
            var firstnamesFemaleDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.FirstnamesFemale);
            var lastnamesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Lastnames);
            var companiesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Companies);
            var departmentsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Departments);
            var teamsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Teams);
            var jobTitlesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.JobTitles);
            var userStatusDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.UserStatuses);

            var firstnameAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.FirstName);
            if (firstnameAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.FirstName),
                    ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = firstnamesMaleDataSet, Order = 0 }, new ExampleDataSetInstance { ExampleDataSet = firstnamesFemaleDataSet, Order = 1 } },
                    PopulatedValuesPercentage = 100
                });
            }

            var lastnameAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.LastName);
            if (lastnameAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.LastName),
                    ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = lastnamesDataSet } },
                    PopulatedValuesPercentage = 100
                });
            }

            var displayNameAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.DisplayName);
            if (displayNameAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.DisplayName),
                    PopulatedValuesPercentage = 100,
                    Pattern = "{First Name} {Last Name}"
                });
            }

            var emailAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Email);
            if (emailAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Email),
                    PopulatedValuesPercentage = 100,
                    Pattern = "{First Name}.{Last Name}[UniqueInt]@demo.tetron.io"
                });
            }

            var employeeIdAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.EmployeeID);
            if (employeeIdAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.EmployeeID),
                    PopulatedValuesPercentage = 100,
                    MinNumber = 100001,
                    SequentialNumbers = true
                });
            }

            var companyAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Company);
            if (companyAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Company),
                    ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = companiesDataSet } },
                    PopulatedValuesPercentage = 100
                });
            }

            var departmentAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Department);
            if (departmentAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Department),
                    ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = departmentsDataSet } },
                    PopulatedValuesPercentage = 100
                });
            }

            var teamAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Team);
            if (teamAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Team),
                    ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = teamsDataSet } },
                    PopulatedValuesPercentage = 76
                });
            }

            var typeAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Type);
            if (typeAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Type),
                    Pattern = "Person",
                    PopulatedValuesPercentage = 100
                });
            }

            var jobTitleAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.JobTitle);
            if (jobTitleAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.JobTitle),
                    ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = jobTitlesDataSet } },
                    PopulatedValuesPercentage = 90
                });
            }

            var startDateAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.StartDate);
            if (startDateAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.StartDate),
                    MinDate = DateTime.Now.AddYears(-20),
                    MaxDate = DateTime.Now.AddMonths(3),
                    PopulatedValuesPercentage = 75
                });
            }

            var endDateAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.EndDate);
            if (endDateAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.EndDate),
                    MinDate = DateTime.Now.AddMonths(-11),
                    MaxDate = DateTime.Now.AddYears(1),
                    PopulatedValuesPercentage = 10
                });
            }

            var objectGuidAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.ObjectGUID);
            if (objectGuidAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.ObjectGUID),
                    PopulatedValuesPercentage = 100
                });
            }

            var managerAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Manager);
            if (managerAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Manager),
                    ManagerDepthPercentage = 25
                });
            }

            var statusAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Status);
            if (statusAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Status),
                    WeightedStringValues = new List<DataGenerationTemplateAttributeWeightedValue>
                    {
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Active", Weight = 0.8f },
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Suspended", Weight = 0.02f },
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Sebatical", Weight = 0.03f },
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Seconded", Weight = 0.03f },
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Maternity", Weight = 0.03f },
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Paternity", Weight = 0.03f },
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Leaving", Weight = 0.03f },
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Leaver", Weight = 0.03f }
                    },
                    PopulatedValuesPercentage = 100
                });
            }
        }

        private static void AddGroupsToDataGenerationTemplate(
            DataGenerationTemplate template, 
            MetaverseObjectType groupType, 
            MetaverseObjectType userType, 
            List<ExampleDataSet> dataSets, 
            List<MetaverseAttribute> metaverseAttributes)
        {
            var groupDataGenerationObjectType = new DataGenerationObjectType
            {
                MetaverseObjectType = groupType,
                ObjectsToCreate = 500
            };
            template.ObjectTypes.Add(groupDataGenerationObjectType);

            // do we have all the attribute definitions?
            var adjectivesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Adjectives);
            var coloursDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Colours);
            var wordsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Words);
            var groupEndingsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.GroupNameEndings);

            var displayNameAttribute = groupDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.DisplayName);
            if (displayNameAttribute == null)
            {
                groupDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.DisplayName),
                    ExampleDataSetInstances = new List<ExampleDataSetInstance> { 
                        new ExampleDataSetInstance { ExampleDataSet = adjectivesDataSet, Order = 0 }, 
                        new ExampleDataSetInstance { ExampleDataSet = coloursDataSet, Order = 1 }, 
                        new ExampleDataSetInstance { ExampleDataSet = wordsDataSet, Order = 2 }, 
                        new ExampleDataSetInstance { ExampleDataSet = groupEndingsDataSet, Order = 3 } },
                    PopulatedValuesPercentage = 100,
                    Pattern = "{0} {1} {2} {3}"
                });
            }

            var groupTypeAttribute = groupDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.GroupType);
            if (groupTypeAttribute == null)
            {
                groupDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.GroupType),
                    WeightedStringValues = new List<DataGenerationTemplateAttributeWeightedValue>
                    {
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Security", Weight = 0.6f },
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Distribution", Weight = 0.4f },
                    },
                    PopulatedValuesPercentage = 100
                });
            }

            var emailAttribute = groupDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Email);
            if (emailAttribute == null)
            {
                groupDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Email),
                    AttributeDependency = new DataGenerationTemplateAttributeDependency { 
                        MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.GroupType),
                        ComparisonType = ComparisonType.Equals,
                        StringValue = "Distribution"
                    },
                    PopulatedValuesPercentage = 100,
                    Pattern = "distro-[UniqueInt]@demo.tetron.io"
                });
            }

            var groupScopeAttribute = groupDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.GroupScope);
            if (groupScopeAttribute == null)
            {
                groupDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.GroupScope),
                    Pattern = "Universal",
                    PopulatedValuesPercentage = 100
                });
            }

            var infoAttribute = groupDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Info);
            if (infoAttribute == null)
            {
                groupDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Info),
                    Pattern = "This group was created by the JIM data generation feature.",
                    PopulatedValuesPercentage = 100
                });
            }

            var staticMembersAttribute = groupDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.StaticMembers);
            if (staticMembersAttribute == null)
            {
                groupDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.StaticMembers),
                    ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { userType },
                    MvaRefMinAssignments = 5,
                    MvaRefMaxAssignments = 200,
                    PopulatedValuesPercentage = 100
                });
            }

            var ownersAttribute = groupDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Owners);
            if (ownersAttribute == null)
            {
                groupDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Owners),
                    ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { userType },
                    MvaRefMinAssignments = 0,
                    MvaRefMaxAssignments = 5,
                    PopulatedValuesPercentage = 75
                });
            }

            var managedByAttribute = groupDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.ManagedBy);
            if (managedByAttribute == null)
            {
                groupDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.ManagedBy),
                    ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { userType },
                    PopulatedValuesPercentage = 75
                });
            }

            var statusAttribute = groupDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Status);
            if (statusAttribute == null)
            {
                groupDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Status),
                    WeightedStringValues = new List<DataGenerationTemplateAttributeWeightedValue>
                    {
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Active", Weight = 0.9f },
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Retiring", Weight = 0.05f },
                        new DataGenerationTemplateAttributeWeightedValue { Value = "Retired", Weight = 0.05f },
                    },
                    PopulatedValuesPercentage = 100
                });
            }
        }
        #endregion
    }
}
