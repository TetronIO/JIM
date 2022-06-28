namespace JIM.Models.Core
{
    public static class Constants
    {
        public static class Config
        {
            public static string DatabaseHostname => "DB_HOSTNAME";
            public static string DatabaseName => "DB_NAME";
            public static string DatabaseUsername => "DB_USERNAME";
            public static string DatabasePassword => "DB_PASSWORD";
        }

        public static class BuiltInObjectTypes
        {
            public static string Users => "Users";
            public static string Groups => "Groups";
        }

        public static class BuiltInAttributes
        {
            // common
            public static string AccountName => "Account Name";
            public static string DisplayName => "Display Name";
            public static string ExtensionAttribute1 => "Extension Attribute 1";
            public static string ExtensionAttribute10 => "Extension Attribute 10";
            public static string ExtensionAttribute11 => "Extension Attribute 11";
            public static string ExtensionAttribute12 => "Extension Attribute 12";
            public static string ExtensionAttribute13 => "Extension Attribute 13";
            public static string ExtensionAttribute14 => "Extension Attribute 14";
            public static string ExtensionAttribute15 => "Extension Attribute 15";
            public static string ExtensionAttribute2 => "Extension Attribute 2";
            public static string ExtensionAttribute3 => "Extension Attribute 3";
            public static string ExtensionAttribute4 => "Extension Attribute 4";
            public static string ExtensionAttribute5 => "Extension Attribute 5";
            public static string ExtensionAttribute6 => "Extension Attribute 6";
            public static string ExtensionAttribute7 => "Extension Attribute 7";
            public static string ExtensionAttribute8 => "Extension Attribute 8";
            public static string ExtensionAttribute9 => "Extension Attribute 9";
            public static string HideFromAddressLists => "Hide From Address Lists";
            public static string MailNickname => "Mail Nickname";
            public static string ObjectGUID => "objectGUID";
            public static string ObjectSid => "objectSid";
            public static string ProxyAddresses => "Proxy Addresses";

            // users
            // https://docs.microsoft.com/en-us/windows/win32/ad/user-object-attributes
            public static string AccountExpires => "Account Expires";
            public static string AltSecurityIdentities => "Alt Security Identities";
            public static string CommonName => "Common Name";
            public static string Company => "Company";
            public static string Country => "Country";
            public static string CountryCode => "Country Code";
            public static string StartDate => "Start Date";
            public static string EndDate => "End Date";
            public static string Department => "Department";
            public static string Description => "Description";
            public static string DistinguishedName => "Distinguished Name";
            public static string Email => "Email";
            public static string EmployeeID => "Employee ID";
            public static string EmployeeType => "Employee Type";
            public static string FacsimileTelephoneNumber => "Facsimile Telephone Number";
            public static string FirstName => "First Name";
            public static string HomeDirectory => "Home Directory";
            public static string HomeDrive => "Home Drive";
            public static string HomePhone => "Home Phone";
            public static string IpPhone => "IP Phone";
            public static string JobTitle => "Job Title";
            public static string LastName => "Last Name";
            public static string Locality => "Locality";
            public static string Manager => "Manager";
            public static string MobileNumber => "Mobile Number";
            public static string ObjectIdentifier => "Object Identifier";
            public static string Office => "Office";
            public static string Organisation => "Organisation";
            public static string OtherFacsimileTelephoneNumbers => "Other Facsimile Telephone Numbers";
            public static string OtherIpPhones => "Other IP Phones";
            public static string OtherMobiles => "Other Mobiles";
            public static string OtherPagers => "Other Pagers";
            public static string OtherTelephones => "Other Telephones";
            public static string Pager => "Pager";
            public static string PhysicalDeliveryOfficeName => "Physical Delivery Office Name";
            public static string PostalAddresses => "Postal Address";
            public static string PostalCode => "Postal Code";
            public static string PostOfficeBoxes => "Post Office Boxes";
            public static string Pronouns => "Pronouns";
            public static string ScriptPath => "Script Path";
            public static string SidHistory => "sIDHistory";
            public static string StateOrProvince => "State or Province";
            public static string Status => "Status";
            public static string StreetAddress => "StreetAddress";
            public static string Team => "Team";
            public static string TelephoneNumber => "Telephone Number";
            public static string Type => "Type";
            public static string Urls => "Urls";
            public static string UserAccountControl => "User Account Control";
            public static string UserCertificates => "User Certificates";
            public static string UserPrincipalName => "User Principal Name";
            public static string UserSharedFolder => "User Shared Folder";
            public static string WebPage => "WebPage";

            // groups
            // https://docs.microsoft.com/en-us/windows/win32/ad/group-objects
            // https://docs.microsoft.com/en-us/exchange/recipients/mail-enabled-security-groups?view=exchserver-2019
            public static string StaticMembers => "Static Members";
            /// <summary>
            /// https://devblogs.microsoft.com/scripting/how-can-i-tell-whether-a-group-is-a-security-group-or-a-distribution-group/
            /// </summary>
            public static string GroupType => "Group Type";
            /// <summary>
            /// https://docs.microsoft.com/en-us/windows/security/identity-protection/access-control/active-directory-security-groups#group-scope
            /// </summary>
            public static string GroupScope => "Group Scope";
            public static string Info => "Info";
            public static string ManagedBy => "Managed By";
            public static string Owners => "Owners";
        }

        public static class BuiltInRoles
        {
            public static string Administrators => "Administrators";
            public static string Users => "Users";
            public static string RoleClaimType => "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
        }

        public static class BuiltInExampleDataSets
        {
            public static string Companies => "Companies";
            public static string Departments => "Departments";
            public static string Teams => "Teams";
            public static string JobTitles => "Job Titles";
            public static string FirstnamesMale => "Firstnames Male";
            public static string FirstnamesFemale => "Firstnames Female";
            public static string Lastnames => "Lastnames";
            public static string Adjectives => "Adjectives";
            public static string Colours => "Colours";
            public static string Words => "Words";
            public static string GroupNameEndings => "Group Name Endings";
        }

        public static class BuiltInDataGenerationTemplates
        {
            public static string UsersEn => "Users En";
            public static string GroupsEn => "Groups En";
        }
    }
}
