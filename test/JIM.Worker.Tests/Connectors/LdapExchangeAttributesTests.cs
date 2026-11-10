// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Exchange;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The connector-defined Exchange attributes (#2021, PRD requirement 4): what the LDAP Connector adds to an Active
/// Directory Object Type when its Connected System manages Exchange recipients. Each class gets only the attributes
/// that apply to it, so an administrator is never offered a mailbox database for a contact.
/// </summary>
[TestFixture]
public class LdapExchangeAttributesTests
{
    [TestCase("user", new[] { "Exchange Recipient Type", "Exchange Alias", "Exchange Database", "Exchange Primary SMTP Address", "Exchange External Address" })]
    [TestCase("group", new[] { "Exchange Recipient Type", "Exchange Alias", "Exchange Primary SMTP Address" })]
    [TestCase("contact", new[] { "Exchange Recipient Type", "Exchange Alias", "Exchange Primary SMTP Address", "Exchange External Address" })]
    public void AddTo_RecipientClass_AddsTheAttributesThatApplyToIt(string className, string[] expected)
    {
        var objectType = new ConnectorSchemaObjectType(className);

        LdapExchangeAttributes.AddTo(objectType);

        Assert.That(objectType.Attributes.Select(a => a.Name), Is.EqualTo(expected));
    }

    [TestCase("organizationalUnit")]
    [TestCase("computer")]
    public void AddTo_ClassThatIsNeverARecipient_AddsNothing(string className)
    {
        var objectType = new ConnectorSchemaObjectType(className);

        LdapExchangeAttributes.AddTo(objectType);

        Assert.That(objectType.Attributes, Is.Empty);
    }

    [Test]
    public void AddTo_EveryAttribute_IsAWritableSingleValuedTextConnectorDefinedAttribute()
    {
        var objectType = new ConnectorSchemaObjectType("user");

        LdapExchangeAttributes.AddTo(objectType);

        Assert.That(objectType.Attributes, Has.All.Matches<ConnectorSchemaAttribute>(a =>
            a.IsConnectorDefined && a.Type == AttributeDataType.Text && a.AttributePlurality == AttributePlurality.SingleValued
            && a.Writability == AttributeWritability.Writable && !string.IsNullOrWhiteSpace(a.Description)));
    }

    [Test]
    public void IsExchangeAttribute_TellsTheConnectorsOwnNamesFromDirectoryAttributes()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LdapExchangeAttributes.IsExchangeAttribute("Exchange Recipient Type"), Is.True);
            Assert.That(LdapExchangeAttributes.IsExchangeAttribute("mailNickname"), Is.False);
            Assert.That(LdapExchangeAttributes.IsExchangeAttribute("exchange recipient type"), Is.False,
                "the names are exact, so no directory attribute can be mistaken for one");
        }
    }

    // ---- Import: deriving the declared state back from what Exchange stamped -------------------------------------

    [TestCase(null, "None")]
    [TestCase("1", "Mailbox")]
    [TestCase("4", "SharedMailbox")]
    [TestCase("16", "RoomMailbox")]
    [TestCase("32", "EquipmentMailbox")]
    [TestCase("128", "MailUser")]
    [TestCase("64", "MailContact")]
    [TestCase("256", "MailEnabledGroup")]
    [TestCase("1024", "MailEnabledGroup")]
    [TestCase("512", "MailEnabledGroup")]
    public void Derive_RecipientType_IsReadFromRecipientTypeDetails(string? recipientTypeDetails, string expected)
    {
        var derived = LdapExchangeAttributes.Derive(Read(("msExchRecipientTypeDetails", recipientTypeDetails)), [LdapExchangeAttributes.RecipientType]);

        Assert.That(Values(derived, LdapExchangeAttributes.RecipientType), Is.EqualTo(new[] { expected }));
    }

    [TestCase("1073741833", "MailEnabledGroup")]
    [TestCase("1", "MailEnabledGroup")]
    [TestCase("6", "MailContact")]
    [TestCase("0", "Unmanaged")]
    [TestCase("7", "Unmanaged")]
    public void Derive_NoRecipientTypeDetails_IsReadFromRecipientDisplayType(string recipientDisplayType, string expected)
    {
        // Exchange stamps msExchRecipientTypeDetails on the user objects it makes recipients, not on a group it
        // mail-enables or a contact it mail-enables: the AD lab's mail-enabled universal security group carried only
        // msExchRecipientDisplayType 1073741833 (Scenario 003 run 42744901343), and a mail contact JIM made in the GAL
        // read as Unmanaged rather than MailContact (Scenario 003 run 42769078688), so each declared type was reported
        // unconfirmed. The display type stands in where the details are absent: 1 is a mail-enabled distribution group,
        // 1073741833 a mail-enabled security group, 6 a mail contact, and any other value is a recipient JIM does not
        // manage.
        var derived = LdapExchangeAttributes.Derive(Read(("msExchRecipientDisplayType", recipientDisplayType)), [LdapExchangeAttributes.RecipientType]);

        Assert.That(Values(derived, LdapExchangeAttributes.RecipientType), Is.EqualTo(new[] { expected }));
    }

    [Test]
    public void Derive_RecipientTypeDetailsPresent_WinsOverTheDisplayType()
    {
        var derived = LdapExchangeAttributes.Derive(Read(("msExchRecipientTypeDetails", "1"), ("msExchRecipientDisplayType", "1073741824")),
            [LdapExchangeAttributes.RecipientType]);

        Assert.That(Values(derived, LdapExchangeAttributes.RecipientType), Is.EqualTo(new[] { ExchangeRecipientTypes.Mailbox }));
    }

    [TestCase("2147483648")]
    [TestCase("2")]
    [TestCase("not a number")]
    public void Derive_RecipientTypeJimDoesNotManage_IsUnmanagedRatherThanAWrongOne(string recipientTypeDetails)
    {
        // A remote mailbox (2147483648) is an Exchange Online recipient and a linked mailbox (2) belongs to another
        // forest; this release manages neither. Leaving the type absent would read as None and invite JIM to enable the
        // recipient again; reporting it as Mailbox would be untrue. Any value present but outside the managed map,
        // unreadable ones included, is a recipient JIM leaves alone.
        var derived = LdapExchangeAttributes.Derive(Read(("msExchRecipientTypeDetails", recipientTypeDetails)), [LdapExchangeAttributes.RecipientType]);

        Assert.That(Values(derived, LdapExchangeAttributes.RecipientType), Is.EqualTo(new[] { ExchangeRecipientTypes.Unmanaged }));
    }

    [Test]
    public void Derive_AddressesDatabaseAndAlias_AreReadFromTheirStampedAttributes()
    {
        var read = Read(
            ("mailNickname", "alice.smith"),
            ("homeMDB", "CN=MDB-VIP,CN=Databases,CN=Exchange Administrative Group (FYDIBOHF23SPDLT),CN=Administrative Groups,CN=PANOPLY,CN=Microsoft Exchange,CN=Services,CN=Configuration,DC=panoply,DC=local"),
            ("proxyAddresses", "smtp:asmith@panoply.local"), ("proxyAddresses", "SMTP:alice.smith@panoply.local"), ("proxyAddresses", "X500:/o=Old/cn=asmith"),
            ("targetAddress", "SMTP:alice@example.com"));

        var derived = LdapExchangeAttributes.Derive(read,
            [LdapExchangeAttributes.Alias, LdapExchangeAttributes.Database, LdapExchangeAttributes.PrimarySmtpAddress, LdapExchangeAttributes.ExternalAddress]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Values(derived, LdapExchangeAttributes.Alias), Is.EqualTo(new[] { "alice.smith" }));
            Assert.That(Values(derived, LdapExchangeAttributes.Database), Is.EqualTo(new[] { "MDB-VIP" }));
            Assert.That(Values(derived, LdapExchangeAttributes.PrimarySmtpAddress), Is.EqualTo(new[] { "alice.smith@panoply.local" }),
                "the primary address is the one whose prefix is upper-case SMTP");
            Assert.That(Values(derived, LdapExchangeAttributes.ExternalAddress), Is.EqualTo(new[] { "alice@example.com" }));
        }
    }

    [Test]
    public void Derive_OnlyWhatIsAskedFor_AndNothingForAbsentValues()
    {
        var derived = LdapExchangeAttributes.Derive(Read(("mailNickname", "alice.smith")),
            [LdapExchangeAttributes.Alias, LdapExchangeAttributes.Database]);

        Assert.That(derived.Select(a => a.Name), Is.EqualTo(new[] { LdapExchangeAttributes.Alias }),
            "an absent homeMDB means no database, not an empty one, and unasked-for attributes are never derived");
    }

    [Test]
    public void SourceAttributes_NamesWhatEachAttributeIsDerivedFrom()
    {
        Assert.That(LdapExchangeAttributes.SourceAttributes([LdapExchangeAttributes.RecipientType, LdapExchangeAttributes.ExternalAddress]),
            Is.EquivalentTo(new[] { "msExchRecipientTypeDetails", "msExchRecipientDisplayType", "targetAddress" }),
            "a group's recipient type is read from its display type, where Exchange stamps no details");
    }

    private static Func<string, IReadOnlyList<string>> Read(params (string Name, string? Value)[] values) =>
        name => values.Where(v => v.Value != null && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).Select(v => v.Value!).ToList();

    private static List<string> Values(IEnumerable<JIM.Models.Staging.ConnectedSystemImportObjectAttribute> derived, string name) =>
        derived.Single(a => a.Name == name).StringValues;
}
