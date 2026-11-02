// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Connectors.LDAP;

/// <summary>
/// The connector-defined attributes through which a Synchronisation Rule declares an object's Exchange recipient state
/// (#2021, PRD requirement 4). The LDAP Connector adds them to Active Directory Object Types when its Connected System
/// manages Exchange recipients. They are never written to the directory as they stand: import derives them from what
/// Exchange stamped on the object, and export hands them to the Exchange step.
/// </summary>
/// <remarks>
/// Their names contain spaces, which no LDAP attribute name can, so a directory attribute can never be mistaken for one,
/// and they read as they should in the portal with no separate display name.
/// </remarks>
internal static class LdapExchangeAttributes
{
    internal const string RecipientType = "Exchange Recipient Type";
    internal const string Alias = "Exchange Alias";
    internal const string Database = "Exchange Database";
    internal const string PrimarySmtpAddress = "Exchange Primary SMTP Address";
    internal const string ExternalAddress = "Exchange External Address";

    private const string User = "user";
    private const string Group = "group";
    private const string Contact = "contact";

    /// <summary>
    /// Each attribute, the classes it applies to, and what an administrator reads about it. In the order the portal
    /// lists them.
    /// </summary>
    private static readonly (string Name, string[] Classes, string Description)[] Definitions =
    [
        (RecipientType, [User, Group, Contact],
            "What kind of Exchange recipient the object is: None, Mailbox, SharedMailbox, RoomMailbox, EquipmentMailbox, MailUser, MailContact or MailEnabledGroup."),
        (Alias, [User, Group, Contact],
            "The recipient's Exchange alias. When empty, the object's mailNickname is used."),
        (Database, [User],
            "The mailbox database for a mailbox, by name. When empty, Exchange chooses one."),
        (PrimarySmtpAddress, [User, Group, Contact],
            "The recipient's primary SMTP address. When empty, Exchange's Email Address Policies decide it."),
        (ExternalAddress, [User, Contact],
            "For a mail user or mail contact, the external SMTP address mail is delivered to.")
    ];

    private static readonly HashSet<string> Names = Definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Adds the Exchange attributes that apply to <paramref name="objectType"/>'s class, if any do.
    /// </summary>
    internal static void AddTo(ConnectorSchemaObjectType objectType)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        foreach (var definition in Definitions.Where(d => d.Classes.Contains(objectType.Name, StringComparer.OrdinalIgnoreCase)))
        {
            objectType.Attributes.Add(new ConnectorSchemaAttribute(definition.Name, AttributeDataType.Text, AttributePlurality.SingleValued)
            {
                Description = definition.Description,
                IsConnectorDefined = true
            });
        }
    }

    /// <summary>
    /// Whether <paramref name="attributeName"/> is one of these attributes rather than a directory attribute.
    /// </summary>
    internal static bool IsExchangeAttribute(string attributeName) => Names.Contains(attributeName);

    // ---- Import -------------------------------------------------------------------------------------------------

    /// <summary>The directory attribute each one is derived from on import: what Exchange stamps when it acts.</summary>
    private static readonly Dictionary<string, string> Sources = new(StringComparer.Ordinal)
    {
        [RecipientType] = "msExchRecipientTypeDetails",
        [Alias] = "mailNickname",
        [Database] = "homeMDB",
        [PrimarySmtpAddress] = "proxyAddresses",
        [ExternalAddress] = "targetAddress"
    };

    /// <summary>
    /// Exchange's <c>msExchRecipientTypeDetails</c> values for the recipient types JIM manages. Anything else (a linked
    /// or remote mailbox, a dynamic group) is a recipient this release does not manage.
    /// </summary>
    private static readonly Dictionary<long, string> RecipientTypes = new()
    {
        [1] = "Mailbox",
        [4] = "SharedMailbox",
        [16] = "RoomMailbox",
        [32] = "EquipmentMailbox",
        [64] = "MailContact",
        [128] = "MailUser",
        [256] = "MailEnabledGroup",  // a universal distribution group
        [512] = "MailEnabledGroup",  // a non-universal group, which older Exchange versions mail-enabled
        [1024] = "MailEnabledGroup"  // a universal security group
    };

    /// <summary>
    /// The directory attributes an import must read so that <paramref name="attributeNames"/> can be derived.
    /// </summary>
    internal static IEnumerable<string> SourceAttributes(IEnumerable<string> attributeNames) =>
        attributeNames.Where(Sources.ContainsKey).Select(n => Sources[n]).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Derives <paramref name="attributeNames"/> from what Exchange stamped on an object, read through
    /// <paramref name="read"/> (a directory attribute's values, empty when it has none). An attribute with nothing to
    /// derive from is left out rather than given an empty value, as an absent directory attribute is, with one
    /// exception: no recipient type at all means the object is not a recipient, which is None.
    /// </summary>
    internal static List<ConnectedSystemImportObjectAttribute> Derive(Func<string, IReadOnlyList<string>> read, IEnumerable<string> attributeNames)
    {
        var derived = new List<ConnectedSystemImportObjectAttribute>();
        foreach (var name in attributeNames.Where(Sources.ContainsKey))
        {
            var values = read(Sources[name]);
            var value = name switch
            {
                RecipientType => DeriveRecipientType(values),
                Database => values.Count > 0 ? FirstRdnValue(values[0]) : null,
                PrimarySmtpAddress => values.FirstOrDefault(v => v.StartsWith("SMTP:", StringComparison.Ordinal))?[5..],
                ExternalAddress => values.Count > 0 ? StripSmtpPrefix(values[0]) : null,
                _ => values.FirstOrDefault()
            };

            if (!string.IsNullOrEmpty(value))
                derived.Add(new ConnectedSystemImportObjectAttribute { Name = name, Type = AttributeDataType.Text, StringValues = [value] });
        }

        return derived;
    }

    private static string? DeriveRecipientType(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return "None";

        return long.TryParse(values[0], out var details) && RecipientTypes.TryGetValue(details, out var recipientType) ? recipientType : null;
    }

    /// <summary>The value of a distinguished name's first RDN: a database's name from its <c>homeMDB</c>.</summary>
    private static string? FirstRdnValue(string distinguishedName) =>
        LdapDistinguishedName.TryParse(distinguishedName, out var dn) ? dn.LeafRdn.Components[0].Value : null;

    private static string StripSmtpPrefix(string address) =>
        address.StartsWith("SMTP:", StringComparison.OrdinalIgnoreCase) ? address[5..] : address;
}
