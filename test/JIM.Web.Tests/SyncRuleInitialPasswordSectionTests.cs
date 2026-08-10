// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Pages.Admin.Components;
using Microsoft.AspNetCore.Components;
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Not.Contain("waiting on a change"));
            Assert.That(cut.Markup, Does.Not.Contain("parked"));
        }
    }

    [Test]
    public void InitialPasswordSection_WithParkedAccounts_ReportsHowManyAreWaiting()
    {
        var cut = Render(OneReason(accounts: 14));

        // The tab's own badge carries the count outside the tab; in here the notice states it in words.
        Assert.That(cut.Markup, Does.Contain("14 accounts are waiting on a change to these settings"));
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Not complex enough."));
            Assert.That(cut.Markup, Does.Contain("Too short."));
            Assert.That(cut.Markup, Does.Contain("14 accounts are waiting"), "the notice totals every reason");
        }
    }

    /// <summary>
    /// A refusal JIM could get no words out of still holds an account up. Dropping the group because there is
    /// nothing to quote would lose the account from the count as well.
    /// </summary>
    [Test]
    public void InitialPasswordSection_WhenTheTargetSaidNothing_StillReportsTheAccounts()
    {
        var cut = Render(OneReason(accounts: 2, message: null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("2 accounts are waiting"));
            Assert.That(cut.Markup, Does.Contain("refused the password without saying why"));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Saving will release 1 parked account"));
            Assert.That(cut.Markup, Does.Not.Contain("1 parked accounts"));
            Assert.That(cut.Markup, Does.Contain("1 account is waiting"));
        }
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

    #region the static password (#1273)
    /// <summary>
    /// The stored value is ciphertext, but it is still the password: it must never reach the page, where it
    /// would sit in the DOM and in every screenshot of it.
    /// </summary>
    private const string StoredCiphertext = "$JIM$v1$QnJvd24tQ2hpY2tlbi1MYWRkZXItNDc=";

    private static SyncRuleInitialPassword StaticConfiguration(string? storedPassword = StoredCiphertext) => new()
    {
        Enabled = true,
        Source = InitialPasswordSource.Static,
        StaticPasswordEncryptedValue = storedPassword,
        StaticPasswordSetAt = storedPassword == null ? null : DateTime.UtcNow.AddDays(-30),
        ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
        EnableAccount = true
    };

    private IRenderedComponent<SyncRuleInitialPasswordSection> RenderStatic(
        SyncRuleInitialPassword? configuration = null,
        SuppliedPasswordAssessment? staticAssessment = null,
        EventCallback<string?>? onStaticPasswordEntered = null)
    {
        return Render<SyncRuleInitialPasswordSection>(p =>
        {
            p.Add(c => c.Configuration, configuration ?? StaticConfiguration())
                .Add(c => c.SupportedExpiryBehaviours, new[] { PasswordExpiryBehaviour.RequireChangeAtNextSignIn })
                .Add(c => c.ConnectorName, "LDAP")
                .Add(c => c.StaticPasswordAssessment, staticAssessment);

            if (onStaticPasswordEntered.HasValue)
                p.Add(c => c.OnStaticPasswordEntered, onStaticPasswordEntered.Value);
        });
    }

    [Test]
    public void InitialPasswordSection_OffersTheStaticSource_MarkedNotRecommended()
    {
        // The recommendation has to be visible while an administrator is choosing, not only after they have
        // chosen. This is the whole mitigation the option ships with.
        var cut = Render(parkedReasons: []);

        Assert.That(cut.Markup, Does.Contain("Not recommended"));
    }

    [Test]
    public void InitialPasswordSection_WithTheStaticSource_HidesTheGeneratorSettings()
    {
        // Replaced rather than sat beside, so no stale generator setting is left on screen looking as though it
        // still applied to what gets delivered.
        var cut = RenderStatic();

        Assert.That(cut.Markup, Does.Not.Contain("Permitted symbols"));
    }

    [Test]
    public void InitialPasswordSection_WithTheCustomSource_StillShowsTheGeneratorSettings()
    {
        var configuration = EnabledConfiguration();
        configuration.Source = InitialPasswordSource.Custom;

        var cut = Render(parkedReasons: [], configuration: configuration);

        Assert.That(cut.Markup, Does.Contain("Permitted symbols"));
    }

    [Test]
    public void InitialPasswordSection_WithTheStaticSource_HidesTheGeneratorAssessment()
    {
        // The generator assessment describes passwords this rule will never produce. Leaving it on screen would
        // have the panel promise an entropy figure for a password an administrator typed.
        var cut = Render<SyncRuleInitialPasswordSection>(p => p
            .Add(c => c.Configuration, StaticConfiguration())
            .Add(c => c.SupportedExpiryBehaviours, new[] { PasswordExpiryBehaviour.RequireChangeAtNextSignIn })
            .Add(c => c.Assessment, new PasswordGenerationAssessment
            {
                GuaranteedMinimumLength = 16,
                GuaranteedCharacterClasses = PasswordCharacterClasses.Lowercase,
                EntropyBits = 91.2,
                Problems = []
            }));

        Assert.That(cut.Markup, Does.Not.Contain("bits of entropy"));
    }

    [Test]
    public void InitialPasswordSection_WithAStoredStaticPassword_NeverRendersTheStoredValue()
    {
        var cut = RenderStatic();

        Assert.That(cut.Markup, Does.Not.Contain(StoredCiphertext),
            "the stored value is write-only on every surface, and the portal is a surface");
    }

    [Test]
    public void InitialPasswordSection_WithAStoredStaticPassword_SaysOneIsSetAndWhen()
    {
        // The password itself is never shown, so the only things that can tell an administrator where they stand
        // are that one exists and how long it has been in use. A shared password wants changing when somebody who
        // knew it leaves, and nothing else in JIM can date it.
        var cut = RenderStatic();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("A password is set"));
            Assert.That(cut.Markup, Does.Contain("ago"), "how long it has been in use is the actionable part");
        });
    }

    [Test]
    public void InitialPasswordSection_WithTheStaticSourceAndNothingStored_SaysSo()
    {
        // Delivery parks in this state rather than generating something nobody expects, so the panel has to say
        // it before a run does.
        var cut = RenderStatic(StaticConfiguration(storedPassword: null));

        Assert.That(cut.Markup, Does.Contain("No password has been set"));
    }

    [Test]
    public void InitialPasswordSection_WithASuppliedPasswordProblem_ShowsIt()
    {
        var cut = RenderStatic(staticAssessment: new SuppliedPasswordAssessment
        {
            Length = 5,
            CharacterClasses = PasswordCharacterClasses.Lowercase,
            Problems = ["This Connected System requires at least 30 characters, and this password has 5."]
        });

        Assert.That(cut.Markup, Does.Contain("requires at least 30 characters"));
    }

    [Test]
    public void InitialPasswordSection_WithAUsableSuppliedPassword_ReportsNoEntropyFigure()
    {
        // Deliberate: entropy is a property of how a value was chosen, and JIM knows nothing about how an
        // administrator chose this one. A figure it cannot stand behind is worse than no figure.
        var cut = RenderStatic(staticAssessment: new SuppliedPasswordAssessment
        {
            Length = 23,
            CharacterClasses = PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                               PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol,
            Problems = []
        });

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("23 characters"));
            Assert.That(cut.Markup, Does.Not.Contain("entropy"));
        });
    }

    [Test]
    public void InitialPasswordSection_WhenTheTwoPasswordFieldsMatch_RaisesThePassword()
    {
        string? raised = null;
        var cut = RenderStatic(onStaticPasswordEntered: EventCallback.Factory.Create<string?>(this, p => raised = p));

        cut.Find("[data-testid=initial-password-static]").Input("Brown-Chicken-Ladder-47");
        cut.Find("[data-testid=initial-password-static-confirm]").Input("Brown-Chicken-Ladder-47");

        Assert.That(raised, Is.EqualTo("Brown-Chicken-Ladder-47"));
    }

    /// <summary>
    /// A password committed from a field the administrator has since edited is the trap here: the two fields
    /// would disagree on screen while the model still held the earlier value, and saving would store a password
    /// nobody typed. Nothing usable is raised until they agree.
    /// </summary>
    [Test]
    public void InitialPasswordSection_WhenTheTwoPasswordFieldsDiffer_RaisesNothingAndSaysSo()
    {
        var raised = new List<string?>();
        var cut = RenderStatic(onStaticPasswordEntered: EventCallback.Factory.Create<string?>(this, p => raised.Add(p)));

        cut.Find("[data-testid=initial-password-static]").Input("Brown-Chicken-Ladder-47");
        cut.Find("[data-testid=initial-password-static-confirm]").Input("Brown-Chicken-Ladder-48");

        Assert.Multiple(() =>
        {
            Assert.That(raised, Has.All.Null);
            Assert.That(cut.Markup, Does.Contain("do not match"));
        });
    }

    [Test]
    public void InitialPasswordSection_OnLoad_LeavesThePasswordFieldsBlank()
    {
        // Blank means "leave the stored password as it is". Prefilling with anything, including a placeholder of
        // the right length, would say something about a password that is never shown.
        var cut = RenderStatic();

        Assert.Multiple(() =>
        {
            // Null where no value attribute is rendered at all, which is the same "blank" from the reader's side.
            Assert.That(cut.Find("[data-testid=initial-password-static]").GetAttribute("value"), Is.Null.Or.Empty);
            Assert.That(cut.Find("[data-testid=initial-password-static-confirm]").GetAttribute("value"), Is.Null.Or.Empty);
        });
    }
    #endregion
}
