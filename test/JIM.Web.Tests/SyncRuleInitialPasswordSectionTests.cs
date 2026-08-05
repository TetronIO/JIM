// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Pages.Admin.Components;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The parked-work panel on a Synchronisation Rule's Initial Password section (#1221 item 4).
/// <para>
/// This is where an administrator learns that accounts are waiting on them, and what to change. Two things have
/// to survive future edits to this component: the target's own words appear unaltered, and the promise that
/// saving will release the parked accounts appears only when saving actually would.
/// </para>
/// </summary>
[TestFixture]
public class SyncRuleInitialPasswordSectionTests : JimComponentTestContext
{
    private const string AdRefusal =
        "0000052D: AtrErr: DSID-03191083, #1: 0: 0000052D: DSID-03191083, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 9005a (unicodePwd)";

    private static SyncRuleInitialPassword EnabledConfiguration() => new()
    {
        Enabled = true,
        Source = InitialPasswordSource.Discovered,
        ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
        EnableAccount = true
    };

    private static IReadOnlyList<InitialPasswordRejection> OneReason(int accounts = 14, string? message = AdRefusal) =>
    [
        new InitialPasswordRejection { TargetMessage = message, AccountCount = accounts, FirstSeenAt = DateTime.UtcNow.AddDays(-2) }
    ];

    private IRenderedComponent<SyncRuleInitialPasswordSection> Render(
        IReadOnlyList<InitialPasswordRejection>? parkedReasons,
        bool willRelease = false,
        SyncRuleInitialPassword? configuration = null)
    {
        return Render<SyncRuleInitialPasswordSection>(p => p
            .Add(c => c.Configuration, configuration ?? EnabledConfiguration())
            .Add(c => c.SupportedExpiryBehaviours, new[] { PasswordExpiryBehaviour.RequireChangeAtNextSignIn })
            .Add(c => c.ConnectorName, "LDAP")
            .Add(c => c.ParkedReasons, parkedReasons)
            .Add(c => c.WillReleaseParkedOnSave, willRelease));
    }

    [Test]
    public void InitialPasswordSection_WithNothingParked_SaysNothingAboutIt()
    {
        var cut = Render(parkedReasons: []);

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("waiting on a change"));
            Assert.That(cut.Markup, Does.Not.Contain("parked"));
        });
    }

    [Test]
    public void InitialPasswordSection_WithParkedAccounts_ShowsTheCountOnThePanelTitle()
    {
        var cut = Render(OneReason(accounts: 14));

        Assert.That(cut.Markup, Does.Contain("14 parked"),
            "the count belongs on the collapsed title too, or a closed panel hides the whole problem");
    }

    /// <summary>
    /// The single most useful thing an administrator can be shown, and the reason nothing paraphrases it: a
    /// directory's rejection code is unreadable and is also the only string precise enough to search for.
    /// </summary>
    [Test]
    public void InitialPasswordSection_ShowsWhatTheTargetSaidVerbatim()
    {
        var cut = Render(OneReason());

        Assert.That(cut.Markup, Does.Contain("CONSTRAINT_ATT_TYPE"));
    }

    [Test]
    public void InitialPasswordSection_WithSeveralReasons_ShowsEachWithItsOwnCount()
    {
        var cut = Render([
            new InitialPasswordRejection { TargetMessage = "Not complex enough.", AccountCount = 11 },
            new InitialPasswordRejection { TargetMessage = "Too short.", AccountCount = 3 }
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Not complex enough."));
            Assert.That(cut.Markup, Does.Contain("Too short."));
            Assert.That(cut.Markup, Does.Contain("14 parked"), "the title carries the total across every reason");
        });
    }

    /// <summary>
    /// A refusal JIM could get no words out of still holds an account up. Dropping the group because there is
    /// nothing to quote would lose the account from the count as well.
    /// </summary>
    [Test]
    public void InitialPasswordSection_WhenTheTargetSaidNothing_StillReportsTheAccounts()
    {
        var cut = Render(OneReason(accounts: 2, message: null));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("2 parked"));
            Assert.That(cut.Markup, Does.Contain("refused the password without saying why"));
        });
    }

    [Test]
    public void InitialPasswordSection_WithParkedWorkAndNoEdit_DoesNotPromiseARelease()
    {
        var cut = Render(OneReason(), willRelease: false);

        Assert.That(cut.Markup, Does.Not.Contain("Saving will release"),
            "promising a release on every save would promise it for the saves that release nothing");
    }

    [Test]
    public void InitialPasswordSection_WhenAnEditWouldChangeDelivery_PromisesTheRelease()
    {
        var cut = Render(OneReason(accounts: 14), willRelease: true);

        Assert.That(cut.Markup, Does.Contain("Saving will release 14 parked accounts"));
    }

    [Test]
    public void InitialPasswordSection_WithOneParkedAccount_ReadsAsSingular()
    {
        var cut = Render(OneReason(accounts: 1), willRelease: true);

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Saving will release 1 parked account"));
            Assert.That(cut.Markup, Does.Not.Contain("1 parked accounts"));
            Assert.That(cut.Markup, Does.Contain("1 account is waiting"));
        });
    }

    /// <summary>
    /// A Connector that cannot set passwords at all replaces the whole editor with an explanation, and there is
    /// nothing parked in that case anyway. Rendering the panel there would offer a fix that does not exist.
    /// </summary>
    [Test]
    public void InitialPasswordSection_WhereTheConnectorCannotSetPasswords_ShowsNoParkedPanel()
    {
        var cut = Render<SyncRuleInitialPasswordSection>(p => p
            .Add(c => c.Configuration, EnabledConfiguration())
            .Add(c => c.SupportedExpiryBehaviours, Array.Empty<PasswordExpiryBehaviour>())
            .Add(c => c.ParkedReasons, OneReason())
            .Add(c => c.WillReleaseParkedOnSave, true));

        Assert.That(cut.Markup, Does.Not.Contain("Saving will release"));
    }
}
