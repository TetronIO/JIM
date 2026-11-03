// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exchange;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The Exchange delivery lane (#2021, Phase 5): one lane per Connected System claims the due changes, opens one Exchange
/// session, converges each change in turn, and records what happened against the change and under its Activity.
/// Driven here against a fake Connector, because the LDAP Connector's own Exchange session arrives with Phase 2.
/// </summary>
[TestFixture]
public class ExchangeDeliveryTests
{
    private const int ConnectedSystemId = 7;
    private const int SyncRuleId = 70;
    private const int ObjectGuidAttributeId = 1;
    private const int DistinguishedNameAttributeId = 2;
    private const int RecipientTypeAttributeId = 3;
    private const string ClaimedBy = "worker-test:exch0001";
    private static readonly DateTime AsOf = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private JIM.InMemoryData.SyncRepository _syncRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private ConnectedSystem _connectedSystem = null!;
    private ConnectedSystemObjectType _userType = null!;
    private FakeExchangeConnector _connector = null!;
    private IConnector _resolvedConnector = null!;
    private List<Activity> _activities = null!;
    private ExchangeDeliveryServer _server = null!;

    [SetUp]
    public void SetUp()
    {
        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _connectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Panoply AD", SettingValues = [] };
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemForExchangeDeliveryAsync(ConnectedSystemId)).ReturnsAsync(_connectedSystem);
        _userType = new ConnectedSystemObjectType { Id = 10, Name = "user", ConnectedSystemId = ConnectedSystemId };
        _connector = new FakeExchangeConnector();
        _resolvedConnector = _connector;
        _activities = [];
        _server = NewServer();
    }

    private ExchangeDeliveryServer NewServer(int claimBatchSize = ExchangeDeliveryServer.ClaimBatchSize, int maximumChangesPerPass = ExchangeDeliveryServer.MaximumChangesPerPass) => new(
        _syncRepository,
        () => _connectedSystemRepository.Object,
        () => new Mock<IActivityRepository>().Object,
        _ => _resolvedConnector,
        activity =>
        {
            activity.InitiatedByType = ActivityInitiatorType.System;
            activity.InitiatedByName = "System";
            _activities.Add(activity);
            return Task.CompletedTask;
        },
        activity =>
        {
            activity.Status = ActivityStatus.Complete;
            return Task.CompletedTask;
        },
        (activity, errorMessage) =>
        {
            activity.Status = ActivityStatus.FailedWithError;
            activity.ErrorMessage = errorMessage;
            return Task.CompletedTask;
        },
        claimBatchSize,
        maximumChangesPerPass);

    // ---- Delivered ------------------------------------------------------------------------------------------------

    [Test]
    public async Task DeliverDueAsync_DueChange_ConvergesTheAccountToItsDeclaredStateAndRemovesTheChangeAsync()
    {
        var change = await QueueMailboxAsync(out var cso, out var objectGuid);

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        var converged = _connector.Converged.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(converged.Target.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(converged.Target.ObjectGuid, Is.EqualTo(objectGuid));
            Assert.That(converged.Target.DistinguishedName, Is.EqualTo("CN=Ada,OU=Users,DC=panoply,DC=local"));
            Assert.That(converged.Target.ObjectType, Is.EqualTo("user"));
            Assert.That(converged.Target.DomainController, Is.EqualTo("dc1.panoply.local"));
            Assert.That(converged.Declared.RecipientType, Is.EqualTo("Mailbox"));
            Assert.That(_syncRepository.PendingExchangeChanges, Is.Empty, "a delivered change is removed; its Activity is the history");
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(_connector.Opened, Is.EqualTo(1));
            Assert.That(_connector.Closed, Is.EqualTo(1));
        }

        var outcome = _activities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.ParentActivityId, Is.EqualTo(change.ActivityId), "the outcome sits under the change's own Activity");
            Assert.That(outcome.TargetType, Is.EqualTo(ActivityTargetType.ExchangeRecipient));
            Assert.That(outcome.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetExchangeRecipient));
            Assert.That(outcome.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(outcome.Status, Is.EqualTo(ActivityStatus.Complete));
        }
    }

    [Test]
    public async Task DeliverDueAsync_SeveralChanges_ConvergesThemAllOverOneSessionAsync()
    {
        for (var i = 0; i < 3; i++)
            await QueueMailboxAsync(out _, out _);

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(3));
            Assert.That(_connector.Opened, Is.EqualTo(1), "one session serves the whole batch");
        }
    }

    // ---- Failed ---------------------------------------------------------------------------------------------------

    [TestCase(ExchangeChangeFailureKind.ObjectNotFound)]
    [TestCase(ExchangeChangeFailureKind.Unreachable)]
    [TestCase(ExchangeChangeFailureKind.AuthenticationFailed)]
    public async Task DeliverDueAsync_FailureThatMayClear_KeepsTheChangeRetryingAndSaysWhyAsync(ExchangeChangeFailureKind kind)
    {
        var change = await QueueMailboxAsync(out _, out _);
        _connector.Answer = _ => ExchangeConvergeResult.Failure(kind, "The operation couldn't be performed because object couldn't be found.");

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        var stored = _syncRepository.PendingExchangeChanges[change.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.NextRetryAt, Is.EqualTo(AsOf.AddMinutes(2)));
            Assert.That(stored.FailureKind, Is.EqualTo(kind));
            Assert.That(stored.ClaimedBy, Is.Null);
            Assert.That(result.RetryingCount, Is.EqualTo(1));
            Assert.That(_activities.Single().Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(_activities.Single().ErrorMessage, Does.Contain("couldn't be found").And.Contain("try again"));
        }
    }

    [TestCase(ExchangeChangeFailureKind.Refused)]
    [TestCase(ExchangeChangeFailureKind.InsufficientPermissions)]
    public async Task DeliverDueAsync_FailureNoRetryCanCure_ParksTheChangeAndSaysSoAsync(ExchangeChangeFailureKind kind)
    {
        var change = await QueueMailboxAsync(out _, out _);
        _connector.Answer = _ => ExchangeConvergeResult.Failure(kind, "Access denied.");

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_syncRepository.PendingExchangeChanges[change.Id].Status, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(result.ParkedCount, Is.EqualTo(1));
            Assert.That(_activities.Single().ErrorMessage, Does.Contain("stopped trying"));
        }
    }

    [Test]
    public async Task DeliverDueAsync_TheSameFailureAgain_UpdatesTheChangeWithoutAnotherActivityAsync()
    {
        // An outage retried for a day would otherwise write one Activity per attempt (decided 2026-11-03): only a
        // failure that says something new is recorded; the row always carries the latest attempt.
        var change = await QueueMailboxAsync(out _, out _);
        _connector.Answer = _ => ExchangeConvergeResult.Failure(ExchangeChangeFailureKind.Unreachable, "Exchange is unreachable.");
        await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf.AddMinutes(2), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_syncRepository.PendingExchangeChanges[change.Id].AttemptCount, Is.EqualTo(2));
            Assert.That(_activities, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task DeliverDueAsync_ADifferentFailure_IsRecordedAsANewActivityAsync()
    {
        await QueueMailboxAsync(out _, out _);
        _connector.Answer = _ => ExchangeConvergeResult.Failure(ExchangeChangeFailureKind.Unreachable, "Exchange is unreachable.");
        await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);
        _connector.Answer = _ => ExchangeConvergeResult.Failure(ExchangeChangeFailureKind.ObjectNotFound, "The object couldn't be found.");

        await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf.AddMinutes(2), CancellationToken.None);

        Assert.That(_activities, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task DeliverDueAsync_TheSameFailureParksTheChange_IsRecordedBecauseTheOutcomeChangedAsync()
    {
        var change = await QueueMailboxAsync(out _, out _);
        _connector.Answer = _ => ExchangeConvergeResult.Failure(ExchangeChangeFailureKind.Unreachable, "Exchange is unreachable.");
        await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf.AddHours(24), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_syncRepository.PendingExchangeChanges[change.Id].Status, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(_activities, Has.Count.EqualTo(2));
            Assert.That(_activities[1].ErrorMessage, Does.Contain("stopped trying"));
        }
    }

    [Test]
    public async Task DeliverDueAsync_ConvergeThrowsUnexpectedly_RecordsItAgainstThatChangeAndCarriesOnAsync()
    {
        var failing = await QueueMailboxAsync(out var failingCso, out _);
        await QueueMailboxAsync(out _, out _);
        _connector.Answer = target => target.ConnectedSystemObjectId == failingCso.Id
            ? throw new InvalidOperationException("The runspace is not open.")
            : ExchangeConvergeResult.Success(changedAnything: true);

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(result.RetryingCount, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingExchangeChanges[failing.Id].FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Unreachable));
        }
    }

    // ---- Withdrawn ------------------------------------------------------------------------------------------------

    [Test]
    public async Task DeliverDueAsync_AccountRemovedFirst_WithdrawsTheChangeWithoutAskingExchangeAsync()
    {
        var change = await QueueMailboxAsync(out _, out _);
        _syncRepository.PendingExchangeChanges[change.Id].ConnectedSystemObjectId = null; // the database's SET NULL

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        AssertWithdrawn(result, change, "account");
    }

    [Test]
    public async Task DeliverDueAsync_DeclaringSynchronisationRuleRemovedFirst_WithdrawsTheChangeAsync()
    {
        var change = await QueueMailboxAsync(out _, out _);
        _syncRepository.PendingExchangeChanges[change.Id].SyncRuleId = null; // the database's SET NULL

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        AssertWithdrawn(result, change, "Synchronisation Rule");
    }

    [Test]
    public async Task DeliverDueAsync_DeclaredStateMovedOnBeforeExchangeActed_WithdrawsTheStaleChangeAsync()
    {
        // The declaration went back to "None" before the lane got here, and export evaluation withdrew the change it held
        // for Exchange; the queued "Mailbox" is a state nobody declares any more.
        var change = await QueueMailboxAsync(out var cso, out _);
        foreach (var pe in _syncRepository.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == cso.Id))
            pe.AttributeValueChanges.Clear();

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        AssertWithdrawn(result, change, "changed");
    }

    // ---- Could not deliver at all ---------------------------------------------------------------------------------

    [Test]
    public async Task DeliverDueAsync_ConnectorCannotManageExchangeRecipients_GivesTheChangesBackUnattemptedAsync()
    {
        var change = await QueueMailboxAsync(out _, out _);
        _resolvedConnector = new Mock<IConnector>().Object;

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        var stored = _syncRepository.PendingExchangeChanges[change.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CouldNotDeliver, Is.True);
            Assert.That(result.ReleasedCount, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.AttemptCount, Is.Zero, "nothing was tried");
            Assert.That(_activities, Is.Empty);
        }
    }

    [Test]
    public async Task DeliverDueAsync_SessionCannotBeOpened_RecordsWhyAgainstEveryChangeClaimedAsync()
    {
        // PRD Scenario 5: Exchange is down. The accounts stay created, and each queued change shows as Retrying with
        // Exchange's reason, so it is visible against the person rather than silently waiting.
        var first = await QueueMailboxAsync(out _, out _);
        var second = await QueueMailboxAsync(out _, out _);
        _connector.OpenFailure = new ExchangeSessionException(ExchangeChangeFailureKind.Unreachable, "Connecting to remote server ex1.panoply.local failed.");

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CouldNotDeliver, Is.True);
            Assert.That(result.RetryingCount, Is.EqualTo(2));
            foreach (var id in new[] { first.Id, second.Id })
            {
                var stored = _syncRepository.PendingExchangeChanges[id];
                Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
                Assert.That(stored.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Unreachable));
                Assert.That(stored.FailureMessage, Does.Contain("ex1.panoply.local"));
                Assert.That(stored.NextRetryAt, Is.EqualTo(AsOf.AddMinutes(2)));
            }

            Assert.That(_activities, Has.Count.EqualTo(2));
            Assert.That(_connector.Converged, Is.Empty);
        }
    }

    [Test]
    public async Task DeliverDueAsync_CancelledPartWay_RecordsWhatWasDoneAndReleasesTheRestAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var first = await QueueMailboxAsync(out _, out _);
        var second = await QueueMailboxAsync(out _, out _);
        _connector.Answer = _ =>
        {
            cancellation.Cancel();
            return ExchangeConvergeResult.Success(changedAnything: true);
        };

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
            Assert.That(result.ReleasedCount, Is.EqualTo(1));
            Assert.That(_syncRepository.PendingExchangeChanges.ContainsKey(first.Id), Is.False);
            Assert.That(_syncRepository.PendingExchangeChanges[second.Id].Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(_syncRepository.PendingExchangeChanges[second.Id].ClaimedBy, Is.Null);
            Assert.That(_connector.Closed, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task DeliverDueAsync_MoreThanOnePassAllows_StopsAtTheBoundAndLeavesTheRestDueAsync()
    {
        _server = NewServer(claimBatchSize: 2, maximumChangesPerPass: 3);
        for (var i = 0; i < 5; i++)
            await QueueMailboxAsync(out _, out _);

        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(3));
            Assert.That(_syncRepository.PendingExchangeChanges.Values.ToList(), Has.Count.EqualTo(2).And.All.Matches<PendingExchangeChange>(c => c.Status == PendingExchangeChangeStatus.Pending));
        }
    }

    [Test]
    public async Task DeliverDueAsync_NothingDue_OpensNoSessionAsync()
    {
        var result = await _server.DeliverDueAsync(ConnectedSystemId, ClaimedBy, AsOf, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasSomethingToReport, Is.False);
            Assert.That(_connector.Opened, Is.Zero);
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private void AssertWithdrawn(ExchangeDeliveryRunResult result, PendingExchangeChange change, string reason)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.WithdrawnCount, Is.EqualTo(1));
            Assert.That(_connector.Converged, Is.Empty, "Exchange is not asked to make a change nobody is owed");
            Assert.That(_syncRepository.PendingExchangeChanges.ContainsKey(change.Id), Is.False);
            var outcome = _activities.Single();
            Assert.That(outcome.ParentActivityId, Is.EqualTo(change.ActivityId));
            Assert.That(outcome.Status, Is.EqualTo(ActivityStatus.Complete), "a withdrawal is not a failure");
            Assert.That(outcome.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.WithdrawExchangeRecipient),
                "a withdrawal is told apart from a delivery by what it is, not by its words");
            Assert.That(outcome.Message, Does.Contain(reason));
        }
    }

    /// <summary>
    /// An account an export has just provisioned with "Exchange Recipient Type" = Mailbox: the object as imported (no
    /// recipient yet), its Pending Export holding the change for Exchange, and the change on the queue, as Phase 4's
    /// staging leaves them.
    /// </summary>
    private Task<PendingExchangeChange> QueueMailboxAsync(out ConnectedSystemObject cso, out Guid objectGuid)
    {
        objectGuid = Guid.NewGuid();
        var recipientType = new ConnectedSystemObjectTypeAttribute
        {
            Id = RecipientTypeAttributeId, Name = ExchangeRecipientAttributes.RecipientType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, IsConnectorDefined = true, ConnectedSystemObjectType = _userType
        };
        cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            Type = _userType,
            TypeId = _userType.Id,
            ExternalIdAttributeId = ObjectGuidAttributeId,
            SecondaryExternalIdAttributeId = DistinguishedNameAttributeId
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = ObjectGuidAttributeId, GuidValue = objectGuid, Attribute = new ConnectedSystemObjectTypeAttribute { Id = ObjectGuidAttributeId, Name = "objectGUID" } });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = DistinguishedNameAttributeId, StringValue = "CN=Ada,OU=Users,DC=panoply,DC=local", Attribute = new ConnectedSystemObjectTypeAttribute { Id = DistinguishedNameAttributeId, Name = "distinguishedName" } });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = RecipientTypeAttributeId, StringValue = "None", Attribute = recipientType });
        _syncRepository.SeedConnectedSystemObject(cso);

        _syncRepository.SeedPendingExport(new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectId = cso.Id,
            ConnectedSystemObject = cso,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Exported,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(), Attribute = recipientType, AttributeId = RecipientTypeAttributeId, StringValue = "Mailbox",
                    ChangeType = PendingExportAttributeChangeType.Add, Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                    ExportAttemptCount = 1, AwaitsExchange = true
                }
            ]
        });

        var change = new PendingExchangeChange
        {
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectId = cso.Id,
            SyncRuleId = SyncRuleId,
            RecipientType = "Mailbox",
            DomainController = "dc1.panoply.local",
            CreatedAt = AsOf.AddMinutes(-1).AddTicks(_syncRepository.PendingExchangeChanges.Count),
            ActivityId = Guid.NewGuid()
        };
        return StageAsync(change);
    }

    private async Task<PendingExchangeChange> StageAsync(PendingExchangeChange change)
    {
        await _syncRepository.StageExchangeChangesAsync([change]);
        return change;
    }

    private sealed class FakeExchangeConnector : IConnector, IConnectorExchangeRecipients
    {
        public string Name => "Fake Exchange Connector";
        public string? Description => null;
        public string? Url => null;

        public int Opened { get; private set; }
        public int Closed { get; private set; }
        public ExchangeSessionException? OpenFailure { get; set; }
        public Func<ExchangeRecipientTarget, ExchangeConvergeResult> Answer { get; set; } = _ => ExchangeConvergeResult.Success(changedAnything: true);
        public List<(ExchangeRecipientTarget Target, ExchangeDeclaredState Declared)> Converged { get; } = [];

        public Task OpenExchangeSessionAsync(IList<ConnectedSystemSettingValue> settings, CancellationToken cancellationToken)
        {
            Opened++;
            return OpenFailure == null ? Task.CompletedTask : throw OpenFailure;
        }

        public Task<ExchangeConvergeResult> ConvergeAsync(ExchangeRecipientTarget target, ExchangeDeclaredState declared, CancellationToken cancellationToken)
        {
            Converged.Add((target, declared));
            return Task.FromResult(Answer(target));
        }

        public ExchangeSessionStatistics GetSessionStatistics() => new() { KerberosLogins = 1, Connections = 1, Commands = Converged.Count * 2 };

        public void CloseExchangeSession() => Closed++;
    }
}
