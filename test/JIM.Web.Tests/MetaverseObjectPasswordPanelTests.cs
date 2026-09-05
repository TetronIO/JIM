// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using AngleSharp.Dom;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The person page's Password tab (#1119 requirement 25, #1635): what needs attention, the one action, what is
/// still owed to a Connected System, and what each past change did.
/// <para>
/// The behaviour worth pinning is that history comes from Activities and the outstanding work comes from the
/// queue, and the panel shows both. The queue row is deleted the moment a password arrives, so a panel built on
/// the queue alone would show an identity's failures and none of its successes, which is the most misleading
/// possible view of whether their password propagated. The panel takes its data and raises its actions rather
/// than reaching the application layer, so it is testable without a data layer behind it; the page owns the
/// reads and the writes.
/// </para>
/// </summary>
[TestFixture]
public class MetaverseObjectPasswordPanelTests : JimComponentTestContext
{
    private const string AttentionMarker = "jim-password-attention";
    private const string AttentionRetryMarker = "jim-password-attention-retry";
    private const string CoverageMarker = "jim-password-coverage";
    private const string SetMarker = "jim-password-set";
    private const string SetUnavailableMarker = "jim-password-set-unavailable";
    private const string QueuedRowMarker = "jim-password-queued-row";
    private const string QueuedRetryMarker = "jim-password-queued-retry";
    private const string QueuedStopMarker = "jim-password-queued-stop";
    private const string KindMarker = "jim-password-kind";

    private static PasswordSynchronisationEvent Change(PendingPasswordChangeOrigin? origin, params PasswordSynchronisationEventOutcome[] outcomes) => new()
    {
        ActivityId = Guid.NewGuid(),
        Created = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc),
        InitiatedByName = "Self-service portal",
        InitiatedByType = ActivityInitiatorType.ApiKey,
        Message = "Password change queued.",
        Origin = origin,
        Outcomes = outcomes
    };

    private static PasswordSynchronisationEventOutcome Outcome(string system, ActivityStatus status, string? error = null) => new()
    {
        ActivityId = Guid.NewGuid(),
        ConnectedSystemId = 3,
        ConnectedSystemName = system,
        Status = status,
        ErrorMessage = error,
        Message = error ?? $"Password set on {system}.",
        OccurredAt = new DateTime(2026, 8, 21, 9, 0, 5, DateTimeKind.Utc)
    };

    private static PendingPasswordChangeHeader Queued(
        int connectedSystemId,
        string system,
        PendingPasswordChangeStatus status,
        PasswordSetFailureReason? reason = null,
        string? targetMessage = null,
        int attempts = 0,
        DateTime? nextRetryAt = null) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = connectedSystemId,
        ConnectedSystemName = system,
        Status = status,
        FailureReason = reason,
        TargetMessage = targetMessage,
        AttemptCount = attempts,
        NextRetryAt = nextRetryAt,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(7)
    };

    private static MetaverseObjectAccount Account(string system, bool canSetPasswords) => new()
    {
        ConnectedSystemObjectId = Guid.NewGuid(),
        ConnectedSystemId = Math.Abs(system.GetHashCode() % 1000),
        ConnectedSystemName = system,
        AccountIdentifier = $"uid=alovelace,{system}",
        ConnectorCanSetPasswords = canSetPasswords,
        SupportedExpiryBehaviours = canSetPasswords ? [PasswordExpiryBehaviour.RequireChangeAtNextSignIn] : []
    };

    private IRenderedComponent<MetaverseObjectPasswordPanel> RenderPanel(
        IReadOnlyList<PasswordSynchronisationEvent>? events = null,
        IReadOnlyList<PendingPasswordChangeHeader>? queued = null,
        IReadOnlyList<MetaverseObjectAccount>? accounts = null,
        Guid? metaverseObjectId = null,
        Action<int>? onRetry = null,
        Action<int>? onStopTrying = null,
        Action? onSetPassword = null) =>
        Render<MetaverseObjectPasswordPanel>(p => p
            .Add(c => c.MetaverseObjectId, metaverseObjectId ?? Guid.NewGuid())
            .Add(c => c.Events, events ?? [])
            .Add(c => c.QueuedChanges, queued ?? [])
            .Add(c => c.Accounts, accounts ?? [Account("Corporate Directory", true)])
            .Add(c => c.OnRetry, onRetry ?? (_ => { }))
            .Add(c => c.OnStopTrying, onStopTrying ?? (_ => { }))
            .Add(c => c.OnSetPassword, onSetPassword ?? (() => { })));

    private static IElement Find(IRenderedComponent<MetaverseObjectPasswordPanel> cut, string marker) =>
        cut.Find($"[data-testid='{marker}']");

    private static IReadOnlyList<IElement> FindAll(IRenderedComponent<MetaverseObjectPasswordPanel> cut, string marker) =>
        cut.FindAll($"[data-testid='{marker}']");

    #region attention strip

    /// <summary>
    /// A parked change is the one thing on this tab an administrator must act on, so it is said first, in one
    /// sentence that names the system, JIM's classification of the refusal, and that nothing more will happen
    /// without them; and the remedy is beside the sentence rather than three panels down.
    /// </summary>
    [Test]
    public void Panel_WithAParkedChange_LeadsWithAnAttentionStripOfferingRetry()
    {
        var retried = new List<int>();
        var cut = RenderPanel(
            queued: [Queued(4, "Corporate Directory", PendingPasswordChangeStatus.Parked, PasswordSetFailureReason.PolicyRejection, "Too short.", attempts: 3)],
            onRetry: retried.Add);

        var strip = Find(cut, AttentionMarker);
        Assert.That(strip.TextContent, Does.Contain("1 password change is parked. Corporate Directory refused it (policy rejection) and JIM has stopped trying."));

        Find(cut, AttentionRetryMarker).Click();

        Assert.That(retried, Is.EqualTo(new[] { 4 }), "retry is for that Connected System alone");
    }

    /// <summary>
    /// Nothing can deliver an expired change, so offering to retry it would be a button that does nothing.
    /// </summary>
    [Test]
    public void Panel_WithAnExpiredChange_SaysSoAndOffersNoRetry()
    {
        var cut = RenderPanel(queued: [Queued(4, "Corporate Directory", PendingPasswordChangeStatus.Expired)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Find(cut, AttentionMarker).TextContent, Does.Contain("expired before it could be delivered"));
            Assert.That(FindAll(cut, AttentionRetryMarker), Is.Empty);
        }
    }

    [Test]
    public void Panel_AttentionStrip_LinksToTheQueueFilteredToThisIdentity()
    {
        var id = Guid.NewGuid();
        var cut = RenderPanel(
            metaverseObjectId: id,
            queued: [Queued(4, "Corporate Directory", PendingPasswordChangeStatus.Parked, PasswordSetFailureReason.Transient)]);

        Assert.That(Find(cut, AttentionMarker).InnerHtml, Does.Contain($"/admin/operations?t=passwords&amp;metaverseObjectId={id}"));
    }

    [Test]
    public void Panel_WithOnlyChangesStillOnTheirWay_DrawsNoAttentionStrip()
    {
        var cut = RenderPanel(queued: [Queued(4, "Corporate Directory", PendingPasswordChangeStatus.Pending, attempts: 1, nextRetryAt: DateTime.UtcNow.AddMinutes(5))]);

        Assert.That(FindAll(cut, AttentionMarker), Is.Empty, "retrying is JIM's to finish, not the administrator's");
    }

    #endregion

    #region the Set Password card

    /// <summary>
    /// The coverage line answers "will this reach everywhere?" before the dialog opens, and the button is offered
    /// or not on the same fact rather than appearing and then turning out to be unavailable once open.
    /// </summary>
    [Test]
    public void Panel_SetPasswordCard_CountsTheAccountsThatCanTakeAPassword()
    {
        var raised = 0;
        var cut = RenderPanel(
            accounts: [Account("Corporate Directory", true), Account("HR Portal", true), Account("Payroll (File)", false), Account("Badge System", true)],
            onSetPassword: () => raised++);

        Assert.That(Find(cut, CoverageMarker).TextContent.Trim(), Is.EqualTo("3 of 4 accounts can take a password"));

        Find(cut, SetMarker).Click();

        Assert.That(raised, Is.EqualTo(1));
    }

    [Test]
    public void Panel_SetPasswordCard_WithNoCapableAccount_DisablesTheButtonAndSaysWhy()
    {
        var cut = RenderPanel(accounts: [Account("Payroll (File)", false)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Find(cut, SetMarker).HasAttribute("disabled"), Is.True);
            Assert.That(Find(cut, SetUnavailableMarker).TextContent, Does.Contain("Connector can set passwords"));
            Assert.That(cut.Markup, Does.Not.Contain("Synchronise Password"), "one operation, one card (#1635)");
        }
    }

    #endregion

    #region still to be delivered

    /// <summary>
    /// Outstanding work is what an administrator acts on; history is what they read afterwards. A parked row can
    /// be retried from where it is read, a retrying row can be stopped, and a row waiting out a backoff says when
    /// JIM will next try, so nobody has to work that out from an attempt count.
    /// </summary>
    [Test]
    public void Panel_WithSomethingStillQueued_ShowsItSeparatelyFromHistoryWithItsActions()
    {
        var retried = new List<int>();
        var stopped = new List<int>();
        var next = new DateTime(2026, 8, 21, 9, 19, 0, DateTimeKind.Utc);
        var cut = RenderPanel(
            events: [Change(PendingPasswordChangeOrigin.Explicit, Outcome("Corporate Directory", ActivityStatus.Complete))],
            queued:
            [
                Queued(4, "HR Portal", PendingPasswordChangeStatus.Parked, PasswordSetFailureReason.PolicyRejection, "Too short.", attempts: 3),
                Queued(5, "Badge System", PendingPasswordChangeStatus.Pending, PasswordSetFailureReason.Transient, "Connection refused", attempts: 2, nextRetryAt: next)
            ],
            onRetry: retried.Add,
            onStopTrying: stopped.Add);

        var rows = FindAll(cut, QueuedRowMarker);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Still to be delivered"));
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0].TextContent, Does.Contain("Too short."));
            Assert.That(rows[1].TextContent, Does.Contain(next.ToLocalTime().ToFriendlyDate()), "the retrying row names its next attempt");
            Assert.That(rows[0].QuerySelector($"[data-testid='{QueuedRetryMarker}']"), Is.Not.Null, "a parked row can be retried in place");
            Assert.That(rows[0].QuerySelector($"[data-testid='{QueuedStopMarker}']"), Is.Null);
            Assert.That(rows[1].QuerySelector($"[data-testid='{QueuedStopMarker}']"), Is.Not.Null, "a retrying row can be stopped in place");
            Assert.That(rows[1].QuerySelector($"[data-testid='{QueuedRetryMarker}']"), Is.Null);
        }

        // Re-queried between clicks: each click re-renders, and a handler id from the previous tree is stale.
        Find(cut, QueuedRetryMarker).Click();
        Find(cut, QueuedStopMarker).Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retried, Is.EqualTo(new[] { 4 }));
            Assert.That(stopped, Is.EqualTo(new[] { 5 }));
        }
    }

    #endregion

    #region recent password changes

    [Test]
    public void Panel_WithNoHistoryAndNothingQueued_SaysSoPlainly()
    {
        var cut = RenderPanel();

        Assert.That(cut.Markup, Does.Contain("never"));
    }

    [Test]
    public void Panel_ShowsEachSystemsOwnOutcome()
    {
        var cut = RenderPanel(events:
        [
            Change(PendingPasswordChangeOrigin.Propagated,
                Outcome("Corporate AD", ActivityStatus.Complete),
                Outcome("HR SQL", ActivityStatus.FailedWithError, "The password does not meet the requirements of the domain."))
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Corporate AD"));
            Assert.That(cut.Markup, Does.Contain("HR SQL"));
            Assert.That(cut.Markup, Does.Contain("requirements of the domain"),
                "the target's own words are what tell an administrator where the remedy lives");
        }
    }

    /// <summary>
    /// One operation, two target modes (#1635): a change an administrator aimed at named accounts and one JIM
    /// propagated to every configured system read differently in history, because one was a decision made about
    /// this person and the other was a consequence of their own password change elsewhere. An Activity from before
    /// origins were recorded carries neither word, and gets no chip rather than a guessed one.
    /// </summary>
    [Test]
    public void Panel_EachChange_CarriesItsKindOrNothingWhenUnknown()
    {
        var cut = RenderPanel(events:
        [
            Change(PendingPasswordChangeOrigin.Explicit, Outcome("Corporate AD", ActivityStatus.Complete)),
            Change(PendingPasswordChangeOrigin.Propagated, Outcome("Corporate AD", ActivityStatus.Complete)),
            Change(null, Outcome("Corporate AD", ActivityStatus.Complete))
        ]);

        var chips = FindAll(cut, KindMarker);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chips, Has.Count.EqualTo(2), "the change with no recorded origin draws no chip");
            Assert.That(chips[0].TextContent.Trim(), Is.EqualTo("Set"));
            Assert.That(chips[1].TextContent.Trim(), Is.EqualTo("Propagated"));
        }
    }

    [Test]
    public void Panel_ChangeThatReachedNoSystem_SaysSoRatherThanLookingSuccessful()
    {
        // Requirement 14 read from the other end: a change with no outcomes must not render as a bare timestamp
        // that an administrator reads as "it went out fine".
        var cut = RenderPanel(events: [Change(PendingPasswordChangeOrigin.Propagated)]);

        Assert.That(cut.Markup, Does.Contain("no Connected System"));
    }

    [Test]
    public void Panel_LinksToTheQueueFilteredToThisIdentity()
    {
        var id = Guid.NewGuid();
        var cut = RenderPanel(metaverseObjectId: id);

        // The queue is the Passwords tab of Operations (#1635), and the link must land on that tab with the
        // identity filter in the same query string, or the reader arrives on the Queue tab and has to go looking.
        Assert.That(cut.Markup, Does.Contain($"/admin/operations?t=passwords&amp;metaverseObjectId={id}"));
    }

    #endregion
}
