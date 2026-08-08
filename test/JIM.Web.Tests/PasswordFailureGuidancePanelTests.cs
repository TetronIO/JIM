// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Staging;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the remediation guidance shown against a password JIM could not set (issue #1172).
/// <para>
/// The point of the component is that the advice is <b>specific to what failed</b>. A refused password and an
/// unreachable directory need opposite answers, and sending an administrator to change a password that was
/// never the problem is the failure this exists to prevent.
/// </para>
/// </summary>
[TestFixture]
public class PasswordFailureGuidancePanelTests : JimComponentTestContext
{
    private const string ToggleMarker = "jim-password-guidance-toggle";
    private const string PanelMarker = "jim-password-guidance";
    private const string VerdictMarker = "jim-password-guidance-verdict";
    private const string LinkMarker = "jim-password-guidance-link";
    private const string RegenerateMarker = "jim-password-guidance-regenerate";

    private IRenderedComponent<PasswordFailureGuidancePanel> Render(
        PasswordSetFailureReason reason,
        int accountCount = 1,
        Action? onRegenerateForAll = null) =>
        Render<PasswordFailureGuidancePanel>(parameters => parameters
            .Add(p => p.FailureReason, reason)
            .Add(p => p.ConnectedSystemId, 7)
            .Add(p => p.ConnectedSystemName, "Fabrikam HR")
            .Add(p => p.AccountCount, accountCount)
            .Add(p => p.OnRegenerateForAll, () => onRegenerateForAll?.Invoke()));

    /// <summary>
    /// An administrator reading three results wants three sentences, not three paragraphs. Guidance that cannot
    /// be folded away is guidance that gets scrolled past, and it pushes the retry action off the screen.
    /// </summary>
    [Test]
    public void PasswordFailureGuidancePanel_ByDefault_ShowsOnlyTheToggle()
    {
        var panel = Render(PasswordSetFailureReason.PolicyRejection);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.FindAll($"[data-testid='{ToggleMarker}']"), Is.Not.Empty);
            Assert.That(panel.FindAll($"[data-testid='{PanelMarker}']"), Is.Empty);
        }
    }

    [Test]
    public void PasswordFailureGuidancePanel_WhenAsked_ShowsTheGuidance()
    {
        var panel = Render(PasswordSetFailureReason.PolicyRejection);

        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        Assert.That(panel.Find($"[data-testid='{PanelMarker}']").TextContent, Does.Contain("read the password and rejected it"));
    }

    /// <summary>
    /// A success is not a failure, so there is nothing to advise. Rendering nothing lets a caller put this
    /// beside every result without branching.
    /// </summary>
    [Test]
    public void PasswordFailureGuidancePanel_ForASuccess_RendersNothing()
    {
        var panel = Render(PasswordSetFailureReason.None);

        Assert.That(panel.FindAll($"[data-testid='{ToggleMarker}']"), Is.Empty);
    }

    /// <summary>
    /// The distinction the whole component exists for. A refused password says try another; an unreachable
    /// directory says the password was never in question. Collapsing them would send administrators to change
    /// something that was fine.
    /// </summary>
    [Test]
    public void PasswordFailureGuidancePanel_ForAPolicyRejection_SaysToTryADifferentPassword()
    {
        var panel = Render(PasswordSetFailureReason.PolicyRejection);
        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        Assert.That(panel.Find($"[data-testid='{VerdictMarker}']").TextContent,
            Does.Contain("different password"));
    }

    [Test]
    public void PasswordFailureGuidancePanel_ForAnUnreachableSystem_SaysToTryAgainUnchanged()
    {
        var panel = Render(PasswordSetFailureReason.Transient);
        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.Find($"[data-testid='{VerdictMarker}']").TextContent, Does.Contain("unchanged"));
            Assert.That(panel.Find($"[data-testid='{PanelMarker}']").TextContent, Does.Contain("LDAPS"),
                "the encryption trap is the most common cause and the least obvious, since exports keep working");
        }
    }

    [Test]
    public void PasswordFailureGuidancePanel_ForMissingRights_SaysNothingChangesWithoutSomebodyElse()
    {
        var panel = Render(PasswordSetFailureReason.ConfigurationFault);
        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        Assert.That(panel.Find($"[data-testid='{VerdictMarker}']").TextContent, Does.Contain("grants the right"));
    }

    /// <summary>
    /// A Connected System that cannot do this at all will answer identically for ever, so saying "try again"
    /// would be a lie that costs a round trip every time somebody believes it.
    /// </summary>
    [Test]
    public void PasswordFailureGuidancePanel_ForAnUnsupportedOperation_SaysRetryingWillNeverHelp()
    {
        var panel = Render(PasswordSetFailureReason.UnsupportedOperation);
        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        Assert.That(panel.Find($"[data-testid='{VerdictMarker}']").TextContent, Does.Contain("never help"));
    }

    /// <summary>
    /// Every piece of guidance ends by pointing at where in JIM the repair happens, except where nothing in JIM
    /// will help.
    /// </summary>
    [Test]
    public void PasswordFailureGuidancePanel_LinksToWhereTheRepairHappens()
    {
        var panel = Render(PasswordSetFailureReason.Transient);
        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        Assert.That(panel.Find($"[data-testid='{LinkMarker}']").GetAttribute("href"),
            Is.EqualTo("/admin/connected-systems/7/?t=schema"));
    }

    [Test]
    public void PasswordFailureGuidancePanel_WhereNothingInJimWillHelp_OffersNoLink()
    {
        var panel = Render(PasswordSetFailureReason.UnsupportedOperation);
        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        Assert.That(panel.FindAll($"[data-testid='{LinkMarker}']"), Is.Empty);
    }

    #region the escape hatch for a refused password

    /// <summary>
    /// Offered only where the password itself was refused, and only where there is more than one account to
    /// keep aligned. With one account, plain retry with a fresh password covers it and a second control would
    /// just be noise.
    /// </summary>
    [Test]
    public void PasswordFailureGuidancePanel_ForARejectionAcrossSeveralAccounts_OffersAFreshPasswordForAllOfThem()
    {
        var panel = Render(PasswordSetFailureReason.PolicyRejection, accountCount: 3);
        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        Assert.That(panel.Find($"[data-testid='{RegenerateMarker}']").TextContent,
            Does.Contain("New password for all 3 accounts"));
    }

    [Test]
    public void PasswordFailureGuidancePanel_ForARejectionOnOneAccount_DoesNotOfferIt()
    {
        var panel = Render(PasswordSetFailureReason.PolicyRejection, accountCount: 1);
        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        Assert.That(panel.FindAll($"[data-testid='{RegenerateMarker}']"), Is.Empty);
    }

    /// <summary>
    /// Not offered where the password was never the problem: regenerating would rewrite accounts that took the
    /// original perfectly well, for nothing.
    /// </summary>
    [Test]
    public void PasswordFailureGuidancePanel_WhereThePasswordWasNotTheProblem_DoesNotOfferAFreshOne()
    {
        var panel = Render(PasswordSetFailureReason.Transient, accountCount: 3);
        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        Assert.That(panel.FindAll($"[data-testid='{RegenerateMarker}']"), Is.Empty);
    }

    [Test]
    public void PasswordFailureGuidancePanel_WhenAFreshPasswordIsAskedFor_RaisesItToTheCaller()
    {
        var raised = false;
        var panel = Render(PasswordSetFailureReason.PolicyRejection, accountCount: 3, onRegenerateForAll: () => raised = true);
        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        panel.Find($"[data-testid='{RegenerateMarker}']").Click();

        Assert.That(raised, Is.True);
    }

    #endregion

    #region how the verdict is coloured

    /// <summary>
    /// The verdict's colour lives in its dot, carried by a modifier class, rather than in MudBlazor's
    /// <c>mud-*-text</c> classes, which would colour the sentence too and put a third text colour into a
    /// six-line panel. The sentence already says what the colour says, so the dot is a second cue rather than
    /// the only one.
    /// </summary>
    [TestCase(PasswordSetFailureReason.PolicyRejection, "jim-password-guidance-verdict--retry")]
    [TestCase(PasswordSetFailureReason.Transient, "jim-password-guidance-verdict--retry")]
    [TestCase(PasswordSetFailureReason.ConfigurationFault, "jim-password-guidance-verdict--somebody-else")]
    [TestCase(PasswordSetFailureReason.UnsupportedOperation, "jim-password-guidance-verdict--never")]
    public void PasswordFailureGuidancePanel_ForEachVerdict_CarriesItsOwnDotColour(
        PasswordSetFailureReason reason, string expectedModifier)
    {
        var panel = Render(reason);
        panel.Find($"[data-testid='{ToggleMarker}']").Click();

        var verdict = panel.Find($"[data-testid='{VerdictMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.ClassName, Does.Contain(expectedModifier));
            Assert.That(verdict.ClassName, Does.Not.Contain("mud-info-text").And.Not.Contain("mud-warning-text")
                .And.Not.Contain("mud-error-text"), "colouring the sentence is what the dot exists to avoid");
        }
    }

    #endregion
}
