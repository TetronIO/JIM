// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using AngleSharp.Dom;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Shared;
using MudBlazor;
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

    private const string DayMarker = "jim-password-day";
    private const string EntryMarker = "jim-password-entry";
    private const string TargetMarker = "jim-password-target";
    private const string TargetDetailMarker = "jim-password-target-detail";
    private const string HistoryRetryMarker = "jim-password-history-retry";
    private const string HistoryStopMarker = "jim-password-history-stop";

    /// <summary>
    /// A change made a few minutes ago, so it sits under Today whatever the wall clock says.
    /// </summary>
    private static readonly DateTime Recently = DateTime.UtcNow.AddMinutes(-10);

    private static PasswordSynchronisationEvent Change(PendingPasswordChangeOrigin? origin, params PasswordSynchronisationEventOutcome[] outcomes) =>
        Change(Recently, origin, outcomes);

    private static PasswordSynchronisationEvent Change(DateTime created, PendingPasswordChangeOrigin? origin, params PasswordSynchronisationEventOutcome[] outcomes) => new()
    {
        ActivityId = Guid.NewGuid(),
        Created = created,
        InitiatedByName = origin == PendingPasswordChangeOrigin.Explicit ? "Admin User" : "Self-service portal",
        InitiatedByType = origin == PendingPasswordChangeOrigin.Explicit ? ActivityInitiatorType.User : ActivityInitiatorType.ApiKey,
        Message = "Password change queued.",
        Origin = origin,
        Outcomes = outcomes
    };

    private static PasswordSynchronisationEventOutcome Outcome(string system, ActivityStatus status, string? error = null, DateTime? occurredAt = null) => new()
    {
        ActivityId = Guid.NewGuid(),
        // A stable id per system name, so two outcomes on one system coalesce into one chip and two systems do not.
        ConnectedSystemId = Math.Abs(system.GetHashCode() % 1000),
        ConnectedSystemName = system,
        Status = status,
        ErrorMessage = error,
        Message = error ?? $"Password set on {system}.",
        OccurredAt = occurredAt ?? Recently.AddSeconds(5)
    };

    private static PendingPasswordChangeHeader Queued(
        int connectedSystemId,
        string system,
        PendingPasswordChangeStatus status,
        PasswordSetFailureReason? reason = null,
        string? targetMessage = null,
        int attempts = 0,
        DateTime? nextRetryAt = null,
        DateTime? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = connectedSystemId,
        ConnectedSystemName = system,
        Status = status,
        FailureReason = reason,
        TargetMessage = targetMessage,
        AttemptCount = attempts,
        NextRetryAt = nextRetryAt,
        CreatedAt = createdAt ?? DateTime.UtcNow,
        LastAttemptedAt = attempts > 0 ? DateTime.UtcNow.AddMinutes(-1) : null,
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("never"));
            Assert.That(FindAll(cut, EntryMarker), Is.Empty);
        }
    }

    /// <summary>
    /// History reads as a timeline (#1635): a heading per day, one entry per change under it with the time of day
    /// alone, because the day is already said once above.
    /// </summary>
    [Test]
    public void Panel_History_GroupsChangesByDayWithTodayYesterdayThenTheDate()
    {
        var yesterday = DateTime.UtcNow.AddDays(-1);
        var lastWeek = DateTime.UtcNow.AddDays(-6);
        var cut = RenderPanel(events:
        [
            Change(Recently, PendingPasswordChangeOrigin.Explicit, Outcome("Corporate AD", ActivityStatus.Complete)),
            Change(yesterday, PendingPasswordChangeOrigin.Explicit, Outcome("Corporate AD", ActivityStatus.Complete)),
            Change(lastWeek, PendingPasswordChangeOrigin.Explicit, Outcome("Corporate AD", ActivityStatus.Complete))
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FindAll(cut, DayMarker).Select(d => d.TextContent.Trim()), Is.EqualTo(new[] { "Today", "Yesterday", lastWeek.ToLocalTime().ToFriendlyDay() }));
            Assert.That(FindAll(cut, EntryMarker), Has.Count.EqualTo(3), "one entry per change");
            Assert.That(FindAll(cut, "jim-password-time").Select(t => t.TextContent.Trim()),
                Is.EqualTo(new[] { Recently.ToLocalTime().ToFriendlyTime(), yesterday.ToLocalTime().ToFriendlyTime(), lastWeek.ToLocalTime().ToFriendlyTime() }));
            Assert.That(cut.FindComponents<MudTimelineItem>(), Has.Count.EqualTo(3));
        }
    }

    /// <summary>
    /// The dot is the entry's verdict at a glance: green when every system took it, amber while one is still being
    /// retried, red when one is parked, and grey for a change nothing has attempted.
    /// </summary>
    [Test]
    public void Panel_EachEntry_ColoursItsDotByTheWorstOutcome()
    {
        var parked = Change(Recently, PendingPasswordChangeOrigin.Explicit, Outcome("HR SQL", ActivityStatus.FailedWithError, "Too short."));
        var retrying = Change(Recently.AddMinutes(-1), PendingPasswordChangeOrigin.Explicit, Outcome("Badge System", ActivityStatus.FailedWithError, "Connection refused"));
        var allSet = Change(Recently.AddMinutes(-2), PendingPasswordChangeOrigin.Explicit, Outcome("Corporate AD", ActivityStatus.Complete));
        var nothingYet = Change(Recently.AddMinutes(-3), PendingPasswordChangeOrigin.Propagated);
        var cut = RenderPanel(
            events: [parked, retrying, allSet, nothingYet],
            queued:
            [
                Queued(Math.Abs("HR SQL".GetHashCode() % 1000), "HR SQL", PendingPasswordChangeStatus.Parked, PasswordSetFailureReason.PolicyRejection, "Too short.", attempts: 3, createdAt: parked.Created),
                Queued(Math.Abs("Badge System".GetHashCode() % 1000), "Badge System", PendingPasswordChangeStatus.Pending, PasswordSetFailureReason.Transient, "Connection refused", attempts: 2, nextRetryAt: DateTime.UtcNow.AddMinutes(5), createdAt: retrying.Created)
            ]);

        var dots = cut.FindComponents<MudTimelineItem>().Select(i => i.Instance.Color).ToList();
        Assert.That(dots, Is.EqualTo(new[] { Color.Error, Color.Warning, Color.Success, Color.Default }));
    }

    /// <summary>
    /// One chip per Connected System, in the Service Health pill vocabulary, naming the system and its state where
    /// that is anything other than a plain success. A live queue row decides the newest change's chip only: the
    /// queue holds one row per person and system, so an older change that already reached the system keeps
    /// reading as set.
    /// </summary>
    [Test]
    public void Panel_Chips_NameEachSystemAndItsStateWithTheQueueRowDecidingOnlyTheNewestChange()
    {
        var older = Change(Recently.AddHours(-2), PendingPasswordChangeOrigin.Explicit,
            Outcome("Corporate AD", ActivityStatus.Complete, occurredAt: Recently.AddHours(-2).AddSeconds(3)));
        var newer = Change(Recently, PendingPasswordChangeOrigin.Explicit,
            Outcome("Corporate AD", ActivityStatus.FailedWithError, "Connection refused"),
            Outcome("HR SQL", ActivityStatus.Complete));
        var cut = RenderPanel(
            events: [newer, older],
            queued: [Queued(Math.Abs("Corporate AD".GetHashCode() % 1000), "Corporate AD", PendingPasswordChangeStatus.Pending, PasswordSetFailureReason.Transient, "Connection refused", attempts: 2, nextRetryAt: DateTime.UtcNow.AddMinutes(5), createdAt: newer.Created)]);

        var entries = FindAll(cut, EntryMarker);
        var newerChips = entries[0].QuerySelectorAll($"[data-testid='{TargetMarker}']");
        var olderChips = entries[1].QuerySelectorAll($"[data-testid='{TargetMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(newerChips.Select(c => c.TextContent.Trim()), Is.EqualTo(new[] { "Corporate AD · retrying", "HR SQL" }));
            Assert.That(newerChips[0].ClassList, Does.Contain("jim-status-pill").And.Contain("jim-status-pill--warn"));
            Assert.That(newerChips[1].ClassList, Does.Contain("jim-status-pill--ok"));
            Assert.That(olderChips.Select(c => c.TextContent.Trim()), Is.EqualTo(new[] { "Corporate AD" }), "the older change already reached the system; the live row is not its");
            Assert.That(olderChips[0].ClassList, Does.Contain("jim-status-pill--ok"));
            // MudTooltip renders its content only while open, so what can be pinned here is that every chip has one
            // with content behind it; the words themselves are PasswordHistoryTimelineModelTests' to pin.
            Assert.That(cut.FindComponents<MudTooltip>().Count(t => t.Instance.TooltipContent != null), Is.EqualTo(3),
                "each of the three chips carries a tooltip with the target's own words");
        }
    }

    /// <summary>
    /// Words appear only where something is wrong or still owed. A success is its chip and nothing more; the
    /// "Password set on X." sentence is gone.
    /// </summary>
    [Test]
    public void Panel_DetailLines_AppearOnlyForSystemsThatAreNotAPlainSuccess()
    {
        var cut = RenderPanel(events:
        [
            Change(PendingPasswordChangeOrigin.Propagated,
                Outcome("Corporate AD", ActivityStatus.Complete),
                Outcome("HR SQL", ActivityStatus.FailedWithError, "The password does not meet the requirements of the domain."))
        ]);

        var details = FindAll(cut, TargetDetailMarker);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(details, Has.Count.EqualTo(1));
            Assert.That(details[0].TextContent, Does.Contain("HR SQL:").And.Contain("requirements of the domain"),
                "the target's own words are what tell an administrator where the remedy lives");
            Assert.That(cut.Markup, Does.Not.Contain("Password set on"));
        }
    }

    /// <summary>
    /// A success that landed long after it was asked for says so, because a password that took a quarter of an
    /// hour to arrive is a fact about the delivery worth a line; one that landed within the minute is not.
    /// </summary>
    [Test]
    public void Panel_SuccessThatLagged_SaysWhenItLandedOnlyWhenItLaggedByMoreThanAMinute()
    {
        var requested = Recently.AddHours(-1);
        var lagged = Change(requested, PendingPasswordChangeOrigin.Explicit, Outcome("Corporate AD", ActivityStatus.Complete, occurredAt: requested.AddMinutes(13)));
        var prompt = Change(Recently, PendingPasswordChangeOrigin.Explicit, Outcome("Corporate AD", ActivityStatus.Complete, occurredAt: Recently.AddSeconds(40)));
        var cut = RenderPanel(events: [prompt, lagged]);

        var entries = FindAll(cut, EntryMarker);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0].QuerySelectorAll($"[data-testid='{TargetDetailMarker}']"), Is.Empty);
            Assert.That(entries[1].QuerySelector($"[data-testid='{TargetDetailMarker}']")!.TextContent,
                Does.Contain($"delivered {requested.AddMinutes(13).ToLocalTime().ToFriendlyTime()}, 13 min after the request"));
        }
    }

    /// <summary>
    /// The remedy sits on the line that says what is wrong: Retry for a parked system, Stop trying for one JIM is
    /// still retrying, each raising the panel's callback for that Connected System alone.
    /// </summary>
    [Test]
    public void Panel_DetailLines_OfferRetryForParkedAndStopTryingForRetrying()
    {
        var retried = new List<int>();
        var stopped = new List<int>();
        var change = Change(PendingPasswordChangeOrigin.Explicit,
            Outcome("HR Portal", ActivityStatus.FailedWithError, "Too short."),
            Outcome("Badge System", ActivityStatus.FailedWithError, "Connection refused"));
        var cut = RenderPanel(
            events: [change],
            queued:
            [
                Queued(Math.Abs("HR Portal".GetHashCode() % 1000), "HR Portal", PendingPasswordChangeStatus.Parked, PasswordSetFailureReason.PolicyRejection, "Too short.", attempts: 3, createdAt: change.Created),
                Queued(Math.Abs("Badge System".GetHashCode() % 1000), "Badge System", PendingPasswordChangeStatus.Pending, PasswordSetFailureReason.Transient, "Connection refused", attempts: 2, nextRetryAt: DateTime.UtcNow.AddMinutes(5), createdAt: change.Created)
            ],
            onRetry: retried.Add,
            onStopTrying: stopped.Add);

        var details = FindAll(cut, TargetDetailMarker);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(details, Has.Count.EqualTo(2));
            Assert.That(details[0].TextContent, Does.Contain("HR Portal:").And.Contain("policy rejection, Too short."));
            Assert.That(details[1].TextContent, Does.Contain("Badge System:").And.Contain("Next attempt"));
        }

        Find(cut, HistoryRetryMarker).Click();
        Find(cut, HistoryStopMarker).Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retried, Is.EqualTo(new[] { Math.Abs("HR Portal".GetHashCode() % 1000) }));
            Assert.That(stopped, Is.EqualTo(new[] { Math.Abs("Badge System".GetHashCode() % 1000) }));
        }
    }

    /// <summary>
    /// The first line says who and, for an administrator's explicit choice of accounts, how many.
    /// </summary>
    [Test]
    public void Panel_EntryLine_SaysWhoAndOnHowManyAccounts()
    {
        var cut = RenderPanel(events:
        [
            Change(Recently, PendingPasswordChangeOrigin.Explicit,
                Outcome("Corporate AD", ActivityStatus.Complete), Outcome("HR SQL", ActivityStatus.Complete), Outcome("Badge System", ActivityStatus.Complete)),
            Change(Recently.AddMinutes(-1), PendingPasswordChangeOrigin.Propagated, Outcome("Corporate AD", ActivityStatus.Complete))
        ]);

        var entries = FindAll(cut, EntryMarker);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0].QuerySelector(".jim-password-entry-head")!.TextContent, Does.Contain("by Admin User on 3 accounts"));
            Assert.That(entries[0].QuerySelector(".jim-password-entry-head b")!.TextContent, Is.EqualTo("Admin User"), "the name carries the emphasis");
            Assert.That(entries[1].QuerySelector(".jim-password-entry-head")!.TextContent, Does.Contain("via Self-service portal (API key)").And.Not.Contain("accounts"));
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
            Change(Recently, PendingPasswordChangeOrigin.Explicit, Outcome("Corporate AD", ActivityStatus.Complete)),
            Change(Recently.AddMinutes(-1), PendingPasswordChangeOrigin.Propagated, Outcome("Corporate AD", ActivityStatus.Complete)),
            Change(Recently.AddMinutes(-2), null, Outcome("Corporate AD", ActivityStatus.Complete))
        ]);

        var chips = FindAll(cut, KindMarker);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FindAll(cut, EntryMarker), Has.Count.EqualTo(3));
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
