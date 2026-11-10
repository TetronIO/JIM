// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Exchange;
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
    // The names come from JIM.Models, so export staging reads the declared state by the same ones.
    internal const string RecipientType = ExchangeRecipientAttributes.RecipientType;
    internal const string Alias = ExchangeRecipientAttributes.Alias;
    internal const string Database = ExchangeRecipientAttributes.Database;
    internal const string PrimarySmtpAddress = ExchangeRecipientAttributes.PrimarySmtpAddress;
    internal const string ExternalAddress = ExchangeRecipientAttributes.ExternalAddress;

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
    /// Where the recipient type is read when Exchange stamped no <c>msExchRecipientTypeDetails</c>: it does not on a group
    /// it mail-enables, which carries only <c>msExchRecipientDisplayType</c>.
    /// </summary>
    private const string RecipientDisplayTypeSource = "msExchRecipientDisplayType";

    /// <summary>
    /// Exchange's <c>msExchRecipientDisplayType</c> values for the mail-enabled groups JIM manages, read only where a
    /// group has no <c>msExchRecipientTypeDetails</c>. Any other display type is a recipient JIM does not manage.
    /// </summary>
    private static readonly Dictionary<long, string> GroupDisplayTypes = new()
    {
        [1] = ExchangeRecipientTypes.MailEnabledGroup,          // a mail-enabled distribution group
        [1073741833] = ExchangeRecipientTypes.MailEnabledGroup  // a mail-enabled security group
    };

    /// <summary>
    /// Exchange's <c>msExchRecipientTypeDetails</c> values for the recipient types JIM manages. Anything else (a linked
    /// or remote mailbox, a dynamic group) is a recipient this release does not manage, imported as
    /// <see cref="ExchangeRecipientTypes.Unmanaged"/>.
    /// </summary>
    private static readonly Dictionary<long, string> RecipientTypes = new()
    {
        [1] = ExchangeRecipientTypes.Mailbox,
        [4] = ExchangeRecipientTypes.SharedMailbox,
        [16] = ExchangeRecipientTypes.RoomMailbox,
        [32] = ExchangeRecipientTypes.EquipmentMailbox,
        [64] = ExchangeRecipientTypes.MailContact,
        [128] = ExchangeRecipientTypes.MailUser,
        [256] = ExchangeRecipientTypes.MailEnabledGroup,  // a universal distribution group
        [512] = ExchangeRecipientTypes.MailEnabledGroup,  // a non-universal group, which older Exchange versions mail-enabled
        [1024] = ExchangeRecipientTypes.MailEnabledGroup  // a universal security group
    };

    /// <summary>
    /// The directory attributes an import must read so that <paramref name="attributeNames"/> can be derived.
    /// </summary>
    internal static IEnumerable<string> SourceAttributes(IEnumerable<string> attributeNames) =>
        attributeNames.Where(Sources.ContainsKey)
            .SelectMany(n => n == RecipientType ? [Sources[n], RecipientDisplayTypeSource] : new[] { Sources[n] })
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Derives <paramref name="attributeNames"/> from what Exchange stamped on an object, read through
    /// <paramref name="read"/> (a directory attribute's values, empty when it has none). An attribute with nothing to
    /// derive from is left out rather than given an empty value, as an absent directory attribute is, with one
    /// exception: no recipient type at all means the object is not a recipient, which is None. A recipient type outside
    /// the ones JIM manages, an unreadable one included, is Unmanaged: left out, it would read as None and invite JIM to
    /// enable a recipient that already exists.
    /// </summary>
    internal static List<ConnectedSystemImportObjectAttribute> Derive(Func<string, IReadOnlyList<string>> read, IEnumerable<string> attributeNames)
    {
        var derived = new List<ConnectedSystemImportObjectAttribute>();
        foreach (var name in attributeNames.Where(Sources.ContainsKey))
        {
            var values = read(Sources[name]);
            var value = name switch
            {
                RecipientType => DeriveRecipientType(values, read(RecipientDisplayTypeSource)),
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

    private static string DeriveRecipientType(IReadOnlyList<string> details, IReadOnlyList<string> displayTypes)
    {
        if (details.Count > 0)
            return long.TryParse(details[0], out var detail) && RecipientTypes.TryGetValue(detail, out var recipientType)
                ? recipientType
                : ExchangeRecipientTypes.Unmanaged;

        // A group Exchange mail-enabled carries a display type and no details.
        if (displayTypes.Count > 0)
            return long.TryParse(displayTypes[0], out var displayType) && GroupDisplayTypes.TryGetValue(displayType, out var groupType)
                ? groupType
                : ExchangeRecipientTypes.Unmanaged;

        return ExchangeRecipientTypes.None;
    }

    /// <summary>The value of a distinguished name's first RDN: a database's name from its <c>homeMDB</c>.</summary>
    private static string? FirstRdnValue(string distinguishedName) =>
        LdapDistinguishedName.TryParse(distinguishedName, out var dn) ? dn.LeafRdn.Components[0].Value : null;

    private static string StripSmtpPrefix(string address) =>
        address.StartsWith("SMTP:", StringComparison.OrdinalIgnoreCase) ? address[5..] : address;
}
