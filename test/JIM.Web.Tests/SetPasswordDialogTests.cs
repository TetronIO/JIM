// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using AngleSharp.Dom;
using Bunit;
using JIM.Models.Staging;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Models;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the set-password dialog (issues #1121, #1172, #1635).
/// <para>
/// Two families of rule. The first decides whether a credential ends up on a screen somebody else can read:
/// masked from the moment it is generated, copyable without unmasking, and a reveal that hides itself again.
/// The second decides whether an administrator can tell what actually happened when one password is queued for
/// several Connected Systems: the result stage is driven by the outcome waiter, so each row reads set, retrying
/// or parked as its system answers, and the actions beside a row are the ones that can still finish the job.
/// </para>
/// <para>
/// The queueing and the stop are delegates the test scripts, as the dialog's hosts supply them; the waiter is a
/// fake registered as the service the dialog injects.
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
    private const string AccountMarker = "jim-set-password-account";
    private const string SelectAllMarker = "jim-set-password-select-all";
    private const string UnsettableMarker = "jim-set-password-unsettable";
    private const string NoClipboardMarker = "jim-set-password-no-clipboard";
    private const string UnsupportedMarker = "jim-set-password-unsupported";
    private const string SharedPermanentMarker = "jim-set-password-shared-permanent";
    private const string ConstraintsMarker = "jim-set-password-constraints";
    private const string IrreconcilableMarker = "jim-set-password-irreconcilable";
    private const string ResultMarker = "jim-set-password-result";
    private const string StopTryingMarker = "jim-set-password-stop-trying";
    private const string TryAnotherMarker = "jim-set-password-try-another";
    private const string StillDeliveringMarker = "jim-set-password-still-delivering";
    private const string PausedMarker = "jim-set-password-paused";
    private const string FailureMarker = "jim-set-password-failure";
    private const string IntroMarker = "jim-set-password-intro";

    private const string GeneratedPassword = "Correct-Horse-42";

    /// <summary>
    /// Long enough that a reveal does not expire mid-assertion, short enough that a test waiting for the
    /// re-conceal finishes promptly. The real dialog uses thirty seconds; the interval is a parameter precisely
    /// so this behaviour is testable at all.
    /// </summary>
    private static readonly TimeSpan ShortReveal = TimeSpan.FromMilliseconds(150);

    private List<MetaverseObjectAccount> _accounts = null!;
    private List<PasswordSetSubmission> _submissions = null!;
    private List<int> _stopped = null!;
    private ScriptedWaiter _waiter = null!;
    private int _generateCalls;

    protected override void ConfigureAdditionalServices()
    {
        _waiter = new ScriptedWaiter();
        Services.AddSingleton<IPasswordChangeOutcomeWaiter>(_waiter);
    }

    [SetUp]
    public void SetUpDialog()
    {
        _submissions = [];
        _stopped = [];
        _pausedSystems.Clear();
        _generateCalls = 0;
        _waiter.Reset();
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

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

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
        PasswordPolicyReconciliation? reconciliation = null,
        TimeSpan? deliveryWait = null,
        Func<PasswordSetSubmission, Task<PasswordQueueResult>>? setPassword = null)
    {
        accounts ??= allowSelection ? _accounts : [_accounts[0]];

        var parameters = new DialogParameters<SetPasswordDialog>
        {
            { x => x.Accounts, accounts },
            { x => x.MetaverseObjectId, Guid.NewGuid() },
            { x => x.AllowSelection, allowSelection },
            { x => x.RevealDuration, ShortReveal },
            { x => x.DeliveryWait, deliveryWait ?? TimeSpan.FromSeconds(5) },
            { x => x.Reconcile, (IReadOnlyList<MetaverseObjectAccount> _) => reconciliation ?? Reconciliation() },
            { x => x.GeneratePassword, (PasswordGenerationPolicy _) => { _generateCalls++; return $"{GeneratedPassword}-{_generateCalls}"; } },
            { x => x.SetPassword, setPassword ?? Queue },
            { x => x.StopTrying, (int connectedSystemId) => { _stopped.Add(connectedSystemId); return Task.CompletedTask; } }
        };

        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        provider.InvokeAsync(() => dialogService.ShowAsync<SetPasswordDialog>("Set Password", parameters));
        provider.WaitForElement($"[data-testid='{SubmitMarker}']");

        return provider;
    }

    /// <summary>
    /// The host's side of a submission: records it and answers with one queued target per account named, every
    /// one enabled unless the test says otherwise.
    /// </summary>
    private Task<PasswordQueueResult> Queue(PasswordSetSubmission submission)
    {
        _submissions.Add(submission);
        var targets = submission.Targets
            .Select(id => _accounts.Single(a => a.ConnectedSystemObjectId == id))
            .Select(a => new PasswordQueueTargetOutcome
            {
                ConnectedSystemId = a.ConnectedSystemId,
                ConnectedSystemName = a.ConnectedSystemName,
                ConnectedSystemObjectId = a.ConnectedSystemObjectId,
                Enabled = !_pausedSystems.Contains(a.ConnectedSystemName)
            })
            .ToList();
        return Task.FromResult(new PasswordQueueResult { ActivityId = Guid.NewGuid(), Targets = targets });
    }

    private readonly HashSet<string> _pausedSystems = [];

    private PasswordChangeTargetOutcome Target(string system, PasswordChangeTargetState state, string? message = null,
        PasswordSetFailureReason? reason = null, DateTime? nextAttemptAt = null) => new()
    {
        ConnectedSystemId = _accounts.Single(a => a.ConnectedSystemName == system).ConnectedSystemId,
        ConnectedSystemName = system,
        State = state,
        Message = message,
        FailureReason = reason,
        NextAttemptAt = nextAttemptAt,
        AttemptCount = state == PasswordChangeTargetState.Queued ? 0 : 1
    };

    /// <summary>
    /// Scripts the waiter to answer settled with these targets from its first call.
    /// </summary>
    private void Settle(params PasswordChangeTargetOutcome[] targets) =>
        _waiter.Answer = _ => Task.FromResult<PasswordChangeOutcomes?>(new PasswordChangeOutcomes { IsSettled = true, Targets = targets });

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

    private static Severity SummarySeverity(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindComponents<MudAlert>()
            .Single(a => a.Instance.UserAttributes.TryGetValue("data-testid", out var id) && (string?)id == SummaryMarker)
            .Instance.Severity;

    /// <summary>
    /// Whether the value is currently concealed. Read off the input's own type attribute, which is the browser
    /// behaviour that actually hides the characters, rather than off anything MudBlazor generates around it.
    /// </summary>
    private static bool IsMasked(IRenderedComponent<MudDialogProvider> provider) =>
        PasswordInput(provider).GetAttribute("type") == "password";

    /// <summary>
    /// Clicks Generate and waits for the generated value to reach the DOM.
    /// <para>
    /// The wait is the point. MudButton's click path is async, so <c>Click()</c> returning does not mean the
    /// re-render carrying the new password has happened; asserting straight afterwards reads whatever the DOM
    /// last settled on. It passes on a quiet machine and fails perhaps one run in thirty on a loaded one, which
    /// is exactly the shape that gets dismissed as "flaky" rather than fixed.
    /// </para>
    /// </summary>
    private static void Generate(IRenderedComponent<MudDialogProvider> provider)
    {
        Button(provider, GenerateMarker).Click();
        provider.WaitForState(() => PasswordInput(provider).GetAttribute("value")?.Length > 0);
    }

    /// <summary>
    /// Clicks a control and waits for the render it causes.
    /// <para>
    /// Every interaction in this fixture goes through here rather than clicking the element directly, because
    /// forgetting the wait at one call site is invisible on a quiet machine. MudBlazor's click path is async, so
    /// <c>Click()</c> returning means the handler ran, not that the re-render it queued has been processed;
    /// asserting straight afterwards reads whatever the DOM last settled on.
    /// </para>
    /// </summary>
    private static void Click(IRenderedComponent<MudDialogProvider> provider, string marker)
    {
        var rendersBefore = provider.RenderCount;
        Button(provider, marker).Click();
        provider.WaitForState(() => provider.RenderCount > rendersBefore);
    }

    /// <inheritdoc cref="Click"/>
    private static void TickAccount(IRenderedComponent<MudDialogProvider> provider, int index)
    {
        var rendersBefore = provider.RenderCount;
        provider.FindAll($"[data-testid='{AccountMarker}'] input[type=checkbox]")[index].Change(true);
        provider.WaitForState(() => provider.RenderCount > rendersBefore);
    }

    /// <summary>
    /// Submits and waits for the result stage to settle (the summary alert is drawn only once it has).
    /// </summary>
    private static void SubmitAndSettle(IRenderedComponent<MudDialogProvider> provider)
    {
        Click(provider, SubmitMarker);
        provider.WaitForElement($"[data-testid='{SummaryMarker}']");
    }

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsMasked(provider), Is.True);
            Assert.That(PasswordInput(provider).GetAttribute("value"), Is.EqualTo($"{GeneratedPassword}-1"));
        }
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
        Click(provider, RevealMarker);
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

        Click(provider, CopyMarker);

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Button(provider, CopyMarker).HasAttribute("disabled"), Is.True);
            Assert.That(provider.FindAll($"[data-testid='{NoClipboardMarker}']"), Is.Not.Empty);
        }
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

        Click(provider, CancelMarker);

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

        Click(provider, RevealMarker);

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
        Click(provider, RevealMarker);
        provider.WaitForState(() => !IsMasked(provider));

        provider.WaitForState(() => IsMasked(provider), TimeSpan.FromSeconds(5));

        Assert.That(IsMasked(provider), Is.True);
    }

    [Test]
    public void SetPasswordDialog_WhenHiddenByHand_MasksImmediately()
    {
        var provider = ShowDialog();
        Generate(provider);
        Click(provider, RevealMarker);
        provider.WaitForState(() => !IsMasked(provider));

        Click(provider, RevealMarker);

        Assert.That(IsMasked(provider), Is.True);
    }

    #endregion

    #region what the dialog promises about storage (decision D4)

    /// <summary>
    /// The password now goes through the queue, so "JIM stores nothing" would be untrue. The dialog says exactly
    /// what is held and for how long, in the words decision D4 settled on, on both surfaces.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void SetPasswordDialog_WhileComposing_SaysThePasswordIsHeldOnlyUntilDelivered(bool allowSelection)
    {
        var provider = ShowDialog(allowSelection: allowSelection);

        // The markup wraps the sentence across source lines; the browser collapses that whitespace and so does this.
        var intro = System.Text.RegularExpressions.Regex.Replace(Button(provider, IntroMarker).TextContent, @"\s+", " ");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(intro, Does.Contain("holds the password encrypted only until"));
            Assert.That(intro, Does.Contain("keeps a refused one so it can finish the job"));
            Assert.That(provider.Markup, Does.Not.Contain("stores nothing"));
        }
    }

    #endregion

    #region one account: the dialog collapses to what shipped for #1121

    /// <summary>
    /// A picker with one option is decoration. The single-account case has to render as the dialog it was
    /// before there was anything to choose between.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WithOneAccount_DrawsNoPicker()
    {
        var provider = ShowDialog();

        Assert.That(provider.FindAll($"[data-testid='{AccountMarker}']"), Is.Empty);
    }

    [Test]
    public void SetPasswordDialog_WithNothingGenerated_DisablesSubmit()
    {
        var provider = ShowDialog();

        Assert.That(Button(provider, SubmitMarker).HasAttribute("disabled"), Is.True);
    }

    [Test]
    public void SetPasswordDialog_WhenSubmitted_SendsTheGeneratedValueTheTargetAndTheChosenOptions()
    {
        Settle(Target("Contoso AD", PasswordChangeTargetState.Set));
        var provider = ShowDialog();
        Generate(provider);

        SubmitAndSettle(provider);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_submissions, Has.Count.EqualTo(1));
            Assert.That(_submissions[0].Password, Is.EqualTo($"{GeneratedPassword}-1"));
            Assert.That(_submissions[0].ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
            Assert.That(_submissions[0].Targets, Is.EqualTo(new[] { _accounts[0].ConnectedSystemObjectId }),
                "the account's Connected System Object id is the target the one operation takes (#1635)");
        }
    }

    /// <summary>
    /// Null rather than false when the switch is off. False would ask the Connector to disable an account
    /// nobody asked it to touch; a reset on an already-enabled account must leave its state alone.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheEnableSwitchIsOff_LeavesTheAccountsStateAlone()
    {
        Settle(Target("Contoso AD", PasswordChangeTargetState.Set));
        var provider = ShowDialog();
        Generate(provider);

        SubmitAndSettle(provider);

        Assert.That(_submissions[0].EnableAccount, Is.Null);
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindAll($"[data-testid='{GenerateMarker}']"), Is.Empty);
            Assert.That(Button(provider, SubmitMarker).HasAttribute("disabled"), Is.True);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindAll($"[data-testid='{AccountMarker}'] input[type=checkbox]:checked"), Is.Empty);
            Assert.That(Button(provider, SubmitMarker).HasAttribute("disabled"), Is.True);
            Assert.That(Button(provider, GenerateMarker).HasAttribute("disabled"), Is.True,
                "there is nothing yet to generate a password for");
        }
    }

    /// <summary>
    /// Accounts whose Connector cannot set a password are a sentence, not rows. A disabled row invites "why can
    /// I not click this"; a sentence answers it.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WithAnAccountThatCannotTakeAPassword_SaysSoWithoutOfferingIt()
    {
        var provider = ShowDialog(allowSelection: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindAll($"[data-testid='{AccountMarker}']"), Has.Count.EqualTo(2));
            Assert.That(provider.Find($"[data-testid='{UnsettableMarker}']").TextContent, Does.Contain("Payroll (File)"));
        }
    }

    [Test]
    public void SetPasswordDialog_WhenSelectAllIsUsed_SelectsEveryAccountThatCanTakeAPassword()
    {
        var provider = ShowDialog(allowSelection: true);

        Click(provider, SelectAllMarker);

        Assert.That(provider.FindAll($"[data-testid='{AccountMarker}'] input[type=checkbox]:checked"), Has.Count.EqualTo(2));
    }

    /// <summary>
    /// The action says what it will do, so what is about to happen is legible without reading back up the
    /// dialog.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WithSeveralAccountsSelected_CountsThemOnTheAction()
    {
        var provider = ShowDialog(allowSelection: true);

        Click(provider, SelectAllMarker);

        Assert.That(Button(provider, SubmitMarker).TextContent, Does.Contain("Set on 2 accounts"));
    }

    [Test]
    public void SetPasswordDialog_WhenSubmitted_SetsOnlyTheSelectedAccounts()
    {
        Settle(Target("Contoso AD", PasswordChangeTargetState.Set));
        var provider = ShowDialog(allowSelection: true);
        TickAccount(provider, 0);
        Generate(provider);

        SubmitAndSettle(provider);

        Assert.That(_submissions[0].Targets, Is.EqualTo(new[] { _accounts[0].ConnectedSystemObjectId }));
    }

    /// <summary>
    /// Only the behaviours every selected Connector can apply. Offering one that some cannot would let an
    /// administrator choose a setting silently downgraded on part of the fan-out. Deliberately arranged so the
    /// intersection and the union of the two Connectors' capabilities produce different answers, and so that
    /// neither contains the dialog's own default.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WithSeveralAccountsSelected_OffersOnlyTheExpiryBehavioursAllOfThemSupport()
    {
        _accounts =
        [
            Account("Contoso AD", true, [PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, PasswordExpiryBehaviour.NeverExpires]),
            Account("Research LDAP", true, [PasswordExpiryBehaviour.NeverExpires])
        ];
        Settle(Target("Contoso AD", PasswordChangeTargetState.Set), Target("Research LDAP", PasswordChangeTargetState.Set));
        var provider = ShowDialog(allowSelection: true, accounts: _accounts);
        Click(provider, SelectAllMarker);
        Generate(provider);

        SubmitAndSettle(provider);

        Assert.That(_submissions[0].ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires),
            "the only behaviour both Connectors can apply");
    }

    [Test]
    public void SetPasswordDialog_WithReconciledConstraints_SaysWhatThePasswordWillSatisfy()
    {
        var provider = ShowDialog(allowSelection: true,
            reconciliation: Reconciliation(constraints: ["15 characters or more", "3 of 4 character categories"]));

        Click(provider, SelectAllMarker);

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

        Click(provider, SelectAllMarker);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Find($"[data-testid='{IrreconcilableMarker}']").TextContent, Does.Contain("Contoso AD"));
            Assert.That(Button(provider, GenerateMarker).HasAttribute("disabled"), Is.True);
            Assert.That(Button(provider, SubmitMarker).HasAttribute("disabled"), Is.True);
        }
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
        Click(provider, SelectAllMarker);
        Generate(provider);

        Assert.That(provider.FindAll($"[data-testid='{SharedPermanentMarker}']"), Is.Empty,
            "the default expiry behaviour is not the combination worth warning about");

        // Dispatched onto the renderer: changing a component's state from the test thread is what the
        // Dispatcher exists to prevent, and doing it directly throws rather than rendering.
        var expirySelect = provider.FindComponents<MudSelect<PasswordExpiryBehaviour>>()[0];
        provider.InvokeAsync(() => expirySelect.Instance.ValueChanged.InvokeAsync(PasswordExpiryBehaviour.NeverExpires));

        provider.WaitForState(() => provider.FindAll($"[data-testid='{SharedPermanentMarker}']").Count > 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindAll($"[data-testid='{SharedPermanentMarker}']"), Is.Not.Empty);
            Assert.That(Button(provider, SubmitMarker).HasAttribute("disabled"), Is.False, "warned, not refused");
        }
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
        using (Assert.EnterMultipleScope())
        {
            // The exact label on the Schema tab's button, so the notice names what they will actually see.
            Assert.That(notice.TextContent, Does.Contain("Refresh Schema"));
            Assert.That(notice.QuerySelector("a")?.GetAttribute("href"), Does.Contain("?t=schema"),
                "the notice has to reach the place the repair happens");
            Assert.That(provider.FindAll("[data-testid='jim-set-password-no-published-policy']"), Is.Empty);
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(notice.TextContent, Does.Contain("nothing to configure"));
            Assert.That(provider.FindAll("[data-testid='jim-set-password-unknown-policy']"), Is.Empty,
                "there is no schema import that would help here");
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindAll("[data-testid='jim-set-password-unknown-policy']"), Is.Empty);
            Assert.That(provider.FindAll("[data-testid='jim-set-password-no-published-policy']"), Is.Empty);
        }
    }

    #endregion

    #region the result stage, driven by the outcome waiter

    /// <summary>
    /// The happy path: every system answered and took the password. The summary is a success, and the row says
    /// what was asked of the password beyond setting it, because that is the fact the administrator has to pass on.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenEverySystemTakesThePassword_ReportsSuccessAndWhatWasAskedOfIt()
    {
        Settle(Target("Contoso AD", PasswordChangeTargetState.Set, "Password set."), Target("Fabrikam HR", PasswordChangeTargetState.Set, "Password set."));
        var provider = ShowDialog(allowSelection: true);
        Click(provider, SelectAllMarker);
        Generate(provider);

        SubmitAndSettle(provider);

        var rows = provider.FindAll($"[data-testid='{ResultMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SummarySeverity(provider), Is.EqualTo(Severity.Success));
            Assert.That(Button(provider, SummaryMarker).TextContent, Does.Contain("Password set on all 2 accounts."));
            Assert.That(rows, Has.Count.EqualTo(2), "one row per account, including the ones that worked");
            Assert.That(rows.Select(r => r.GetAttribute("data-state")), Is.All.EqualTo("Set"));
            Assert.That(rows[0].TextContent, Does.Contain("must be changed at next sign-in"));
            Assert.That(provider.FindAll($"[data-testid='{AccountMarker}']"), Is.Empty,
                "the picker has nothing left to ask once the password is queued");
            Assert.That(provider.FindAll($"[data-testid='{TryAnotherMarker}']"), Is.Empty, "nothing was refused");
            Assert.That(Button(provider, CancelMarker).TextContent, Does.Contain("Done"));
        }
    }

    /// <summary>
    /// The rows appear from what was queued, so the stage is never empty while the first wait is in flight, and
    /// each one moves as its system answers.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhileDelivering_ShowsTheQueuedRowsBeforeTheWaiterAnswers()
    {
        var release = new TaskCompletionSource<PasswordChangeOutcomes?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiter.Answer = _ => release.Task;
        var provider = ShowDialog();
        Generate(provider);

        Click(provider, SubmitMarker);

        provider.WaitForAssertion(() =>
        {
            var rows = provider.FindAll($"[data-testid='{ResultMarker}']");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rows, Has.Count.EqualTo(1));
                Assert.That(rows[0].GetAttribute("data-state"), Is.EqualTo("Queued"));
                Assert.That(provider.FindAll($"[data-testid='{SummaryMarker}']"), Is.Empty, "nothing to summarise until it settles");
            }
        });

        release.SetResult(new PasswordChangeOutcomes { IsSettled = true, Targets = [Target("Contoso AD", PasswordChangeTargetState.Set)] });

        provider.WaitForAssertion(() =>
            Assert.That(provider.FindAll($"[data-testid='{ResultMarker}']")[0].GetAttribute("data-state"), Is.EqualTo("Set")));
    }

    /// <summary>
    /// A system JIM could not reach is not a failure: the password is kept and retried. The summary says so in
    /// amber, names when the next attempt falls due, and the row offers the one thing an administrator can do
    /// about it here, which is decide not to wait.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenASystemCannotBeReached_ReportsAWarningWithTheNextAttemptAndOffersStopTrying()
    {
        var next = DateTime.UtcNow.AddMinutes(5);
        Settle(
            Target("Contoso AD", PasswordChangeTargetState.Set, "Password set."),
            Target("Fabrikam HR", PasswordChangeTargetState.Retrying, "Connection refused", PasswordSetFailureReason.Transient, next));
        var provider = ShowDialog(allowSelection: true);
        Click(provider, SelectAllMarker);
        Generate(provider);

        SubmitAndSettle(provider);

        var summary = Button(provider, SummaryMarker).TextContent;
        var retryingRow = provider.FindAll($"[data-testid='{ResultMarker}']")[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SummarySeverity(provider), Is.EqualTo(Severity.Warning));
            Assert.That(summary, Does.Contain("Set on 1 of 2 accounts."));
            Assert.That(summary, Does.Contain("Fabrikam HR could not be reached, so JIM has kept the password and will try again in 5 minutes"));
            Assert.That(summary, Does.Contain("then with a longer wait each time"));
            Assert.That(retryingRow.GetAttribute("data-state"), Is.EqualTo("Retrying"));
            Assert.That(retryingRow.TextContent, Does.Contain("Target unavailable: Connection refused"));
            Assert.That(retryingRow.TextContent, Does.Contain($"Next attempt {next.ToLocalTime().ToFriendlyDate()}"));
            Assert.That(provider.FindAll($"[data-testid='{TryAnotherMarker}']"), Is.Empty, "the password was not refused, so there is nothing to replace");
        }

        Click(provider, StopTryingMarker);

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_stopped, Is.EqualTo(new[] { _accounts[1].ConnectedSystemId }), "stop trying is for that Connected System alone");
                Assert.That(provider.FindAll($"[data-testid='{ResultMarker}']")[1].GetAttribute("data-state"), Is.EqualTo("Cancelled"));
            }
        });
    }

    /// <summary>
    /// A refusal is an error, not a caution: the person now holds two different passwords and nothing will
    /// change that without somebody acting. The row carries the target's own words, the guidance the queue page
    /// offers for that failure, and the one action that can still finish the job.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenASystemRefuses_ReportsAnErrorWithTheTargetsWordsAndGuidance()
    {
        Settle(
            Target("Contoso AD", PasswordChangeTargetState.Set, "Password set."),
            Target("Fabrikam HR", PasswordChangeTargetState.Parked, "Refused: too short.", PasswordSetFailureReason.PolicyRejection));
        var provider = ShowDialog(allowSelection: true);
        Click(provider, SelectAllMarker);
        Generate(provider);

        SubmitAndSettle(provider);

        var summary = Button(provider, SummaryMarker).TextContent;
        var rows = provider.FindAll($"[data-testid='{ResultMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SummarySeverity(provider), Is.EqualTo(Severity.Error));
            Assert.That(summary, Does.Contain("Set on 1 of 2 accounts."));
            Assert.That(summary, Does.Contain("The password in Fabrikam HR is unchanged"));
            Assert.That(rows[1].GetAttribute("data-state"), Is.EqualTo("Parked"));
            Assert.That(rows[1].ClassName, Does.Contain("jim-password-result--failed"), "the row carries the failure, not the sentence");
            Assert.That(rows[0].ClassName, Does.Not.Contain("jim-password-result--failed"));
            Assert.That(rows[1].TextContent, Does.Contain("Refused: too short."));
            Assert.That(rows[1].QuerySelector("[data-testid='jim-password-guidance-toggle']"), Is.Not.Null,
                "guidance is the only thing telling them what to do next");
            Assert.That(provider.FindAll($"[data-testid='{TryAnotherMarker}']"), Has.Count.EqualTo(1));
        }
    }

    /// <summary>
    /// One account, refused: the summary is the whole story, the guidance still appears, and a fresh password is
    /// still offered.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheOnlySystemRefuses_ReportsTheRefusalAndOffersGuidance()
    {
        Settle(Target("Contoso AD", PasswordChangeTargetState.Parked, "Refused.", PasswordSetFailureReason.PolicyRejection));
        var provider = ShowDialog();
        Generate(provider);

        SubmitAndSettle(provider);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SummarySeverity(provider), Is.EqualTo(Severity.Error));
            Assert.That(Button(provider, SummaryMarker).TextContent, Does.Contain("The password was not set. Contoso AD refused it."));
            Assert.That(provider.FindAll("[data-testid='jim-password-guidance-toggle']"), Is.Not.Empty);
            Assert.That(provider.FindAll($"[data-testid='{SubmitMarker}']"), Is.Empty, "the dialog is past composing; the way forward is a fresh password");
        }
    }

    /// <summary>
    /// Three outcomes, three severities. Reporting "nothing was set" in the same amber as "most of it was set"
    /// understates it, and the colour is what an administrator reads before the sentence.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenNoSystemTookThePassword_ReportsItAsAnErrorRatherThanAWarning()
    {
        Settle(
            Target("Contoso AD", PasswordChangeTargetState.Parked, "Refused.", PasswordSetFailureReason.PolicyRejection),
            Target("Fabrikam HR", PasswordChangeTargetState.Parked, "Refused.", PasswordSetFailureReason.PolicyRejection));
        var provider = ShowDialog(allowSelection: true);
        Click(provider, SelectAllMarker);
        Generate(provider);

        SubmitAndSettle(provider);

        Assert.That(SummarySeverity(provider), Is.EqualTo(Severity.Error));
    }

    /// <summary>
    /// The one case a retry cannot cover. A refused password will be refused again, and replacing it only where
    /// it failed would leave the person with two, so the escape hatch generates a fresh one and queues it for
    /// every account the change touched, including the accounts that took the first; the queue holds one change
    /// per person per system, so the new value simply supersedes whatever is still owed.
    /// </summary>
    [Test]
    public void SetPasswordDialog_TryAnotherPassword_GeneratesAFreshValueAndQueuesItForEveryAccountAgain()
    {
        Settle(
            Target("Contoso AD", PasswordChangeTargetState.Set, "Password set."),
            Target("Fabrikam HR", PasswordChangeTargetState.Parked, "Refused.", PasswordSetFailureReason.PolicyRejection));
        var provider = ShowDialog(allowSelection: true);
        Click(provider, SelectAllMarker);
        Generate(provider);
        SubmitAndSettle(provider);

        Click(provider, TryAnotherMarker);

        provider.WaitForState(() => _submissions.Count == 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_submissions[1].Targets, Is.EquivalentTo(new[] { _accounts[0].ConnectedSystemObjectId, _accounts[1].ConnectedSystemObjectId }),
                "the accounts that succeeded are rewritten too, so this person keeps one password");
            Assert.That(_submissions[1].Password, Is.Not.EqualTo(_submissions[0].Password));
            Assert.That(_generateCalls, Is.EqualTo(2));
        }
    }

    /// <summary>
    /// The same escape hatch from inside the guidance panel, which offers it where the failure was a policy
    /// rejection across more than one account.
    /// </summary>
    [Test]
    public void SetPasswordDialog_GuidancePanelsNewPasswordForAll_QueuesAFreshValueForEveryAccount()
    {
        Settle(
            Target("Contoso AD", PasswordChangeTargetState.Set, "Password set."),
            Target("Fabrikam HR", PasswordChangeTargetState.Parked, "Refused.", PasswordSetFailureReason.PolicyRejection));
        var provider = ShowDialog(allowSelection: true);
        Click(provider, SelectAllMarker);
        Generate(provider);
        SubmitAndSettle(provider);

        provider.WaitForElement("[data-testid='jim-password-guidance-toggle']").Click();
        provider.WaitForElement("[data-testid='jim-password-guidance-regenerate']").Click();

        provider.WaitForState(() => _submissions.Count == 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_submissions[1].Targets, Has.Count.EqualTo(2));
            Assert.That(_submissions[1].Password, Is.Not.EqualTo(_submissions[0].Password));
        }
    }

    /// <summary>
    /// The wait is bounded. A directory that has not answered in ten seconds is followed on the Password tab, not
    /// from a spinner, and the dialog says where.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenTheWaitRunsOut_SaysDeliveryContinuesAndWhereToFollowIt()
    {
        _waiter.Answer = _ => Task.FromResult<PasswordChangeOutcomes?>(new PasswordChangeOutcomes
        {
            IsSettled = false,
            Targets = [Target("Contoso AD", PasswordChangeTargetState.Queued)]
        });
        var provider = ShowDialog(deliveryWait: TimeSpan.FromMilliseconds(300));
        Generate(provider);

        Click(provider, SubmitMarker);

        provider.WaitForElement($"[data-testid='{StillDeliveringMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SummarySeverity(provider), Is.EqualTo(Severity.Warning));
            Assert.That(Button(provider, SummaryMarker).TextContent, Does.Contain("Contoso AD has not answered yet"));
            Assert.That(Button(provider, StillDeliveringMarker).InnerHtml, Does.Contain("t=passwords&amp;metaverseObjectId="),
                "the note links to this person's rows on the Passwords tab of Operations");
        }
    }

    /// <summary>
    /// Decision D1: a named account is written to whether or not its system takes propagated passwords. The row
    /// reads Set like any other; the sentence beneath stops that being taken as evidence that the person's own
    /// password changes will reach this system too.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenASystemIsPausedForPropagation_SaysThePasswordIsDeliveredThereAnyway()
    {
        _pausedSystems.Add("Contoso AD");
        Settle(Target("Contoso AD", PasswordChangeTargetState.Set, "Password set."));
        var provider = ShowDialog();
        Generate(provider);

        SubmitAndSettle(provider);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindAll($"[data-testid='{ResultMarker}']")[0].GetAttribute("data-state"), Is.EqualTo("Set"));
            Assert.That(Button(provider, PausedMarker).TextContent, Does.Contain("Contoso AD is not taking propagated passwords; this one is delivered there because you named the account."));
        }
    }

    /// <summary>
    /// A request JIM refuses before recording anything (an account that is not this person's, two in one system)
    /// is shown in its own words and leaves the administrator composing, with the selection to change.
    /// </summary>
    [Test]
    public void SetPasswordDialog_WhenJimRefusesTheRequest_ShowsTheReasonAndStaysComposing()
    {
        var provider = ShowDialog(setPassword: _ => throw new ArgumentException("Connected System Objects A and B are both in Contoso AD; a password can be set on one account per Connected System at a time."));
        Generate(provider);

        Click(provider, SubmitMarker);

        provider.WaitForElement($"[data-testid='{FailureMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Button(provider, FailureMarker).TextContent, Does.Contain("one account per Connected System at a time"));
            Assert.That(provider.FindAll($"[data-testid='{SubmitMarker}']"), Is.Not.Empty, "still composing");
            Assert.That(provider.FindAll($"[data-testid='{ResultMarker}']"), Is.Empty, "nothing was queued, so there is nothing to follow");
            Assert.That(_waiter.Calls, Is.Zero);
        }
    }

    #endregion

    /// <summary>
    /// A waiter the test scripts. <see cref="Answer"/> may return a task that is not yet complete, to hold the dialog
    /// in its delivering stage.
    /// </summary>
    private sealed class ScriptedWaiter : IPasswordChangeOutcomeWaiter
    {
        public Func<Guid, Task<PasswordChangeOutcomes?>> Answer { get; set; } = _ => Task.FromResult<PasswordChangeOutcomes?>(null);

        public int Calls { get; private set; }

        public void Reset()
        {
            Calls = 0;
            Answer = _ => Task.FromResult<PasswordChangeOutcomes?>(null);
        }

        public Task<PasswordChangeOutcomes?> WaitForOutcomesAsync(Guid activityId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            return Answer(activityId);
        }
    }
}
