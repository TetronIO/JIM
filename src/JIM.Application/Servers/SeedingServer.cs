// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Interfaces;
using JIM.Models.Scheduling;
using JIM.Models.Search;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Application.Utilities;
using Serilog;
using System.Diagnostics;

namespace JIM.Application.Servers;

internal class SeedingServer
{
    #region accessors
    private JimApplication Application { get; }

    /// <summary>
    /// The parent Activity for the current seeding pass, created lazily by
    /// <see cref="GetOrCreateSeedingActivityAsync"/> the first time a seed step is about to create something.
    /// Null until then, and cleared once <see cref="CompleteSeedingActivityAsync"/> or
    /// <see cref="FailSeedingActivityAsync"/> has run, so every application startup that actually seeds
    /// something groups all of it under exactly one "System Initialisation" Activity, while a startup where
    /// every seed step no-ops (the normal case after the first deployment) records nothing at all.
    /// </summary>
    private JIM.Models.Activities.Activity? _seedingActivity;
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
        // IMPORTANT: ServiceSettings is created at the END of seeding (in SeedDataAsync) to ensure
        // that if the process crashes during seeding, the next restart will retry seeding from scratch.
        // This prevents a race condition where ServiceSettings exists but MetaverseAttributes don't.
        if (await Application.ServiceSettings.ServiceSettingsExistAsync())
        {
            Log.Information("SeedAsync: ServiceSettings already exists so believe seeding has already been performed. Stopping.");
            return;
        }

        // get attributes, if they don't exist, prepare object in list for bulk submission via seeding method
        // create object types as needed
        // if attributes don't exist on type, prepare type attributes and submit in bulk via seeding method

        var attributesToCreate = new List<MetaverseAttribute>();
        var objectTypesToCreate = new List<MetaverseObjectType>();
        var predefinedSearchesToCreate = new List<PredefinedSearch>();
        var exampleDataSetsToCreate = new List<ExampleDataSet>();
        var dataGenerationTemplatesToCreate = new List<ExampleDataTemplate>();
        var connectorDefinitions = new List<ConnectorDefinition>();

        #region MetaverseAttributes
        // common attributes
        var accountNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.AccountName, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var descriptionAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Description, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var displayNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.DisplayName, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var distinguishedNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.DistinguishedName, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var emailAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Email, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute1 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute1, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute10 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute10, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute11 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute11, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute12 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute12, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute13 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute13, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute14 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute14, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute15 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute15, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute2 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute2, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute3 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute3, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute4 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute4, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute5 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute5, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute6 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute6, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute7 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute7, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute8 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute8, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var extensionAttribute1Attribute9 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute9, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var hideFromAddressListsAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HideFromAddressLists, AttributePlurality.SingleValued, AttributeDataType.Boolean, attributesToCreate);
        var infoAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Info, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var mailNicknameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.MailNickname, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var objectGuidAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ObjectGuid, AttributePlurality.SingleValued, AttributeDataType.Guid, attributesToCreate);
        var objectSidAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ObjectSid, AttributePlurality.SingleValued, AttributeDataType.Binary, attributesToCreate);
        var typeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Type, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);

        // user-specific attributes
        var accountExpiresAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.AccountExpires, AttributePlurality.SingleValued, AttributeDataType.DateTime, attributesToCreate);
        var altSecurityIdentitiesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.AltSecurityIdentities, AttributePlurality.MultiValued, AttributeDataType.Text, attributesToCreate, AttributeRenderingHint.List);
        var commonNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.CommonName, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var companyAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Company, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var countryAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Country, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var countryCodeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.CountryCode, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var departmentAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Department, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var employeeEndDateAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeEndDate, AttributePlurality.SingleValued, AttributeDataType.DateTime, attributesToCreate);
        var employeeIdAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeId, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var employeeNumberAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeNumber, AttributePlurality.SingleValued, AttributeDataType.Number, attributesToCreate);
        var employeeStartDateAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeStartDate, AttributePlurality.SingleValued, AttributeDataType.DateTime, attributesToCreate);
        var employeeTypeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeType, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var facsimileTelephoneNumberAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.FacsimileTelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var firstNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.FirstName, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var homeDirectoryAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HomeDirectory, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var homeDriveAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HomeDrive, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var homePhoneAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HomePhone, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var identityAssuranceLevelAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.IdentityAssuranceLevel, AttributePlurality.SingleValued, AttributeDataType.Number, attributesToCreate);
        var ipPhoneAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.IpPhone, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var jobTitleAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.JobTitle, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var lastNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.LastName, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var localityAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Locality, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var managerAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Manager, AttributePlurality.SingleValued, AttributeDataType.Reference, attributesToCreate);
        var mobileNumberAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.MobileNumber, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var objectIdentifierAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ObjectIdentifier, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var subjectIdentifierAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.SubjectIdentifier, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var officeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Office, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var organisationAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Organisation, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var otherFacsimileTelephoneNumbersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherFacsimileTelephoneNumbers, AttributePlurality.MultiValued, AttributeDataType.Text, attributesToCreate, AttributeRenderingHint.ChipSet);
        var otherIpPhonesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherIpPhones, AttributePlurality.MultiValued, AttributeDataType.Text, attributesToCreate, AttributeRenderingHint.ChipSet);
        var otherMobilesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherMobiles, AttributePlurality.MultiValued, AttributeDataType.Text, attributesToCreate, AttributeRenderingHint.ChipSet);
        var otherPagersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherPagers, AttributePlurality.MultiValued, AttributeDataType.Text, attributesToCreate, AttributeRenderingHint.ChipSet);
        var otherTelephonesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherTelephones, AttributePlurality.MultiValued, AttributeDataType.Text, attributesToCreate, AttributeRenderingHint.ChipSet);
        var pagerAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Pager, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var photoAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Photo, AttributePlurality.SingleValued, AttributeDataType.Binary, attributesToCreate);
        var physicalDeliveryOfficeNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PhysicalDeliveryOfficeName, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var postOfficeBoxesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PostOfficeBoxes, AttributePlurality.MultiValued, AttributeDataType.Text, attributesToCreate, AttributeRenderingHint.ChipSet);
        var postalAddressesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PostalAddresses, AttributePlurality.MultiValued, AttributeDataType.Text, attributesToCreate, AttributeRenderingHint.List);
        var postalCodeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PostalCode, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var pronounsAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Pronouns, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var proxyAddressesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ProxyAddresses, AttributePlurality.MultiValued, AttributeDataType.Text, attributesToCreate, AttributeRenderingHint.List);
        var scriptPathAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ScriptPath, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var sidHistoryAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.SidHistory, AttributePlurality.MultiValued, AttributeDataType.Binary, attributesToCreate, AttributeRenderingHint.List);
        var stateOrProvinceAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.StateOrProvince, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var statusAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Status, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var streetAddressAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.StreetAddress, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var teamAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Team, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var telephoneNumberAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.TelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var urlsAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Urls, AttributePlurality.MultiValued, AttributeDataType.Text, attributesToCreate, AttributeRenderingHint.List);
        var userAccountControlAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserAccountControl, AttributePlurality.SingleValued, AttributeDataType.Number, attributesToCreate);
        var userCertificatesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserCertificates, AttributePlurality.MultiValued, AttributeDataType.Binary, attributesToCreate, AttributeRenderingHint.List);
        var userPrincipalNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserPrincipalName, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var userSharedFolderAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserSharedFolder, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var webPageAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.WebPage, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);

        // group-specific attributes
        var groupScopeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.GroupScope, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var groupTypeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.GroupType, AttributePlurality.SingleValued, AttributeDataType.Text, attributesToCreate);
        var groupTypeFlagsAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.GroupTypeFlags, AttributePlurality.SingleValued, AttributeDataType.Number, attributesToCreate);
        var managedByAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ManagedBy, AttributePlurality.SingleValued, AttributeDataType.Reference, attributesToCreate);
        var ownersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Owners, AttributePlurality.MultiValued, AttributeDataType.Reference, attributesToCreate, AttributeRenderingHint.Table);
        var staticMembersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.StaticMembers, AttributePlurality.MultiValued, AttributeDataType.Reference, attributesToCreate, AttributeRenderingHint.Table);
        #endregion

        #region MetaverseObjectTypes
        // prepare the user object type and attribute mappings
        var userObjectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.User, true);
        if (userObjectType == null)
        {
            userObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User, PluralName = "Users", BuiltIn = true, Icon = "Person" };
            AuditHelper.SetCreatedBySystem(userObjectType);
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
        AddAttributeToObjectType(userObjectType, employeeEndDateAttribute);
        AddAttributeToObjectType(userObjectType, employeeIdAttribute);
        AddAttributeToObjectType(userObjectType, employeeNumberAttribute);
        AddAttributeToObjectType(userObjectType, employeeStartDateAttribute);
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
        AddAttributeToObjectType(userObjectType, identityAssuranceLevelAttribute);
        AddAttributeToObjectType(userObjectType, infoAttribute);
        AddAttributeToObjectType(userObjectType, ipPhoneAttribute);
        AddAttributeToObjectType(userObjectType, jobTitleAttribute);
        AddAttributeToObjectType(userObjectType, lastNameAttribute);
        AddAttributeToObjectType(userObjectType, localityAttribute);
        AddAttributeToObjectType(userObjectType, mailNicknameAttribute);
        AddAttributeToObjectType(userObjectType, managerAttribute);
        AddAttributeToObjectType(userObjectType, mobileNumberAttribute);
        AddAttributeToObjectType(userObjectType, objectGuidAttribute);
        AddAttributeToObjectType(userObjectType, objectIdentifierAttribute);
        AddAttributeToObjectType(userObjectType, subjectIdentifierAttribute);
        AddAttributeToObjectType(userObjectType, objectSidAttribute);
        AddAttributeToObjectType(userObjectType, officeAttribute);
        AddAttributeToObjectType(userObjectType, organisationAttribute);
        AddAttributeToObjectType(userObjectType, otherFacsimileTelephoneNumbersAttribute);
        AddAttributeToObjectType(userObjectType, otherIpPhonesAttribute);
        AddAttributeToObjectType(userObjectType, otherMobilesAttribute);
        AddAttributeToObjectType(userObjectType, otherPagersAttribute);
        AddAttributeToObjectType(userObjectType, otherTelephonesAttribute);
        AddAttributeToObjectType(userObjectType, pagerAttribute);
        AddAttributeToObjectType(userObjectType, photoAttribute);
        AddAttributeToObjectType(userObjectType, physicalDeliveryOfficeNameAttribute);
        AddAttributeToObjectType(userObjectType, postOfficeBoxesAttribute);
        AddAttributeToObjectType(userObjectType, postalAddressesAttribute);
        AddAttributeToObjectType(userObjectType, postalCodeAttribute);
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

        // create the group object type and attribute mappings
        var groupObjectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.Group, true);
        if (groupObjectType == null)
        {
            groupObjectType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.Group, PluralName = "Groups", BuiltIn = true, Icon = "Groups" };
            AuditHelper.SetCreatedBySystem(groupObjectType);
            objectTypesToCreate.Add(groupObjectType);
            Log.Information("SeedAsync: Preparing MetaverseObjectType Group");
        }

        AddAttributeToObjectType(groupObjectType, accountNameAttribute);
        AddAttributeToObjectType(groupObjectType, commonNameAttribute);
        AddAttributeToObjectType(groupObjectType, descriptionAttribute);
        AddAttributeToObjectType(groupObjectType, displayNameAttribute);
        AddAttributeToObjectType(groupObjectType, distinguishedNameAttribute);
        AddAttributeToObjectType(groupObjectType, emailAttribute);
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
        AddAttributeToObjectType(groupObjectType, groupScopeAttribute);
        AddAttributeToObjectType(groupObjectType, groupTypeAttribute);
        AddAttributeToObjectType(groupObjectType, groupTypeFlagsAttribute);
        AddAttributeToObjectType(groupObjectType, hideFromAddressListsAttribute);
        AddAttributeToObjectType(groupObjectType, infoAttribute);
        AddAttributeToObjectType(groupObjectType, mailNicknameAttribute);
        AddAttributeToObjectType(groupObjectType, managedByAttribute);
        AddAttributeToObjectType(groupObjectType, objectGuidAttribute);
        AddAttributeToObjectType(groupObjectType, objectSidAttribute);
        AddAttributeToObjectType(groupObjectType, ownersAttribute);
        AddAttributeToObjectType(groupObjectType, proxyAddressesAttribute);
        AddAttributeToObjectType(groupObjectType, staticMembersAttribute);
        AddAttributeToObjectType(groupObjectType, statusAttribute);
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

            var item = new PredefinedSearchAttribute();
            item.MetaverseAttribute = displayNameAttribute;
            item.Position = 0;
            usersPredefinedSearch.Attributes.Add(item);
            usersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = jobTitleAttribute, Position = 1 });
            usersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = departmentAttribute, Position = 2 });
            usersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = companyAttribute, Position = 3 });
            usersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = emailAttribute, Position = 4 });
            usersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = statusAttribute, Position = 5 });
                
            AuditHelper.SetCreatedBySystem(usersPredefinedSearch);
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

            peopleUsersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = displayNameAttribute, Position = 0 });
            peopleUsersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = jobTitleAttribute, Position = 1 });
            peopleUsersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = departmentAttribute, Position = 2 });
            peopleUsersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = companyAttribute, Position = 3 });
            peopleUsersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = emailAttribute, Position = 4 });
            peopleUsersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = statusAttribute, Position = 5 });

            peopleUsersPredefinedSearch.CriteriaGroups.Add(new PredefinedSearchCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria = new List<PredefinedSearchCriteria> {
                    new() {
                        ComparisonType = SearchComparisonType.Equals,
                        MetaverseAttribute = typeAttribute,
                        StringValue = "PersonEntity"
                    }
                }
            });

            AuditHelper.SetCreatedBySystem(peopleUsersPredefinedSearch);
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

            servicePrincipleUsersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = displayNameAttribute, Position = 0 });
            servicePrincipleUsersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = accountNameAttribute, Position = 1 });
            servicePrincipleUsersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = emailAttribute, Position = 2 });
            servicePrincipleUsersPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = statusAttribute, Position = 3 });

            servicePrincipleUsersPredefinedSearch.CriteriaGroups.Add(new PredefinedSearchCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria = new List<PredefinedSearchCriteria> {
                    new() {
                        ComparisonType = SearchComparisonType.Equals,
                        MetaverseAttribute = typeAttribute,
                        StringValue = "NonPersonEntity"
                    }
                }
            });

            AuditHelper.SetCreatedBySystem(servicePrincipleUsersPredefinedSearch);
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

            groupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = displayNameAttribute, Position = 0 });
            groupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = groupTypeAttribute, Position = 1 });
            groupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = groupScopeAttribute, Position = 2 });
            groupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = emailAttribute, Position = 3 });
            groupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = statusAttribute, Position = 4 });
                
            AuditHelper.SetCreatedBySystem(groupsPredefinedSearch);
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

            securityGroupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = displayNameAttribute, Position = 0 });
            securityGroupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = groupTypeAttribute, Position = 1 });
            securityGroupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = groupScopeAttribute, Position = 2 });
            securityGroupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = emailAttribute, Position = 3 });
            securityGroupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = statusAttribute, Position = 4 });

            securityGroupsPredefinedSearch.CriteriaGroups.Add(new PredefinedSearchCriteriaGroup {
                Type = SearchGroupType.All,
                Criteria = new List<PredefinedSearchCriteria> {
                    new() {
                        ComparisonType = SearchComparisonType.Equals,
                        MetaverseAttribute = groupTypeAttribute,
                        StringValue = "Security" 
                    } 
                }
            });

            AuditHelper.SetCreatedBySystem(securityGroupsPredefinedSearch);
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

            distributionGroupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = displayNameAttribute, Position = 0 });
            distributionGroupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = groupTypeAttribute, Position = 1 });
            distributionGroupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = groupScopeAttribute, Position = 2 });
            distributionGroupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = emailAttribute, Position = 3 });
            distributionGroupsPredefinedSearch.Attributes.Add(new() { MetaverseAttribute = statusAttribute, Position = 4 });

            distributionGroupsPredefinedSearch.CriteriaGroups.Add(new PredefinedSearchCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria = new List<PredefinedSearchCriteria> {
                    new() {
                        ComparisonType = SearchComparisonType.Equals,
                        MetaverseAttribute = groupTypeAttribute,
                        StringValue = "Distribution"
                    }
                }
            });

            AuditHelper.SetCreatedBySystem(distributionGroupsPredefinedSearch);
            predefinedSearchesToCreate.Add(distributionGroupsPredefinedSearch);
            Log.Information("SeedAsync: Preparing Distribution Groups PredefinedSearch");
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

        #region ExampleDataTemplates
        var template = await PrepareUsersAndGroupsExampleDataTemplateAsync(userObjectType, groupObjectType, exampleDataSetsToCreate, attributesToCreate);
        if (template != null)
        {
            AuditHelper.SetCreatedBySystem(template);
            dataGenerationTemplatesToCreate.Add(template);
        }
        #endregion

        #region Connector Definitions
        var ldapConnector = new LdapConnector();
        var ldapConnectorDefinition = await PrepareConnectorDefinitionAsync(ldapConnector);
        if (ldapConnectorDefinition != null)
            connectorDefinitions.Add(ldapConnectorDefinition);

        var fileConnector = new FileConnector();
        var fileConnectorDefinition = await PrepareConnectorDefinitionAsync(fileConnector);
        if (fileConnectorDefinition != null)
            connectorDefinitions.Add(fileConnectorDefinition);
        #endregion

        // submit all the preparations to the repository for creation. Roles are not seeded here: built-in Roles
        // carry configuration change history, so they are seeded through the audited create path instead
        // (see SeedBuiltInRolesAsync), matching the Temporal Scope Reconciliation schedule precedent.
        await Application.Repository.Seeding.SeedDataAsync(
            attributesToCreate,
            objectTypesToCreate,
            predefinedSearchesToCreate,
            exampleDataSetsToCreate,
            dataGenerationTemplatesToCreate,
            connectorDefinitions);
        stopwatch.Stop();
        Log.Verbose($"SeedAsync: Completed in: {stopwatch.Elapsed}");
    }

    /// <summary>
    /// Seeds the built-in schedules that JIM provides and maintains itself. Currently this is the Temporal Scope
    /// Reconciliation schedule (issue #892), which periodically re-evaluates relative-date scoping for objects
    /// whose scope membership drifts with the clock but whose source data has not changed. Idempotent: it does
    /// nothing if the built-in schedule already exists. Administrators may enable/disable it and change its
    /// interval, but may not rename or delete it (enforced at the API/UI layer). Runs at service startup and
    /// again after a factory reset (the wipe truncates the Schedules table).
    /// </summary>
    internal async Task SeedBuiltInSchedulesAsync()
    {
        var schedules = await Application.Repository.Scheduling.GetAllSchedulesAsync();
        var reconciliationScheduleExists = schedules.Any(s => s.BuiltIn &&
            s.Steps.Any(st => st.StepType == ScheduleStepType.TemporalScopeReconciliation));
        if (reconciliationScheduleExists)
        {
            Log.Verbose("SeedBuiltInSchedulesAsync: Temporal Scope Reconciliation schedule already present; skipping.");
            return;
        }

        var schedule = new Schedule
        {
            Name = "Temporal Scope Reconciliation",
            Description = "Built-in schedule that re-evaluates relative-date scoping for objects whose scope membership " +
                          "changes as time passes (for example a leaver whose end date passes) but whose source data has " +
                          "not changed, so the synchronisation and export hot paths would otherwise skip them.",
            BuiltIn = true,
            IsEnabled = true,
            TriggerType = ScheduleTriggerType.Cron,
            PatternType = SchedulePatternType.Interval,
            IntervalValue = 1,
            IntervalUnit = ScheduleIntervalUnit.Hours,
            DaysOfWeek = "0,1,2,3,4,5,6",
            CronExpression = "0 * * * *",
            CreatedByType = ActivityInitiatorType.System,
            CreatedByName = "System",
            Steps = new List<ScheduleStep>
            {
                new()
                {
                    StepIndex = 0,
                    Name = "Reconcile Temporal Scope",
                    StepType = ScheduleStepType.TemporalScopeReconciliation,
                    ExecutionMode = StepExecutionMode.Sequential,
                    ContinueOnFailure = false,
                    CreatedByType = ActivityInitiatorType.System,
                    CreatedByName = "System"
                }
            }
        };

        // Create through the audited path, not the repository, so the schedule's origin is visible in the
        // portal: a Create Activity attributed to System and a version-1 configuration change snapshot.
        // A repository-direct seed leaves no audit trace, so the change history would start at whichever
        // principal touched the schedule next, misattributing its origin.
        var parentActivityId = await GetOrCreateSeedingActivityAsync();
        await Application.Scheduler.CreateScheduleAsync(schedule, ActivityInitiatorType.System, null, "System",
            changeReason: "Built-in schedule created automatically by JIM.", parentActivityId: parentActivityId);
        Log.Information("SeedBuiltInSchedulesAsync: Created built-in Temporal Scope Reconciliation schedule {ScheduleId} (hourly).", schedule.Id);
    }

    /// <summary>
    /// Seeds the built-in Administrator Role that JIM provides, through the audited create path
    /// (<see cref="SecurityServer.CreateRoleAsync(Role, MetaverseObject?, string?)"/>) so its change history begins
    /// with a System-attributed Create Activity and a version-1 configuration change snapshot, rather than starting
    /// blank the first time an administrator touches its membership. Idempotent: does nothing if the built-in Role
    /// already exists. Runs at every application startup, mirroring <see cref="SeedBuiltInSchedulesAsync"/>.
    /// </summary>
    internal async Task SeedBuiltInRolesAsync()
    {
        var administratorRole = await Application.Security.GetRoleAsync(Constants.BuiltInRoles.Administrator);
        if (administratorRole != null)
        {
            Log.Verbose("SeedBuiltInRolesAsync: Administrator Role already present; skipping.");
            return;
        }

        var role = new Role
        {
            BuiltIn = true,
            Name = Constants.BuiltInRoles.Administrator
        };

        // Create through the audited path, not the repository, so the Role's origin is visible in the portal: a
        // Create Activity attributed to System and a version-1 configuration change snapshot. A repository-direct
        // seed leaves no audit trace, so the change history would start at whichever principal touched the Role
        // next, misattributing its origin.
        var parentActivityId = await GetOrCreateSeedingActivityAsync();
        await Application.Security.CreateRoleAsync(role, changeReason: "Built-in Role created automatically by JIM.", parentActivityId: parentActivityId);
        Log.Information("SeedBuiltInRolesAsync: Created built-in Role {RoleName} (ID: {RoleId}).", role.Name, role.Id);
    }

    /// <summary>
    /// Returns the id of the current seeding pass's parent "System Initialisation" Activity, creating it on
    /// first call. Deliberately lazy: the parent is only created the first time a seed step is actually about to
    /// create something, so a startup where every seed step no-ops never records an Activity at all. Subsequent
    /// calls within the same pass return the same id, so every built-in object created during one startup groups
    /// under a single parent, and each becomes a child via <see cref="Activity.ParentActivityId"/>.
    /// </summary>
    private async Task<Guid> GetOrCreateSeedingActivityAsync()
    {
        if (_seedingActivity != null)
            return _seedingActivity.Id;

        var activity = new JIM.Models.Activities.Activity
        {
            TargetType = ActivityTargetType.SystemInitialisation,
            TargetOperationType = ActivityTargetOperationType.Create,
            TargetName = "Built-in configuration",
            Message = "Applying built-in configuration"
        };
        await Application.Activities.CreateSystemActivityAsync(activity);
        _seedingActivity = activity;
        return activity.Id;
    }

    /// <summary>
    /// Completes the current seeding pass's parent Activity, if one was created (i.e. if at least one built-in
    /// object was actually seeded this startup). A no-op when nothing needed seeding, so a normal restart that
    /// changes nothing records, and touches, no Activity at all. Call once, after every seed step has run.
    /// </summary>
    internal async Task CompleteSeedingActivityAsync()
    {
        if (_seedingActivity == null)
            return;

        _seedingActivity.Message = "Applied built-in configuration";
        await Application.Activities.CompleteActivityAsync(_seedingActivity);
        _seedingActivity = null;
    }

    /// <summary>
    /// Fails the current seeding pass's parent Activity with the given exception, if one was created. A no-op
    /// when nothing had been seeded yet this startup (the failure occurred before any seed step needed to create
    /// the parent).
    /// </summary>
    internal async Task FailSeedingActivityAsync(Exception ex)
    {
        if (_seedingActivity == null)
            return;

        await Application.Activities.FailActivityWithErrorAsync(_seedingActivity, ex);
        _seedingActivity = null;
    }

    /// <summary>
    /// Synchronises built-in connector definitions with the latest settings from the connector code.
    /// This should be called on every application startup to ensure connector settings are up-to-date.
    /// Unlike SeedAsync, this method updates existing connector definitions when their settings change.
    /// </summary>
    internal async Task SyncBuiltInConnectorDefinitionsAsync()
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        Log.Information("SyncBuiltInConnectorDefinitionsAsync: Starting built-in connector definition synchronisation...");

        var connectors = new List<IConnector>
        {
            new LdapConnector(),
            new FileConnector()
        };

        foreach (var connector in connectors)
        {
            await SyncConnectorDefinitionAsync(connector);
        }

        stopwatch.Stop();
        Log.Information($"SyncBuiltInConnectorDefinitionsAsync: Completed in: {stopwatch.Elapsed}");
    }

    /// <summary>
    /// Synchronises rendering hints for built-in metaverse attributes.
    /// This should be called on every application startup to ensure existing deployments
    /// get rendering hints set correctly without requiring a fresh seed.
    /// Uses the repository directly to avoid creating audit Activities for system-level changes.
    /// </summary>
    internal async Task SyncBuiltInAttributeRenderingHintsAsync()
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        Log.Information("SyncBuiltInAttributeRenderingHintsAsync: Starting...");

        var renderingHints = new Dictionary<string, AttributeRenderingHint>
        {
            // Table: large reference MVAs needing columns/search/pagination
            { Constants.BuiltInAttributes.StaticMembers, AttributeRenderingHint.Table },
            { Constants.BuiltInAttributes.Owners, AttributeRenderingHint.Table },

            // ChipSet: short text values that display well as horizontal chips
            { Constants.BuiltInAttributes.OtherTelephones, AttributeRenderingHint.ChipSet },
            { Constants.BuiltInAttributes.OtherMobiles, AttributeRenderingHint.ChipSet },
            { Constants.BuiltInAttributes.OtherIpPhones, AttributeRenderingHint.ChipSet },
            { Constants.BuiltInAttributes.OtherPagers, AttributeRenderingHint.ChipSet },
            { Constants.BuiltInAttributes.OtherFacsimileTelephoneNumbers, AttributeRenderingHint.ChipSet },
            { Constants.BuiltInAttributes.PostOfficeBoxes, AttributeRenderingHint.ChipSet },

            // List: long/variable-length values that need vertical stacking
            { Constants.BuiltInAttributes.ProxyAddresses, AttributeRenderingHint.List },
            { Constants.BuiltInAttributes.AltSecurityIdentities, AttributeRenderingHint.List },
            { Constants.BuiltInAttributes.PostalAddresses, AttributeRenderingHint.List },
            { Constants.BuiltInAttributes.Urls, AttributeRenderingHint.List },
            { Constants.BuiltInAttributes.SidHistory, AttributeRenderingHint.List },
            { Constants.BuiltInAttributes.UserCertificates, AttributeRenderingHint.List },
        };

        var updatedCount = 0;
        foreach (var (name, hint) in renderingHints)
        {
            var attribute = await Application.Metaverse.GetMetaverseAttributeAsync(name, withChangeTracking: true);
            if (attribute != null && attribute.RenderingHint != hint)
            {
                attribute.RenderingHint = hint;
                await Application.Repository.Metaverse.UpdateMetaverseAttributeAsync(attribute);
                updatedCount++;
                Log.Debug("SyncBuiltInAttributeRenderingHintsAsync: Set {Name} to {Hint}", name, hint);
            }
        }

        stopwatch.Stop();
        Log.Information("SyncBuiltInAttributeRenderingHintsAsync: Completed in {Elapsed}. Updated {Count} attributes.",
            stopwatch.Elapsed, updatedCount);
    }

    /// <summary>
    /// Seeds and synchronises service settings from environment variables.
    /// This should be called on every application startup to ensure settings are available.
    /// Read-only settings (from environment) are updated; user-modified settings are preserved.
    /// </summary>
    internal async Task SyncServiceSettingsAsync()
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        Log.Information("SyncServiceSettingsAsync: Starting service settings synchronisation...");

        // SSO Settings (read-only, from environment variables)
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.SsoAuthority,
            DisplayName = "SSO authority",
            Description = "The OIDC authority URL for single sign-on authentication.",
            Category = ServiceSettingCategory.SSO,
            ValueType = ServiceSettingValueType.String,
            DefaultValue = Environment.GetEnvironmentVariable(Constants.Config.SsoAuthority),
            Value = Environment.GetEnvironmentVariable(Constants.Config.SsoAuthority),
            IsReadOnly = true
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.SsoClientId,
            DisplayName = "SSO client ID",
            Description = "The OIDC client identifier for JIM.",
            Category = ServiceSettingCategory.SSO,
            ValueType = ServiceSettingValueType.String,
            DefaultValue = Environment.GetEnvironmentVariable(Constants.Config.SsoClientId),
            Value = Environment.GetEnvironmentVariable(Constants.Config.SsoClientId),
            IsReadOnly = true
        });

        // SSO Secret - encrypt the value before storing
        var ssoSecretValue = Environment.GetEnvironmentVariable(Constants.Config.SsoSecret);
        if (!string.IsNullOrEmpty(ssoSecretValue) && Application.CredentialProtection != null)
        {
            ssoSecretValue = Application.CredentialProtection.Protect(ssoSecretValue);
        }
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.SsoSecret,
            DisplayName = "SSO secret",
            Description = "The OIDC client secret for JIM.",
            Category = ServiceSettingCategory.SSO,
            ValueType = ServiceSettingValueType.StringEncrypted,
            DefaultValue = null, // Never store secrets as defaults
            Value = ssoSecretValue,
            IsReadOnly = true
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.SsoApiScope,
            DisplayName = "SSO API scope",
            Description = "The OIDC API scope required for accessing JIM.",
            Category = ServiceSettingCategory.SSO,
            ValueType = ServiceSettingValueType.String,
            DefaultValue = Environment.GetEnvironmentVariable(Constants.Config.SsoApiScope),
            Value = Environment.GetEnvironmentVariable(Constants.Config.SsoApiScope),
            IsReadOnly = true
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.SsoClaimType,
            DisplayName = "SSO claim type",
            Description = "The claim type used to identify the user in SSO tokens.",
            Category = ServiceSettingCategory.SSO,
            ValueType = ServiceSettingValueType.String,
            DefaultValue = Environment.GetEnvironmentVariable(Constants.Config.SsoClaimType),
            Value = Environment.GetEnvironmentVariable(Constants.Config.SsoClaimType),
            IsReadOnly = true
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.SsoMvAttribute,
            DisplayName = "SSO Metaverse attribute",
            Description = "The Metaverse attribute used to match SSO claims to JIM users.",
            Category = ServiceSettingCategory.SSO,
            ValueType = ServiceSettingValueType.String,
            DefaultValue = Environment.GetEnvironmentVariable(Constants.Config.SsoMvAttribute),
            Value = Environment.GetEnvironmentVariable(Constants.Config.SsoMvAttribute),
            IsReadOnly = true
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.SsoUniqueIdentifierClaimType,
            DisplayName = "SSO unique identifier claim type",
            Description = "The claim type containing the unique identifier for SSO users (e.g., 'sub' or 'oid').",
            Category = ServiceSettingCategory.SSO,
            ValueType = ServiceSettingValueType.String,
            DefaultValue = "sub",
            IsReadOnly = true
        });

        // SSO Settings (configurable)
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.SsoEnableLogOut,
            DisplayName = "SSO enable log-out",
            Description = "When enabled, users can log out of JIM and be redirected to the SSO provider's logout endpoint.",
            Category = ServiceSettingCategory.SSO,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            IsReadOnly = false
        });

        // Synchronisation Settings
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.PartitionValidationMode,
            DisplayName = "Run Profile partition validation",
            Description = "Controls how JIM behaves when a Run Profile is executed for a Connected System that supports partitions but has none selected. 'Error' blocks execution; 'Warning' allows execution but logs a warning.",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Enum,
            DefaultValue = PartitionValidationMode.Error.ToString(),
            EnumTypeName = typeof(PartitionValidationMode).FullName,
            IsReadOnly = false
        });

        // History Settings
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.HistoryRetentionPeriod,
            DisplayName = "History retention period",
            Description = "The duration for which activity and audit history is retained. Format: d.hh:mm:ss (e.g., '90.00:00:00' for 90 days).",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.TimeSpan,
            DefaultValue = "90.00:00:00", // 90 days
            IsReadOnly = false
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ConfigurationChangeRetentionPeriod,
            DisplayName = "Configuration change retention period",
            Description = "The duration for which configuration change history (versioned Connected System, Synchronisation Rule, and Schedule snapshots) is retained. Kept separately from, and typically much longer than, the history retention period. Format: d.hh:mm:ss (e.g., '3650.00:00:00' for ~10 years).",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.TimeSpan,
            DefaultValue = "3650.00:00:00", // ~10 years
            IsReadOnly = false
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.HistoryCleanupBatchSize,
            DisplayName = "History cleanup batch size",
            Description = "Maximum number of records to delete per cleanup batch during housekeeping. Lower values reduce database load but take longer to clean up large volumes. Higher values are faster but may cause temporary performance impact.",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.Integer,
            DefaultValue = "100",
            IsReadOnly = false
        });

        // Change Tracking Settings
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ChangeTrackingCsoChangesEnabled,
            DisplayName = "Track CSO changes",
            Description = "When enabled, change history is recorded for all Connected System Object create/update/delete operations. Disable to improve performance at the expense of audit trail.",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            IsReadOnly = false
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ChangeTrackingMvoChangesEnabled,
            DisplayName = "Track MVO changes",
            Description = "When enabled, change history is recorded for all Metaverse Object create/update/delete operations. Disable to improve performance at the expense of audit trail.",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            IsReadOnly = false
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
            DisplayName = "Track configuration changes",
            Description = "When enabled, a redacted, versioned configuration snapshot is recorded on the Activity for every configuration create/update/delete (Synchronisation Rules, Connected Systems). Disable to stop capturing configuration change history.",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            IsReadOnly = false
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ChangeTrackingSyncOutcomesLevel,
            DisplayName = "Sync outcome tracking level",
            Description = "Controls how much detail is recorded for sync outcome graphs on each Run Profile execution item. " +
                          "None: no outcome tracking (legacy behaviour). Standard: root-level outcomes (enables stat chips). " +
                          "Detailed: full causal chain with nested outcomes (default). " +
                          "Higher levels provide richer audit trails but increase storage usage.",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.Enum,
            EnumTypeName = nameof(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel),
            DefaultValue = nameof(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed),
            IsReadOnly = false
        });

        // Maintenance Settings
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.MaintenanceMode,
            DisplayName = "Maintenance mode",
            Description = "When enabled, JIM enters maintenance mode. Background jobs and synchronisation tasks are paused.",
            Category = ServiceSettingCategory.Maintenance,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "false",
            IsReadOnly = false
        });

        // Synchronisation Settings - Verbose no-change recording
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.VerboseNoChangeRecording,
            DisplayName = "Verbose no-change recording",
            Description = "When enabled, creates detailed Activity execution items for exports where CSO already has current values. Default: disabled for performance.",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "false",
            IsReadOnly = false
        });

        // Synchronisation Settings - Page size
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.SyncPageSize,
            DisplayName = "Sync page size",
            Description = "The number of Connected System Objects to process per database page during sync operations. Larger values improve throughput by reducing database round trips. UI progress updates occur every 100 objects regardless of page size. Recommended range: 200-1000.",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Integer,
            DefaultValue = "500",
            IsReadOnly = false
        });

        // Security Settings
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.CredentialEncryptionEnabled,
            DisplayName = "Credential encryption",
            Description = "When enabled, connector passwords are encrypted at rest using ASP.NET Core Data Protection with AES-256-GCM.",
            Category = ServiceSettingCategory.Security,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            IsReadOnly = false
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.EncryptionKeyPath,
            DisplayName = "Encryption key storage path",
            Description = "The file system path where encryption keys are stored. Set via JIM_ENCRYPTION_KEY_PATH environment variable. If not set, defaults to /data/keys (Docker) or the application data directory.",
            Category = ServiceSettingCategory.Security,
            ValueType = ServiceSettingValueType.String,
            DefaultValue = null,
            Value = Environment.GetEnvironmentVariable(Constants.Config.EncryptionKeyPath),
            IsReadOnly = true
        });

        // UI Settings
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ProgressUpdateInterval,
            DisplayName = "Progress update interval",
            Description = "The interval at which progress updates are reported and polled for in the UI. Affects both the Operations page polling frequency and background task progress reporting. Default: 1 second.",
            Category = ServiceSettingCategory.UI,
            ValueType = ServiceSettingValueType.TimeSpan,
            DefaultValue = "00:00:01",
            IsReadOnly = false
        });

        // Instance Settings
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceName,
            DisplayName = "Service Name",
            Description = "A friendly, editable name for this JIM instance. Appears in the sidebar, browser tab title, and footer so you can tell instances apart.",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.String,
            DefaultValue = null,
            IsReadOnly = false
        });

        await SeedSettingOnceAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceId,
            DisplayName = "Service ID",
            Description = "A stable, immutable identifier generated once when this JIM instance was created. Used by tooling, logs, and telemetry to identify this instance. Cannot be changed.",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.Guid,
            DefaultValue = null,
            IsReadOnly = true
        }, () => Guid.NewGuid().ToString());

        stopwatch.Stop();
        Log.Information($"SyncServiceSettingsAsync: Completed in: {stopwatch.Elapsed}");
    }

    /// <summary>
    /// Seeds a single service setting. Creates if it doesn't exist, updates read-only settings from environment.
    /// </summary>
    private async Task SeedSettingAsync(ServiceSetting setting)
    {
        await Application.ServiceSettings.CreateOrUpdateSettingAsync(setting);
        Log.Verbose($"SeedSettingAsync: Processed setting '{setting.Key}'");
    }

    /// <summary>
    /// Seeds a single service setting exactly once. Creates the setting with a generated value
    /// on first run; on subsequent runs, leaves the existing setting completely untouched.
    /// Use for identifiers that must never be regenerated (e.g. Service ID).
    /// </summary>
    private async Task SeedSettingOnceAsync(ServiceSetting template, Func<string> valueFactory)
    {
        if (await Application.ServiceSettings.SettingExistsAsync(template.Key))
        {
            Log.Verbose("SeedSettingOnceAsync: '{Key}' already exists; preserving existing value.", template.Key);
            return;
        }

        template.Value = valueFactory();
        await Application.ServiceSettings.CreateSettingAsync(template);
        Log.Information("SeedSettingOnceAsync: Generated '{Key}'.", template.Key);
        Log.Verbose("SeedSettingOnceAsync: '{Key}' value is '{Value}'.", template.Key, template.Value);
    }

    /// <summary>
    /// Synchronises a single connector definition with the latest settings from the connector code.
    /// Updates settings if they have changed (e.g., category, description, default values).
    /// </summary>
    private async Task SyncConnectorDefinitionAsync(IConnector connector)
    {
        var connectorCapabilities = (IConnectorCapabilities)connector;
        var connectorSettings = (IConnectorSettings)connector;

        var existingDefinition = await Application.ConnectedSystems.GetConnectorDefinitionAsync(connector.Name, withChangeTracking: true);
        if (existingDefinition == null)
        {
            Log.Debug($"SyncConnectorDefinitionAsync: Connector '{connector.Name}' not found in database, skipping sync (will be created during seeding)");
            return;
        }

        var latestSettings = connectorSettings.GetSettings();
        var hasChanges = false;

        // First, remove any duplicate settings (settings with the same name)
        // This can happen if a previous sync added settings without properly loading existing ones
        var duplicateSettings = existingDefinition.Settings
            .GroupBy(s => s.Name)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Skip(1)) // Keep the first, remove the rest
            .ToList();

        foreach (var duplicate in duplicateSettings)
        {
            existingDefinition.Settings.Remove(duplicate);
            hasChanges = true;
            Log.Information($"SyncConnectorDefinitionAsync: Removed duplicate setting '{duplicate.Name}' from '{connector.Name}'");
        }

        // Update capability flags
        if (existingDefinition.SupportsFullImport != connectorCapabilities.SupportsFullImport ||
            existingDefinition.SupportsDeltaImport != connectorCapabilities.SupportsDeltaImport ||
            existingDefinition.SupportsExport != connectorCapabilities.SupportsExport ||
            existingDefinition.SupportsPartitions != connectorCapabilities.SupportsPartitions ||
            existingDefinition.SupportsPartitionContainers != connectorCapabilities.SupportsPartitionContainers ||
            existingDefinition.SupportsSecondaryExternalId != connectorCapabilities.SupportsSecondaryExternalId ||
            existingDefinition.SupportsUserSelectedExternalId != connectorCapabilities.SupportsUserSelectedExternalId ||
            existingDefinition.SupportsUserSelectedAttributeTypes != connectorCapabilities.SupportsUserSelectedAttributeTypes ||
            existingDefinition.SupportsAutoConfirmExport != connectorCapabilities.SupportsAutoConfirmExport ||
            existingDefinition.SupportsParallelExport != connectorCapabilities.SupportsParallelExport ||
            existingDefinition.SupportsPaging != connectorCapabilities.SupportsPaging ||
            existingDefinition.SupportsFilePaths != connectorCapabilities.SupportsFilePaths)
        {
            existingDefinition.SupportsFullImport = connectorCapabilities.SupportsFullImport;
            existingDefinition.SupportsDeltaImport = connectorCapabilities.SupportsDeltaImport;
            existingDefinition.SupportsExport = connectorCapabilities.SupportsExport;
            existingDefinition.SupportsPartitions = connectorCapabilities.SupportsPartitions;
            existingDefinition.SupportsPartitionContainers = connectorCapabilities.SupportsPartitionContainers;
            existingDefinition.SupportsSecondaryExternalId = connectorCapabilities.SupportsSecondaryExternalId;
            existingDefinition.SupportsUserSelectedExternalId = connectorCapabilities.SupportsUserSelectedExternalId;
            existingDefinition.SupportsUserSelectedAttributeTypes = connectorCapabilities.SupportsUserSelectedAttributeTypes;
            existingDefinition.SupportsAutoConfirmExport = connectorCapabilities.SupportsAutoConfirmExport;
            existingDefinition.SupportsParallelExport = connectorCapabilities.SupportsParallelExport;
            existingDefinition.SupportsPaging = connectorCapabilities.SupportsPaging;
            existingDefinition.SupportsFilePaths = connectorCapabilities.SupportsFilePaths;
            hasChanges = true;
            Log.Information($"SyncConnectorDefinitionAsync: Updated capability flags for '{connector.Name}'");
        }

        // Sync settings - update existing and add new ones
        foreach (var latestSetting in latestSettings)
        {
            var existingSetting = existingDefinition.Settings.FirstOrDefault(s => s.Name == latestSetting.Name);
            if (existingSetting == null)
            {
                // Add new setting
                existingDefinition.Settings.Add(new ConnectorDefinitionSetting
                {
                    Category = latestSetting.Category,
                    DefaultCheckboxValue = latestSetting.DefaultCheckboxValue,
                    DefaultStringValue = latestSetting.DefaultStringValue,
                    DefaultIntValue = latestSetting.DefaultIntValue,
                    Description = latestSetting.Description,
                    DropDownValues = latestSetting.DropDownValues,
                    Name = latestSetting.Name,
                    Type = latestSetting.Type,
                    Required = latestSetting.Required,
                    RequiredGroup = latestSetting.RequiredGroup,
                    RequiredGroupCardinality = latestSetting.RequiredGroupCardinality,
                    RequiredWhenSetting = latestSetting.RequiredWhenSetting,
                    RequiredWhenValue = latestSetting.RequiredWhenValue
                });
                hasChanges = true;
                Log.Information($"SyncConnectorDefinitionAsync: Added new setting '{latestSetting.Name}' for '{connector.Name}'");
            }
            else
            {
                // Update existing setting if changed
                if (existingSetting.Category != latestSetting.Category ||
                    existingSetting.Description != latestSetting.Description ||
                    existingSetting.Type != latestSetting.Type ||
                    existingSetting.Required != latestSetting.Required ||
                    existingSetting.RequiredGroup != latestSetting.RequiredGroup ||
                    existingSetting.RequiredGroupCardinality != latestSetting.RequiredGroupCardinality ||
                    existingSetting.RequiredWhenSetting != latestSetting.RequiredWhenSetting ||
                    existingSetting.RequiredWhenValue != latestSetting.RequiredWhenValue ||
                    existingSetting.DefaultCheckboxValue != latestSetting.DefaultCheckboxValue ||
                    existingSetting.DefaultStringValue != latestSetting.DefaultStringValue ||
                    existingSetting.DefaultIntValue != latestSetting.DefaultIntValue)
                {
                    existingSetting.Category = latestSetting.Category;
                    existingSetting.Description = latestSetting.Description;
                    existingSetting.Type = latestSetting.Type;
                    existingSetting.Required = latestSetting.Required;
                    existingSetting.RequiredGroup = latestSetting.RequiredGroup;
                    existingSetting.RequiredGroupCardinality = latestSetting.RequiredGroupCardinality;
                    existingSetting.RequiredWhenSetting = latestSetting.RequiredWhenSetting;
                    existingSetting.RequiredWhenValue = latestSetting.RequiredWhenValue;
                    existingSetting.DefaultCheckboxValue = latestSetting.DefaultCheckboxValue;
                    existingSetting.DefaultStringValue = latestSetting.DefaultStringValue;
                    existingSetting.DefaultIntValue = latestSetting.DefaultIntValue;
                    hasChanges = true;
                    Log.Information($"SyncConnectorDefinitionAsync: Updated setting '{latestSetting.Name}' for '{connector.Name}'");
                }
            }
        }

        // Remove settings that no longer exist in the connector
        var settingsToRemove = existingDefinition.Settings
            .Where(s => !latestSettings.Any(ls => ls.Name == s.Name))
            .ToList();

        foreach (var settingToRemove in settingsToRemove)
        {
            existingDefinition.Settings.Remove(settingToRemove);
            hasChanges = true;
            Log.Information($"SyncConnectorDefinitionAsync: Removed obsolete setting '{settingToRemove.Name}' from '{connector.Name}'");
        }

        if (hasChanges)
        {
            await Application.ConnectedSystems.UpdateConnectorDefinitionAsync(existingDefinition);
            Log.Information($"SyncConnectorDefinitionAsync: Saved changes for '{connector.Name}'");
        }
        else
        {
            Log.Debug($"SyncConnectorDefinitionAsync: No changes detected for '{connector.Name}'");
        }
    }

    #region private methods
    private async Task<MetaverseAttribute> GetOrPrepareMetaverseAttributeAsync(string name, AttributePlurality attributePlurality, AttributeDataType attributeDataType, List<MetaverseAttribute> attributeList, AttributeRenderingHint renderingHint = AttributeRenderingHint.Default)
    {
        var attribute = await Application.Metaverse.GetMetaverseAttributeAsync(name);
        if (attribute == null)
        {
            attribute = new MetaverseAttribute
            {
                Name = name,
                AttributePlurality = attributePlurality,
                Type = attributeDataType,
                BuiltIn = true,
                RenderingHint = renderingHint
            };
            AuditHelper.SetCreatedBySystem(attribute);
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
        var exampleDataSet = await Application.Repository.ExampleData.GetExampleDataSetAsync(name, culture, withChangeTracking: true);
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

        // check if the dataset has all the necessary values
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

        return changes ? exampleDataSet : null;
    }

    private async Task<ExampleDataTemplate?> PrepareUsersAndGroupsExampleDataTemplateAsync(MetaverseObjectType userType, MetaverseObjectType groupType, List<ExampleDataSet> dataSets, List<MetaverseAttribute> metaverseAttributes)
    {
        var templateName = "Users & Groups";

        // does a template exist already?
        var template = await Application.Repository.ExampleData.GetTemplateAsync(templateName);
        if (template != null)
            return null;

        template = new ExampleDataTemplate { Name = templateName, BuiltIn = true };
        AddUsersToExampleDataTemplate(template, userType, dataSets, metaverseAttributes);
        AddGroupsToExampleDataTemplate(template, groupType, userType, dataSets, metaverseAttributes);
        return template;
    }

    /// <summary>
    /// Ensures the built-in "Users &amp; Groups" example data template exists and is complete, (re)creating it from the
    /// same definition used at first-run seeding when it is missing or has lost its attributes. A factory reset's
    /// TRUNCATE ... CASCADE removes the template's attributes as collateral (they share a foreign-key graph with the
    /// Connected System schema), leaving an attribute-less shell that ordinary seeding does not repair (it skips an
    /// existing template). This restores the out-of-box template so it survives a reset. Idempotent: a present, complete
    /// template is left untouched, so it is safe to call on every startup and after a reset.
    /// </summary>
    internal async Task EnsureBuiltInExampleDataTemplateAsync()
    {
        const string templateName = "Users & Groups";

        var existing = await Application.Repository.ExampleData.GetTemplateAsync(templateName);
        if (existing != null && existing.ObjectTypes.Any(ot => ot.TemplateAttributes.Count > 0))
            return; // present and complete: the common case, kept cheap.

        var userType = await Application.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.User, includeChildObjects: false);
        var groupType = await Application.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.Group, includeChildObjects: false);
        if (userType == null || groupType == null)
        {
            Log.Warning("EnsureBuiltInExampleDataTemplateAsync: built-in User/Group Metaverse Object Types not found; cannot restore the example data template.");
            return;
        }

        // remove the incomplete shell (if any) so the template is recreated whole.
        if (existing != null)
            await Application.Repository.ExampleData.DeleteTemplateAsync(existing.Id);

        var metaverseAttributes = (await Application.Metaverse.GetMetaverseAttributesAsync())?.ToList() ?? new List<MetaverseAttribute>();
        var dataSets = await Application.ExampleData.GetExampleDataSetsAsync();

        var template = new ExampleDataTemplate { Name = templateName, BuiltIn = true };
        AddUsersToExampleDataTemplate(template, userType, dataSets, metaverseAttributes);
        AddGroupsToExampleDataTemplate(template, groupType, userType, dataSets, metaverseAttributes);
        await Application.Repository.ExampleData.CreateTemplateGraphAsync(template);

        Log.Information("EnsureBuiltInExampleDataTemplateAsync: (re)created the built-in '{TemplateName}' example data template (was {State}).",
            templateName, existing == null ? "missing" : "an incomplete shell");
    }

    /// <summary>
    /// Prepare the built-in connectors for seeding.
    /// </summary>
    private async Task<ConnectorDefinition?> PrepareConnectorDefinitionAsync(IConnector connector)
    {
        var connectorCapabilities = (IConnectorCapabilities)connector ?? throw new ArgumentException("connector does not implement IConnectorCapabilities");
        var connectorSettings = (IConnectorSettings)connector ?? throw new ArgumentException("connector does not implement IConnectorSettings");
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
            SupportsPartitionContainers = connectorCapabilities.SupportsPartitionContainers,
            SupportsSecondaryExternalId = connectorCapabilities.SupportsSecondaryExternalId,
            SupportsUserSelectedExternalId = connectorCapabilities.SupportsUserSelectedExternalId,
            SupportsUserSelectedAttributeTypes = connectorCapabilities.SupportsUserSelectedAttributeTypes,
            SupportsAutoConfirmExport = connectorCapabilities.SupportsAutoConfirmExport,
            SupportsParallelExport = connectorCapabilities.SupportsParallelExport,
            SupportsPaging = connectorCapabilities.SupportsPaging,
            SupportsFilePaths = connectorCapabilities.SupportsFilePaths
        };

        Application.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(connectorSettings, connectorDefinition);
        return connectorDefinition;
    }

    private static void AddUsersToExampleDataTemplate(ExampleDataTemplate template, MetaverseObjectType userType, List<ExampleDataSet> dataSets, List<MetaverseAttribute> metaverseAttributes)
    {
        var userExampleDataObjectType = new ExampleDataObjectType
        {
            MetaverseObjectType = userType,
            ObjectsToCreate = 10000
        };
        template.ObjectTypes.Add(userExampleDataObjectType);            

        // do we have all the attribute definitions?
        var firstnamesMaleDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.FirstnamesMale);
        var firstnamesFemaleDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.FirstnamesFemale);
        var lastnamesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Lastnames);
        var companiesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Companies);
        var departmentsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Departments);
        var teamsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Teams);
        var jobTitlesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.JobTitles);
        var userStatusDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.UserStatuses);

        var firstnameAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.FirstName);
        if (firstnameAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.FirstName),
                ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = firstnamesMaleDataSet, Order = 0 }, new ExampleDataSetInstance { ExampleDataSet = firstnamesFemaleDataSet, Order = 1 } },
                PopulatedValuesPercentage = 100
            });
        }

        var lastnameAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.LastName);
        if (lastnameAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.LastName),
                ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = lastnamesDataSet } },
                PopulatedValuesPercentage = 100
            });
        }

        var displayNameAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.DisplayName);
        if (displayNameAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.DisplayName),
                PopulatedValuesPercentage = 100,
                Pattern = "{First Name} {Last Name}"
            });
        }

        var emailAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Email);
        if (emailAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Email),
                PopulatedValuesPercentage = 100,
                Pattern = "{First Name}.{Last Name}[UniqueInt]@panoply.local"
            });
        }

        var employeeIdAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.EmployeeId);
        if (employeeIdAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.EmployeeId),
                PopulatedValuesPercentage = 100,
                MinNumber = 100001,
                SequentialNumbers = true
            });
        }

        var companyAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Company);
        if (companyAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Company),
                ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = companiesDataSet } },
                PopulatedValuesPercentage = 100
            });
        }

        var departmentAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Department);
        if (departmentAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Department),
                ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = departmentsDataSet } },
                PopulatedValuesPercentage = 100
            });
        }

        var teamAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Team);
        if (teamAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Team),
                ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = teamsDataSet } },
                PopulatedValuesPercentage = 76
            });
        }

        var typeAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Type);
        if (typeAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Type),
                Pattern = "PersonEntity",
                PopulatedValuesPercentage = 100
            });
        }

        var jobTitleAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.JobTitle);
        if (jobTitleAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.JobTitle),
                ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance { ExampleDataSet = jobTitlesDataSet } },
                PopulatedValuesPercentage = 90
            });
        }

        var employeeStartDateAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.EmployeeStartDate);
        if (employeeStartDateAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.EmployeeStartDate),
                MinDate = DateTime.UtcNow.AddYears(-20),
                MaxDate = DateTime.UtcNow.AddMonths(3),
                PopulatedValuesPercentage = 75
            });
        }

        var employeeEndDateAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.EmployeeEndDate);
        if (employeeEndDateAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.EmployeeEndDate),
                MinDate = DateTime.UtcNow.AddMonths(-11),
                MaxDate = DateTime.UtcNow.AddYears(1),
                PopulatedValuesPercentage = 10
            });
        }

        var objectGuidAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.ObjectGuid);
        if (objectGuidAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.ObjectGuid),
                PopulatedValuesPercentage = 100
            });
        }

        var managerAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Manager);
        if (managerAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Manager),
                ManagerDepthPercentage = 25
            });
        }

        var pronounsTemplateAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Pronouns);
        if (pronounsTemplateAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Pronouns),
                WeightedStringValues = new List<ExampleDataTemplateAttributeWeightedValue>
                {
                    new() { Value = "he/him", Weight = 0.35f },
                    new() { Value = "she/her", Weight = 0.35f },
                    new() { Value = "they/them", Weight = 0.20f },
                    new() { Value = "he/they", Weight = 0.05f },
                    new() { Value = "she/they", Weight = 0.05f }
                },
                PopulatedValuesPercentage = 25
            });
        }

        var statusAttribute = userExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Status);
        if (statusAttribute == null)
        {
            userExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Status),
                WeightedStringValues = new List<ExampleDataTemplateAttributeWeightedValue>
                {
                    new() { Value = "Active", Weight = 0.8f },
                    new() { Value = "Suspended", Weight = 0.02f },
                    new() { Value = "Sabbatical", Weight = 0.03f },
                    new() { Value = "Seconded", Weight = 0.03f },
                    new() { Value = "Maternity", Weight = 0.03f },
                    new() { Value = "Paternity", Weight = 0.03f },
                    new() { Value = "Leaving", Weight = 0.03f },
                    new() { Value = "Leaver", Weight = 0.03f }
                },
                PopulatedValuesPercentage = 100
            });
        }
    }

    private static void AddGroupsToExampleDataTemplate(
        ExampleDataTemplate template, 
        MetaverseObjectType groupType, 
        MetaverseObjectType userType, 
        IReadOnlyCollection<ExampleDataSet> dataSets, 
        IReadOnlyCollection<MetaverseAttribute> metaverseAttributes)
    {
        var groupExampleDataObjectType = new ExampleDataObjectType
        {
            MetaverseObjectType = groupType,
            ObjectsToCreate = 500
        };
        template.ObjectTypes.Add(groupExampleDataObjectType);

        // do we have all the attribute definitions?
        var adjectivesDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Adjectives);
        var coloursDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Colours);
        var wordsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.Words);
        var groupEndingsDataSet = dataSets.Single(q => q.Name == Constants.BuiltInExampleDataSets.GroupNameEndings);

        var displayNameAttribute = groupExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.DisplayName);
        if (displayNameAttribute == null)
        {
            groupExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.DisplayName),
                ExampleDataSetInstances = new List<ExampleDataSetInstance> { 
                    new() { ExampleDataSet = adjectivesDataSet, Order = 0 }, 
                    new() { ExampleDataSet = coloursDataSet, Order = 1 }, 
                    new() { ExampleDataSet = wordsDataSet, Order = 2 }, 
                    new() { ExampleDataSet = groupEndingsDataSet, Order = 3 } },
                PopulatedValuesPercentage = 100,
                Pattern = "{0} {1} {2} {3}"
            });
        }

        var groupTypeAttribute = groupExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.GroupType);
        if (groupTypeAttribute == null)
        {
            groupExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.GroupType),
                WeightedStringValues = new List<ExampleDataTemplateAttributeWeightedValue>
                {
                    new() { Value = "Security", Weight = 0.6f },
                    new() { Value = "Distribution", Weight = 0.4f },
                },
                PopulatedValuesPercentage = 100
            });
        }

        var emailAttribute = groupExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Email);
        if (emailAttribute == null)
        {
            groupExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Email),
                AttributeDependency = new ExampleDataTemplateAttributeDependency { 
                    MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.GroupType),
                    ComparisonType = ComparisonType.Equals,
                    StringValue = "Distribution"
                },
                PopulatedValuesPercentage = 100,
                Pattern = "distro-[UniqueInt]@panoply.local"
            });
        }

        var groupScopeAttribute = groupExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.GroupScope);
        if (groupScopeAttribute == null)
        {
            groupExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.GroupScope),
                Pattern = "Universal",
                PopulatedValuesPercentage = 100
            });
        }

        var infoAttribute = groupExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Info);
        if (infoAttribute == null)
        {
            groupExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Info),
                Pattern = "This group was created by the JIM data generation feature.",
                PopulatedValuesPercentage = 100
            });
        }

        var staticMembersAttribute = groupExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.StaticMembers);
        if (staticMembersAttribute == null)
        {
            groupExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.StaticMembers),
                ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { userType },
                MvaRefMinAssignments = 5,
                MvaRefMaxAssignments = 200,
                PopulatedValuesPercentage = 100
            });
        }

        var ownersAttribute = groupExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Owners);
        if (ownersAttribute == null)
        {
            groupExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Owners),
                ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { userType },
                MvaRefMinAssignments = 0,
                MvaRefMaxAssignments = 5,
                PopulatedValuesPercentage = 75
            });
        }

        var managedByAttribute = groupExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.ManagedBy);
        if (managedByAttribute == null)
        {
            groupExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.ManagedBy),
                ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { userType },
                PopulatedValuesPercentage = 75
            });
        }

        var statusAttribute = groupExampleDataObjectType.TemplateAttributes.SingleOrDefault(q => q.MetaverseAttribute != null && q.MetaverseAttribute.Name == Constants.BuiltInAttributes.Status);
        if (statusAttribute == null)
        {
            groupExampleDataObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = metaverseAttributes.Single(q => q.Name == Constants.BuiltInAttributes.Status),
                WeightedStringValues = new List<ExampleDataTemplateAttributeWeightedValue>
                {
                    new() { Value = "Active", Weight = 0.9f },
                    new() { Value = "Retiring", Weight = 0.05f },
                    new() { Value = "Retired", Weight = 0.05f },
                },
                PopulatedValuesPercentage = 100
            });
        }
    }
    #endregion
}