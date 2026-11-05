// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exchange;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// An administrator's retry of queued Exchange changes (#2021, Phase 6b): one top-level Activity for the action, attributed
/// to whoever took it and recorded even when it matched nothing or failed, and one child under each retried change's own
/// Activity, so the change's history says who retried it.
/// </summary>
[TestFixture]
public class ExchangeQueueRetryTests
{
    private static readonly DateTime At = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private JIM.InMemoryData.SyncRepository _syncRepository = null!;
    private List<Activity> _created = null!;
    private ExchangeDeliveryServer _server = null!;

    [SetUp]
    public void SetUp()
    {
        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _syncRepository.SeedConnectedSystem(new ConnectedSystem { Id = 1, Name = "Panoply AD" });
        _created = [];
        _server = NewServer(_syncRepository);
    }

    [Test]
    public async Task RetryAsync_ParkedChange_RecordsTheActionAndAChildUnderTheChangeAsync()
    {
        var change = await SeedParkedAsync();
        var person = new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Jay" };

        var retried = await _server.RetryAsync(new PendingExchangeChangeFilter { ConnectedSystemId = 1 }, person, null);

        var action = _created.Single();
        var child = _syncRepository.Activities.Values.Single(a => a.ParentActivityId == change.ActivityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retried, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingExchangeChanges[change.Id].IsDue(At), Is.True);

            Assert.That(action.TargetType, Is.EqualTo(ActivityTargetType.ExchangeRecipient));
            Assert.That(action.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.RetryExchangeRecipient));
            Assert.That(action.ParentActivityId, Is.Null, "the administrator's action is a top-level Activity");
            Assert.That(action.ConnectedSystemId, Is.EqualTo(1));
            Assert.That(action.InitiatedById, Is.EqualTo(person.Id));
            Assert.That(action.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(action.Message, Is.EqualTo("1 queued Exchange change will be attempted again."));
            Assert.That(ExchangeChangeOutcome.IsChangeActivity(action), Is.False, "an action over changes is not itself a change");

            Assert.That(child.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.RetryExchangeRecipient));
            Assert.That(child.TargetName, Is.EqualTo("Panoply AD"));
            Assert.That(child.TargetContext, Is.EqualTo(ExchangeRecipientTypes.Mailbox));
            Assert.That(child.ConnectedSystemId, Is.EqualTo(1));
            Assert.That(child.MetaverseObjectId, Is.EqualTo(change.MetaverseObjectId));
            Assert.That(child.ConnectedSystemObjectId, Is.EqualTo(change.ConnectedSystemObjectId));
            Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User), "the child names whoever took the action");
            Assert.That(child.InitiatedById, Is.EqualTo(person.Id));
            Assert.That(child.Status, Is.EqualTo(ActivityStatus.Complete));
        }
    }

    [Test]
    public async Task RetryAsync_ByApiKey_AttributesTheActionAndEveryChildToTheKeyAsync()
    {
        var change = await SeedParkedAsync();
        var key = new ApiKey { Id = Guid.NewGuid(), Name = "Automation" };

        await _server.RetryAsync(new PendingExchangeChangeFilter(), null, key);

        var child = _syncRepository.Activities.Values.Single(a => a.ParentActivityId == change.ActivityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_created.Single().InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(child.InitiatedById, Is.EqualTo(key.Id));
            Assert.That(child.InitiatedByName, Is.EqualTo("Automation"));
        }
    }

    [Test]
    public async Task RetryAsync_HeldChange_IsLeftWithItsRetryClockAsync()
    {
        // Its Connected System stopped managing Exchange recipients after the failure: the change waits on the switch, so
        // a retry would only wipe its history while nothing can be attempted.
        var change = await SeedParkedAsync();
        var stored = _syncRepository.PendingExchangeChanges[change.Id];
        stored.Retry();
        stored.Claim("worker-1:aaaa1111", At.AddMinutes(-10));
        stored.RecordFailure(ExchangeChangeFailureKind.Unreachable, "The Exchange server did not answer.", At.AddMinutes(-10));
        _syncRepository.SeedConnectedSystem(new ConnectedSystem { Id = 1, Name = "Panoply AD", ManagesExchangeRecipients = false });

        var retried = await _server.RetryAsync(new PendingExchangeChangeFilter { Ids = [change.Id] }, null, new ApiKey { Id = Guid.NewGuid(), Name = "Automation" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retried, Is.Zero);
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.LastAttemptedAt, Is.EqualTo(At.AddMinutes(-10)), "the held change keeps its attempt history");
            Assert.That(stored.NextRetryAt, Is.Not.Null);
            Assert.That(_created.Single().Message, Is.EqualTo("Nothing was retried: the change is no longer parked or retrying."));
        }
    }

    [Test]
    public async Task RetryAsync_ChangeNoLongerRetryable_StillRecordsTheActionSayingNothingWasRetriedAsync()
    {
        // A named change that moved on before the click: the administrator must be able to see the retry ran and why it
        // did nothing, rather than find no trace of it.
        var change = await SeedParkedAsync();
        _syncRepository.PendingExchangeChanges[change.Id].Retry();

        var retried = await _server.RetryAsync(new PendingExchangeChangeFilter { Ids = [change.Id] }, null, new ApiKey { Id = Guid.NewGuid(), Name = "Automation" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retried, Is.Zero);
            Assert.That(_created.Single().Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(_created.Single().Message, Is.EqualTo("Nothing was retried: the change is no longer parked or retrying."));
            Assert.That(_created.Single().TargetName, Is.EqualTo("One Exchange change"));
            Assert.That(_syncRepository.Activities.Values.Where(a => a.ParentActivityId == change.ActivityId), Is.Empty);
        }
    }

    [Test]
    public void RetryAsync_RetryFails_RecordsTheActionAsFailedAndRethrowsAsync()
    {
        var failing = new Mock<ISyncRepository>();
        failing.Setup(r => r.RetryExchangeChangesAsync(It.IsAny<PendingExchangeChangeFilter>()))
            .ThrowsAsync(new InvalidOperationException("The database went away."));
        var server = NewServer(failing.Object);

        Assert.That(async () => await server.RetryAsync(new PendingExchangeChangeFilter(), null, new ApiKey { Id = Guid.NewGuid(), Name = "Automation" }),
            Throws.InvalidOperationException);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_created.Single().Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(_created.Single().ErrorMessage, Does.Contain("The database went away."));
        }
    }

    [TestCase(null, null, null, "The Exchange queue")]
    [TestCase(ExchangeChangeState.Parked, null, null, "Parked Exchange changes")]
    [TestCase(ExchangeChangeState.Retrying, ExchangeChangeFailureKind.Unreachable, "panoply", "Retrying, Unreachable Exchange changes matching \"panoply\"")]
    public async Task RetryAsync_Filter_NamesWhatTheAdministratorChoseAsync(ExchangeChangeState? state, ExchangeChangeFailureKind? kind, string? search, string expected)
    {
        await _server.RetryAsync(new PendingExchangeChangeFilter { State = state, FailureKind = kind, SearchText = search }, null, new ApiKey { Id = Guid.NewGuid(), Name = "Automation" });

        Assert.That(_created.Single().TargetName, Is.EqualTo(expected));
    }

    private ExchangeDeliveryServer NewServer(ISyncRepository syncRepository) => new(
        syncRepository,
        () => new Mock<IConnectedSystemRepository>().Object,
        () => new Mock<IActivityRepository>().Object,
        _ => throw new InvalidOperationException("retrying builds no Connector"),
        (activity, initiatedBy, initiatedByApiKey) =>
        {
            // Stamped as the Activity server stamps it.
            activity.Status = ActivityStatus.InProgress;
            activity.InitiatedByType = initiatedByApiKey != null ? ActivityInitiatorType.ApiKey : ActivityInitiatorType.User;
            activity.InitiatedById = initiatedByApiKey?.Id ?? initiatedBy?.Id;
            activity.InitiatedByName = initiatedByApiKey?.Name ?? initiatedBy?.CachedDisplayName;
            _created.Add(activity);
            return Task.CompletedTask;
        },
        _ => Task.CompletedTask,
        activity => { activity.Status = ActivityStatus.Complete; return Task.CompletedTask; },
        (activity, error) => { activity.Status = ActivityStatus.FailedWithError; activity.ErrorMessage = error; return Task.CompletedTask; });

    private async Task<PendingExchangeChange> SeedParkedAsync()
    {
        var changeActivity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ExchangeRecipient,
            TargetOperationType = ActivityTargetOperationType.SetExchangeRecipient,
            TargetName = "Panoply AD",
            Status = ActivityStatus.Complete,
            Created = At.AddHours(-1)
        };
        _syncRepository.SeedActivity(changeActivity);
        var change = new PendingExchangeChange
        {
            ConnectedSystemId = 1,
            ConnectedSystemObjectId = Guid.NewGuid(),
            MetaverseObjectId = Guid.NewGuid(),
            RecipientType = ExchangeRecipientTypes.Mailbox,
            CreatedAt = At.AddHours(-1),
            ActivityId = changeActivity.Id
        };
        await _syncRepository.StageExchangeChangesAsync([change]);
        var stored = _syncRepository.PendingExchangeChanges[change.Id];
        stored.Claim("worker-1:aaaa1111", At.AddMinutes(-30));
        stored.RecordFailure(ExchangeChangeFailureKind.InsufficientPermissions, "Access denied.", At.AddMinutes(-30));
        return stored;
    }
}
