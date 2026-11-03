// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// An execution item's follow-ups (#2668): the Activities recording work the item set in motion that finishes later,
/// such as the Initial Password of an account an export provisioned, each with where that work stands now.
/// </summary>
[TestFixture]
public class ExecutionItemFollowUpTests
{
    private const int DirectoryId = 7;
    private static readonly DateTime Created = new(2026, 11, 1, 10, 2, 14, DateTimeKind.Utc);

    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private JIM.InMemoryData.SyncRepository _syncRepository = null!;
    private JimApplication _application = null!;
    private List<Activity> _followUps = null!;

    [SetUp]
    public void SetUp()
    {
        _followUps = [];
        _activityRepository = new Mock<IActivityRepository>();
        _activityRepository.Setup(r => r.GetFollowUpActivitiesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => _followUps
                .Where(a => a.ParentRunProfileExecutionItemId is { } item && ids.Contains(item)).ToList());
        _activityRepository.Setup(r => r.GetActivityAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _followUps.SingleOrDefault(a => a.Id == id));
        _activityRepository.Setup(r => r.GetPasswordSynchronisationOutcomesAsync(It.IsAny<Guid>())).ReturnsAsync([]);
        _activityRepository.Setup(r => r.GetExchangeChangeEventsAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([]);

        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _connectedSystemRepository.Setup(r => r.GetPasswordSynchronisationTargetsAsync())
            .ReturnsAsync([new PasswordSynchronisationTarget { ConnectedSystemId = DirectoryId, ConnectedSystemName = "PANOPLY", Enabled = true }]);

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _application = new JimApplication(repository.Object, _syncRepository);
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    [Test]
    public async Task GetExecutionItemFollowUpsAsync_ItemWithAQueuedInitialPassword_ReturnsItWithWhereItStandsAsync()
    {
        var itemId = Guid.NewGuid();
        var activity = InitialPasswordActivity(itemId);
        await _syncRepository.QueuePasswordChangesAsync([new PendingPasswordChange
        {
            MetaverseObjectId = activity.MetaverseObjectId!.Value,
            ConnectedSystemId = DirectoryId,
            Origin = PendingPasswordChangeOrigin.Provisioned,
            AttemptCount = 2,
            LastAttemptedAt = Created.AddMinutes(2),
            NextRetryAt = Created.AddMinutes(6),
            CreatedAt = Created,
            ExpiresAt = Created.AddDays(7),
            ActivityId = activity.Id
        }]);

        var followUps = await _application.Activities.GetExecutionItemFollowUpsAsync(itemId);

        var followUp = followUps.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(followUp.ActivityId, Is.EqualTo(activity.Id));
            Assert.That(followUp.RunProfileExecutionItemId, Is.EqualTo(itemId));
            Assert.That(followUp.TargetType, Is.EqualTo(ActivityTargetType.PasswordSynchronisation));
            Assert.That(followUp.TargetName, Is.EqualTo("PANOPLY"));
            Assert.That(followUp.PasswordOutcomes, Is.Not.Null);
            Assert.That(followUp.PasswordOutcomes!.Targets.Single().State, Is.EqualTo(PasswordChangeTargetState.Retrying));
            Assert.That(followUp.PasswordOutcomes!.Targets.Single().AttemptCount, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task GetExecutionItemFollowUpsAsync_ItemWithNoFollowUps_ReturnsNothingAsync()
    {
        InitialPasswordActivity(Guid.NewGuid());

        var followUps = await _application.Activities.GetExecutionItemFollowUpsAsync(Guid.NewGuid());

        Assert.That(followUps, Is.Empty);
    }

    [Test]
    public async Task GetExecutionItemFollowUpsAsync_SeveralItems_GroupsEachItemsFollowUpsUnderItAsync()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var firstActivity = InitialPasswordActivity(first);
        var secondActivity = InitialPasswordActivity(second);

        var byItem = await _application.Activities.GetExecutionItemFollowUpsAsync([first, second, Guid.NewGuid()]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byItem.Keys, Is.EquivalentTo(new[] { first, second }), "an item with none is left out");
            Assert.That(byItem[first].Single().ActivityId, Is.EqualTo(firstActivity.Id));
            Assert.That(byItem[second].Single().ActivityId, Is.EqualTo(secondActivity.Id));
        }
    }

    /// <summary>
    /// An Exchange change's follow-up carries where the change stands (#2021, Phase 6), read from the queue: a change
    /// waiting out a backoff is Retrying, not the Complete its Activity was recorded as when it was queued.
    /// </summary>
    [Test]
    public async Task GetExecutionItemFollowUpsAsync_ItemWithAnExchangeChangeRetrying_ReturnsItWithWhereItStandsAsync()
    {
        var itemId = Guid.NewGuid();
        var activity = ExchangeChangeActivity(itemId);
        await _syncRepository.StageExchangeChangesAsync([new PendingExchangeChange
        {
            ConnectedSystemId = DirectoryId, ConnectedSystemObjectId = Guid.NewGuid(), RecipientType = "Mailbox",
            CreatedAt = Created, ActivityId = activity.Id
        }]);
        var row = _syncRepository.PendingExchangeChanges.Values.Single();
        row.RecordFailure(ExchangeChangeFailureKind.Unreachable, "Exchange did not answer within 60 seconds.", Created.AddMinutes(1));

        var followUp = (await _application.Activities.GetExecutionItemFollowUpsAsync(itemId)).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(followUp.Status, Is.EqualTo(ActivityStatus.Complete), "the Activity itself was complete the moment it was queued");
            Assert.That(followUp.ExchangeOutcome, Is.Not.Null);
            Assert.That(followUp.ExchangeOutcome!.State, Is.EqualTo(ExchangeChangeState.Retrying));
            Assert.That(followUp.ExchangeOutcome!.RecipientType, Is.EqualTo("Mailbox"));
            Assert.That(followUp.PasswordOutcomes, Is.Null);
        }
    }

    /// <summary>
    /// Many items' Exchange follow-ups are read together, as the causality timeline asks for them: one read of the
    /// Activities under the changes, not one per change.
    /// </summary>
    [Test]
    public async Task GetExecutionItemFollowUpsAsync_SeveralItemsWithExchangeChanges_ReadsTheirOutcomesTogetherAsync()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        ExchangeChangeActivity(first);
        ExchangeChangeActivity(second);

        var byItem = await _application.Activities.GetExecutionItemFollowUpsAsync([first, second]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byItem[first].Single().ExchangeOutcome, Is.Not.Null);
            Assert.That(byItem[second].Single().ExchangeOutcome, Is.Not.Null);
        }
        _activityRepository.Verify(r => r.GetExchangeChangeEventsAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Once);
    }

    private Activity ExchangeChangeActivity(Guid itemId)
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            ParentRunProfileExecutionItemId = itemId,
            TargetType = ActivityTargetType.ExchangeRecipient,
            TargetOperationType = ActivityTargetOperationType.SetExchangeRecipient,
            TargetContext = "Mailbox",
            TargetName = "PANOPLY",
            ConnectedSystemId = DirectoryId,
            Status = ActivityStatus.Complete,
            Message = "Mailbox queued for Exchange.",
            Created = Created
        };
        _followUps.Add(activity);
        return activity;
    }

    private Activity InitialPasswordActivity(Guid itemId)
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            ParentRunProfileExecutionItemId = itemId,
            TargetType = ActivityTargetType.PasswordSynchronisation,
            TargetOperationType = ActivityTargetOperationType.SetPassword,
            TargetContext = nameof(PendingPasswordChangeOrigin.Provisioned),
            TargetName = "PANOPLY",
            ConnectedSystemId = DirectoryId,
            MetaverseObjectId = Guid.NewGuid(),
            Status = ActivityStatus.Complete,
            Message = "Initial password queued for delivery to PANOPLY.",
            Created = Created
        };
        _followUps.Add(activity);
        return activity;
    }
}
