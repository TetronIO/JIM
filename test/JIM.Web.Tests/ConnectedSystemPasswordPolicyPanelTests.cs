// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Password Policy panel on a Connected System's Passwords tab (#1702).
/// <para>
/// The panel cannot know which directory it is describing (the Connector's persisted data is opaque to it by
/// design), so everything it says has to follow from the policy row alone: why nothing was read, when it was,
/// whether the directory checks more than it publishes, and whether some objects answer to another policy. Each
/// of those is a different thing for an administrator to do, which is why the wording per state is pinned here
/// rather than left to read as one "no policy" notice.
/// </para>
/// </summary>
[TestFixture]
public class ConnectedSystemPasswordPolicyPanelTests : JimComponentTestContext
{
    private const string OutcomeMarker = "jim-password-policy-outcome";
    private const string FurtherChecksMarker = "jim-password-policy-further-checks";
    private const string OverrideMarker = "jim-password-policy-override";

    /// <summary>
    /// The claim the panel used to make, and must not make anywhere now that other directories publish a policy.
    /// </summary>
    private const string ActiveDirectoryOnlyClaim = "Only Active Directory";

    private const PasswordCharacterClasses FiveClasses =
        PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase | PasswordCharacterClasses.Digit |
        PasswordCharacterClasses.Symbol | PasswordCharacterClasses.OtherUnicodeLetter;

    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new UnusedJimApplicationFactory());
    }

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private sealed class UnusedJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication Create() =>
            throw new InvalidOperationException("The Password Policy panel reached the application layer while merely rendering, which it should not do.");
    }

    private static ConnectedSystem ConnectedSystemWith(ConnectedSystemPasswordPolicy? policy) => new()
    {
        Id = 1,
        Name = "Research LDAP",
        PasswordPolicy = policy
    };

    private static ConnectedSystemPasswordPolicy ReadPolicy(
        bool furtherChecksApply = false,
        PolicyOverrideSignal overrideSignal = PolicyOverrideSignal.Absent) => new()
    {
        MinimumLength = 12,
        ComplexityRequired = true,
        RequiredCharacterClassCount = 3,
        RecognisedCharacterClasses = FiveClasses,
        PasswordHistoryLength = 5,
        DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.Read,
        FurtherChecksApply = furtherChecksApply,
        PolicyOverrideSignal = overrideSignal
    };

    private IRenderedComponent<ConnectedSystemPasswordPolicyPanel> RenderPanel(ConnectedSystemPasswordPolicy? policy) =>
        Render<ConnectedSystemPasswordPolicyPanel>(p => p.Add(c => c.ConnectedSystem, ConnectedSystemWith(policy)));

    /// <summary>
    /// Razor markup breaks a sentence across lines, and each break reaches the DOM as a run of whitespace. This
    /// folds every run to one space so a test can assert on the sentence an administrator reads.
    /// </summary>
    private static string Flattened(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    #region why nothing was read

    /// <summary>
    /// No row at all means the schema has never been retrieved, which is the one state the administrator can
    /// change from this page. It is not the moment to speculate about which directories publish a policy.
    /// </summary>
    [Test]
    public void PasswordPolicyPanel_WithNoPolicyRow_SaysToRetrieveTheSchema()
    {
        var cut = RenderPanel(null);

        var notice = Flattened(cut.Find($"[data-testid='{OutcomeMarker}']").TextContent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(notice, Does.Contain("JIM has not read a password policy from this Connected System"));
            Assert.That(notice, Does.Contain("Retrieve the schema"));
            Assert.That(notice, Does.Not.Contain(ActiveDirectoryOnlyClaim));
        }
    }

    /// <summary>
    /// A row recorded as read but holding no constraint predates the outcome being recorded, or came from a
    /// reader that found nothing to say. Either way the honest advice is the same as for no row.
    /// </summary>
    [Test]
    public void PasswordPolicyPanel_WithAReadRowHoldingNoConstraint_SaysToRetrieveTheSchema()
    {
        var cut = RenderPanel(new ConnectedSystemPasswordPolicy { DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.Read });

        var notice = Flattened(cut.Find($"[data-testid='{OutcomeMarker}']").TextContent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(notice, Does.Contain("JIM has not read a password policy from this Connected System"));
            Assert.That(notice, Does.Contain("Retrieve the schema"));
        }
    }

    /// <summary>
    /// PRD Scenario 5. A directory that publishes nothing is not a gap to close; telling the administrator to
    /// retrieve the schema again would send them after something that will never arrive.
    /// </summary>
    [Test]
    public void PasswordPolicyPanel_WhenTheDirectoryPublishesNoPolicy_SaysSoAndDoesNotAskForARefresh()
    {
        var cut = RenderPanel(new ConnectedSystemPasswordPolicy { DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.NotPublished });

        var notice = Flattened(cut.Find($"[data-testid='{OutcomeMarker}']").TextContent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(notice, Does.Contain("This directory publishes no password policy that JIM can read"));
            Assert.That(notice, Does.Not.Contain("Retrieve the schema"));
        }
    }

    /// <summary>
    /// PRD Scenario 4. The policy exists and JIM was refused it. Naming the missing right is what makes this
    /// notice actionable; "no policy" would send the administrator looking in the wrong place.
    /// </summary>
    [Test]
    public void PasswordPolicyPanel_WhenTheConfigurationCouldNotBeRead_NamesTheMissingRight()
    {
        var cut = RenderPanel(new ConnectedSystemPasswordPolicy { DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable });

        var notice = Flattened(cut.Find($"[data-testid='{OutcomeMarker}']").TextContent);
        Assert.That(notice, Does.Contain("JIM could not read this directory's password policy: the account it connects as cannot read the server configuration that holds it"));
    }

    [Test]
    public void PasswordPolicyPanel_WhenNoPolicyIsConfigured_SaysNoRulesApply()
    {
        var cut = RenderPanel(new ConnectedSystemPasswordPolicy { DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.NoPolicyConfigured });

        var notice = Flattened(cut.Find($"[data-testid='{OutcomeMarker}']").TextContent);
        Assert.That(notice, Does.Contain("This directory's password policy mechanism is loaded but no policy is configured, so no rules apply"));
    }

    /// <summary>
    /// A row that recorded an outcome was a read attempt at a moment in time. Saying when lets the administrator
    /// judge whether granting a right, or loading an overlay, since then has been picked up yet.
    /// </summary>
    [TestCase(PasswordPolicyDiscoveryOutcome.NotPublished)]
    [TestCase(PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable)]
    [TestCase(PasswordPolicyDiscoveryOutcome.NoPolicyConfigured)]
    public void PasswordPolicyPanel_WithARowThatReadNothing_StillSaysWhenItWasRead(PasswordPolicyDiscoveryOutcome outcome)
    {
        var cut = RenderPanel(new ConnectedSystemPasswordPolicy { DiscoveryOutcome = outcome });

        Assert.That(cut.Markup, Does.Contain("Refresh the schema to read it again"));
    }

    /// <summary>
    /// The old panel said only Active Directory publishes a policy a client can read. OpenLDAP and 389 Directory
    /// Server do too, so the claim must be gone from every state the panel can be in.
    /// </summary>
    [TestCase(PasswordPolicyDiscoveryOutcome.Read)]
    [TestCase(PasswordPolicyDiscoveryOutcome.NotPublished)]
    [TestCase(PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable)]
    [TestCase(PasswordPolicyDiscoveryOutcome.NoPolicyConfigured)]
    public void PasswordPolicyPanel_InEveryOutcome_NeverClaimsOnlyActiveDirectoryPublishesAPolicy(PasswordPolicyDiscoveryOutcome outcome)
    {
        var cut = RenderPanel(new ConnectedSystemPasswordPolicy { DiscoveryOutcome = outcome });

        Assert.That(cut.Markup, Does.Not.Contain(ActiveDirectoryOnlyClaim));
    }

    #endregion

    #region further checks the directory does not publish

    [Test]
    public void PasswordPolicyPanel_WhenFurtherChecksApply_SaysAPasswordCanStillBeRefused()
    {
        var cut = RenderPanel(ReadPolicy(furtherChecksApply: true));

        var line = Flattened(cut.Find($"[data-testid='{FurtherChecksMarker}']").TextContent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(line, Does.Contain("The directory applies further checks JIM cannot see, so a password satisfying everything above can still be refused"));
            Assert.That(cut.Markup, Does.Contain("12 characters"), "the rules that were read are still shown above the caveat");
        }
    }

    [Test]
    public void PasswordPolicyPanel_WhenNoFurtherChecksApply_SaysNothingAboutThem()
    {
        var cut = RenderPanel(ReadPolicy(furtherChecksApply: false));

        Assert.That(cut.FindAll($"[data-testid='{FurtherChecksMarker}']"), Is.Empty);
    }

    #endregion

    #region objects governed by another policy

    /// <summary>
    /// The panel does not know which directory it describes, so the override alert names every mechanism it
    /// might be rather than presuming Fine-Grained Password Policies, which only Active Directory has.
    /// </summary>
    [Test]
    public void PasswordPolicyPanel_WhenOverridesArePresent_NamesEveryDirectorysMechanism()
    {
        var cut = RenderPanel(ReadPolicy(overrideSignal: PolicyOverrideSignal.Present));

        var alert = Flattened(cut.Find($"[data-testid='{OverrideMarker}']").TextContent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(alert, Does.Contain("Fine-Grained Password Policies"));
            Assert.That(alert, Does.Contain("pwdPolicySubentry"));
            Assert.That(alert, Does.Contain("389 Directory Server"));
            Assert.That(alert, Does.Not.Contain("could not establish"), "present is a finding, not a doubt");
        }
    }

    [Test]
    public void PasswordPolicyPanel_WhenOverridesCouldNotBeDetermined_SaysSoAndNamesEveryDirectorysMechanism()
    {
        var cut = RenderPanel(ReadPolicy(overrideSignal: PolicyOverrideSignal.CouldNotDetermine));

        var alert = Flattened(cut.Find($"[data-testid='{OverrideMarker}']").TextContent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(alert, Does.Contain("could not establish"));
            Assert.That(alert, Does.Contain("Fine-Grained Password Policies"));
            Assert.That(alert, Does.Contain("pwdPolicySubentry"));
            Assert.That(alert, Does.Contain("389 Directory Server"));
            Assert.That(alert, Does.Contain("Treat the policy above as a minimum"));
        }
    }

    [Test]
    public void PasswordPolicyPanel_WhenOverridesAreProvedAbsent_ShowsNoOverrideAlert()
    {
        var cut = RenderPanel(ReadPolicy(overrideSignal: PolicyOverrideSignal.Absent));

        Assert.That(cut.FindAll($"[data-testid='{OverrideMarker}']"), Is.Empty);
    }

    #endregion
}
