// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using AngleSharp.Dom;
using Bunit;
using JIM.Models.Staging;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// Covers the administrator set-password dialog (issue #1121).
/// <para>
/// The rules worth guarding are the ones that decide whether a credential ends up on a screen somebody else can
/// read: the value is masked from the moment it is generated, copying works without unmasking it (so handing a
/// password over never requires showing it), and a reveal hides itself again on a timer rather than staying up
/// until the administrator remembers. Each is mutation-checked.
/// </para>
/// </summary>
[TestFixture]
public class SetPasswordDialogTests : JimComponentTestContext
{
    private const string ValueMarker = "jim-set-password-value";
    private const string GenerateMarker = "jim-set-password-generate";
    private const string CopyMarker = "jim-set-password-copy";
    private const string RevealMarker = "jim-set-password-reveal";
    private const string SubmitMarker = "jim-set-password-submit";
    private const string FailureMarker = "jim-set-password-failure";
    private const string NoClipboardMarker = "jim-set-password-no-clipboard";
    private const string UnsupportedMarker = "jim-set-password-unsupported";

    private const string GeneratedPassword = "Correct-Horse-42";

    /// <summary>
    /// Long enough that a reveal does not expire mid-assertion, short enough that a test waiting for the
    /// re-conceal finishes promptly. The real dialog uses thirty seconds; the interval is a parameter precisely
    /// so this behaviour is testable at all.
    /// </summary>
    private static readonly TimeSpan ShortReveal = TimeSpan.FromMilliseconds(150);

    [SetUp]
    public void SetUpClipboard()
    {
        // Loose mode returns default(bool) for an unconfigured call, which would render every test as though the
        // page were served over plain HTTP. Configured explicitly so the secure-context case is the default and
        // the insecure one is opted into by the test that covers it.
        JSInterop.Setup<bool>("jimInterop.isClipboardAvailable").SetResult(true);
        JSInterop.Setup<bool>("jimInterop.copyToClipboard", _ => true)
            .SetResult(true);
        JSInterop.Setup<bool>("jimInterop.clearClipboard").SetResult(true);
    }

    private IRenderedComponent<MudDialogProvider> ShowDialog(Action<DialogParameters<SetPasswordDialog>>? configure = null)
    {
        var parameters = new DialogParameters<SetPasswordDialog>
        {
            { x => x.ObjectName, "CN=Ada Lovelace,OU=People,DC=contoso,DC=com" },
            { x => x.ConnectedSystemName, "Contoso AD" },
            { x => x.SupportedExpiryBehaviours, new[] { PasswordExpiryBehaviour.RequireChangeAtNextSignIn, PasswordExpiryBehaviour.NeverExpires } },
            { x => x.GeneratePassword, () => GeneratedPassword },
            { x => x.SetPassword, (string _, PasswordSetOptions _) => Task.FromResult(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn)) },
            { x => x.RevealDuration, ShortReveal }
        };
        configure?.Invoke(parameters);

        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        provider.InvokeAsync(() => dialogService.ShowAsync<SetPasswordDialog>("Set Password", parameters));
        provider.WaitForElement($"[data-testid='{GenerateMarker}']");

        return provider;
    }

    private static IElement Button(IRenderedComponent<MudDialogProvider> provider, string marker) =>
        provider.Find($"[data-testid='{marker}']");

    private static IElement PasswordInput(IRenderedComponent<MudDialogProvider> provider) =>
        provider.Find($"input[data-testid='{ValueMarker}']");

    /// <summary>
    /// Whether the value is currently concealed. Read off the input's own type attribute, which is the browser
    /// behaviour that actually hides the characters, rather than off anything MudBlazor generates around it.
    /// </summary>
    private static bool IsMasked(IRenderedComponent<MudDialogProvider> provider) =>
        PasswordInput(provider).GetAttribute("type") == "password";

    private static void Generate(IRenderedComponent<MudDialogProvider> provider) =>
        Button(provider, GenerateMarker).Click();

    #region masked by default

    [Test]
    public void SetPasswordDialog_BeforeAnythingIsGenerated_MasksTheValue()
    {
        var provider = ShowDialog();

        Assert.That(IsMasked(provider), Is.True);
    }

    /// <summary>
    /// The rule the whole dialog is built around. An administrator asking for a password has not asked to look
    /// at one, and a generated credential appearing in clear text is the failure this design exists to prevent.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenAPasswordIsGenerated_StillMasksTheValue()
    {
        var provider = ShowDialog();

        Generate(provider);

        Assert.Multiple(() =>
        {
            Assert.That(IsMasked(provider), Is.True);
            Assert.That(PasswordInput(provider).GetAttribute("value"), Is.EqualTo(GeneratedPassword));
        });
    }

    /// <summary>
    /// Generating again after a reveal must re-conceal, or the second password lands on screen because the first
    /// one was looked at.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenGeneratingAfterARereveal_ReturnsToMasked()
    {
        var provider = ShowDialog();
        Generate(provider);
        Button(provider, RevealMarker).Click();
        provider.WaitForState(() => !IsMasked(provider));

        Generate(provider);

        Assert.That(IsMasked(provider), Is.True);
    }

    #endregion

    #region copy works while masked

    /// <summary>
    /// Copying must not require a reveal. Transferring a password to the person who needs it is the common case,
    /// and forcing it through the screen to get there would defeat masking entirely.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenMasked_CopyIsAvailableAndSendsTheValue()
    {
        var provider = ShowDialog();
        Generate(provider);

        Assert.That(IsMasked(provider), Is.True, "precondition: the value must still be masked");
        Assert.That(Button(provider, CopyMarker).HasAttribute("disabled"), Is.False);

        Button(provider, CopyMarker).Click();

        provider.WaitForState(() => JSInterop.Invocations["jimInterop.copyToClipboard"]
            .Any(invocation => Equals(invocation.Arguments[0], GeneratedPassword)));

        Assert.That(JSInterop.Invocations["jimInterop.copyToClipboard"]
            .Select(invocation => invocation.Arguments[0]), Does.Contain(GeneratedPassword));
    }

    [Test]
    public void SetPasswordDialog_WithNothingGenerated_DisablesCopy()
    {
        var provider = ShowDialog();

        Assert.That(Button(provider, CopyMarker).HasAttribute("disabled"), Is.True);
    }

    /// <summary>
    /// The Clipboard API is unavailable outside a secure context, so over plain HTTP the button would silently
    /// do nothing. Disabled with the reason stated instead, because a dead control that looks alive is worse
    /// than one that explains itself.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheContextIsNotSecure_DisablesCopyAndSaysWhy()
    {
        JSInterop.Setup<bool>("jimInterop.isClipboardAvailable").SetResult(false);

        var provider = ShowDialog();
        Generate(provider);

        provider.WaitForState(() => provider.FindAll($"[data-testid='{NoClipboardMarker}']").Count > 0);

        Assert.Multiple(() =>
        {
            Assert.That(Button(provider, CopyMarker).HasAttribute("disabled"), Is.True);
            Assert.That(provider.FindAll($"[data-testid='{NoClipboardMarker}']"), Is.Not.Empty);
        });
    }

    /// <summary>
    /// Closing the dialog makes a best-effort clipboard clear. It cannot be relied on (the browser may refuse
    /// without transient user activation, and the operating system's clipboard history is out of reach either
    /// way), which is why the dialog says so; attempting it is still worth doing.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenCancelled_AttemptsToClearTheClipboard()
    {
        var provider = ShowDialog();
        Generate(provider);

        Button(provider, "jim-set-password-cancel").Click();

        provider.WaitForState(() => JSInterop.Invocations["jimInterop.clearClipboard"].Count > 0);

        Assert.That(JSInterop.Invocations["jimInterop.clearClipboard"], Is.Not.Empty);
    }

    #endregion

    #region reveal re-conceals on the timer

    [Test]
    public void SetPasswordDialog_WhenRevealed_ShowsTheValue()
    {
        var provider = ShowDialog();
        Generate(provider);

        Button(provider, RevealMarker).Click();

        provider.WaitForState(() => !IsMasked(provider));

        Assert.That(IsMasked(provider), Is.False);
    }

    /// <summary>
    /// The reveal has to close itself. A password left on screen because the administrator was called away is
    /// exactly the exposure masking is for, and nothing else in the dialog would ever put it back.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenRevealed_ReconcealsAfterTheInterval()
    {
        var provider = ShowDialog();
        Generate(provider);
        Button(provider, RevealMarker).Click();
        provider.WaitForState(() => !IsMasked(provider));

        provider.WaitForState(() => IsMasked(provider), TimeSpan.FromSeconds(5));

        Assert.That(IsMasked(provider), Is.True);
    }

    [Test]
    public void SetPasswordDialog_WhenHiddenByHand_MasksImmediately()
    {
        var provider = ShowDialog();
        Generate(provider);
        Button(provider, RevealMarker).Click();
        provider.WaitForState(() => !IsMasked(provider));

        Button(provider, RevealMarker).Click();

        Assert.That(IsMasked(provider), Is.True);
    }

    #endregion

    #region setting the password

    [Test]
    public void SetPasswordDialog_WithNothingGenerated_DisablesSubmit()
    {
        var provider = ShowDialog();

        Assert.That(Button(provider, SubmitMarker).HasAttribute("disabled"), Is.True);
    }

    [Test]
    public void SetPasswordDialog_WhenSubmitted_SendsTheGeneratedValueAndTheChosenOptions()
    {
        string? sent = null;
        PasswordSetOptions? options = null;
        var provider = ShowDialog(parameters => parameters.Add(x => x.SetPassword, (string password, PasswordSetOptions passwordOptions) =>
        {
            sent = password;
            options = passwordOptions;
            return Task.FromResult(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
        }));
        Generate(provider);

        Button(provider, SubmitMarker).Click();

        provider.WaitForState(() => sent != null);

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.EqualTo(GeneratedPassword));
            Assert.That(options?.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
        });
    }

    /// <summary>
    /// Null rather than false when the switch is off. False would ask the Connector to disable an account nobody
    /// asked it to touch; a reset on an already-enabled account must leave its state alone.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheEnableSwitchIsOff_LeavesTheAccountsStateAlone()
    {
        PasswordSetOptions? options = null;
        var provider = ShowDialog(parameters => parameters.Add(x => x.SetPassword, (string _, PasswordSetOptions passwordOptions) =>
        {
            options = passwordOptions;
            return Task.FromResult(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
        }));
        Generate(provider);

        Button(provider, SubmitMarker).Click();

        provider.WaitForState(() => options != null);

        Assert.That(options!.EnableAccount, Is.Null);
    }

    /// <summary>
    /// A refusal keeps the dialog open carrying the target's own words. Closing would lose the reason, and the
    /// administrator's next move is almost always to try a different password in the same dialog.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheTargetRefuses_StaysOpenAndShowsTheReason()
    {
        const string reason = "The password does not meet the length, complexity or history requirements of the domain.";
        var provider = ShowDialog(parameters => parameters.Add(x => x.SetPassword, (string _, PasswordSetOptions _) =>
            Task.FromResult(PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, reason))));
        Generate(provider);

        Button(provider, SubmitMarker).Click();

        provider.WaitForState(() => provider.FindAll($"[data-testid='{FailureMarker}']").Count > 0);

        Assert.Multiple(() =>
        {
            Assert.That(provider.Find($"[data-testid='{FailureMarker}']").TextContent, Does.Contain(reason));
            Assert.That(provider.FindAll($"[data-testid='{GenerateMarker}']"), Is.Not.Empty, "the dialog must stay open");
        });
    }

    /// <summary>
    /// An empty supported-behaviour set is how a Connector says it cannot set passwords at all. The dialog says
    /// so rather than offering controls that cannot work.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheConnectorCannotSetPasswords_OffersNothing()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        provider.InvokeAsync(() => dialogService.ShowAsync<SetPasswordDialog>("Set Password", new DialogParameters<SetPasswordDialog>
        {
            { x => x.SupportedExpiryBehaviours, Array.Empty<PasswordExpiryBehaviour>() }
        }));
        provider.WaitForElement($"[data-testid='{UnsupportedMarker}']");

        Assert.Multiple(() =>
        {
            Assert.That(provider.FindAll($"[data-testid='{GenerateMarker}']"), Is.Empty);
            Assert.That(provider.Find($"[data-testid='{SubmitMarker}']").HasAttribute("disabled"), Is.True);
        });
    }

    /// <summary>
    /// Opening on a behaviour the Connector cannot apply would let an administrator submit a selection that is
    /// silently downgraded on the target.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheDefaultBehaviourIsUnsupported_SelectsOneThatIs()
    {
        PasswordSetOptions? options = null;
        var provider = ShowDialog(parameters =>
        {
            parameters.Add(x => x.SupportedExpiryBehaviours, new[] { PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy });
            parameters.Add(x => x.SetPassword, (string _, PasswordSetOptions passwordOptions) =>
            {
                options = passwordOptions;
                return Task.FromResult(PasswordSetResult.Succeeded(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy));
            });
        });
        Generate(provider);

        Button(provider, SubmitMarker).Click();

        provider.WaitForState(() => options != null);

        Assert.That(options!.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy));
    }

    #endregion
}
