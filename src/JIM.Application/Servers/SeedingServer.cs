using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Security;
using Serilog;
using System.Diagnostics;

namespace JIM.Application.Servers
{
    internal class SeedingServer
    {
        private JimApplication Application { get; }

        internal SeedingServer(JimApplication application)
        {
            Application = application;
        }

        internal async Task SeedAsync()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            if (!await Application.ServiceSettings.ServiceSettingsExistAsync())
                await Application.ServiceSettings.CreateServiceSettingsAsync(new ServiceSettings());

            // get attributes, if they don't exist, prepare object in list for bulk submission via seeding method
            // create object types as needed
            // if attributes don't exist on type, prepare type attributes and submit in bulk via seeding method

            var attributesToCreate = new List<MetaverseAttribute>();
            var objectTypesToCreate = new List<MetaverseObjectType>();
            var rolesToCreate = new List<Role>();
            var exampleDataSetsToCreate = new List<ExampleDataSet>();
            var dataGenerationTemplatesToCreate = new List<DataGenerationTemplate>();

            #region MetaverseAttributes
            // generic attributes
            var accountNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.AccountName, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var descriptionAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Description, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var displayNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.DisplayName, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var distinguishedNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.DistinguishedName, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
            var emailAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Email, AttributePlurality.SingleValued, AttributeDataType.String, attributesToCreate);
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
            var endDateAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EndDate, AttributePlurality.SingleValued, AttributeDataType.DateTime, attributesToCreate);

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
            var userObjectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.User);
            if (userObjectType == null)
            {
                userObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User, BuiltIn = true };
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
            AddAttributeToObjectType(userObjectType, startDateAttribute);
            AddAttributeToObjectType(userObjectType, endDateAttribute);

            // create the group object type and attribute mappings
            var groupObjectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.Group);
            if (groupObjectType == null)
            {
                groupObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.Group, BuiltIn = true };
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
            #endregion

            #region DataGenerationTemplates
            var userDataGenerationTemplate = await PrepareUserDataGenerationTemplateAsync(userObjectType, exampleDataSetsToCreate, attributesToCreate);
            if (userDataGenerationTemplate != null)
                dataGenerationTemplatesToCreate.Add(userDataGenerationTemplate);

            var groupDataGenerationTemplate = await PrepareGroupDataGenerationTemplateAsync(groupObjectType, exampleDataSetsToCreate, attributesToCreate);
            if (groupDataGenerationTemplate != null)
                dataGenerationTemplatesToCreate.Add(groupDataGenerationTemplate);
            #endregion

            // submit all the preparations to the repository for creation
            await Application.Repository.Seeding.SeedDataAsync(attributesToCreate, objectTypesToCreate, rolesToCreate, exampleDataSetsToCreate, dataGenerationTemplatesToCreate);
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

        private async Task<DataGenerationTemplate?> PrepareUserDataGenerationTemplateAsync(MetaverseObjectType userType, List<ExampleDataSet> dataSets, List<MetaverseAttribute> metaverseAttributes)
        {
            var changes = false;
            var dgt = await Application.Repository.DataGeneration.GetTemplateAsync(Constants.BuiltInDataGenerationTemplates.UsersEn);
            if (dgt == null)
            {
                dgt = new DataGenerationTemplate { Name = Constants.BuiltInDataGenerationTemplates.UsersEn };
                changes = true;
            }

            // do we have the user data generation object type?
            var userDataGenerationObjectType = dgt.ObjectTypes.SingleOrDefault(q => q.MetaverseObjectType.Name == Constants.BuiltInObjectTypes.User);
            if (userDataGenerationObjectType == null)
            {
                userDataGenerationObjectType = new DataGenerationObjectType
                {
                    MetaverseObjectType = userType,
                    ObjectsToCreate = 10000
                };
                dgt.ObjectTypes.Add(userDataGenerationObjectType);
            }

            // do we have all the attribute definitions?
            var firstnamesMaleDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.FirstnamesMale);
            var firstnamesFemaleDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.FirstnamesFemale);
            var lastnamesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Lastnames);
            var companiesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Companies);
            var departmentsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Departments);
            var teamsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Teams);
            var jobTitlesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.JobTitles);

            var firstnameAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.FirstName);
            if (firstnameAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.FirstName),
                    ExampleDataSets = { firstnamesMaleDataSet, firstnamesFemaleDataSet },
                    PopulatedValuesPercentage = 100
                });
            }

            var lastnameAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.LastName);
            if (lastnameAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.LastName),
                    ExampleDataSets = { lastnamesDataSet },
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
                    ExampleDataSets = { companiesDataSet },
                    PopulatedValuesPercentage = 100
                });
            }

            var departmentAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Department);
            if (departmentAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Department),
                    ExampleDataSets = { departmentsDataSet },
                    PopulatedValuesPercentage = 100
                });
            }

            var teamAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Team);
            if (teamAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Team),
                    ExampleDataSets = { teamsDataSet },
                    PopulatedValuesPercentage = 76
                });
            }

            var jobTitleAttribute = userDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.JobTitle);
            if (jobTitleAttribute == null)
            {
                userDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.JobTitle),
                    ExampleDataSets = { jobTitlesDataSet },
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

            if (changes)
                return dgt;
            else
                return null;
        }

        private async Task<DataGenerationTemplate?> PrepareGroupDataGenerationTemplateAsync(MetaverseObjectType groupType, List<ExampleDataSet> dataSets, List<MetaverseAttribute> metaverseAttributes)
        {
            var changes = false;
            var dgt = await Application.Repository.DataGeneration.GetTemplateAsync(Constants.BuiltInDataGenerationTemplates.GroupsEn);
            if (dgt == null)
            {
                dgt = new DataGenerationTemplate { Name = Constants.BuiltInDataGenerationTemplates.GroupsEn };
                changes = true;
            }

            // do we have the group data generation object type?
            var groupDataGenerationObjectType = dgt.ObjectTypes.SingleOrDefault(q => q.MetaverseObjectType.Name == Constants.BuiltInObjectTypes.Group);
            if (groupDataGenerationObjectType == null)
            {
                groupDataGenerationObjectType = new DataGenerationObjectType
                {
                    MetaverseObjectType = groupType,
                    ObjectsToCreate = 1000
                };
                dgt.ObjectTypes.Add(groupDataGenerationObjectType);
            }

            // do we have all the attribute definitions?
            var adjectivesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Adjectives);
            var coloursDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Colours);
            var groupEndingsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.GroupNameEndings);
            var wordsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Words);

            var displayNameAttribute = groupDataGenerationObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.DisplayName);
            if (displayNameAttribute == null)
            {
                groupDataGenerationObjectType.TemplateAttributes.Add(new DataGenerationTemplateAttribute
                {
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.DisplayName),
                    ExampleDataSets = { adjectivesDataSet, coloursDataSet, wordsDataSet, groupEndingsDataSet },
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
                    Pattern = "Security",
                    PopulatedValuesPercentage = 100
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
                    MvaRefMinAssignments = 0,
                    MvaRefMaxAssignments = 5,
                    PopulatedValuesPercentage = 75
                });
            }

            if (changes)
                return dgt;
            else
                return null;
        }
        #endregion
    }
}
