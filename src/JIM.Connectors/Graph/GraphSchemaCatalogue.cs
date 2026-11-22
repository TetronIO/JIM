// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Connectors.Graph;

/// <summary>
/// The curated Microsoft Entra ID user and group attributes the Connector offers. Graph's <c>$metadata</c> runs to
/// several megabytes and describes thousands of types JIM never touches, so the identity-relevant v1.0 properties are
/// listed here instead, with the writability Graph documents for each, and a tenant's directory extensions are added
/// when the schema is read.
/// </summary>
/// <remarks>
/// Attributes that a synchronised (hybrid) object takes from on-premises Active Directory are still listed as
/// writable: Graph refuses the write for a synchronised object and accepts it for a cloud one, and which is which is
/// what <c>sourceOfAuthority</c> tells an administrator to scope by.
/// </remarks>
internal static class GraphSchemaCatalogue
{
    public const string UserObjectType = "user";
    public const string GroupObjectType = "group";

    public const string Id = "id";
    public const string Members = "members";
    public const string Owners = "owners";
    public const string Manager = "manager";
    public const string SourceOfAuthority = "sourceOfAuthority";
    public const string OnPremisesExtensionAttributes = "onPremisesExtensionAttributes";
    public const string Visibility = "visibility";

    /// <summary>The <c>visibility</c> of a group whose members only its members, owners and administrators can see.</summary>
    public const string HiddenMembership = "HiddenMembership";

    /// <summary>How many <c>extensionAttribute</c> values <c>onPremisesExtensionAttributes</c> carries.</summary>
    public const int ExtensionAttributeCount = 15;

    private const AttributeWritability Writable = AttributeWritability.Writable;
    private const AttributeWritability ReadOnly = AttributeWritability.ReadOnly;
    private const AttributeWritability OnCreate = AttributeWritability.WritableOnCreate;
    private const AttributeDataType Text = AttributeDataType.Text;
    private const AttributeDataType Boolean = AttributeDataType.Boolean;
    private const AttributeDataType DateTime = AttributeDataType.DateTime;
    private const AttributeDataType Reference = AttributeDataType.Reference;
    private const AttributePlurality Multi = AttributePlurality.MultiValued;

    public static IReadOnlyList<GraphSchemaAttribute> UserAttributes { get; } = BuildUserAttributes();

    public static IReadOnlyList<GraphSchemaAttribute> GroupAttributes { get; } = BuildGroupAttributes();

    /// <summary>The properties Graph takes only on a create, such as a group's <c>mailEnabled</c>, which an update must leave out.</summary>
    public static IReadOnlySet<string> CreateOnlyProperties { get; } = UserAttributes.Concat(GroupAttributes)
        .Where(a => a.Writability == AttributeWritability.WritableOnCreate)
        .Select(a => a.Name)
        .ToHashSet(StringComparer.Ordinal);

    private static List<GraphSchemaAttribute> BuildUserAttributes()
    {
        var attributes = new List<GraphSchemaAttribute>
        {
            IdAttribute(),
            new("userPrincipalName", Text, Writable, "The user's sign-in name, i.e. ada@contoso.com. Required to create a user.", "userPrincipalName", Required: true),
            new("displayName", Text, Writable, "The name shown in address lists. Required to create a user.", "displayName", Required: true),
            new("mailNickname", Text, Writable, "The mail alias. Required to create a user.", "mailNickname", Required: true),
            new("accountEnabled", Boolean, Writable, "Whether the account can sign in. Required to create a user.", "accountEnabled", Required: true),
            new("givenName", Text, Writable, "The user's given name.", "givenName"),
            new("surname", Text, Writable, "The user's surname.", "surname"),
            new("mail", Text, Writable, "The user's primary SMTP address.", "mail"),
            new("otherMails", Text, Writable, "Additional email addresses.", "otherMails", Multi),
            new("proxyAddresses", Text, ReadOnly, "Every address the mail system knows the user by. Read-only in Graph; Exchange Online maintains it.", "proxyAddresses", Multi),
            new("jobTitle", Text, Writable, "The user's job title.", "jobTitle"),
            new("department", Text, Writable, "The user's department.", "department"),
            new("companyName", Text, Writable, "The user's company.", "companyName"),
            new("employeeId", Text, Writable, "The organisation's identifier for the employee.", "employeeId"),
            new("employeeType", Text, Writable, "The kind of worker, i.e. Employee or Contractor.", "employeeType"),
            new("employeeHireDate", DateTime, Writable, "When the user was hired, or will start.", "employeeHireDate"),
            new("officeLocation", Text, Writable, "The user's office.", "officeLocation"),
            new("businessPhones", Text, Writable, "The user's business telephone number. Graph holds one.", "businessPhones", Multi),
            new("mobilePhone", Text, Writable, "The user's mobile telephone number.", "mobilePhone"),
            new("streetAddress", Text, Writable, "The street address of the user's place of business.", "streetAddress"),
            new("city", Text, Writable, "The city of the user's place of business.", "city"),
            new("state", Text, Writable, "The state or province of the user's place of business.", "state"),
            new("postalCode", Text, Writable, "The postal code of the user's place of business.", "postalCode"),
            new("country", Text, Writable, "The country of the user's place of business.", "country"),
            new("usageLocation", Text, Writable, "A two-letter country code, required before Microsoft 365 licences can be assigned.", "usageLocation"),
            new("preferredLanguage", Text, Writable, "The user's preferred language, as an ISO 639-1 code such as en-GB.", "preferredLanguage"),
            new("userType", Text, Writable, "Member, or Guest for an account invited from outside the organisation.", "userType"),
            new("onPremisesImmutableId", Text, Writable, "Links a cloud user to an on-premises account, for a user whose domain is federated.", "onPremisesImmutableId"),
            new(Manager, Reference, Writable, "The user's manager.", null, ReferencesObjectType: UserObjectType),
            new("creationType", Text, ReadOnly, "How the account was created, i.e. Invitation for a guest; null for an ordinary account.", "creationType", IsOperational: true),
            new("externalUserState", Text, ReadOnly, "For a guest, whether the invitation has been accepted.", "externalUserState", IsOperational: true),
            new("createdDateTime", DateTime, ReadOnly, "When the user was created.", "createdDateTime", IsOperational: true)
        };

        attributes.AddRange(OnPremisesAttributes(includeUserOnly: true));
        for (var i = 1; i <= ExtensionAttributeCount; i++)
        {
            attributes.Add(new GraphSchemaAttribute($"extensionAttribute{i}", Text, Writable,
                $"Exchange custom attribute {i}. For a synchronised user, written in on-premises Active Directory instead.",
                OnPremisesExtensionAttributes, Member: $"extensionAttribute{i}"));
        }

        attributes.Add(SourceOfAuthorityAttribute("user"));
        return attributes;
    }

    private static List<GraphSchemaAttribute> BuildGroupAttributes()
    {
        var attributes = new List<GraphSchemaAttribute>
        {
            IdAttribute(),
            new("displayName", Text, Writable, "The group's name. Required to create a group.", "displayName", Required: true),
            new("mailNickname", Text, Writable, "The mail alias. Required to create a group.", "mailNickname", Required: true),
            new("mailEnabled", Boolean, OnCreate, "Whether the group has a mailbox or receives mail. Required to create a group.", "mailEnabled", Required: true),
            new("securityEnabled", Boolean, Writable, "Whether the group can be used to grant access. Required to create a group.", "securityEnabled", Required: true),
            new("groupTypes", Text, OnCreate, "Unified for a Microsoft 365 group, and DynamicMembership for a group whose members follow its membership rule. Empty for a security group.", "groupTypes", Multi),
            new("description", Text, Writable, "The group's description.", "description"),
            new("visibility", Text, Writable, "Public, Private or HiddenMembership, for a Microsoft 365 group.", "visibility"),
            new("membershipRule", Text, Writable, "The rule that decides a dynamic group's members.", "membershipRule"),
            new("membershipRuleProcessingState", Text, Writable, "On or Paused: whether a dynamic group's rule is being applied.", "membershipRuleProcessingState"),
            new("mail", Text, ReadOnly, "The group's SMTP address. Read-only in Graph.", "mail"),
            new("proxyAddresses", Text, ReadOnly, "Every address the mail system knows the group by. Read-only in Graph.", "proxyAddresses", Multi),
            new("securityIdentifier", Text, ReadOnly, "The group's security identifier in Entra ID.", "securityIdentifier", IsOperational: true),
            new(Members, Reference, Writable, "The group's members: users and groups.", null, Multi),
            new(Owners, Reference, Writable, "The group's owners.", null, Multi, ReferencesObjectType: UserObjectType),
            new("createdDateTime", DateTime, ReadOnly, "When the group was created.", "createdDateTime", IsOperational: true)
        };

        attributes.AddRange(OnPremisesAttributes(includeUserOnly: false));
        attributes.Add(SourceOfAuthorityAttribute("group"));
        return attributes;
    }

    private static GraphSchemaAttribute IdAttribute() =>
        new(Id, Text, ReadOnly, "The object's identifier, assigned by Entra ID and never changed. The External ID.", Id, Required: true, AlwaysImported: true);

    /// <summary>
    /// What a synchronised object carries from on-premises Active Directory. All read-only in Graph, and what
    /// <c>sourceOfAuthority</c> is derived from.
    /// </summary>
    private static IEnumerable<GraphSchemaAttribute> OnPremisesAttributes(bool includeUserOnly)
    {
        yield return new GraphSchemaAttribute("onPremisesSyncEnabled", Boolean, ReadOnly, "True while the object is synchronised from on-premises Active Directory.", "onPremisesSyncEnabled", IsOperational: true);
        yield return new GraphSchemaAttribute("onPremisesLastSyncDateTime", DateTime, ReadOnly, "When the object was last synchronised from on-premises Active Directory.", "onPremisesLastSyncDateTime", IsOperational: true);
        yield return new GraphSchemaAttribute("onPremisesSecurityIdentifier", Text, ReadOnly, "The object's security identifier in on-premises Active Directory.", "onPremisesSecurityIdentifier", IsOperational: true);
        yield return new GraphSchemaAttribute("onPremisesSamAccountName", Text, ReadOnly, "The object's sAMAccountName in on-premises Active Directory.", "onPremisesSamAccountName", IsOperational: true);
        yield return new GraphSchemaAttribute("onPremisesDomainName", Text, ReadOnly, "The on-premises Active Directory domain the object comes from.", "onPremisesDomainName", IsOperational: true);

        if (!includeUserOnly)
            yield break;

        yield return new GraphSchemaAttribute("onPremisesDistinguishedName", Text, ReadOnly, "The user's distinguished name in on-premises Active Directory.", "onPremisesDistinguishedName", IsOperational: true);
        yield return new GraphSchemaAttribute("onPremisesUserPrincipalName", Text, ReadOnly, "The user's userPrincipalName in on-premises Active Directory.", "onPremisesUserPrincipalName", IsOperational: true);
    }

    private static GraphSchemaAttribute SourceOfAuthorityAttribute(string objectTypeName) =>
        new(SourceOfAuthority, Text, ReadOnly,
            $"Where the {objectTypeName} is managed, derived by JIM: cloud (in Entra ID, so JIM can write to it through Microsoft Graph), " +
            "onPremisesDirectory (synchronised from Active Directory, so most attributes are written there instead) or externalEntraDirectory " +
            "(a guest from another tenant). An object whose Source of Authority was switched to Entra ID reads as cloud only with Track Source " +
            "of Authority Changes on. Flow it into a Metaverse Attribute to scope export Synchronisation Rules on it.",
            null, AlwaysImported: true, IsConnectorDefined: true);
}
