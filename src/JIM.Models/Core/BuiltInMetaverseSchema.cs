// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// The declarative catalogue of JIM's built-in Metaverse schema: every built-in Metaverse
/// Attribute, its shape, its bindings to the built-in Metaverse Object Types, and its advisory
/// Standard Mappings (SCIM 2.0 and LDAP/AD counterpart names).
///
/// The built-in schema is JIM's own canonical vocabulary; no wire standard (LDAP/AD or SCIM 2.0)
/// is its foundation. Standard Mappings are advisory metadata only: they power Attribute Flow
/// editor hints, connector wizard default-flow suggestions, and generated schema documentation,
/// and are NEVER consulted by the synchronisation engine; Attribute Flow configuration remains
/// the single source of mapping truth.
///
/// The built-in schema synchronisation pass (SeedingServer.SyncBuiltInMetaverseSchemaAsync)
/// converges the database towards this catalogue on every startup, so additions here reach
/// existing deployments as well as fresh installs.
/// </summary>
public static class BuiltInMetaverseSchema
{
    private static readonly string[] UserOnly = { Constants.BuiltInObjectTypes.User };
    private static readonly string[] GroupOnly = { Constants.BuiltInObjectTypes.Group };
    private static readonly string[] UserAndGroup = { Constants.BuiltInObjectTypes.User, Constants.BuiltInObjectTypes.Group };

    private const string ScimEnterpriseNote = "SCIM Enterprise User extension (urn:ietf:params:scim:schemas:extension:enterprise:2.0:User).";

    private static StandardMappingDefinition Scim(string counterpartName, string? notes = null) => new(AttributeStandard.Scim, counterpartName, notes);

    private static StandardMappingDefinition Ldap(string counterpartName, string? notes = null) => new(AttributeStandard.Ldap, counterpartName, notes);

    private static BuiltInMetaverseAttributeDefinition Define(
        string name,
        AttributeDataType type,
        AttributePlurality plurality,
        string[] objectTypeNames,
        AttributeRenderingHint renderingHint = AttributeRenderingHint.Default,
        params StandardMappingDefinition[] standardMappings) =>
        new(name, type, plurality, objectTypeNames, renderingHint, standardMappings);

    /// <summary>
    /// All built-in Metaverse Attribute definitions.
    /// </summary>
    public static IReadOnlyList<BuiltInMetaverseAttributeDefinition> Attributes { get; } = BuildAttributes();

    private static List<BuiltInMetaverseAttributeDefinition> BuildAttributes()
    {
        var attributes = new List<BuiltInMetaverseAttributeDefinition>
        {
            // common attributes (bound to both User and Group)
            Define(Constants.BuiltInAttributes.AccountName, AttributeDataType.Text, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: Ldap("sAMAccountName", "Active Directory pre-Windows 2000 logon name.")),
            Define(Constants.BuiltInAttributes.CommonName, AttributeDataType.Text, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: Ldap("cn")),
            Define(Constants.BuiltInAttributes.Description, AttributeDataType.Text, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: Ldap("description")),
            Define(Constants.BuiltInAttributes.DisplayName, AttributeDataType.Text, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: new[] { Scim("displayName"), Ldap("displayName") }),
            Define(Constants.BuiltInAttributes.DistinguishedName, AttributeDataType.Text, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: Ldap("distinguishedName")),
            Define(Constants.BuiltInAttributes.Email, AttributeDataType.Text, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: new[]
                {
                    Scim("emails", "SCIM emails is multi-valued; this single-valued attribute suits the primary value. Use Emails for the full collection."),
                    Ldap("mail")
                }),
            Define(Constants.BuiltInAttributes.Emails, AttributeDataType.Text, AttributePlurality.MultiValued, UserAndGroup, AttributeRenderingHint.List,
                Scim("emails", "Values only; SCIM sub-attributes such as type and primary are not modelled."),
                Ldap("mail", "LDAP mail is multi-valued; deployments commonly treat the first value as primary.")),
            Define(Constants.BuiltInAttributes.HideFromAddressLists, AttributeDataType.Boolean, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: Ldap("msExchHideFromAddressLists", "Exchange attribute.")),
            Define(Constants.BuiltInAttributes.Info, AttributeDataType.Text, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: Ldap("info")),
            Define(Constants.BuiltInAttributes.MailNickname, AttributeDataType.Text, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: Ldap("mailNickname", "Exchange alias attribute.")),
            Define(Constants.BuiltInAttributes.ObjectGuid, AttributeDataType.Guid, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: Ldap("objectGUID", "Active Directory; OpenLDAP's counterpart is entryUUID.")),
            Define(Constants.BuiltInAttributes.ObjectSid, AttributeDataType.Binary, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: Ldap("objectSid", "Active Directory security identifier.")),
            Define(Constants.BuiltInAttributes.ProxyAddresses, AttributeDataType.Text, AttributePlurality.MultiValued, UserAndGroup, AttributeRenderingHint.List,
                Ldap("proxyAddresses", "Exchange address collection with type prefixes, e.g. SMTP:.")),
            Define(Constants.BuiltInAttributes.Status, AttributeDataType.Text, AttributePlurality.SingleValued, UserAndGroup),
            Define(Constants.BuiltInAttributes.Type, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly),

            // user-specific attributes
            Define(Constants.BuiltInAttributes.AccountEnabled, AttributeDataType.Boolean, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("active"),
                    Ldap("userAccountControl", "Inverse of the ACCOUNTDISABLE bit (0x2); requires a transform, not a direct flow.")
                }),
            Define(Constants.BuiltInAttributes.AccountExpires, AttributeDataType.DateTime, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Ldap("accountExpires", "Active Directory FILETIME value; requires a transform, not a direct flow.")),
            Define(Constants.BuiltInAttributes.AltSecurityIdentities, AttributeDataType.Text, AttributePlurality.MultiValued, UserOnly, AttributeRenderingHint.List,
                Ldap("altSecurityIdentities")),
            Define(Constants.BuiltInAttributes.Company, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("organization", ScimEnterpriseNote), Ldap("company") }),
            Define(Constants.BuiltInAttributes.Country, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Ldap("co", "Active Directory friendly country name.")),
            Define(Constants.BuiltInAttributes.CountryCode, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("addresses.country", "SCIM requires an ISO 3166-1 alpha-2 code."),
                    Ldap("c", "ISO 3166-1 alpha-2 country code.")
                }),
            Define(Constants.BuiltInAttributes.Department, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("department", ScimEnterpriseNote), Ldap("department") }),
            Define(Constants.BuiltInAttributes.EmployeeEndDate, AttributeDataType.DateTime, AttributePlurality.SingleValued, UserOnly),
            Define(Constants.BuiltInAttributes.EmployeeId, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("employeeNumber", ScimEnterpriseNote + " A string value despite the name."),
                    Ldap("employeeID")
                }),
            Define(Constants.BuiltInAttributes.EmployeeNumber, AttributeDataType.Number, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Ldap("employeeNumber", "LDAP employeeNumber is a string; numeric values flow cleanly, other values do not.")),
            Define(Constants.BuiltInAttributes.EmployeeStartDate, AttributeDataType.DateTime, AttributePlurality.SingleValued, UserOnly),
            Define(Constants.BuiltInAttributes.EmployeeType, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("userType"), Ldap("employeeType") }),
            Define(Constants.BuiltInAttributes.FacsimileTelephoneNumber, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("phoneNumbers", "The value with type fax."),
                    Ldap("facsimileTelephoneNumber")
                }),
            Define(Constants.BuiltInAttributes.FirstName, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("name.givenName"), Ldap("givenName") }),
            Define(Constants.BuiltInAttributes.HomeDirectory, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Ldap("homeDirectory")),
            Define(Constants.BuiltInAttributes.HomeDrive, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Ldap("homeDrive")),
            Define(Constants.BuiltInAttributes.HomePhone, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("phoneNumbers", "The value with type home."),
                    Ldap("homePhone")
                }),
            Define(Constants.BuiltInAttributes.HonorificPrefix, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("name.honorificPrefix"),
                    Ldap("personalTitle", "Active Directory; LDAP title is a job title, not an honorific.")
                }),
            Define(Constants.BuiltInAttributes.HonorificSuffix, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("name.honorificSuffix"), Ldap("generationQualifier") }),
            Define(Constants.BuiltInAttributes.IdentityAssuranceLevel, AttributeDataType.Number, AttributePlurality.SingleValued, UserOnly),
            Define(Constants.BuiltInAttributes.IpPhone, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Ldap("ipPhone")),
            Define(Constants.BuiltInAttributes.JobTitle, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("title"), Ldap("title") }),
            Define(Constants.BuiltInAttributes.LastName, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("name.familyName"), Ldap("sn") }),
            Define(Constants.BuiltInAttributes.Locale, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Scim("locale", "Used for formatting purposes, e.g. en-GB.")),
            Define(Constants.BuiltInAttributes.Locality, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("addresses.locality"),
                    Ldap("l", "The town or city.")
                }),
            Define(Constants.BuiltInAttributes.Manager, AttributeDataType.Reference, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("manager", ScimEnterpriseNote + " The manager.value sub-attribute carries the referenced User's id."),
                    Ldap("manager", "A Distinguished Name reference.")
                }),
            Define(Constants.BuiltInAttributes.MiddleName, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("name.middleName"), Ldap("middleName", "Active Directory attribute.") }),
            Define(Constants.BuiltInAttributes.MobileNumber, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("phoneNumbers", "The value with type mobile."),
                    Ldap("mobile")
                }),
            Define(Constants.BuiltInAttributes.Nickname, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Scim("nickName")),
            Define(Constants.BuiltInAttributes.ObjectIdentifier, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly),
            Define(Constants.BuiltInAttributes.SubjectIdentifier, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly),
            Define(Constants.BuiltInAttributes.Office, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly),
            Define(Constants.BuiltInAttributes.Organisation, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Ldap("o", "The organisation name.")),
            Define(Constants.BuiltInAttributes.OtherFacsimileTelephoneNumbers, AttributeDataType.Text, AttributePlurality.MultiValued, UserOnly, AttributeRenderingHint.ChipSet,
                Ldap("otherFacsimileTelephoneNumber")),
            Define(Constants.BuiltInAttributes.OtherIpPhones, AttributeDataType.Text, AttributePlurality.MultiValued, UserOnly, AttributeRenderingHint.ChipSet,
                Ldap("otherIpPhone")),
            Define(Constants.BuiltInAttributes.OtherMobiles, AttributeDataType.Text, AttributePlurality.MultiValued, UserOnly, AttributeRenderingHint.ChipSet,
                Ldap("otherMobile")),
            Define(Constants.BuiltInAttributes.OtherPagers, AttributeDataType.Text, AttributePlurality.MultiValued, UserOnly, AttributeRenderingHint.ChipSet,
                Ldap("otherPager")),
            Define(Constants.BuiltInAttributes.OtherTelephones, AttributeDataType.Text, AttributePlurality.MultiValued, UserOnly, AttributeRenderingHint.ChipSet,
                Ldap("otherTelephone")),
            Define(Constants.BuiltInAttributes.Pager, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("phoneNumbers", "The value with type pager."),
                    Ldap("pager")
                }),
            Define(Constants.BuiltInAttributes.Photo, AttributeDataType.Binary, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("photos", "SCIM photos carries URLs, not binary values; requires a transform, not a direct flow."),
                    Ldap("thumbnailPhoto", "Active Directory; the LDAP standard counterpart is jpegPhoto.")
                }),
            Define(Constants.BuiltInAttributes.PhysicalDeliveryOfficeName, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Ldap("physicalDeliveryOfficeName")),
            Define(Constants.BuiltInAttributes.PostOfficeBoxes, AttributeDataType.Text, AttributePlurality.MultiValued, UserOnly, AttributeRenderingHint.ChipSet,
                Ldap("postOfficeBox")),
            Define(Constants.BuiltInAttributes.PostalAddresses, AttributeDataType.Text, AttributePlurality.MultiValued, UserOnly, AttributeRenderingHint.List,
                Scim("addresses.formatted", "The full mailing address."),
                Ldap("postalAddress")),
            Define(Constants.BuiltInAttributes.PostalCode, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("addresses.postalCode"), Ldap("postalCode") }),
            Define(Constants.BuiltInAttributes.PreferredLanguage, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("preferredLanguage", "For example en-GB, as per RFC 7231 Accept-Language."),
                    Ldap("preferredLanguage", "inetOrgPerson attribute (RFC 2798).")
                }),
            Define(Constants.BuiltInAttributes.Pronouns, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly),
            Define(Constants.BuiltInAttributes.ScriptPath, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Ldap("scriptPath")),
            Define(Constants.BuiltInAttributes.SidHistory, AttributeDataType.Binary, AttributePlurality.MultiValued, UserOnly, AttributeRenderingHint.List,
                Ldap("sIDHistory")),
            Define(Constants.BuiltInAttributes.StateOrProvince, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("addresses.region"), Ldap("st") }),
            Define(Constants.BuiltInAttributes.StreetAddress, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("addresses.streetAddress"), Ldap("streetAddress") }),
            Define(Constants.BuiltInAttributes.Team, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly),
            Define(Constants.BuiltInAttributes.TelephoneNumber, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("phoneNumbers", "The value with type work."),
                    Ldap("telephoneNumber")
                }),
            Define(Constants.BuiltInAttributes.TimeZone, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Scim("timezone", "IANA time zone database format, e.g. Europe/London.")),
            Define(Constants.BuiltInAttributes.Urls, AttributeDataType.Text, AttributePlurality.MultiValued, UserOnly, AttributeRenderingHint.List,
                Ldap("url")),
            Define(Constants.BuiltInAttributes.UserAccountControl, AttributeDataType.Number, AttributePlurality.SingleValued, UserOnly,
                standardMappings: Ldap("userAccountControl", "Active Directory bitmask.")),
            Define(Constants.BuiltInAttributes.UserCertificates, AttributeDataType.Binary, AttributePlurality.MultiValued, UserOnly, AttributeRenderingHint.List,
                Scim("x509Certificates"),
                Ldap("userCertificate")),
            Define(Constants.BuiltInAttributes.UserPrincipalName, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[]
                {
                    Scim("userName", "Deployments vary; some map userName from Account Name instead."),
                    Ldap("userPrincipalName")
                }),
            Define(Constants.BuiltInAttributes.UserSharedFolder, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly),
            Define(Constants.BuiltInAttributes.WebPage, AttributeDataType.Text, AttributePlurality.SingleValued, UserOnly,
                standardMappings: new[] { Scim("profileUrl"), Ldap("wWWHomePage", "Active Directory attribute.") }),

            // group-specific attributes
            Define(Constants.BuiltInAttributes.GroupScope, AttributeDataType.Text, AttributePlurality.SingleValued, GroupOnly),
            Define(Constants.BuiltInAttributes.GroupType, AttributeDataType.Text, AttributePlurality.SingleValued, GroupOnly),
            Define(Constants.BuiltInAttributes.GroupTypeFlags, AttributeDataType.Number, AttributePlurality.SingleValued, GroupOnly,
                standardMappings: Ldap("groupType", "Active Directory bitmask.")),
            Define(Constants.BuiltInAttributes.ManagedBy, AttributeDataType.Reference, AttributePlurality.SingleValued, GroupOnly,
                standardMappings: Ldap("managedBy", "A Distinguished Name reference.")),
            Define(Constants.BuiltInAttributes.Owners, AttributeDataType.Reference, AttributePlurality.MultiValued, GroupOnly, AttributeRenderingHint.Table),
            Define(Constants.BuiltInAttributes.StaticMembers, AttributeDataType.Reference, AttributePlurality.MultiValued, GroupOnly, AttributeRenderingHint.Table,
                Scim("members", "SCIM Group resource."),
                Ldap("member", "Distinguished Name references."))
        };

        // the fifteen Exchange extension attributes are shape-identical; generate them rather than hand-list them
        for (var i = 1; i <= 15; i++)
        {
            var number = i;
            attributes.Add(Define($"Extension Attribute {number}", AttributeDataType.Text, AttributePlurality.SingleValued, UserAndGroup,
                standardMappings: Ldap($"extensionAttribute{number}", "Exchange custom attribute.")));
        }

        return attributes;
    }
}