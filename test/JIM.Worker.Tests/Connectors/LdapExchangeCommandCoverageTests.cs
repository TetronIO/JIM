// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Exchange;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Which Exchange commands JIM needs for which recipient types, and the warning when the account may not run some
/// (#2021, PRD requirement 3). Exchange hides a command an account's roles do not grant rather than refusing it, so
/// the commands the account can see are the commands it may run; anything JIM needs and cannot see is named, with the
/// recipient types it stops JIM managing. The settings still save.
/// </summary>
[TestFixture]
public class LdapExchangeCommandCoverageTests
{
    private const string Principal = "svc-jim@PANOPLY.LOCAL";

    [Test]
    public void WarningFor_EveryCommandVisible_IsNull()
    {
        Assert.That(LdapExchangeCommandCoverage.WarningFor(Principal, LdapExchangeCommandCoverage.RequiredCommands.ToHashSet()), Is.Null);
    }

    [Test]
    public void RequiredCommands_CoverReadingAndCreatingAndUpdatingEveryManagedType()
    {
        Assert.That(LdapExchangeCommandCoverage.RequiredCommands, Is.EquivalentTo(new[]
        {
            "Get-Recipient", "Get-User", "Get-Contact", "Get-Group",
            "Enable-Mailbox", "Set-Mailbox",
            "Enable-MailUser", "Set-MailUser",
            "Enable-MailContact", "Set-MailContact",
            "Enable-DistributionGroup", "Set-DistributionGroup"
        }));
    }

    [Test]
    public void WarningFor_MailboxCommandsHidden_NamesThemAndTheFourMailboxTypes()
    {
        var warning = LdapExchangeCommandCoverage.WarningFor(Principal, Visible(except: ["Enable-Mailbox", "Set-Mailbox"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning, Does.Contain(Principal).And.Contain("Enable-Mailbox").And.Contain("Set-Mailbox"));
            Assert.That(warning, Does.Contain(ExchangeRecipientTypes.Mailbox).And.Contain(ExchangeRecipientTypes.SharedMailbox)
                .And.Contain(ExchangeRecipientTypes.RoomMailbox).And.Contain(ExchangeRecipientTypes.EquipmentMailbox));
            Assert.That(warning, Does.Not.Contain(ExchangeRecipientTypes.MailContact), "contacts are unaffected");
            Assert.That(warning, Does.Contain("Recipient Management"), "says how to fix it");
        }
    }

    [Test]
    public void WarningFor_GroupCommandHidden_NamesOnlyGroups()
    {
        var warning = LdapExchangeCommandCoverage.WarningFor(Principal, Visible(except: ["Enable-DistributionGroup"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning, Does.Contain("Enable-DistributionGroup").And.Contain(ExchangeRecipientTypes.MailEnabledGroup));
            Assert.That(warning, Does.Not.Contain(ExchangeRecipientTypes.SharedMailbox));
        }
    }

    [Test]
    public void WarningFor_GetRecipientHidden_NamesEveryManagedType()
    {
        var warning = LdapExchangeCommandCoverage.WarningFor(Principal, Visible(except: ["Get-Recipient"]));

        Assert.That(warning, Does.Contain(ExchangeRecipientTypes.Mailbox).And.Contain(ExchangeRecipientTypes.MailUser)
            .And.Contain(ExchangeRecipientTypes.MailContact).And.Contain(ExchangeRecipientTypes.MailEnabledGroup));
    }

    [Test]
    public void WarningFor_CommandNamesInAnotherCase_AreTheSameCommands()
    {
        var visible = LdapExchangeCommandCoverage.RequiredCommands.Select(c => c.ToLowerInvariant()).ToHashSet();

        Assert.That(LdapExchangeCommandCoverage.WarningFor(Principal, visible), Is.Null, "PowerShell command names ignore case");
    }

    private static HashSet<string> Visible(string[] except) =>
        LdapExchangeCommandCoverage.RequiredCommands.Except(except).ToHashSet();
}
