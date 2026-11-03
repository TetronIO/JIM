// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Exchange;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Reading where queued Exchange changes stand (#2021, Phase 6): one read of the queue and one of the Activities
/// under the changes, however many changes are asked about, merged per change.
/// </summary>
[TestFixture]
public class ExchangeChangeOutcomeReadTests
{
    private static readonly DateTime Queued = new(2026, 11, 3, 10, 2, 14, DateTimeKind.Utc);

    private JIM.InMemoryData.SyncRepository _syncRepository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private ExchangeDeliveryServer _server = null!;

    [SetUp]
    public void SetUp()
    {
        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _activityRepository = new Mock<IActivityRepository>();
        _activityRepository.Setup(r => r.GetExchangeChangeEventsAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([]);
        _server = new ExchangeDeliveryServer(
            _syncRepository,
            () => new Mock<IConnectedSystemRepository>().Object,
            () => _activityRepository.Object,
            _ => throw new InvalidOperationException("reading outcomes builds no Connector"),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
    }

    [Test]
    public async Task GetChangeOutcomesAsync_OneOnTheQueueAndOneDelivered_DescribesEachFromWhatItHasAsync()
    {
        var queued = ChangeActivity();
        var delivered = ChangeActivity();
        await _syncRepository.StageExchangeChangesAsync([Row(queued.Id)]);
        _activityRepository.Setup(r => r.GetExchangeChangeEventsAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync(
        [
            new ExchangeChangeEvent
            {
                ChangeActivityId = delivered.Id, ActivityId = Guid.NewGuid(), Operation = ActivityTargetOperationType.SetExchangeRecipient,
                Status = ActivityStatus.Complete, Message = "Exchange recipient set: Mailbox.", OccurredAt = Queued.AddMinutes(1)
            }
        ]);

        var outcomes = await _server.GetChangeOutcomesAsync([queued, delivered]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes, Has.Count.EqualTo(2));
            Assert.That(outcomes[queued.Id].State, Is.EqualTo(ExchangeChangeState.Queued));
            Assert.That(outcomes[delivered.Id].State, Is.EqualTo(ExchangeChangeState.Delivered));
        }
        _activityRepository.Verify(r => r.GetExchangeChangeEventsAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Once,
            "one read for every change asked about, not one per change");
    }

    [Test]
    public async Task GetChangeOutcomesAsync_ActivitiesThatAreNotExchangeChanges_AreLeftOutAsync()
    {
        var password = ChangeActivity(ActivityTargetType.PasswordSynchronisation);
        var replacedNotice = ChangeActivity();
        replacedNotice.ParentActivityId = Guid.NewGuid();

        var outcomes = await _server.GetChangeOutcomesAsync([password, replacedNotice]);

        Assert.That(outcomes, Is.Empty);
        _activityRepository.Verify(r => r.GetExchangeChangeEventsAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Never,
            "nothing to read when nothing asked about is an Exchange change");
    }

    [Test]
    public async Task GetChangeOutcomeAsync_AnExchangeChange_DescribesItAsync()
    {
        var change = ChangeActivity();
        _activityRepository.Setup(r => r.GetActivityAsync(change.Id)).ReturnsAsync(change);
        await _syncRepository.StageExchangeChangesAsync([Row(change.Id)]);

        var outcome = await _server.GetChangeOutcomeAsync(change.Id);

        Assert.That(outcome?.State, Is.EqualTo(ExchangeChangeState.Queued));
    }

    [Test]
    public async Task GetChangeOutcomeAsync_NoSuchActivityOrNotAChange_ReturnsNullAsync()
    {
        var notice = ChangeActivity();
        notice.ParentActivityId = Guid.NewGuid();
        _activityRepository.Setup(r => r.GetActivityAsync(notice.Id)).ReturnsAsync(notice);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await _server.GetChangeOutcomeAsync(Guid.NewGuid()), Is.Null);
            Assert.That(await _server.GetChangeOutcomeAsync(notice.Id), Is.Null, "an Activity recorded under a change is not itself a change");
        }
    }

    private static Activity ChangeActivity(ActivityTargetType targetType = ActivityTargetType.ExchangeRecipient) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = targetType,
        TargetOperationType = ActivityTargetOperationType.SetExchangeRecipient,
        TargetContext = ExchangeRecipientTypes.Mailbox,
        TargetName = "PANOPLY",
        Status = ActivityStatus.Complete,
        Created = Queued
    };

    private static PendingExchangeChange Row(Guid activityId) => new()
    {
        ConnectedSystemId = 1,
        ConnectedSystemObjectId = Guid.NewGuid(),
        RecipientType = ExchangeRecipientTypes.Mailbox,
        CreatedAt = Queued,
        ActivityId = activityId
    };
}
