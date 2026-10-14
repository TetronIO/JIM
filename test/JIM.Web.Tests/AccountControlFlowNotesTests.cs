// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// When the portal tells an administrator that a userAccountControl Attribute Flow, rather than a delivered password,
/// decides an account's flags (#2159): on the Attribute Flow tab, beside the flow; and on the Passwords tab, where a
/// change at the next sign-in would be defeated by a flow that sets "Password never expires".
/// </summary>
[TestFixture]
public class AccountControlFlowNotesTests
{
    private static SyncRuleMapping Flow(string targetName, bool enabled = true) => new()
    {
        Enabled = enabled,
        TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Name = targetName }
    };

    private static SyncRule Rule(SyncRuleDirection direction, bool initialPassword, params SyncRuleMapping[] flows) => new()
    {
        Direction = direction,
        InitialPassword = initialPassword ? new SyncRuleInitialPassword { Enabled = true } : null,
        AttributeFlowRules = [.. flows]
    };

    [TestCase("userAccountControl", true, false, true, TestName = "ShowsPasswordDeliveryNote_UserAccountControlWithAnInitialPassword_Shows")]
    [TestCase("USERACCOUNTCONTROL", true, false, true, TestName = "ShowsPasswordDeliveryNote_MatchesTheNameWhateverItsCase")]
    [TestCase("userAccountControl", false, true, true, TestName = "ShowsPasswordDeliveryNote_UserAccountControlWithPasswordSynchronisation_Shows")]
    [TestCase("userAccountControl", false, false, false, TestName = "ShowsPasswordDeliveryNote_NoPasswordIsEverDelivered_Hides")]
    [TestCase("description", true, true, false, TestName = "ShowsPasswordDeliveryNote_AnotherAttribute_Hides")]
    public void ShowsPasswordDeliveryNote_DecidesFromTheTargetAndWhetherPasswordsAreDelivered(
        string targetName, bool initialPassword, bool passwordSynchronisationConfigured, bool expected)
    {
        var flow = Flow(targetName);
        var rule = Rule(SyncRuleDirection.Export, initialPassword, flow);

        Assert.That(AccountControlFlowNotes.ShowsPasswordDeliveryNote(rule, flow, passwordSynchronisationConfigured), Is.EqualTo(expected));
    }

    [Test]
    public void ShowsPasswordDeliveryNote_DisabledFlow_Hides()
    {
        var flow = Flow("userAccountControl", enabled: false);
        var rule = Rule(SyncRuleDirection.Export, initialPassword: true, flow);

        Assert.That(AccountControlFlowNotes.ShowsPasswordDeliveryNote(rule, flow, passwordSynchronisationConfigured: true), Is.False);
    }

    [Test]
    public void ShowsPasswordDeliveryNote_ImportRule_Hides()
    {
        var flow = Flow("userAccountControl");
        var rule = Rule(SyncRuleDirection.Import, initialPassword: true, flow);

        Assert.That(AccountControlFlowNotes.ShowsPasswordDeliveryNote(rule, flow, passwordSynchronisationConfigured: true), Is.False);
    }

    [TestCase(PasswordExpiryBehaviour.RequireChangeAtNextSignIn, true, true, true, TestName = "WarnsRequireChangeMayBeIgnored_RequireChangeUnderAUserAccountControlFlow_Warns")]
    [TestCase(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, true, true, false, TestName = "WarnsRequireChangeMayBeIgnored_AnotherExpiry_DoesNotWarn")]
    [TestCase(PasswordExpiryBehaviour.RequireChangeAtNextSignIn, false, true, false, TestName = "WarnsRequireChangeMayBeIgnored_NoUserAccountControlFlow_DoesNotWarn")]
    [TestCase(PasswordExpiryBehaviour.RequireChangeAtNextSignIn, true, false, false, TestName = "WarnsRequireChangeMayBeIgnored_InitialPasswordOff_DoesNotWarn")]
    public void WarnsRequireChangeMayBeIgnored_DecidesFromTheExpiryAndTheFlows(
        PasswordExpiryBehaviour expiryBehaviour, bool mapsUserAccountControl, bool initialPasswordEnabled, bool expected)
    {
        var rule = Rule(SyncRuleDirection.Export, initialPassword: false, Flow(mapsUserAccountControl ? "userAccountControl" : "displayName"));
        var configuration = new SyncRuleInitialPassword { Enabled = initialPasswordEnabled, ExpiryBehaviour = expiryBehaviour };

        Assert.That(AccountControlFlowNotes.WarnsRequireChangeMayBeIgnored(rule, configuration), Is.EqualTo(expected));
    }
}
