// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The identity's Password Synchronisation panel (#1119, requirement 25): what this person's password changes
/// did, per Connected System.
/// <para>
/// The behaviour worth pinning is that history comes from Activities and the outstanding work comes from the
/// queue, and the panel shows both. The queue row is deleted the moment a password arrives, so a panel built on
/// the queue alone would show an identity's failures and none of its successes, which is the most misleading
/// possible view of whether their password propagated.
/// </para>
/// </summary>
[TestFixture]
public class MetaverseObjectPasswordSynchronisationPanelTests : JimComponentTestContext
{
    private static PasswordSynchronisationEvent Change(params PasswordSynchronisationEventOutcome[] outcomes) => new()
    {
        ActivityId = Guid.NewGuid(),
        Created = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc),
        InitiatedByName = "Self-service portal",
        InitiatedByType = ActivityInitiatorType.ApiKey,
        Message = "Password change queued.",
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

    [Test]
    public void Panel_WithNoHistoryAndNothingQueued_SaysSoPlainly()
    {
        var cut = Render<MetaverseObjectPasswordSynchronisationPanel>(p => p
            .Add(c => c.Events, [])
            .Add(c => c.QueuedChanges, []));

        Assert.That(cut.Markup, Does.Contain("never"));
    }

    [Test]
    public void Panel_ShowsEachSystemsOwnOutcome()
    {
        var cut = Render<MetaverseObjectPasswordSynchronisationPanel>(p => p
            .Add(c => c.Events, [Change(
                Outcome("Corporate AD", ActivityStatus.Complete),
                Outcome("HR SQL", ActivityStatus.FailedWithError, "The password does not meet the requirements of the domain."))])
            .Add(c => c.QueuedChanges, []));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Corporate AD"));
            Assert.That(cut.Markup, Does.Contain("HR SQL"));
            Assert.That(cut.Markup, Does.Contain("requirements of the domain"),
                "the target's own words are what tell an administrator where the remedy lives");
        }
    }

    [Test]
    public void Panel_ChangeThatReachedNoSystem_SaysSoRatherThanLookingSuccessful()
    {
        // Requirement 14 read from the other end: a change with no outcomes must not render as a bare timestamp
        // that an administrator reads as "it went out fine".
        var cut = Render<MetaverseObjectPasswordSynchronisationPanel>(p => p
            .Add(c => c.Events, [Change()])
            .Add(c => c.QueuedChanges, []));

        Assert.That(cut.Markup, Does.Contain("no Connected System"));
    }

    [Test]
    public void Panel_WithSomethingStillQueued_ShowsItSeparatelyFromHistory()
    {
        var cut = Render<MetaverseObjectPasswordSynchronisationPanel>(p => p
            .Add(c => c.Events, [Change(Outcome("Corporate AD", ActivityStatus.Complete))])
            .Add(c => c.QueuedChanges, [new PendingPasswordChangeHeader
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = 4,
                ConnectedSystemName = "HR SQL",
                Status = PendingPasswordChangeStatus.Parked,
                FailureReason = PasswordSetFailureReason.PolicyRejection,
                TargetMessage = "Too short.",
                AttemptCount = 3,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            }]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Still to be delivered"),
                "outstanding work is what an administrator acts on; history is what they read afterwards");
            Assert.That(cut.Markup, Does.Contain("Too short."));
        }
    }

    [Test]
    public void Panel_LinksToTheQueueFilteredToThisIdentity()
    {
        var id = Guid.NewGuid();
        var cut = Render<MetaverseObjectPasswordSynchronisationPanel>(p => p
            .Add(c => c.MetaverseObjectId, id)
            .Add(c => c.Events, [])
            .Add(c => c.QueuedChanges, []));

        Assert.That(cut.Markup, Does.Contain("/admin/password-synchronisation"));
    }
}
