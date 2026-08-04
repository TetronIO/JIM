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
/// Covers the set-password dialog (issues #1121, #1172).
/// <para>
/// Two families of rule. The first decides whether a credential ends up on a screen somebody else can read:
/// masked from the moment it is generated, copyable without unmasking, and a reveal that hides itself again.
/// The second decides whether an administrator can tell what actually happened when one password is set across
/// several Connected Systems, which is the case that routinely goes partly wrong.
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
    private const string CancelMarker = "jim-set-password-cancel";
    private const string SummaryMarker = "jim-set-password-summary";
    private const string RailMarker = "jim-set-password-rail";
    private const string AccountMarker = "jim-set-password-account";
    private const string SelectAllMarker = "jim-set-password-select-all";
    private const string UnsettableMarker = "jim-set-password-unsettable";
    private const string NoClipboardMarker = "jim-set-password-no-clipboard";
    private const string UnsupportedMarker = "jim-set-password-unsupported";
    private const string SharedPermanentMarker = "jim-set-password-shared-permanent";
    private const string ConstraintsMarker = "jim-set-password-constraints";
    private const string IrreconcilableMarker = "jim-set-password-irreconcilable";
    private const string ResultsMarker = "jim-set-password-results";
    private const string ResultMarker = "jim-set-password-result";

    private const string GeneratedPassword = "Correct-Horse-42";

    /// <summary>
    /// Long enough that a reveal does not expire mid-assertion, short enough that a test waiting for the
    /// re-conceal finishes promptly. The real dialog uses thirty seconds; the interval is a parameter precisely
    /// so this behaviour is testable at all.
    /// </summary>
    private static readonly TimeSpan ShortReveal = TimeSpan.FromMilliseconds(150);

    private List<MetaverseObjectAccount> _accounts = null!;
    private List<(IReadOnlyList<MetaverseObjectAccount> Accounts, string Password, PasswordSetOptions Options)> _runs = null!;
    private Dictionary<string, PasswordSetResult> _resultsBySystem = null!;
    private int _generateCalls;

    [SetUp]
    public void SetUpDialog()
    {
        _runs = [];
        _generateCalls = 0;
        _resultsBySystem = [];
        _accounts =
        [
            Account("Contoso AD", canSetPasswords: true),
            Account("Fabrikam HR", canSetPasswords: true),
            Account("Payroll (File)", canSetPasswords: false)
        ];

        // Loose mode returns default(bool) for an unconfigured call, which would render every test as though the
        // page were served over plain HTTP. Configured explicitly so the secure-context case is the default and
        // the insecure one is opted into by the test that covers it.
        JSInterop.Setup<bool>("jimInterop.isClipboardAvailable").SetResult(true);
        JSInterop.Setup<bool>("jimInterop.copyToClipboard", _ => true).SetResult(true);
        JSInterop.Setup<bool>("jimInterop.clearClipboard").SetResult(true);
    }

    private static MetaverseObjectAccount Account(
        string name,
        bool canSetPasswords,
        IReadOnlyCollection<PasswordExpiryBehaviour>? expiryBehaviours = null,
        bool canDiscoverPolicy = true,
        ConnectedSystemPasswordPolicy? discoveredPolicy = null) =>
        new()
        {
            ConnectorCanDiscoverPasswordPolicy = canDiscoverPolicy,
            DiscoveredPolicy = discoveredPolicy,
            ConnectedSystemObjectId = Guid.NewGuid(),
            ConnectedSystemId = Math.Abs(name.GetHashCode() % 1000),
            ConnectedSystemName = name,
            AccountIdentifier = $"uid=alovelace,{name}",
            ConnectorCanSetPasswords = canSetPasswords,
            SupportedExpiryBehaviours = canSetPasswords
                ? expiryBehaviours ?? [PasswordExpiryBehaviour.RequireChangeAtNextSignIn, PasswordExpiryBehaviour.NeverExpires]
                : []
        };

    private IRenderedComponent<MudDialogProvider> ShowDialog(
        bool allowSelection = false,
        IReadOnlyList<MetaverseObjectAccount>? accounts = null,
        PasswordPolicyReconciliation? reconciliation = null)
    {
        accounts ??= allowSelection ? _accounts : [_accounts[0]];

        var parameters = new DialogParameters<SetPasswordDialog>
        {
            { x => x.Accounts, accounts },
            { x => x.AllowSelection, allowSelection },
            { x => x.RevealDuration, ShortReveal },
            { x => x.Reconcile, (IReadOnlyList<MetaverseObjectAccount> _) => reconciliation ?? Reconciliation() },
            { x => x.GeneratePassword, (PasswordGenerationPolicy _) => { _generateCalls++; return $"{GeneratedPassword}-{_generateCalls}"; } },
            { x => x.SetPassword, RunFanOut }
        };

        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        provider.InvokeAsync(() => dialogService.ShowAsync<SetPasswordDialog>("Set Password", parameters));
        provider.WaitForElement($"[data-testid='{SubmitMarker}']");

        return provider;
    }

    private Task<MultiAccountPasswordSetResult> RunFanOut(
        IReadOnlyList<MetaverseObjectAccount> accounts,
        string password,
        PasswordSetOptions options,
        IProgress<AccountPasswordSetOutcome> progress)
    {
        _runs.Add((accounts, password, options));

        var outcomes = accounts.Select(account => new AccountPasswordSetOutcome
        {
            ConnectedSystemObjectId = account.ConnectedSystemObjectId,
            ConnectedSystemId = account.ConnectedSystemId,
            ConnectedSystemName = account.ConnectedSystemName,
            Result = _resultsBySystem.TryGetValue(account.ConnectedSystemName, out var result)
                ? result
                : PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn),
            Duration = TimeSpan.FromMilliseconds(1)
        }).ToList();

        foreach (var outcome in outcomes)
            progress.Report(outcome);

        return Task.FromResult(new MultiAccountPasswordSetResult { Outcomes = outcomes });
    }

    private static PasswordPolicyReconciliation Reconciliation(
        IReadOnlyList<string>? constraints = null,
        IReadOnlyList<string>? conflicts = null) =>
        new()
        {
            Policy = new PasswordGenerationPolicy(),
            Constraints = constraints ?? [],
            SystemsWithNoDiscoveredPolicy = [],
            Conflicts = conflicts ?? [],
            MayBeStricterThanDiscovered = false
        };

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

    private static void TickAccount(IRenderedComponent<MudDialogProvider> provider, int index) =>
        provider.FindAll($"[data-testid='{AccountMarker}'] input[type=checkbox]")[index].Change(true);

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
            Assert.That(PasswordInput(provider).GetAttribute("value"), Is.EqualTo($"{GeneratedPassword}-1"));
        });
    }

    /// <summary>
    /// Generating again after a reveal must re-conceal, or the second password lands on screen because the
    /// first one was looked at.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenGeneratingAfterAReveal_ReturnsToMasked()
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
    /// Copying must not require a reveal. Transferring a password to the person who needs it is the common
    /// case, and forcing it through the screen to get there would defeat masking entirely.
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
            .Any(invocation => Equals(invocation.Arguments[0], $"{GeneratedPassword}-1")));

        Assert.That(JSInterop.Invocations["jimInterop.copyToClipboard"]
            .Select(invocation => invocation.Arguments[0]), Does.Contain($"{GeneratedPassword}-1"));
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

        Button(provider, CancelMarker).Click();

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

    #region one account: the dialog collapses to what shipped for #1121

    /// <summary>
    /// A picker with one option and a rail with one step are both decoration. The single-account case has to
    /// render as the dialog it was before there was anything to choose between.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WithOneAccount_DrawsNoPickerAndNoRail()
    {
        var provider = ShowDialog();

        Assert.Multiple(() =>
        {
            Assert.That(provider.FindAll($"[data-testid='{AccountMarker}']"), Is.Empty);
            Assert.That(provider.FindAll($"[data-testid='{RailMarker}']"), Is.Empty);
        });
    }

    [Test]
    public void SetPasswordDialog_WithNothingGenerated_DisablesSubmit()
    {
        var provider = ShowDialog();

        Assert.That(Button(provider, SubmitMarker).HasAttribute("disabled"), Is.True);
    }

    [Test]
    public void SetPasswordDialog_WhenSubmitted_SendsTheGeneratedValueAndTheChosenOptions()
    {
        var provider = ShowDialog();
        Generate(provider);

        Button(provider, SubmitMarker).Click();

        provider.WaitForState(() => _runs.Count > 0);

        Assert.Multiple(() =>
        {
            Assert.That(_runs[0].Password, Is.EqualTo($"{GeneratedPassword}-1"));
            Assert.That(_runs[0].Options.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
            Assert.That(_runs[0].Accounts, Has.Count.EqualTo(1));
        });
    }

    /// <summary>
    /// Null rather than false when the switch is off. False would ask the Connector to disable an account
    /// nobody asked it to touch; a reset on an already-enabled account must leave its state alone.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheEnableSwitchIsOff_LeavesTheAccountsStateAlone()
    {
        var provider = ShowDialog();
        Generate(provider);

        Button(provider, SubmitMarker).Click();

        provider.WaitForState(() => _runs.Count > 0);

        Assert.That(_runs[0].Options.EnableAccount, Is.Null);
    }

    /// <summary>
    /// A refusal keeps the dialog open carrying the target's own words. Closing would lose the reason, and the
    /// administrator's next move is almost always to try a different password in the same dialog.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheTargetRefuses_StaysOpenAndShowsTheReason()
    {
        const string reason = "The password does not meet the length, complexity or history requirements of the domain.";
        _resultsBySystem["Contoso AD"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, reason);

        var provider = ShowDialog();
        Generate(provider);

        Button(provider, SubmitMarker).Click();

        provider.WaitForState(() => provider.FindAll($"[data-testid='{SummaryMarker}']").Count > 0);

        Assert.Multiple(() =>
        {
            Assert.That(provider.Find($"[data-testid='{SummaryMarker}']").TextContent, Does.Contain("Contoso AD"));
            Assert.That(provider.FindAll($"[data-testid='{SubmitMarker}']"), Is.Not.Empty, "the dialog must stay open");
        });
    }

    /// <summary>
    /// An empty supported-behaviour set is how a Connector says it cannot set passwords at all. The dialog says
    /// so rather than offering controls that cannot work.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheConnectorCannotSetPasswords_OffersNothing()
    {
        var provider = ShowDialog(accounts: [Account("Payroll (File)", canSetPasswords: false)]);

        provider.WaitForElement($"[data-testid='{UnsupportedMarker}']");

        Assert.Multiple(() =>
        {
            Assert.That(provider.FindAll($"[data-testid='{GenerateMarker}']"), Is.Empty);
            Assert.That(Button(provider, SubmitMarker).HasAttribute("disabled"), Is.True);
        });
    }

    #endregion

    #region several accounts: choosing where the password goes

    /// <summary>
    /// Nothing preselected. Somebody resetting a forgotten password in one Connected System must not silently
    /// reset the others, and a wrong default here is expensive every single time.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WithSeveralAccounts_PreselectsNothing()
    {
        var provider = ShowDialog(allowSelection: true);

        Assert.Multiple(() =>
        {
            Assert.That(provider.FindAll($"[data-testid='{AccountMarker}'] input[type=checkbox]:checked"), Is.Empty);
            Assert.That(Button(provider, SubmitMarker).HasAttribute("disabled"), Is.True);
            Assert.That(Button(provider, GenerateMarker).HasAttribute("disabled"), Is.True,
                "there is nothing yet to generate a password for");
        });
    }

    /// <summary>
    /// Accounts whose Connector cannot set a password are a sentence, not rows. A disabled row invites "why can
    /// I not click this"; a sentence answers it.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WithAnAccountThatCannotTakeAPassword_SaysSoWithoutOfferingIt()
    {
        var provider = ShowDialog(allowSelection: true);

        Assert.Multiple(() =>
        {
            Assert.That(provider.FindAll($"[data-testid='{AccountMarker}']"), Has.Count.EqualTo(2));
            Assert.That(provider.Find($"[data-testid='{UnsettableMarker}']").TextContent, Does.Contain("Payroll (File)"));
        });
    }

    [Test]
    public void SetPasswordDialog_WhenSelectAllIsUsed_SelectsEveryAccountThatCanTakeAPassword()
    {
        var provider = ShowDialog(allowSelection: true);

        Button(provider, SelectAllMarker).Click();

        Assert.That(provider.FindAll($"[data-testid='{AccountMarker}'] input[type=checkbox]:checked"), Has.Count.EqualTo(2));
    }

    /// <summary>
    /// A rail of one step is decoration; two or more is a sequence worth narrating.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WithOneAccountSelected_DrawsNoRail()
    {
        var provider = ShowDialog(allowSelection: true);

        TickAccount(provider, 0);

        Assert.That(provider.FindAll($"[data-testid='{RailMarker}']"), Is.Empty);
    }

    [Test]
    public void SetPasswordDialog_WithTwoAccountsSelected_DrawsTheRail()
    {
        var provider = ShowDialog(allowSelection: true);

        Button(provider, SelectAllMarker).Click();

        Assert.That(provider.FindAll($"[data-testid='{RailMarker}']"), Is.Not.Empty);
    }

    /// <summary>
    /// The action says what it will do, so what is about to happen is legible without reading back up the
    /// dialog.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WithSeveralAccountsSelected_CountsThemOnTheAction()
    {
        var provider = ShowDialog(allowSelection: true);

        Button(provider, SelectAllMarker).Click();

        Assert.That(Button(provider, SubmitMarker).TextContent, Does.Contain("Set on 2 accounts"));
    }

    [Test]
    public void SetPasswordDialog_WhenSubmitted_SetsOnlyTheSelectedAccounts()
    {
        var provider = ShowDialog(allowSelection: true);
        TickAccount(provider, 0);
        Generate(provider);

        Button(provider, SubmitMarker).Click();

        provider.WaitForState(() => _runs.Count > 0);

        Assert.That(_runs[0].Accounts.Select(a => a.ConnectedSystemName), Is.EqualTo(new[] { "Contoso AD" }));
    }

    /// <summary>
    /// Only the behaviours every selected Connector can apply. Offering one that some cannot would let an
    /// administrator choose a setting silently downgraded on part of the fan-out.
    /// </summary>
    /// <summary>
    /// Deliberately arranged so the intersection and the union of the two Connectors' capabilities produce
    /// different answers, and so that neither contains the dialog's own default. Overlapping sets that both
    /// include the default would let a union pass this test while shipping a behaviour one Connector silently
    /// downgrades.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WithSeveralAccountsSelected_OffersOnlyTheExpiryBehavioursAllOfThemSupport()
    {
        var accounts = new List<MetaverseObjectAccount>
        {
            Account("Contoso AD", true, [PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, PasswordExpiryBehaviour.NeverExpires]),
            Account("Research LDAP", true, [PasswordExpiryBehaviour.NeverExpires])
        };
        var provider = ShowDialog(allowSelection: true, accounts: accounts);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);

        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count > 0);

        Assert.That(_runs[0].Options.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires),
            "the only behaviour both Connectors can apply");
    }

    [Test]
    public void SetPasswordDialog_WithReconciledConstraints_SaysWhatThePasswordWillSatisfy()
    {
        var provider = ShowDialog(allowSelection: true,
            reconciliation: Reconciliation(constraints: ["15 characters or more", "3 of 4 character categories"]));

        Button(provider, SelectAllMarker).Click();

        Assert.That(provider.Find($"[data-testid='{ConstraintsMarker}']").TextContent, Does.Contain("15 characters or more"));
    }

    /// <summary>
    /// Where no single password can satisfy every selected system, that is stated before anything is generated
    /// rather than discovered as a rejection on the second account, after the first has already been changed.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenThePoliciesCannotBeReconciled_BlocksBeforeGenerating()
    {
        var provider = ShowDialog(allowSelection: true,
            reconciliation: Reconciliation(conflicts: ["Contoso AD: requires at least 20 characters."]));

        Button(provider, SelectAllMarker).Click();

        Assert.Multiple(() =>
        {
            Assert.That(provider.Find($"[data-testid='{IrreconcilableMarker}']").TextContent, Does.Contain("Contoso AD"));
            Assert.That(Button(provider, GenerateMarker).HasAttribute("disabled"), Is.True);
            Assert.That(Button(provider, SubmitMarker).HasAttribute("disabled"), Is.True);
        });
    }

    /// <summary>
    /// Advisory, not refused. A shared password is defensible when it must be changed at the next sign-in; one
    /// that never expires, held in several systems, means a credential taken from any one of them opens the
    /// others with nothing to rotate it.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WithSeveralAccountsAndNeverExpires_WarnsWithoutRefusing()
    {
        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);

        Assert.That(provider.FindAll($"[data-testid='{SharedPermanentMarker}']"), Is.Empty,
            "the default expiry behaviour is not the combination worth warning about");

        // Dispatched onto the renderer: changing a component's state from the test thread is what the
        // Dispatcher exists to prevent, and doing it directly throws rather than rendering.
        var expirySelect = provider.FindComponents<MudSelect<PasswordExpiryBehaviour>>()[0];
        provider.InvokeAsync(() => expirySelect.Instance.ValueChanged.InvokeAsync(PasswordExpiryBehaviour.NeverExpires));

        provider.WaitForState(() => provider.FindAll($"[data-testid='{SharedPermanentMarker}']").Count > 0);

        Assert.Multiple(() =>
        {
            Assert.That(provider.FindAll($"[data-testid='{SharedPermanentMarker}']"), Is.Not.Empty);
            Assert.That(Button(provider, SubmitMarker).HasAttribute("disabled"), Is.False, "warned, not refused");
        });
    }

    #endregion

    #region partial failure

    /// <summary>
    /// The state the whole design exists to make legible. "Two of three succeeded" leaves the administrator to
    /// work out which, with somebody on the telephone whose password now works in two places out of three, so
    /// the summary names the consequence instead of the count.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenSomeAccountsRefuse_NamesWhichPasswordIsUnchanged()
    {
        _resultsBySystem["Fabrikam HR"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();

        provider.WaitForState(() => provider.FindAll($"[data-testid='{SummaryMarker}']").Count > 0);

        var summary = provider.Find($"[data-testid='{SummaryMarker}']").TextContent;
        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("Fabrikam HR"));
            Assert.That(summary, Does.Contain("unchanged"));
        });
    }

    /// <summary>
    /// Retry names the system when one failed, and counts them when several did.
    /// </summary>
    [Test]
    public void SetPasswordDialog_AfterAPartialFailure_OffersToRetryTheFailedSystemByName()
    {
        _resultsBySystem["Fabrikam HR"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => provider.FindAll($"[data-testid='{SummaryMarker}']").Count > 0);

        Assert.That(Button(provider, SubmitMarker).TextContent, Does.Contain("Retry Fabrikam HR"));
    }

    /// <summary>
    /// Retry touches only the accounts that did not take the password. Re-sending it to an account that did
    /// would reset a password the person may already be using.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenRetried_SetsOnlyTheAccountsThatFailed()
    {
        _resultsBySystem["Fabrikam HR"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 1);

        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 2);

        Assert.That(_runs[1].Accounts.Select(a => a.ConnectedSystemName), Is.EqualTo(new[] { "Fabrikam HR" }));
    }

    /// <summary>
    /// Retry reuses the password already in memory. The administrator may have conveyed it already, and for the
    /// reasons that never judged the password, re-sending it is exactly right.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenRetried_ReusesTheSamePassword()
    {
        _resultsBySystem["Fabrikam HR"] = PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "Unreachable.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 1);

        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 2);

        Assert.Multiple(() =>
        {
            Assert.That(_runs[1].Password, Is.EqualTo(_runs[0].Password));
            Assert.That(_generateCalls, Is.EqualTo(1), "no new password was generated for the retry");
        });
    }

    /// <summary>
    /// The one case retry cannot cover. A refused password will be refused again, and replacing it only where
    /// it failed would leave the person with two, so the escape hatch generates a fresh one and sets it
    /// everywhere the fan-out touched, including the accounts that succeeded.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenANewPasswordIsAskedForAfterARejection_SetsItOnEveryAccountAgain()
    {
        _resultsBySystem["Fabrikam HR"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 1);

        provider.WaitForElement("[data-testid='jim-password-guidance-toggle']").Click();
        provider.WaitForElement("[data-testid='jim-password-guidance-regenerate']").Click();

        provider.WaitForState(() => _runs.Count == 2);

        Assert.Multiple(() =>
        {
            Assert.That(_runs[1].Accounts.Select(a => a.ConnectedSystemName),
                Is.EqualTo(new[] { "Contoso AD", "Fabrikam HR" }), "the accounts that succeeded are rewritten too, so this person keeps one password");
            Assert.That(_runs[1].Password, Is.Not.EqualTo(_runs[0].Password));
        });
    }

    #endregion

    #region what to do about a system whose rules JIM does not have

    /// <summary>
    /// Two situations wear the same face, and only one is the administrator's to fix. A Connector that could
    /// read the rules but has not been asked to is a schema refresh away; naming the button they will actually
    /// find ("Refresh Schema") is the difference between a notice they can act on and one they can only shrug
    /// at.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenASystemsPolicyIsNotHeld_SaysToRefreshItsSchemaAndLinksThere()
    {
        var provider = ShowDialog(allowSelection: true, accounts:
        [
            Account("Contoso AD", canSetPasswords: true, canDiscoverPolicy: true)
        ]);
        TickAccount(provider, 0);

        var notice = provider.Find("[data-testid='jim-set-password-unknown-policy']");
        Assert.Multiple(() =>
        {
            // The exact label on the Schema tab's button, so the notice names what they will actually see.
            Assert.That(notice.TextContent, Does.Contain("Refresh Schema"));
            Assert.That(notice.QuerySelector("a")?.GetAttribute("href"), Does.Contain("?t=schema"),
                "the notice has to reach the place the repair happens");
            Assert.That(provider.FindAll("[data-testid='jim-set-password-no-published-policy']"), Is.Empty);
        });
    }

    /// <summary>
    /// A system that publishes no password rules for any client to read is not a gap somebody forgot to close.
    /// Telling an administrator to go and import a schema there would send them after something that will never
    /// arrive, so this says plainly that there is nothing to configure.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenASystemPublishesNoPolicy_SaysThereIsNothingToConfigure()
    {
        var provider = ShowDialog(allowSelection: true, accounts:
        [
            Account("Payroll", canSetPasswords: true, canDiscoverPolicy: false)
        ]);
        TickAccount(provider, 0);

        var notice = provider.Find("[data-testid='jim-set-password-no-published-policy']");
        Assert.Multiple(() =>
        {
            Assert.That(notice.TextContent, Does.Contain("nothing to configure"));
            Assert.That(provider.FindAll("[data-testid='jim-set-password-unknown-policy']"), Is.Empty,
                "there is no schema import that would help here");
        });
    }

    [Test]
    public void SetPasswordDialog_WhenASystemsPolicyIsKnown_SaysNothingAboutIt()
    {
        var provider = ShowDialog(allowSelection: true, accounts:
        [
            Account("Contoso AD", canSetPasswords: true, canDiscoverPolicy: true,
                discoveredPolicy: new ConnectedSystemPasswordPolicy { MinimumLength = 15 })
        ]);
        TickAccount(provider, 0);

        Assert.Multiple(() =>
        {
            Assert.That(provider.FindAll("[data-testid='jim-set-password-unknown-policy']"), Is.Empty);
            Assert.That(provider.FindAll("[data-testid='jim-set-password-no-published-policy']"), Is.Empty);
        });
    }

    #endregion

    #region how the outcome is reported

    /// <summary>
    /// Choosing where to write and reading what happened are different questions, and the first shipped
    /// answering both in one list: status was bolted onto the picker rows, so a finished fan-out left a column
    /// of checkboxes beside three failures nobody could tick their way out of.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheFanOutFinishes_ReplacesThePickerWithItsOwnResultsList()
    {
        _resultsBySystem["Fabrikam HR"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 1);

        Assert.Multiple(() =>
        {
            Assert.That(provider.FindAll($"[data-testid='{AccountMarker}']"), Is.Empty,
                "the picker has nothing left to ask once the writing is done");
            Assert.That(provider.FindAll($"[data-testid='{ResultMarker}']"), Has.Count.EqualTo(2),
                "one row per account the password was written to");
        });
    }

    /// <summary>
    /// Every account gets its own row, including the ones that worked. A list of only the failures reads as a
    /// list of everything that happened, and leaves the administrator to infer the rest.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenSomeAccountsSucceeded_StillReportsThemByName()
    {
        _resultsBySystem["Fabrikam HR"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 1);

        var rows = provider.FindAll($"[data-testid='{ResultMarker}']").Select(r => r.TextContent).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(rows[0], Does.Contain("Contoso AD").And.Contain("Password set."));
            Assert.That(rows[1], Does.Contain("Fabrikam HR").And.Contain("Refused."));
        });
    }

    /// <summary>
    /// One account needs no list: the summary above it is already that same sentence, and a one-row table
    /// under a one-sentence summary says everything twice. Its guidance still has to appear somewhere, so it
    /// hangs off the summary instead.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenOnlyOneAccountWasWrittenTo_DrawsNoResultsListButStillOffersGuidance()
    {
        _resultsBySystem["Contoso AD"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var provider = ShowDialog(allowSelection: true);
        TickAccount(provider, 0);
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 1);

        Assert.Multiple(() =>
        {
            Assert.That(provider.FindAll($"[data-testid='{ResultsMarker}']"), Is.Empty);
            Assert.That(provider.FindAll($"[data-testid='{SummaryMarker}']"), Is.Not.Empty);
            Assert.That(provider.FindAll("[data-testid='jim-password-guidance-toggle']"), Is.Not.Empty,
                "guidance must survive the collapse; it is the only thing telling them what to do next");
        });
    }

    /// <summary>
    /// A leg of the rail belongs to the step it leaves, so it carries that step's outcome. Filling every
    /// traversed leg with the success colour drew a green rail straight through three red markers on a run
    /// where nothing worked at all.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenAStepFailed_DoesNotDrawItsLegAsSucceeded()
    {
        _resultsBySystem["Contoso AD"] = PasswordSetResult.Failed(PasswordSetFailureReason.Transient, "Unreachable.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 1);

        // One leg, between the failed first step and the second.
        var leg = provider.Find($"[data-testid='{RailMarker}'] .jim-password-rail-connector-fill");
        Assert.Multiple(() =>
        {
            Assert.That(leg.ClassName, Does.Contain("jim-password-rail-connector-fill--failed"));
            Assert.That(leg.ClassName, Does.Not.Contain("jim-password-rail-connector-fill--completed"));
        });
    }

    /// <summary>
    /// Three outcomes, three severities. Reporting "nothing was set" in the same amber as "most of it was set"
    /// understates it, and the colour is what an administrator reads before the sentence.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenNoAccountTookThePassword_ReportsItAsAnErrorRatherThanAWarning()
    {
        _resultsBySystem["Contoso AD"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");
        _resultsBySystem["Fabrikam HR"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 1);

        Assert.That(provider.FindComponents<MudAlert>()
                .Single(a => a.Instance.UserAttributes.TryGetValue("data-testid", out var id)
                             && (string?)id == SummaryMarker)
                .Instance.Severity,
            Is.EqualTo(Severity.Error));
    }

    /// <summary>
    /// Partly set stays a warning: some accounts did take the password, and the person now holds two different
    /// ones, which is a caution about a half-finished job rather than a failure.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenSomeAccountsTookThePassword_ReportsItAsAWarning()
    {
        _resultsBySystem["Fabrikam HR"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 1);

        Assert.That(provider.FindComponents<MudAlert>()
                .Single(a => a.Instance.UserAttributes.TryGetValue("data-testid", out var id)
                             && (string?)id == SummaryMarker)
                .Instance.Severity,
            Is.EqualTo(Severity.Warning));
    }

    /// <summary>
    /// A failed row carries its severity in the row rather than in the sentence, so the modifier has to reach
    /// the markup: without it the row is painted like any other and the failure is left to red prose, which is
    /// what read as milder than the thing it was reporting.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenAnAccountFailed_MarksItsWholeRowAsFailed()
    {
        _resultsBySystem["Fabrikam HR"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 1);

        var rows = provider.FindAll($"[data-testid='{ResultMarker}']");
        Assert.Multiple(() =>
        {
            Assert.That(rows[0].ClassName, Does.Not.Contain("jim-password-result--failed"));
            Assert.That(rows[1].ClassName, Does.Contain("jim-password-result--failed"));
        });
    }

    /// <summary>
    /// The rail's markers are the same four states the Run Profile stepper reports, carried as modifiers so
    /// one set of rules paints both. A marker with no state modifier renders as an unstyled ring.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheFanOutFinishes_MarksEachStepWithItsOwnOutcome()
    {
        _resultsBySystem["Fabrikam HR"] = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var provider = ShowDialog(allowSelection: true);
        Button(provider, SelectAllMarker).Click();
        Generate(provider);
        Button(provider, SubmitMarker).Click();
        provider.WaitForState(() => _runs.Count == 1);

        var markers = provider.FindAll($"[data-testid='{RailMarker}'] .jim-password-rail-marker")
            .Select(m => m.ClassName ?? string.Empty).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(markers[0], Does.Contain("jim-password-rail-marker--completed"));
            Assert.That(markers[1], Does.Contain("jim-password-rail-marker--failed"));
        });
    }

    #endregion
}
