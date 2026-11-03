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

    // ---- An account's Exchange recipient ---------------------------------------------------------------------------

    [Test]
    public async Task GetRecipientStateAsync_AccountWithAChangeQueued_IsDeclaredAheadOfWhatWasImportedAsync()
    {
        var change = ChangeActivity();
        _activityRepository.Setup(r => r.GetActivityAsync(change.Id)).ReturnsAsync(change);
        var account = SeedAccount(imported: ExchangeRecipientTypes.None, held: ExchangeRecipientTypes.Mailbox);
        await _syncRepository.StageExchangeChangesAsync([Row(change.Id, account.Id)]);

        var state = await _server.GetRecipientStateAsync(account.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.Declared.RecipientType, Is.EqualTo(ExchangeRecipientTypes.Mailbox), "the held change, laid over the import");
            Assert.That(state.Imported.RecipientType, Is.EqualTo(ExchangeRecipientTypes.None), "not a recipient until an import shows one");
            Assert.That(state.QueuedChange?.State, Is.EqualTo(ExchangeChangeState.Queued));
            Assert.That(state.IsInStep, Is.False);
        }
    }

    [Test]
    public async Task GetRecipientStateAsync_AccountAlreadyTheDeclaredRecipient_IsInStepAsync()
    {
        var account = SeedAccount(imported: ExchangeRecipientTypes.Mailbox, held: null);

        var state = await _server.GetRecipientStateAsync(account.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state?.QueuedChange, Is.Null);
            Assert.That(state?.IsInStep, Is.True);
        }
    }

    [Test]
    public async Task GetRecipientStateAsync_AccountWithNothingForExchange_IsNullAsync()
    {
        var account = SeedAccount(imported: null, held: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await _server.GetRecipientStateAsync(account.Id), Is.Null, "nothing to show for an account Exchange has no part in");
            Assert.That(await _server.GetRecipientStateAsync(Guid.NewGuid()), Is.Null);
        }
    }

    private JIM.Models.Staging.ConnectedSystemObject SeedAccount(string? imported, string? held)
    {
        var type = new JIM.Models.Staging.ConnectedSystemObjectType { Id = 10, Name = "user", ConnectedSystemId = 1 };
        var recipientType = new JIM.Models.Staging.ConnectedSystemObjectTypeAttribute
        {
            Id = 3, Name = ExchangeRecipientAttributes.RecipientType, Type = JIM.Models.Core.AttributeDataType.Text,
            AttributePlurality = JIM.Models.Core.AttributePlurality.SingleValued, IsConnectorDefined = true, ConnectedSystemObjectType = type
        };
        var cso = new JIM.Models.Staging.ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 1, Type = type, TypeId = type.Id };
        if (imported != null)
            cso.AttributeValues.Add(new JIM.Models.Staging.ConnectedSystemObjectAttributeValue { AttributeId = 3, StringValue = imported, Attribute = recipientType });
        _syncRepository.SeedConnectedSystemObject(cso);

        if (held != null)
        {
            _syncRepository.SeedPendingExport(new JIM.Models.Transactional.PendingExport
            {
                Id = Guid.NewGuid(), ConnectedSystemId = 1, ConnectedSystemObjectId = cso.Id, ConnectedSystemObject = cso,
                ChangeType = JIM.Models.Transactional.PendingExportChangeType.Update, Status = JIM.Models.Transactional.PendingExportStatus.Exported,
                AttributeValueChanges =
                [
                    new JIM.Models.Transactional.PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(), Attribute = recipientType, AttributeId = 3, StringValue = held,
                        ChangeType = JIM.Models.Transactional.PendingExportAttributeChangeType.Update,
                        Status = JIM.Models.Transactional.PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                        ExportAttemptCount = 1, AwaitsExchange = true
                    }
                ]
            });
        }

        return cso;
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

    private static PendingExchangeChange Row(Guid activityId, Guid? accountId = null) => new()
    {
        ConnectedSystemId = 1,
        ConnectedSystemObjectId = accountId ?? Guid.NewGuid(),
        RecipientType = ExchangeRecipientTypes.Mailbox,
        CreatedAt = Queued,
        ActivityId = activityId
    };
}
