// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Connectors.SCIM;
using JIM.Connectors.Sql;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Interfaces;
using JIM.Models.Scheduling;
using JIM.Models.Search;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Application.Utilities;
using JIM.Utilities;
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

    /// <summary>
    /// Converges the built-in configuration a JIM instance needs to run towards what this release ships: the
    /// Metaverse schema, the Predefined Searches, and the Example Data Sets and Template. Runs on every startup and
    /// again after a factory reset, creating whatever is absent and leaving everything else alone.
    /// <para>
    /// Every step is therefore idempotent: check-then-create against the persisted state, never assuming the
    /// database is empty. Several steps once assumed it was, and a retry against a partially-seeded database
    /// crash-looped on every subsequent start (issue #1287). Anything already persisted is left alone, so no step
    /// records a second Create Activity for an object it did not create.
    /// </para>
    /// <para>
    /// This pass used to stop the moment ServiceSettings existed, on the reasoning that seeding had already
    /// happened. Once every step became check-then-create that guard protected nothing and only prevented
    /// convergence: a built-in Metaverse Object Type, Predefined Search or Example Data Set introduced in a later
    /// release reached brand-new deployments only, and a new built-in Object Type additionally crashed worker
    /// startup on existing ones, because <see cref="SyncBuiltInMetaverseSchemaAsync"/> throws when the catalogue
    /// names an Object Type it cannot find (issue #916). ServiceSettings is still created last and in the same
    /// transaction as the rest of the batch, so a crash part way through a first seed still leaves the database
    /// unseeded.
    /// </para>
    /// </summary>
    internal async Task SeedAsync()
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        // get attributes, if they don't exist, prepare object in list for bulk submission via seeding method
        // create object types as needed
        // if attributes don't exist on type, prepare type attributes and submit in bulk via seeding method

        // These two hold every built-in Metaverse Attribute and Example Data Set, whether it was already persisted
        // or is being created this pass; the create batches are derived from them below. Holding all of them is what
        // makes a retry after a partial seed work: the built-in Example Data Template is built from these lists, and
        // a list of only this pass's creations is empty on a retry, so the template could not resolve anything.
        var allAttributes = new List<MetaverseAttribute>();
        var allExampleDataSets = new List<ExampleDataSet>();
        var objectTypesToCreate = new List<MetaverseObjectType>();
        var predefinedSearchesToCreate = new List<PredefinedSearch>();
        var dataGenerationTemplatesToCreate = new List<ExampleDataTemplate>();

        #region MetaverseAttributes
        // common attributes
        var accountNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.AccountName, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var descriptionAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Description, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var displayNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.DisplayName, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var distinguishedNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.DistinguishedName, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var emailAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Email, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute1 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute1, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute10 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute10, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute11 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute11, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute12 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute12, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute13 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute13, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute14 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute14, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute15 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute15, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute2 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute2, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute3 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute3, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute4 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute4, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute5 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute5, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute6 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute6, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute7 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute7, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute8 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute8, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var extensionAttribute1Attribute9 = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ExtensionAttribute9, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var hideFromAddressListsAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HideFromAddressLists, AttributePlurality.SingleValued, AttributeDataType.Boolean, allAttributes);
        var infoAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Info, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var mailNicknameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.MailNickname, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var objectGuidAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ObjectGuid, AttributePlurality.SingleValued, AttributeDataType.Guid, allAttributes);
        var objectSidAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ObjectSid, AttributePlurality.SingleValued, AttributeDataType.Binary, allAttributes);
        var typeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Type, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);

        // user-specific attributes
        var accountExpiresAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.AccountExpires, AttributePlurality.SingleValued, AttributeDataType.DateTime, allAttributes);
        var altSecurityIdentitiesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.AltSecurityIdentities, AttributePlurality.MultiValued, AttributeDataType.Text, allAttributes, AttributeRenderingHint.List);
        var commonNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.CommonName, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var companyAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Company, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var countryAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Country, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var countryCodeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.CountryCode, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var departmentAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Department, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var employeeEndDateAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeEndDate, AttributePlurality.SingleValued, AttributeDataType.DateTime, allAttributes);
        var employeeIdAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeId, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var employeeNumberAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeNumber, AttributePlurality.SingleValued, AttributeDataType.Number, allAttributes);
        var employeeStartDateAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeStartDate, AttributePlurality.SingleValued, AttributeDataType.DateTime, allAttributes);
        var employeeTypeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.EmployeeType, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var facsimileTelephoneNumberAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.FacsimileTelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var firstNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.FirstName, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var homeDirectoryAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HomeDirectory, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var homeDriveAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HomeDrive, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var homePhoneAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.HomePhone, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var identityAssuranceLevelAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.IdentityAssuranceLevel, AttributePlurality.SingleValued, AttributeDataType.Number, allAttributes);
        var ipPhoneAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.IpPhone, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var jobTitleAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.JobTitle, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var lastNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.LastName, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var localityAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Locality, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var managerAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Manager, AttributePlurality.SingleValued, AttributeDataType.Reference, allAttributes);
        var mobileNumberAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.MobileNumber, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var objectIdentifierAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ObjectIdentifier, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var subjectIdentifierAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.SubjectIdentifier, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var officeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Office, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var organisationAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Organisation, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var otherFacsimileTelephoneNumbersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherFacsimileTelephoneNumbers, AttributePlurality.MultiValued, AttributeDataType.Text, allAttributes, AttributeRenderingHint.ChipSet);
        var otherIpPhonesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherIpPhones, AttributePlurality.MultiValued, AttributeDataType.Text, allAttributes, AttributeRenderingHint.ChipSet);
        var otherMobilesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherMobiles, AttributePlurality.MultiValued, AttributeDataType.Text, allAttributes, AttributeRenderingHint.ChipSet);
        var otherPagersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherPagers, AttributePlurality.MultiValued, AttributeDataType.Text, allAttributes, AttributeRenderingHint.ChipSet);
        var otherTelephonesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.OtherTelephones, AttributePlurality.MultiValued, AttributeDataType.Text, allAttributes, AttributeRenderingHint.ChipSet);
        var pagerAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Pager, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var photoAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Photo, AttributePlurality.SingleValued, AttributeDataType.Binary, allAttributes);
        var physicalDeliveryOfficeNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PhysicalDeliveryOfficeName, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var postOfficeBoxesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PostOfficeBoxes, AttributePlurality.MultiValued, AttributeDataType.Text, allAttributes, AttributeRenderingHint.ChipSet);
        var postalAddressesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PostalAddresses, AttributePlurality.MultiValued, AttributeDataType.Text, allAttributes, AttributeRenderingHint.List);
        var postalCodeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.PostalCode, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var pronounsAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Pronouns, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var proxyAddressesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ProxyAddresses, AttributePlurality.MultiValued, AttributeDataType.Text, allAttributes, AttributeRenderingHint.List);
        var scriptPathAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ScriptPath, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var sidHistoryAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.SidHistory, AttributePlurality.MultiValued, AttributeDataType.Binary, allAttributes, AttributeRenderingHint.List);
        var stateOrProvinceAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.StateOrProvince, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var statusAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Status, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var streetAddressAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.StreetAddress, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var teamAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Team, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var telephoneNumberAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.TelephoneNumber, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var urlsAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Urls, AttributePlurality.MultiValued, AttributeDataType.Text, allAttributes, AttributeRenderingHint.List);
        var userAccountControlAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserAccountControl, AttributePlurality.SingleValued, AttributeDataType.Number, allAttributes);
        var userCertificatesAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserCertificates, AttributePlurality.MultiValued, AttributeDataType.Binary, allAttributes, AttributeRenderingHint.List);
        var userPrincipalNameAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserPrincipalName, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var userSharedFolderAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.UserSharedFolder, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var webPageAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.WebPage, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);

        // group-specific attributes
        var groupScopeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.GroupScope, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var groupTypeAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.GroupType, AttributePlurality.SingleValued, AttributeDataType.Text, allAttributes);
        var groupTypeFlagsAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.GroupTypeFlags, AttributePlurality.SingleValued, AttributeDataType.Number, allAttributes);
        var managedByAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.ManagedBy, AttributePlurality.SingleValued, AttributeDataType.Reference, allAttributes);
        var ownersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.Owners, AttributePlurality.MultiValued, AttributeDataType.Reference, allAttributes, AttributeRenderingHint.Table);
        var staticMembersAttribute = await GetOrPrepareMetaverseAttributeAsync(Constants.BuiltInAttributes.StaticMembers, AttributePlurality.MultiValued, AttributeDataType.Reference, allAttributes, AttributeRenderingHint.Table);
        #endregion

        #region MetaverseObjectTypes
        // prepare the user object type and attribute mappings
        var userObjectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.User, true, withChangeTracking: true);
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
        var groupObjectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.Group, true, withChangeTracking: true);
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

        var securityGroupsPredefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync("security-groups");
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

        var distributionGroupsPredefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync("distribution-groups");
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
        foreach (var (name, culture, resource) in BuiltInExampleDataSets())
            await GetOrPrepareExampleDataSetAsync(name, culture, resource, allExampleDataSets);
        #endregion

        #region ExampleDataTemplates
        var template = await PrepareUsersAndGroupsExampleDataTemplateAsync(userObjectType, groupObjectType, allExampleDataSets, allAttributes);
        if (template != null)
        {
            AuditHelper.SetCreatedBySystem(template);
            dataGenerationTemplatesToCreate.Add(template);
        }
        #endregion

        // Only the objects that do not exist yet are submitted for creation; an unsaved object is the one without a
        // database id. Handing an already-persisted object to the create batch inserts it with its existing primary
        // key, which is the duplicate-key crash that made a retry of a partial seed impossible (issue #1287). Values
        // added to an already-persisted Example Data Set are written by the same transaction: those sets are loaded
        // change-tracked, so their modifications flush with this batch's save.
        var attributesToCreate = allAttributes.Where(q => q.Id == 0).ToList();
        var exampleDataSetsToCreate = allExampleDataSets.Where(q => q.Id == 0).ToList();

        // submit all the preparations to the repository for creation. Roles are not seeded here: built-in Roles
        // carry configuration change history, so they are seeded through the audited create path instead
        // (see SeedBuiltInRolesAsync), matching the Temporal Scope Reconciliation schedule precedent. Built-in
        // Connector Definitions are not seeded here either: they are created by SyncBuiltInConnectorDefinitionsAsync,
        // which runs on every start, so a Connector added in a later release reaches upgraded deployments too.
        await Application.Repository.Seeding.SeedDataAsync(
            attributesToCreate,
            objectTypesToCreate,
            predefinedSearchesToCreate,
            exampleDataSetsToCreate,
            dataGenerationTemplatesToCreate);

        // Record a System-attributed Create Activity and version-1 baseline for each built-in Metaverse Object Type and
        // Metaverse Attribute created this pass, grouped under the seeding pass's parent Activity. Like the Predefined
        // Searches below, the schema is persisted in one cross-referencing batch (attributes bind to object types), so
        // the baseline is recorded after the batch rather than by re-routing each object through an individual audited
        // create. Baseline the create batches, never the full lists: a retry finds everything already persisted and
        // must re-baseline nothing, or every restart would record a second Create Activity for the same object.
        // Object types are recorded before attributes so the higher-level schema entities lead the children list.
        if (objectTypesToCreate.Count > 0)
        {
            var parentActivityId = await GetOrCreateSeedingActivityAsync();
            foreach (var objectType in objectTypesToCreate)
                await Application.Metaverse.RecordSeededMetaverseObjectTypeBaselineAsync(objectType.Id, objectType.Name, parentActivityId);
        }

        if (attributesToCreate.Count > 0)
        {
            var parentActivityId = await GetOrCreateSeedingActivityAsync();
            foreach (var attribute in attributesToCreate)
                await Application.Metaverse.RecordSeededMetaverseAttributeBaselineAsync(attribute.Id, attribute.Name, parentActivityId);
        }

        // Record a System-attributed Create Activity and version-1 baseline snapshot for each built-in Predefined
        // Search created above, grouped under the seeding pass's parent Activity, so their origin is visible in the
        // change history and under System Initialisation (matching the built-in Role and Schedule). The list holds
        // only searches created in this pass (SeedDataAsync populated their ids), so a restart where they already
        // exist records nothing and creates no parent Activity.
        if (predefinedSearchesToCreate.Count > 0)
        {
            var parentActivityId = await GetOrCreateSeedingActivityAsync();
            foreach (var predefinedSearch in predefinedSearchesToCreate)
                await Application.Search.RecordSeededPredefinedSearchBaselineAsync(predefinedSearch.Id, predefinedSearch.Name, parentActivityId);
        }

        // Record baselines for the built-in Example Data Sets and the built-in Example Data Template created this pass.
        // Both are batch-seeded (like the Predefined Searches), so their baseline is recorded after the batch persists;
        // the create batch holds only the sets that did not already exist, and
        // PrepareUsersAndGroupsExampleDataTemplateAsync returns null when the template does, so a restart re-baselines
        // nothing.
        if (exampleDataSetsToCreate.Count > 0)
        {
            var parentActivityId = await GetOrCreateSeedingActivityAsync();
            foreach (var exampleDataSet in exampleDataSetsToCreate)
                await Application.ExampleData.RecordSeededExampleDataSetBaselineAsync(exampleDataSet.Id, exampleDataSet.Name, parentActivityId);
        }

        if (dataGenerationTemplatesToCreate.Count > 0)
        {
            var parentActivityId = await GetOrCreateSeedingActivityAsync();
            foreach (var exampleDataTemplate in dataGenerationTemplatesToCreate)
                await Application.ExampleData.RecordSeededExampleDataTemplateBaselineAsync(exampleDataTemplate.Id, exampleDataTemplate.Name, parentActivityId);
        }

        stopwatch.Stop();
        Log.Verbose($"SeedAsync: Completed in: {stopwatch.Elapsed}");
    }

    /// <summary>
    /// The declarative catalogue of built-in Schedules JIM ships and maintains itself. Each entry is matched
    /// against the database by <see cref="Schedule.Name"/>, which is a safe identity because built-in Schedules
    /// cannot be renamed or deleted (enforced at the API/UI layer, and by SchedulerServer.DeleteScheduleAsync);
    /// administrators may only enable, disable and re-time them.
    /// <para>
    /// A catalogue rather than a hardcoded check because the pass that seeds these once asked "does any built-in
    /// Schedule carry a Temporal Scope Reconciliation step?" and returned if one did, so a second built-in Schedule
    /// could never have reached an existing deployment (issue #916). Adding an entry here is all a future release
    /// needs to do; convergence brings it to deployments that already exist.
    /// </para>
    /// </summary>
    internal static IEnumerable<Schedule> BuiltInSchedules()
    {
        // Temporal Scope Reconciliation (issue #892): periodically re-evaluates relative-date scoping for objects
        // whose scope membership drifts with the clock but whose source data has not changed, so the
        // synchronisation and export hot paths do not skip them.
        yield return new Schedule
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
    }

    /// <summary>
    /// Converges the database towards the built-in Schedule catalogue, creating any entry it does not hold.
    /// Runs at service startup and again after a factory reset (the wipe truncates the Schedules table).
    /// </summary>
    internal async Task SeedBuiltInSchedulesAsync()
    {
        var existingNames = (await Application.Repository.Scheduling.GetAllSchedulesAsync())
            .Where(s => s.BuiltIn)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var schedule in BuiltInSchedules().Where(s => !existingNames.Contains(s.Name)))
        {
            // Create through the audited path, not the repository, so the schedule's origin is visible in the
            // portal: a Create Activity attributed to System and a version-1 configuration change snapshot.
            // A repository-direct seed leaves no audit trace, so the change history would start at whichever
            // principal touched the schedule next, misattributing its origin.
            var parentActivityId = await GetOrCreateSeedingActivityAsync();
            await Application.Scheduler.CreateScheduleAsync(schedule, ActivityInitiatorType.System, null, "System",
                changeReason: "Built-in schedule created automatically by JIM.", parentActivityId: parentActivityId);
            Log.Information("SeedBuiltInSchedulesAsync: Created built-in schedule '{ScheduleName}' {ScheduleId}.", schedule.Name, schedule.Id);
        }
    }

    /// <summary>
    /// The declarative catalogue of built-in Roles JIM ships, matched against the database by name. Only the
    /// Administrator Role is a stored Role; <see cref="Constants.BuiltInRoles.User"/> is a claim added to every
    /// authenticated identity rather than a row, so it deliberately does not appear here.
    /// <para>
    /// A catalogue rather than a hardcoded check for the same reason as <see cref="BuiltInSchedules"/>: the pass
    /// that seeds these looked for the Administrator Role alone and returned, so a second built-in Role could never
    /// have reached an existing deployment (issue #916).
    /// </para>
    /// </summary>
    internal static IEnumerable<string> BuiltInRoleNames()
    {
        yield return Constants.BuiltInRoles.Administrator;
    }

    /// <summary>
    /// Converges the database towards the built-in Role catalogue, creating any entry it does not hold through the
    /// audited create path (<see cref="SecurityServer.CreateRoleAsync(Role, MetaverseObject?, string?, Guid?)"/>) so
    /// each Role's change history begins with a System-attributed Create Activity and a version-1 configuration
    /// change snapshot, rather than starting blank the first time an administrator touches its membership. Runs at
    /// every application startup, mirroring <see cref="SeedBuiltInSchedulesAsync"/>.
    /// </summary>
    internal async Task SeedBuiltInRolesAsync()
    {
        foreach (var roleName in BuiltInRoleNames())
        {
            if (await Application.Security.GetRoleAsync(roleName) != null)
            {
                Log.Verbose("SeedBuiltInRolesAsync: Role '{RoleName}' already present; skipping.", roleName);
                continue;
            }

            var role = new Role
            {
                BuiltIn = true,
                Name = roleName
            };

            // Create through the audited path, not the repository, so the Role's origin is visible in the portal: a
            // Create Activity attributed to System and a version-1 configuration change snapshot. A repository-direct
            // seed leaves no audit trace, so the change history would start at whichever principal touched the Role
            // next, misattributing its origin.
            var parentActivityId = await GetOrCreateSeedingActivityAsync();
            await Application.Security.CreateRoleAsync(role, changeReason: "Built-in Role created automatically by JIM.", parentActivityId: parentActivityId);
            Log.Information("SeedBuiltInRolesAsync: Created built-in Role {RoleName} (ID: {RoleId}).", role.Name, role.Id);
        }
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
    /// Declares which pass keeps each kind of built-in configuration converged. Every entity type carrying a
    /// <c>BuiltIn</c> flag is configuration JIM ships, so something has to create it on a deployment that predates
    /// it and restore it after a factory reset truncates or cascades it away.
    /// <para>
    /// This exists to be asserted against the EF model, so a new table carrying BuiltIn rows cannot be added
    /// without that decision being made. Three built-ins reached production without one: the Example Data Template
    /// (#866), the Temporal Scope Reconciliation Schedule (#911), and everything SeedAsync owned (#916). Each was
    /// found in production rather than in review.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyDictionary<Type, string> BuiltInConvergencePaths = new Dictionary<Type, string>
    {
        [typeof(MetaverseObjectType)] = nameof(SeedAsync),
        [typeof(MetaverseAttribute)] = $"{nameof(SeedAsync)} then {nameof(SyncBuiltInMetaverseSchemaAsync)}",
        [typeof(PredefinedSearch)] = nameof(SeedAsync),
        [typeof(ExampleDataSet)] = nameof(SeedAsync),
        [typeof(ExampleDataTemplate)] = $"{nameof(SeedAsync)} then {nameof(EnsureBuiltInExampleDataTemplateAsync)}",
        [typeof(ConnectorDefinition)] = nameof(SyncBuiltInConnectorDefinitionsAsync),
        [typeof(Schedule)] = nameof(SeedBuiltInSchedulesAsync),
        [typeof(Role)] = nameof(SeedBuiltInRolesAsync)
    };

    /// <summary>
    /// Runs every built-in configuration pass in dependency order, inside the parent Activity boundary that groups
    /// what they create. The single definition of "apply JIM's built-in configuration": called at worker startup
    /// (<see cref="JimApplication.InitialiseDatabaseAsync"/>) and after a factory reset
    /// (<see cref="SystemServer.ResetSystemAsync"/>), so a built-in added in a later release reaches an existing
    /// deployment and survives a reset without either caller needing to know the list.
    /// </summary>
    internal Task ApplyBuiltInConfigurationAsync() => RunBuiltInConfigurationPipelineAsync(rebaselinePreservedConfiguration: false);

    /// <summary>
    /// The factory reset's entry point into the pipeline. Identical to <see cref="ApplyBuiltInConfigurationAsync"/>
    /// except that it also re-records the version-1 baselines of built-ins the wipe *preserved*: the reset truncates
    /// the Activities table but keeps BuiltIn rows, so the ordinary passes no-op for them and their factory-state
    /// provenance would be permanently lost. No seeding pass can do that job, which is why the rebaseline is not
    /// one of the bespoke repairs this pipeline replaced.
    /// </summary>
    internal Task RestoreBuiltInConfigurationAfterResetAsync() => RunBuiltInConfigurationPipelineAsync(rebaselinePreservedConfiguration: true);

    private async Task RunBuiltInConfigurationPipelineAsync(bool rebaselinePreservedConfiguration)
    {
        try
        {
            // Order matters: SeedAsync creates the built-in Metaverse Object Types the schema sync binds attributes
            // to (that sync throws rather than creating a missing one), and the Example Data Sets the template
            // repair resolves against.
            await SeedAsync();
            await SyncBuiltInMetaverseSchemaAsync();
            await SeedBuiltInSchedulesAsync();
            await SeedBuiltInRolesAsync();
            await SyncBuiltInConnectorDefinitionsAsync();
            await SyncServiceSettingsAsync();
            await EnsureBuiltInExampleDataTemplateAsync();

            if (rebaselinePreservedConfiguration)
                await RebaselineBuiltInConfigurationAsync();

            await CompleteSeedingActivityAsync();
        }
        catch (Exception ex)
        {
            // Catch-all is deliberate: this is an Activity execution boundary (the "System Initialisation" parent
            // Activity, if one was created), and any failure here must be recorded on it via
            // FailSeedingActivityAsync rather than escape silently, then rethrown so the caller still fails loudly.
            await FailSeedingActivityAsync(ex);
            throw;
        }
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
    /// The connectors JIM ships with, named once because seeding and the startup reconciliation must
    /// agree. A connector in one list and not the other would either never appear to administrators or
    /// never pick up settings added in a later release, and both failures are silent.
    /// </summary>
    internal static List<IConnector> BuiltInConnectors()
    {
        return [new LdapConnector(), new FileConnector(), new ScimConnector(), new SqlConnector()];
    }

    /// <summary>
    /// The Example Data Sets JIM ships with, and the embedded resource each is seeded from, named once so that
    /// seeding and anything that repairs example data agree on what "built-in" means.
    /// </summary>
    internal static IEnumerable<(string Name, string Culture, string Resource)> BuiltInExampleDataSets()
    {
        yield return (Constants.BuiltInExampleDataSets.Companies, "en", Properties.Resources.Companies_en);
        yield return (Constants.BuiltInExampleDataSets.Departments, "en", Properties.Resources.Departments_en);
        yield return (Constants.BuiltInExampleDataSets.Teams, "en", Properties.Resources.Teams_en);
        yield return (Constants.BuiltInExampleDataSets.JobTitles, "en", Properties.Resources.JobTitles_en);
        yield return (Constants.BuiltInExampleDataSets.FirstnamesMale, "en", Properties.Resources.FirstnamesMale_en);
        yield return (Constants.BuiltInExampleDataSets.FirstnamesFemale, "en", Properties.Resources.FirstnamesFemale_en);
        yield return (Constants.BuiltInExampleDataSets.Lastnames, "en", Properties.Resources.Lastnames_en);
        yield return (Constants.BuiltInExampleDataSets.Adjectives, "en", Properties.Resources.Adjectives_en);
        yield return (Constants.BuiltInExampleDataSets.Colours, "en", Properties.Resources.Colours_en);
        yield return (Constants.BuiltInExampleDataSets.GroupNameEndings, "en", Properties.Resources.GroupNameEndings_en);
        yield return (Constants.BuiltInExampleDataSets.Words, "en", Properties.Resources.Words_en);
        yield return (Constants.BuiltInExampleDataSets.UserStatuses, "en", Properties.Resources.UserStatuses_en);
        yield return (Constants.BuiltInExampleDataSets.GroupStatuses, "en", Properties.Resources.GroupStatuses_en);
    }

    /// <summary>
    /// Creates and synchronises the built-in Connector Definitions from the connector code. Called on every
    /// application startup: it creates any definition the database does not hold yet, and updates the ones it does
    /// when their declarations or settings have changed. This pass owns creation (SeedAsync does not seed Connector
    /// Definitions) precisely because SeedAsync short-circuits once ServiceSettings exists; a Connector added to
    /// <see cref="BuiltInConnectors"/> in a later release must reach upgraded deployments, not just fresh ones
    /// (issue #1287). Idempotent: a converged database results in no writes and no Activities.
    /// </summary>
    internal async Task SyncBuiltInConnectorDefinitionsAsync()
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        Log.Information("SyncBuiltInConnectorDefinitionsAsync: Starting built-in connector definition synchronisation...");

        foreach (var connector in BuiltInConnectors())
        {
            await SyncConnectorDefinitionAsync(connector);
        }

        stopwatch.Stop();
        Log.Information($"SyncBuiltInConnectorDefinitionsAsync: Completed in: {stopwatch.Elapsed}");
    }

    /// <summary>
    /// Converges the database towards <see cref="BuiltInMetaverseSchema"/> (issue #1104): creates missing built-in
    /// Metaverse Attributes (with a System-attributed baseline under the System Initialisation Activity), adds
    /// missing built-in Object Type bindings, and reconciles advisory Standard Mappings. Runs on every application
    /// startup, mirroring <see cref="SyncBuiltInConnectorDefinitionsAsync"/>: SeedAsync short-circuits once
    /// ServiceSettings exists, so this pass is how newly-introduced built-in schema reaches existing deployments.
    /// Idempotent: a converged database results in no writes and no Activities.
    /// </summary>
    internal async Task SyncBuiltInMetaverseSchemaAsync()
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        var objectTypes = await Application.Repository.Metaverse.GetBuiltInMetaverseObjectTypesForSchemaSyncAsync();
        var attributes = await Application.Repository.Metaverse.GetMetaverseAttributesForSchemaSyncAsync();
        var objectTypesByName = objectTypes.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        // built tolerantly rather than via ToDictionary: attribute names are unique case-insensitively at the
        // application layer, but no database constraint enforces it, and a pre-existing anomaly must not crash
        // startup. Built-in attributes win the name so the converge targets the right instance.
        var attributesByName = new Dictionary<string, MetaverseAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes.OrderByDescending(a => a.BuiltIn))
        {
            var added = attributesByName.TryAdd(attribute.Name, attribute);
            if (!added)
                Log.Error($"SyncBuiltInMetaverseSchemaAsync: Multiple Metaverse Attributes share the name '{attribute.Name}' (case-insensitively); ignoring attribute id {attribute.Id}. Investigate and resolve the duplicate.");
        }

        var newAttributes = new List<MetaverseAttribute>();
        var bindingsAdded = 0;
        var mappingsAdded = 0;
        var mappingsRemoved = 0;
        var mappingsUpdated = 0;

        foreach (var definition in BuiltInMetaverseSchema.Attributes)
        {
            if (!attributesByName.TryGetValue(definition.Name, out var attribute))
            {
                attribute = new MetaverseAttribute
                {
                    Name = definition.Name,
                    Type = definition.Type,
                    AttributePlurality = definition.Plurality,
                    RenderingHint = definition.RenderingHint,
                    BuiltIn = true
                };
                AuditHelper.SetCreatedBySystem(attribute);
                newAttributes.Add(attribute);
                attributesByName.Add(definition.Name, attribute);
                Log.Information($"SyncBuiltInMetaverseSchemaAsync: Preparing built-in Metaverse Attribute '{definition.Name}'");
            }
            else if (!attribute.BuiltIn)
            {
                // a custom attribute already uses this name (most likely created before an upgrade introduced the
                // built-in definition). Adopting it would force bindings, overwrite its Standard Mappings, and leave
                // its shape contradicting the catalogue, so skip the definition entirely and tell the administrator
                // how to resolve it. Everything else still converges; this is not a startup failure.
                Log.Error($"SyncBuiltInMetaverseSchemaAsync: A custom Metaverse Attribute already uses the name '{definition.Name}', so the built-in attribute of that name cannot be created. Rename the custom attribute to receive the built-in definition.");
                continue;
            }
            else if (attribute.Type != definition.Type || attribute.AttributePlurality != definition.Plurality || attribute.RenderingHint != definition.RenderingHint)
            {
                // shape drift can only come from database tampering or a catalogue edit; built-in attributes are
                // immutable to administrators. Surface it, but do not modify the attribute: changing type or
                // plurality under stored values is a data-integrity operation this pass must not perform.
                Log.Warning($"SyncBuiltInMetaverseSchemaAsync: Built-in Metaverse Attribute '{definition.Name}' has a different shape to the catalogue (type/plurality/rendering hint); leaving it unmodified. Investigate the drift.");
            }

            // ensure the attribute is bound to each built-in Metaverse Object Type the catalogue declares.
            // bindings are only ever added, never removed: the catalogue is the floor, not a ceiling.
            foreach (var objectTypeName in definition.ObjectTypeNames)
            {
                if (!objectTypesByName.TryGetValue(objectTypeName, out var objectType))
                {
                    // the built-in Object Types are created by SeedAsync before this pass runs, so this indicates
                    // a seriously malformed database; fail fast rather than continue with a partial converge.
                    throw new InvalidOperationException(
                        $"SyncBuiltInMetaverseSchemaAsync: Built-in Metaverse Object Type '{objectTypeName}' was not found; cannot bind built-in Metaverse Attribute '{definition.Name}'.");
                }

                if (!objectType.Attributes.Any(a => a.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    objectType.Attributes.Add(attribute);
                    bindingsAdded++;
                    Log.Information($"SyncBuiltInMetaverseSchemaAsync: Binding '{definition.Name}' to '{objectTypeName}'");
                }
            }

            // reconcile the advisory Standard Mappings to the catalogue. built-in attributes are immutable to
            // administrators, so every mapping on one is system-seeded and full reconciliation (add missing,
            // update drifted notes, remove stale) is safe and self-healing.
            foreach (var mappingDefinition in definition.StandardMappings)
            {
                var existingMapping = attribute.StandardMappings.SingleOrDefault(m =>
                    m.Standard == mappingDefinition.Standard && m.CounterpartName == mappingDefinition.CounterpartName);
                if (existingMapping == null)
                {
                    attribute.StandardMappings.Add(new MetaverseAttributeStandardMapping
                    {
                        Standard = mappingDefinition.Standard,
                        CounterpartName = mappingDefinition.CounterpartName,
                        Notes = mappingDefinition.Notes
                    });
                    mappingsAdded++;
                }
                else if (existingMapping.Notes != mappingDefinition.Notes)
                {
                    existingMapping.Notes = mappingDefinition.Notes;
                    mappingsUpdated++;
                }
            }

            var staleMappings = attribute.StandardMappings
                .Where(m => !definition.StandardMappings.Any(d => d.Standard == m.Standard && d.CounterpartName == m.CounterpartName))
                .ToList();
            foreach (var staleMapping in staleMappings)
            {
                attribute.StandardMappings.Remove(staleMapping);
                mappingsRemoved++;
                Log.Information($"SyncBuiltInMetaverseSchemaAsync: Removing stale Standard Mapping '{staleMapping}' from '{definition.Name}'");
            }
        }

        var hasChanges = newAttributes.Count > 0 || bindingsAdded > 0 || mappingsAdded > 0 || mappingsRemoved > 0 || mappingsUpdated > 0;
        if (hasChanges)
        {
            // persist repository-direct in one transaction. mapping and binding reconciliation follows the
            // SyncConnectorDefinitionAsync precedent (no per-change Activity); newly created attributes are
            // baselined below, matching the batch-seed baseline pattern in SeedAsync.
            await Application.Repository.Seeding.SaveBuiltInSchemaChangesAsync(newAttributes);

            if (newAttributes.Count > 0)
            {
                var parentActivityId = await GetOrCreateSeedingActivityAsync();
                foreach (var attribute in newAttributes)
                    await Application.Metaverse.RecordSeededMetaverseAttributeBaselineAsync(attribute.Id, attribute.Name, parentActivityId);
            }
        }

        stopwatch.Stop();
        Log.Information($"SyncBuiltInMetaverseSchemaAsync: Completed in {stopwatch.Elapsed}. " +
                        $"Attributes created: {newAttributes.Count}, bindings added: {bindingsAdded}, Standard Mappings added: {mappingsAdded}, " +
                        $"updated: {mappingsUpdated}, removed: {mappingsRemoved}.");
    }

    /// <summary>
    /// Re-records System-attributed version-1 baseline Activities for every preserved built-in configuration object,
    /// grouped under the seeding pass's parent Activity. Called after a factory reset: the reset truncates the
    /// Activities table (so all configuration-change baselines are lost) but preserves the built-in seed objects
    /// (BuiltIn = true rows are not customer data), and the ordinary re-seed no-ops for them because they still exist.
    /// Without this, a factory reset would permanently strip the factory-state provenance from the change history.
    /// Schedules are excluded: they are truncated and re-created through their audited path
    /// (<see cref="SeedBuiltInSchedulesAsync"/>), which records their baseline. The capture dedupe-guard makes each
    /// record idempotent, so a second call finds the just-written baseline and records nothing further.
    /// </summary>
    internal async Task RebaselineBuiltInConfigurationAsync()
    {
        // If configuration change tracking is disabled there is no change history to restore, so skip re-baselining
        // entirely (each RecordSeeded...Baseline call would no-op inside the capture guard anyway). This also avoids
        // enumerating every configuration type, and creating a seeding parent Activity, when there is nothing to record.
        if (!await Application.ServiceSettings.GetConfigurationChangeTrackingEnabledAsync())
            return;

        var parentActivityId = await GetOrCreateSeedingActivityAsync();

        foreach (var objectType in (await Application.Metaverse.GetMetaverseObjectTypesAsync(includeChildObjects: false)).Where(t => t.BuiltIn))
            await Application.Metaverse.RecordSeededMetaverseObjectTypeBaselineAsync(objectType.Id, objectType.Name, parentActivityId);

        foreach (var attribute in (await Application.Metaverse.GetMetaverseAttributesAsync() ?? new List<MetaverseAttribute>()).Where(a => a.BuiltIn))
            await Application.Metaverse.RecordSeededMetaverseAttributeBaselineAsync(attribute.Id, attribute.Name, parentActivityId);

        foreach (var search in (await Application.Search.GetPredefinedSearchHeadersAsync()).Where(s => s.BuiltIn))
            await Application.Search.RecordSeededPredefinedSearchBaselineAsync(search.Id, search.Name, parentActivityId);

        foreach (var connector in (await Application.ConnectedSystems.GetConnectorDefinitionHeadersAsync()).Where(c => c.BuiltIn))
            await Application.ConnectedSystems.RecordSeededConnectorDefinitionBaselineAsync(connector.Id, connector.Name, parentActivityId);

        foreach (var dataSet in (await Application.ExampleData.GetExampleDataSetsAsync()).Where(d => d.BuiltIn))
            await Application.ExampleData.RecordSeededExampleDataSetBaselineAsync(dataSet.Id, dataSet.Name, parentActivityId);

        foreach (var template in (await Application.ExampleData.GetTemplatesAsync()).Where(t => t.BuiltIn))
            await Application.ExampleData.RecordSeededExampleDataTemplateBaselineAsync(template.Id, template.Name, parentActivityId);

        foreach (var role in (await Application.Security.GetRolesAsync()).Where(r => r.BuiltIn))
            await Application.Security.RecordSeededRoleBaselineAsync(role.Id, role.Name, parentActivityId);

        foreach (var setting in await Application.ServiceSettings.GetAllSettingsAsync())
            await Application.ServiceSettings.RecordSeededServiceSettingBaselineAsync(setting.Key, setting.DisplayName, parentActivityId);
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

        // Capture the keys that already exist so that, after the sync loop, a System-attributed version-1 baseline can
        // be recorded for only the settings genuinely created in this pass. Diffing keys before/after keeps this out of
        // the ~20 individual SeedSettingAsync call sites; see RecordSeededServiceSettingBaselineAsync for why the
        // baseline is deliberately recorded after the full loop (the capture reads the toggle and hash-key settings,
        // which are themselves seeded here).
        var existingSettingKeys = (await Application.ServiceSettings.GetAllSettingsAsync()).Select(s => s.Key).ToHashSet();

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

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.SecurityEventRetentionPeriod,
            DisplayName = "Security event retention period",
            Description = "The duration for which security audit event Activities (interactive sign-in success/failure, API key authentication failure) are retained. Kept separately from the history and configuration change retention periods. Format: d.hh:mm:ss (e.g., '365.00:00:00' for ~1 year).",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.TimeSpan,
            DefaultValue = "365.00:00:00", // ~1 year
            IsReadOnly = false
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.InitialPasswordRetentionPeriod,
            DisplayName = "Initial password record retention period",
            Description = "The duration for which an initial-password record that has reached a terminal state (parked for an administrator, or expired without one being set) is kept before housekeeping removes it. Records still being worked are never removed, however old. The Activity recording what happened to the account outlives this. Format: d.hh:mm:ss (e.g., '90.00:00:00' for 90 days).",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.TimeSpan,
            DefaultValue = "90.00:00:00", // 90 days
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

        // Configuration change preview (#827) - where a preview runs
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ConfigurationChangePreviewWorkerThreshold,
            DisplayName = "Preview worker threshold",
            Description = "The estimated number of affected objects above which a configuration change preview is evaluated by JIM.Worker rather than in the portal's own process. Smaller previews run in-process so they return without waiting for the worker to pick them up. Both paths produce identical results.",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Integer,
            DefaultValue = "2500",
            IsReadOnly = false
        });

        // Configuration change preview (#827) - when to ask before capping the drill-down rows
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ConfigurationChangePreviewFullDataSetPromptThreshold,
            DisplayName = "Preview full data set prompt threshold",
            Description = "The estimated number of object-level rows above which a configuration change preview asks whether to keep the full data set or only a sample of each summary group. Below this, previews keep a sample without asking. Summary counts are exact either way; the choice only affects how much can be drilled into, and how much storage the preview uses.",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Integer,
            DefaultValue = "100000",
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

        // API rate limiting settings (issue #500, OWASP Top 10:2025 A02). Runtime-tunable so administrators can
        // adjust limits without a restart; JIM.Web's rate limiter reads these through a short-TTL cache rather
        // than on every request (see RateLimitSettingsCache), so a change here takes effect within that
        // propagation delay, not instantly.
        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.RateLimitingEnabled,
            DisplayName = "API rate limiting enabled",
            Description = "When enabled, REST API requests are throttled per client (see the authenticated and unauthenticated request limits below). When disabled, no limiter is applied to any API request.",
            Category = ServiceSettingCategory.Security,
            ValueType = ServiceSettingValueType.Boolean,
            // Lowercase to match every other boolean setting's stored convention ("true"/"false"); bool.ToString()
            // would produce "True" instead. bool.Parse accepts either case, but the stored value should be consistent.
            DefaultValue = Constants.RateLimitDefaults.Enabled ? "true" : "false",
            IsReadOnly = false
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute,
            DisplayName = "Authenticated API requests per minute",
            Description = "The maximum number of REST API requests an authenticated client (a signed-in user or an API key) may make per rolling minute. Each authenticated principal is limited independently.",
            Category = ServiceSettingCategory.Security,
            ValueType = ServiceSettingValueType.Integer,
            DefaultValue = Constants.RateLimitDefaults.AuthenticatedRequestsPerMinute.ToString(),
            IsReadOnly = false
        });

        await SeedSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute,
            DisplayName = "Unauthenticated API requests per minute",
            Description = "The maximum number of REST API requests an unauthenticated client may make per one-minute window, identified by client IP address. Applies to anonymous endpoints and requests that failed API key authentication.",
            Category = ServiceSettingCategory.Security,
            ValueType = ServiceSettingValueType.Integer,
            DefaultValue = Constants.RateLimitDefaults.UnauthenticatedRequestsPerMinute.ToString(),
            IsReadOnly = false
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

        // Record a System-attributed Create Activity and version-1 baseline for each built-in Service Setting created
        // this pass, grouped under the seeding parent, so their factory origin is visible in the change history and
        // under System Initialisation (matching the other seeded configuration types). The list is diffed against the
        // keys present before the loop, so a normal restart (all settings already present) records nothing and creates
        // no parent Activity; a single new setting shipped in an upgrade appears under its own System Initialisation
        // entry. Recording here, after the full loop, guarantees the toggle and hash-key settings the capture depends
        // on already exist.
        var settingsAfterSync = await Application.ServiceSettings.GetAllSettingsAsync();
        var createdSettings = settingsAfterSync.Where(s => !existingSettingKeys.Contains(s.Key)).ToList();
        if (createdSettings.Count > 0)
        {
            var parentActivityId = await GetOrCreateSeedingActivityAsync();
            foreach (var setting in createdSettings)
                await Application.ServiceSettings.RecordSeededServiceSettingBaselineAsync(setting.Key, setting.DisplayName, parentActivityId);
        }

        // Audit environment-driven changes to pre-existing read-only settings (SSO endpoints, secrets, encryption key
        // path, ...): when an operator changes the deployment's .env and restarts, SyncServiceSettingsAsync rewrites
        // these repository-direct, which by itself records nothing. Capture a System-attributed Update for each one
        // whose value actually changed; the method is churn-free (no Activity, and no parent Activity, when the value
        // is unchanged), so an ordinary restart records nothing.
        foreach (var setting in settingsAfterSync.Where(s => existingSettingKeys.Contains(s.Key) && s.IsReadOnly))
            await Application.ServiceSettings.RecordSeededServiceSettingUpdateIfChangedAsync(setting.Key, setting.DisplayName, GetOrCreateSeedingActivityAsync);

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
    /// Copies everything a Connector declares about itself (capability flags and the advisory schema standard)
    /// onto its Connector Definition, and reports whether anything actually changed so the caller can decide
    /// whether to persist and audit an update.
    /// Shared by the create and startup-reconcile paths: a declaration added to <see cref="IConnectorCapabilities"/>
    /// is applied to fresh installs and existing deployments alike, from one place.
    /// </summary>
    internal static bool ApplyConnectorDeclarations(IConnectorCapabilities connectorCapabilities, ConnectorDefinition definition)
    {
        // Driven off the shape of IConnectorCapabilities rather than a written-out list of every declaration.
        // A hand-written list is the failure this exists to avoid: declaring a capability and forgetting to add
        // it here leaves the flag permanently false in the database with nothing failing, so the Connector
        // advertises a feature the rest of JIM cannot see. Declaring it on the interface is the only step.
        var changed = ConnectorCapabilityMirror.GetDifferences(connectorCapabilities, definition);
        if (changed.Count == 0)
            return false;

        ConnectorCapabilityMirror.CopyTo(connectorCapabilities, definition);
        Log.Debug("ApplyConnectorDeclarations: Updated declarations for '{ConnectorName}': {ChangedDeclarations}",
            LogSanitiser.Sanitise(definition.Name), string.Join(", ", changed));
        return true;
    }

    /// <summary>
    /// Creates a built-in Connector Definition that the database does not hold yet, recording a System-attributed
    /// Create Activity and version-1 baseline under the seeding pass's parent Activity, exactly as a first-run seed
    /// would. Creation lives here rather than in <see cref="SeedAsync"/> because SeedAsync short-circuits once
    /// ServiceSettings exists: a Connector added to <see cref="BuiltInConnectors"/> in a later release would
    /// otherwise ship only to brand-new deployments and be silently absent from every upgraded one (issue #1287).
    /// </summary>
    private async Task CreateBuiltInConnectorDefinitionAsync(IConnector connector, IConnectorCapabilities connectorCapabilities, IConnectorSettings connectorSettings)
    {
        var connectorDefinition = new ConnectorDefinition
        {
            Name = connector.Name,
            Description = connector.Description,
            Url = connector.Url,
            BuiltIn = true
        };

        ApplyConnectorDeclarations(connectorCapabilities, connectorDefinition);
        Application.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(connectorSettings, connectorDefinition);
        await Application.Repository.ConnectedSystems.CreateConnectorDefinitionAsync(connectorDefinition);

        var parentActivityId = await GetOrCreateSeedingActivityAsync();
        await Application.ConnectedSystems.RecordSeededConnectorDefinitionBaselineAsync(connectorDefinition.Id, connectorDefinition.Name, parentActivityId);

        Log.Information($"CreateBuiltInConnectorDefinitionAsync: Created built-in Connector Definition '{connector.Name}'");
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
            await CreateBuiltInConnectorDefinitionAsync(connector, connectorCapabilities, connectorSettings);
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

        // Update the Connector's own declarations (capability flags and schema standard)
        if (ApplyConnectorDeclarations(connectorCapabilities, existingDefinition))
        {
            hasChanges = true;
            Log.Information($"SyncConnectorDefinitionAsync: Updated declarations for '{connector.Name}'");
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
            // Audit the drift-sync as a System-attributed configuration change grouped under the seeding pass's parent,
            // so capability/setting changes shipped in new connector code are visible in the definition's history. The
            // parent is created lazily here (only when there is actually drift to record), matching the other seed steps.
            var parentActivityId = await GetOrCreateSeedingActivityAsync();
            await Application.ConnectedSystems.UpdateConnectorDefinitionAsync(existingDefinition,
                ActivityInitiatorType.System, null, "System",
                changeReason: "Connector Definition updated automatically by JIM to match the latest connector.",
                parentActivityId: parentActivityId);

            // Detaching an obsolete setting from the definition above only severs it: its foreign key is nullable, so
            // the row survives holding no definition while every value an administrator saved against it still points
            // at it, and the withdrawn setting keeps appearing on Connected Systems that hold one. Delete the rows so
            // those values cascade away with them.
            if (settingsToRemove.Count > 0)
                await Application.Repository.ConnectedSystems.DeleteConnectorDefinitionSettingsAsync(settingsToRemove);

            Log.Information($"SyncConnectorDefinitionAsync: Saved changes for '{connector.Name}'");
        }
        else
        {
            Log.Debug($"SyncConnectorDefinitionAsync: No changes detected for '{connector.Name}'");
        }
    }

    #region private methods
    /// <summary>
    /// Returns the built-in Metaverse Attribute of this name, preparing it for creation if it does not exist yet, and
    /// adds it to <paramref name="allAttributes"/> either way. The caller derives its create batch from the attributes
    /// in that list that have no database id, and builds the built-in Example Data Template from the whole list, which
    /// is what lets seeding be retried: on a retry nothing needs creating, and a template built from this pass's
    /// creations alone could not resolve a single attribute (issue #1287).
    /// </summary>
    private async Task<MetaverseAttribute> GetOrPrepareMetaverseAttributeAsync(string name, AttributePlurality attributePlurality, AttributeDataType attributeDataType, List<MetaverseAttribute> allAttributes, AttributeRenderingHint renderingHint = AttributeRenderingHint.Default)
    {
        // Loaded change-tracked because this pass mutates and saves what it loads: an already-persisted attribute is
        // bound to Object Types, referenced by newly-created Predefined Searches, and handed to the create batch as
        // part of their object graph. An untracked instance would be walked into by AddRange and re-inserted (see
        // MetaverseRepository.GetMetaverseObjectTypeAsync's tracking flag for the other half of the same rule).
        var attribute = await Application.Metaverse.GetMetaverseAttributeAsync(name, withChangeTracking: true);
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
            Log.Verbose($"GetOrPrepareMetaverseAttributeAsync: Prepared {name}");
        }

        allAttributes.Add(attribute);
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

    /// <summary>
    /// Normalises an embedded example-data resource into the values to seed: split on either line-ending style,
    /// trimmed, with any leading byte-order mark, blank lines and repeats dropped. Neither line endings nor a leading
    /// mark can be assumed away here: the resource files are stored LF but a Windows checkout converts them to CRLF,
    /// so what is compiled into the assembly depends on the build host, while <see cref="Environment.NewLine"/>
    /// depends on the host the container runs on. Splitting on <see cref="Environment.NewLine"/> left a trailing
    /// carriage return on every value, which never matched the trimmed value already stored, so every
    /// already-persisted Example Data Set looked incomplete on a retry (issue #1287). A byte-order mark is stripped
    /// for the same reason: several of the resource files carry one, and although the resource reader consumes it
    /// today, a mark that reached a value would sit invisibly at the front of it for the life of the deployment.
    /// </summary>
    internal static List<string> NormaliseExampleDataSetValues(string resourceValues)
    {
        return resourceValues
            .TrimStart('\uFEFF')
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Returns the built-in Example Data Set of this name and culture, preparing it for creation if it does not exist
    /// yet and topping up any values the resource holds that it does not, and adds it to
    /// <paramref name="allDataSets"/> either way. An already-persisted set is never handed to the create batch (the
    /// caller derives that from the sets with no database id): inserting one carries its existing primary key, which
    /// is the duplicate-key crash that made a retry of a partial seed impossible (issue #1287). A persisted set that
    /// gains values is modified in place; it is loaded change-tracked, so those values are written by the seed
    /// batch's transaction.
    /// </summary>
    private async Task<ExampleDataSet> GetOrPrepareExampleDataSetAsync(string name, string culture, string resourceValues, List<ExampleDataSet> allDataSets)
    {
        var exampleDataSet = await Application.Repository.ExampleData.GetExampleDataSetAsync(name, culture, withChangeTracking: true);
        if (exampleDataSet == null)
        {
            exampleDataSet = new ExampleDataSet
            {
                Name = name,
                Culture = culture,
                BuiltIn = true
            };
            AuditHelper.SetCreatedBySystem(exampleDataSet);
            Log.Information($"GetOrPrepareExampleDataSetAsync: Preparing Example Data Set '{name}' ({culture})");
        }

        // Both sides of the comparison are normalised. Stored values are trimmed, so comparing them against a raw
        // resource line never matched once a line ending survived the split, and every persisted set then looked
        // incomplete on every start.
        var storedValues = new HashSet<string>(exampleDataSet.Values.Select(q => q.StringValue.Trim()), StringComparer.Ordinal);
        var valuesAdded = 0;
        foreach (var value in NormaliseExampleDataSetValues(resourceValues))
        {
            if (!storedValues.Add(value))
                continue;

            exampleDataSet.Values.Add(new ExampleDataSetValue { StringValue = value });
            valuesAdded++;
        }

        if (valuesAdded > 0 && exampleDataSet.Id != 0)
            Log.Information($"GetOrPrepareExampleDataSetAsync: Added {valuesAdded} missing value(s) to the existing Example Data Set '{name}' ({culture})");

        allDataSets.Add(exampleDataSet);
        return exampleDataSet;
    }

    /// <summary>
    /// Prepares the built-in "Users &amp; Groups" Example Data Template for creation, or returns null when it already
    /// exists. <paramref name="dataSets"/> and <paramref name="metaverseAttributes"/> must hold every built-in
    /// Example Data Set and Metaverse Attribute, persisted or pending, not just the ones being created this pass: the
    /// template resolves each of its attributes and data sets by name and fails fast when one is absent, and on a
    /// retry after a partial seed there is nothing being created at all (issue #1287).
    /// </summary>
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

        // Change-tracked for the same reason SeedAsync tracks its loads: the template graph created below references
        // these objects, and an untracked instance of a row the pipeline already tracked would collide with it on
        // TrackGraph ("another instance with the same key value is already being tracked").
        var userType = await Application.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.User, includeChildObjects: false, withChangeTracking: true);
        var groupType = await Application.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.Group, includeChildObjects: false, withChangeTracking: true);
        if (userType == null || groupType == null)
        {
            Log.Warning("EnsureBuiltInExampleDataTemplateAsync: built-in User/Group Metaverse Object Types not found; cannot restore the example data template.");
            return;
        }

        // remove the incomplete shell (if any) so the template is recreated whole.
        if (existing != null)
            await Application.Repository.ExampleData.DeleteTemplateAsync(existing.Id);

        var metaverseAttributes = (await Application.Metaverse.GetMetaverseAttributesAsync(withChangeTracking: true))?.ToList() ?? new List<MetaverseAttribute>();
        var dataSets = await Application.ExampleData.GetExampleDataSetsAsync(withChangeTracking: true);

        var template = new ExampleDataTemplate { Name = templateName, BuiltIn = true };
        AddUsersToExampleDataTemplate(template, userType, dataSets, metaverseAttributes);
        AddGroupsToExampleDataTemplate(template, groupType, userType, dataSets, metaverseAttributes);
        await Application.Repository.ExampleData.CreateTemplateGraphAsync(template);

        Log.Information("EnsureBuiltInExampleDataTemplateAsync: (re)created the built-in '{TemplateName}' example data template (was {State}).",
            templateName, existing == null ? "missing" : "an incomplete shell");
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