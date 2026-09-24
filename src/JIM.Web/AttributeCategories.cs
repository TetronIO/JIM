// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using MudBlazor;

namespace JIM.Web;

/// <summary>
/// Heuristic mapping of built-in attribute names to display categories, and the categories' display order and
/// icons. A presentation-layer concern that will be replaced by admin-configurable layouts in the future.
/// <para>
/// This used to be duplicated between <c>MvoDetailsPanel</c> and <c>MvoDetailsTabs</c> (identical copies), and
/// the Inspect view's "Group by: Category" needs the same mapping again; extracted here so there is exactly one
/// place that decides which category an attribute belongs to, per JIM.Web/CLAUDE.md's "Conventions hierarchy".
/// </para>
/// </summary>
public static class AttributeCategories
{
    /// <summary>The category every attribute not named below falls into.</summary>
    public const string Other = "Other";

    private static readonly Dictionary<string, string> Map = new()
    {
        // Identity
        { Constants.BuiltInAttributes.DisplayName, "Identity" },
        { Constants.BuiltInAttributes.AccountName, "Identity" },
        { Constants.BuiltInAttributes.CommonName, "Identity" },
        { Constants.BuiltInAttributes.DistinguishedName, "Identity" },
        { Constants.BuiltInAttributes.UserPrincipalName, "Identity" },
        { Constants.BuiltInAttributes.ObjectGuid, "Identity" },
        { Constants.BuiltInAttributes.ObjectSid, "Identity" },
        { Constants.BuiltInAttributes.EmployeeId, "Identity" },
        { Constants.BuiltInAttributes.EmployeeNumber, "Identity" },
        { Constants.BuiltInAttributes.ObjectIdentifier, "Identity" },
        { Constants.BuiltInAttributes.SubjectIdentifier, "Identity" },
        { Constants.BuiltInAttributes.Type, "Identity" },
        { Constants.BuiltInAttributes.FirstName, "Identity" },
        { Constants.BuiltInAttributes.LastName, "Identity" },
        { Constants.BuiltInAttributes.Description, "Identity" },

        // Contact
        { Constants.BuiltInAttributes.Email, "Contact" },
        { Constants.BuiltInAttributes.TelephoneNumber, "Contact" },
        { Constants.BuiltInAttributes.MobileNumber, "Contact" },
        { Constants.BuiltInAttributes.FacsimileTelephoneNumber, "Contact" },
        { Constants.BuiltInAttributes.IpPhone, "Contact" },
        { Constants.BuiltInAttributes.Pager, "Contact" },
        { Constants.BuiltInAttributes.HomePhone, "Contact" },
        { Constants.BuiltInAttributes.OtherTelephones, "Contact" },
        { Constants.BuiltInAttributes.OtherMobiles, "Contact" },
        { Constants.BuiltInAttributes.OtherIpPhones, "Contact" },
        { Constants.BuiltInAttributes.OtherPagers, "Contact" },
        { Constants.BuiltInAttributes.OtherFacsimileTelephoneNumbers, "Contact" },
        { Constants.BuiltInAttributes.MailNickname, "Contact" },
        { Constants.BuiltInAttributes.ProxyAddresses, "Contact" },
        { Constants.BuiltInAttributes.WebPage, "Contact" },
        { Constants.BuiltInAttributes.Urls, "Contact" },

        // Organisation
        { Constants.BuiltInAttributes.Company, "Organisation" },
        { Constants.BuiltInAttributes.Department, "Organisation" },
        { Constants.BuiltInAttributes.JobTitle, "Organisation" },
        { Constants.BuiltInAttributes.Manager, "Organisation" },
        { Constants.BuiltInAttributes.ManagedBy, "Organisation" },
        { Constants.BuiltInAttributes.Office, "Organisation" },
        { Constants.BuiltInAttributes.Team, "Organisation" },
        { Constants.BuiltInAttributes.Organisation, "Organisation" },
        { Constants.BuiltInAttributes.Pronouns, "Organisation" },

        // Location
        { Constants.BuiltInAttributes.Country, "Location" },
        { Constants.BuiltInAttributes.CountryCode, "Location" },
        { Constants.BuiltInAttributes.Locality, "Location" },
        { Constants.BuiltInAttributes.PostalCode, "Location" },
        { Constants.BuiltInAttributes.StateOrProvince, "Location" },
        { Constants.BuiltInAttributes.StreetAddress, "Location" },
        { Constants.BuiltInAttributes.PostalAddresses, "Location" },
        { Constants.BuiltInAttributes.PhysicalDeliveryOfficeName, "Location" },
        { Constants.BuiltInAttributes.PostOfficeBoxes, "Location" },

        // Group
        { Constants.BuiltInAttributes.StaticMembers, "Group" },
        { Constants.BuiltInAttributes.Owners, "Group" },
        { Constants.BuiltInAttributes.GroupType, "Group" },
        { Constants.BuiltInAttributes.GroupScope, "Group" },
        { Constants.BuiltInAttributes.GroupTypeFlags, "Group" },

        // Account
        { Constants.BuiltInAttributes.UserAccountControl, "Account" },
        { Constants.BuiltInAttributes.AccountExpires, "Account" },
        { Constants.BuiltInAttributes.EmployeeType, "Account" },
        { Constants.BuiltInAttributes.EmployeeStartDate, "Account" },
        { Constants.BuiltInAttributes.EmployeeEndDate, "Account" },
        { Constants.BuiltInAttributes.Status, "Account" },
        { Constants.BuiltInAttributes.HomeDirectory, "Account" },
        { Constants.BuiltInAttributes.HomeDrive, "Account" },
        { Constants.BuiltInAttributes.ScriptPath, "Account" },
        { Constants.BuiltInAttributes.UserSharedFolder, "Account" },
        { Constants.BuiltInAttributes.IdentityAssuranceLevel, "Account" },

        // Security
        { Constants.BuiltInAttributes.AltSecurityIdentities, "Security" },
        { Constants.BuiltInAttributes.SidHistory, "Security" },
        { Constants.BuiltInAttributes.UserCertificates, "Security" },
        { Constants.BuiltInAttributes.Photo, "Security" },
    };

    private static readonly Dictionary<string, string> CategoryIcons = new()
    {
        { "Identity", Icons.Material.Filled.Badge },
        { "Contact", Icons.Material.Filled.ContactPhone },
        { "Organisation", Icons.Material.Filled.Business },
        { "Location", Icons.Material.Filled.LocationOn },
        { "Group", Icons.Material.Filled.Group },
        { "Account", Icons.Material.Filled.ManageAccounts },
        { "Security", Icons.Material.Filled.Security },
        { Other, Icons.Material.Filled.MoreHoriz },
    };

    /// <summary>Category display order. A caller skips categories with no populated attributes itself.</summary>
    public static readonly string[] CategoryOrder =
        ["Identity", "Contact", "Organisation", "Location", "Group", "Account", "Security", Other];

    /// <summary>The category a built-in attribute name belongs to, or <see cref="Other"/> when it is not mapped.</summary>
    public static string GetCategory(string attributeName) => Map.GetValueOrDefault(attributeName, Other);

    /// <summary>The icon for a category name, as returned by <see cref="GetCategory"/> or a member of <see cref="CategoryOrder"/>.</summary>
    public static string GetIcon(string categoryName) => CategoryIcons.GetValueOrDefault(categoryName, CategoryIcons[Other]);
}
