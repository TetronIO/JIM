// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Exchange;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How the LDAP Connector converges a recipient (#2021, requirements 8, 9 and 15 to 20): from what Exchange holds now
/// and what the Synchronisation Rules declare, the commands that bring the one to the other, and nothing more. A
/// recipient already in its declared state needs no command at all.
/// </summary>
[TestFixture]
public class LdapExchangeConvergePlannerTests
{
    private static readonly Guid AliceGuid = Guid.Parse("6f0b1a2c-3d4e-4f50-8a9b-0c1d2e3f4a5b");

    private static readonly ExchangeRecipientTarget Alice = new()
    {
        ConnectedSystemObjectId = Guid.NewGuid(),
        ObjectGuid = AliceGuid,
        DistinguishedName = "CN=Alice,OU=People,DC=panoply,DC=local",
        ObjectType = "user",
        DomainController = "dc1.panoply.local"
    };

    // ---- Exchange's RecipientTypeDetails ------------------------------------------------------------------------

    [TestCase("UserMailbox", ExchangeRecipientTypes.Mailbox)]
    [TestCase("SharedMailbox", ExchangeRecipientTypes.SharedMailbox)]
    [TestCase("RoomMailbox", ExchangeRecipientTypes.RoomMailbox)]
    [TestCase("EquipmentMailbox", ExchangeRecipientTypes.EquipmentMailbox)]
    [TestCase("MailUser", ExchangeRecipientTypes.MailUser)]
    [TestCase("MailContact", ExchangeRecipientTypes.MailContact)]
    [TestCase("MailUniversalDistributionGroup", ExchangeRecipientTypes.MailEnabledGroup)]
    [TestCase("MailUniversalSecurityGroup", ExchangeRecipientTypes.MailEnabledGroup)]
    [TestCase("MailNonUniversalGroup", ExchangeRecipientTypes.MailEnabledGroup)]
    [TestCase("User", ExchangeRecipientTypes.None)]
    [TestCase("DisabledUser", ExchangeRecipientTypes.None)]
    [TestCase("Contact", ExchangeRecipientTypes.None)]
    [TestCase("UniversalDistributionGroup", ExchangeRecipientTypes.None)]
    [TestCase("UniversalSecurityGroup", ExchangeRecipientTypes.None)]
    [TestCase("NonUniversalGroup", ExchangeRecipientTypes.None)]
    [TestCase("usermailbox", ExchangeRecipientTypes.Mailbox)]
    [TestCase("RemoteUserMailbox", ExchangeRecipientTypes.Unmanaged)]
    [TestCase("LinkedMailbox", ExchangeRecipientTypes.Unmanaged)]
    [TestCase("DynamicDistributionGroup", ExchangeRecipientTypes.Unmanaged)]
    [TestCase("", ExchangeRecipientTypes.Unmanaged)]
    public void RecipientTypeOf_ExchangesDetails_IsJimsType(string details, string expected)
    {
        Assert.That(LdapExchangeConvergePlanner.RecipientTypeOf(details), Is.EqualTo(expected));
    }

    [TestCase("user", "Get-User")]
    [TestCase("contact", "Get-Contact")]
    [TestCase("group", "Get-Group")]
    public void ReadCommandFor_ObjectType_IsTheCmdletThatReadsItWhetherOrNotItIsARecipient(string objectType, string expected)
    {
        Assert.That(LdapExchangeConvergePlanner.ReadCommandFor(objectType), Is.EqualTo(expected));
    }

    // ---- None to a recipient type: enable ------------------------------------------------------------------------

    [TestCase(ExchangeRecipientTypes.Mailbox, null)]
    [TestCase(ExchangeRecipientTypes.SharedMailbox, "Shared")]
    [TestCase(ExchangeRecipientTypes.RoomMailbox, "Room")]
    [TestCase(ExchangeRecipientTypes.EquipmentMailbox, "Equipment")]
    public void Plan_UserNotYetARecipient_EnablesTheDeclaredMailboxOnTheDomainControllerTheExportUsed(string type, string? expectedSwitch)
    {
        var plan = LdapExchangeConvergePlanner.Plan(Alice, NotARecipient(), Declared(type));

        var enable = plan.Commands.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Refusal, Is.Null);
            Assert.That(plan.ReadAgainAfter, Is.True, "An enabled recipient is read again so what the enable left unset is set.");
            Assert.That(enable.Name, Is.EqualTo("Enable-Mailbox"));
            Assert.That(enable.Value("Identity"), Is.EqualTo(AliceGuid.ToString()), "The object GUID survives a rename; the DN does not.");
            Assert.That(enable.Value("DomainController"), Is.EqualTo("dc1.panoply.local"));
            Assert.That(enable.Names, expectedSwitch == null
                ? Has.None.AnyOf("Shared", "Room", "Equipment")
                : Has.Member(expectedSwitch));
        }
    }

    [Test]
    public void Plan_MailboxWithDatabaseAndAlias_EnablesItThereUnderThatAlias()
    {
        var plan = LdapExchangeConvergePlanner.Plan(Alice, NotARecipient(), Declared(ExchangeRecipientTypes.Mailbox) with { Database = "DB02", Alias = "alice" });

        var enable = plan.Commands.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enable.Value("Database"), Is.EqualTo("DB02"));
            Assert.That(enable.Value("Alias"), Is.EqualTo("alice"));
        }
    }

    [Test]
    public void Plan_MailboxWithNoDatabase_LeavesTheChoiceToExchange()
    {
        // Requirement 19: Exchange's automatic distribution chooses, honouring databases excluded from provisioning.
        var plan = LdapExchangeConvergePlanner.Plan(Alice, NotARecipient(), Declared(ExchangeRecipientTypes.Mailbox));

        Assert.That(plan.Commands.Single().Names, Has.No.Member("Database"));
    }

    [Test]
    public void Plan_MailUser_EnablesItWithItsExternalAddress()
    {
        var plan = LdapExchangeConvergePlanner.Plan(Alice, NotARecipient(), Declared(ExchangeRecipientTypes.MailUser) with { ExternalAddress = "alice@partner.example" });

        var enable = plan.Commands.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enable.Name, Is.EqualTo("Enable-MailUser"));
            Assert.That(enable.Value("ExternalEmailAddress"), Is.EqualTo("alice@partner.example"));
        }
    }

    [Test]
    public void Plan_MailContactWithoutAnExternalAddress_IsRefusedNamingWhatIsMissing()
    {
        var contact = Alice with { ObjectType = "contact" };

        var plan = LdapExchangeConvergePlanner.Plan(contact, NotARecipient(), Declared(ExchangeRecipientTypes.MailContact));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Commands, Is.Empty);
            Assert.That(plan.Refusal, Does.Contain("external address"));
        }
    }

    [Test]
    public void Plan_Group_EnablesADistributionGroup()
    {
        var group = Alice with { ObjectType = "group" };

        var plan = LdapExchangeConvergePlanner.Plan(group, NotARecipient(), Declared(ExchangeRecipientTypes.MailEnabledGroup));

        Assert.That(plan.Commands.Single().Name, Is.EqualTo("Enable-DistributionGroup"));
    }

    [Test]
    public void Plan_TargetWithNoGuid_IsIdentifiedByItsDistinguishedName()
    {
        var plan = LdapExchangeConvergePlanner.Plan(Alice with { ObjectGuid = null }, NotARecipient(), Declared(ExchangeRecipientTypes.Mailbox));

        Assert.That(plan.Commands.Single().Value("Identity"), Is.EqualTo("CN=Alice,OU=People,DC=panoply,DC=local"));
    }

    [Test]
    public void Plan_TargetWithNoDomainController_LetsExchangeChoose()
    {
        var plan = LdapExchangeConvergePlanner.Plan(Alice with { DomainController = null }, NotARecipient(), Declared(ExchangeRecipientTypes.Mailbox));

        Assert.That(plan.Commands.Single().Names, Has.No.Member("DomainController"));
    }

    // ---- The same type: only what differs ------------------------------------------------------------------------

    [Test]
    public void Plan_RecipientAlreadyInItsDeclaredState_NeedsNothing()
    {
        var current = new LdapExchangeRecipient(ExchangeRecipientTypes.Mailbox, "alice", "alice@panoply.example", null);

        var plan = LdapExchangeConvergePlanner.Plan(Alice, current, Declared(ExchangeRecipientTypes.Mailbox) with { Alias = "ALICE", PrimarySmtpAddress = "Alice@Panoply.example", Database = "DB09" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Commands, Is.Empty, "Addresses and aliases compare without case, and a database is chosen only at creation.");
            Assert.That(plan.Refusal, Is.Null);
        }
    }

    [Test]
    public void Plan_EmptyDeclaredValues_LeaveExchangesOwnChoicesAlone()
    {
        var current = new LdapExchangeRecipient(ExchangeRecipientTypes.Mailbox, "asmith", "a.smith@panoply.example", null);

        var plan = LdapExchangeConvergePlanner.Plan(Alice, current, Declared(ExchangeRecipientTypes.Mailbox));

        Assert.That(plan.Commands, Is.Empty);
    }

    [Test]
    public void Plan_AliasAndPrimaryAddressDiffer_SetsBothInOneCommandAndTurnsTheAddressPolicyOff()
    {
        var current = new LdapExchangeRecipient(ExchangeRecipientTypes.SharedMailbox, "old", "old@panoply.example", null);

        var plan = LdapExchangeConvergePlanner.Plan(Alice, current, Declared(ExchangeRecipientTypes.SharedMailbox) with { Alias = "sales", PrimarySmtpAddress = "sales@panoply.example" });

        var set = plan.Commands.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.ReadAgainAfter, Is.False);
            Assert.That(set.Name, Is.EqualTo("Set-Mailbox"));
            Assert.That(set.Value("Alias"), Is.EqualTo("sales"));
            Assert.That(set.Value("PrimarySmtpAddress"), Is.EqualTo("sales@panoply.example"));
            Assert.That(set.Value("EmailAddressPolicyEnabled"), Is.EqualTo(false),
                "Exchange's Email Address Policy would otherwise put its own primary address back.");
            Assert.That(set.Value("DomainController"), Is.EqualTo("dc1.panoply.local"));
        }
    }

    [Test]
    public void Plan_OnlyTheAliasDiffers_SetsOnlyTheAlias()
    {
        var current = new LdapExchangeRecipient(ExchangeRecipientTypes.Mailbox, "old", "alice@panoply.example", null);

        var plan = LdapExchangeConvergePlanner.Plan(Alice, current, Declared(ExchangeRecipientTypes.Mailbox) with { Alias = "alice" });

        Assert.That(plan.Commands.Single().Names, Is.EquivalentTo(new[] { "Identity", "DomainController", "Alias" }));
    }

    [TestCase("user", ExchangeRecipientTypes.MailUser, "Set-MailUser")]
    [TestCase("contact", ExchangeRecipientTypes.MailContact, "Set-MailContact")]
    public void Plan_ExternalAddressDiffers_SetsIt(string objectType, string type, string expectedCommand)
    {
        var current = new LdapExchangeRecipient(type, "alice", "alice@panoply.example", "old@partner.example");

        var plan = LdapExchangeConvergePlanner.Plan(Alice with { ObjectType = objectType }, current, Declared(type) with { ExternalAddress = "alice@partner.example" });

        var set = plan.Commands.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(set.Name, Is.EqualTo(expectedCommand));
            Assert.That(set.Value("ExternalEmailAddress"), Is.EqualTo("alice@partner.example"));
        }
    }

    [Test]
    public void Plan_ExternalAddressWithAndWithoutItsSmtpPrefix_IsTheSameAddress()
    {
        var current = new LdapExchangeRecipient(ExchangeRecipientTypes.MailUser, "alice", null, "SMTP:alice@partner.example");

        var plan = LdapExchangeConvergePlanner.Plan(Alice, current, Declared(ExchangeRecipientTypes.MailUser) with { ExternalAddress = "alice@partner.example" });

        Assert.That(plan.Commands, Is.Empty);
    }

    [Test]
    public void Plan_GroupAliasDiffers_SetsTheDistributionGroup()
    {
        var current = new LdapExchangeRecipient(ExchangeRecipientTypes.MailEnabledGroup, "old", null, null);

        var plan = LdapExchangeConvergePlanner.Plan(Alice with { ObjectType = "group" }, current, Declared(ExchangeRecipientTypes.MailEnabledGroup) with { Alias = "sales" });

        Assert.That(plan.Commands.Single().Name, Is.EqualTo("Set-DistributionGroup"));
    }

    // ---- What is never done ------------------------------------------------------------------------------------

    [TestCase(ExchangeRecipientTypes.Mailbox, ExchangeRecipientTypes.SharedMailbox)]
    [TestCase(ExchangeRecipientTypes.MailUser, ExchangeRecipientTypes.Mailbox)]
    [TestCase(ExchangeRecipientTypes.Mailbox, ExchangeRecipientTypes.None)]
    public void Plan_TypeChange_IsRefusedNamingBothTypesAndChangesNothing(string current, string declared)
    {
        // Requirement 16: a change of type on an existing recipient, including to None, is not made in this release.
        var plan = LdapExchangeConvergePlanner.Plan(Alice, new LdapExchangeRecipient(current, "alice", null, null), Declared(declared));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Commands, Is.Empty);
            Assert.That(plan.Refusal, Does.Contain(current).And.Contain(declared));
        }
    }

    [Test]
    public void Plan_RecipientExchangeHoldsAsATypeJimDoesNotManage_IsRefused()
    {
        var plan = LdapExchangeConvergePlanner.Plan(Alice, new LdapExchangeRecipient(ExchangeRecipientTypes.Unmanaged, null, null, null), Declared(ExchangeRecipientTypes.Mailbox));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Commands, Is.Empty);
            Assert.That(plan.Refusal, Does.Contain("does not manage"));
        }
    }

    [Test]
    public void Plan_NoneDeclaredForAnObjectThatIsNoRecipient_NeedsNothing()
    {
        var plan = LdapExchangeConvergePlanner.Plan(Alice, NotARecipient(), Declared(ExchangeRecipientTypes.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Commands, Is.Empty);
            Assert.That(plan.Refusal, Is.Null);
        }
    }

    [TestCase("contact", ExchangeRecipientTypes.Mailbox)]
    [TestCase("group", ExchangeRecipientTypes.MailUser)]
    [TestCase("user", ExchangeRecipientTypes.MailContact)]
    public void Plan_TypeThatDoesNotFitTheObject_IsRefused(string objectType, string declared)
    {
        // Staging refuses these (requirement 5); the Connector refuses them again rather than send Exchange a command it
        // would reject anyway.
        var plan = LdapExchangeConvergePlanner.Plan(Alice with { ObjectType = objectType }, NotARecipient(), Declared(declared) with { ExternalAddress = "x@partner.example" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Commands, Is.Empty);
            Assert.That(plan.Refusal, Does.Contain(objectType));
        }
    }

    private static LdapExchangeRecipient NotARecipient() => new(ExchangeRecipientTypes.None, null, null, null);

    private static ExchangeDeclaredState Declared(string type) => new() { RecipientType = type };
}
