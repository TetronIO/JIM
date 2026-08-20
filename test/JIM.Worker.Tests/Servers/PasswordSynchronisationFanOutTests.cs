// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Worker.Tests.Services;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Fan-out: turning one password change into one queued change per Connected System the identity has an account
/// in (#1119, requirement 6).
/// <para>
/// The behaviour worth pinning is what fan-out refuses to do. It never delivers system to system, it never
/// queues for a system nobody enabled, and it never silently reaches nothing: a change that found no target is
/// still recorded, because an administrator who believes a password propagated when it did not is exactly the
/// situation this feature exists to prevent.
/// </para>
/// </summary>
[TestFixture]
public class PasswordSynchronisationFanOutTests
{
    private const int UserObjectTypeId = 200;
    private const int GroupObjectTypeId = 201;

    private JIM.InMemoryData.SyncRepository _syncRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private TestCredentialProtection _protection = null!;
    private List<Activity> _createdActivities = null!;
    private List<int?> _deliveryRequests = null!;
    private PasswordSynchronisationServer _server = null!;

    [SetUp]
    public void SetUp()
    {
        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _protection = new TestCredentialProtection();
        _createdActivities = [];
        _deliveryRequests = [];

        _connectedSystemRepository
            .Setup(r => r.GetEnabledPasswordSynchronisationTargetsAsync())
            .ReturnsAsync([]);
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync([]);

        _server = new PasswordSynchronisationServer(
            _syncRepository,
            () => _connectedSystemRepository.Object,
            () => _protection,
            // These fixtures never reach a Connector: they exercise queueing and one-change delivery with a
            // Connector handed in directly. Resolving one here would be answering a question they do not ask.
            _ => throw new NotSupportedException("This fixture does not resolve Connectors."),
            (activity, _) =>
            {
                _createdActivities.Add(activity);
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            connectedSystemId =>
            {
                _deliveryRequests.Add(connectedSystemId);
                return Task.CompletedTask;
            });
    }

    private void ArrangeTargets(params PasswordSynchronisationTarget[] targets) =>
        _connectedSystemRepository
            .Setup(r => r.GetEnabledPasswordSynchronisationTargetsAsync())
            .ReturnsAsync(targets.ToList());

    private void ArrangeAccounts(Guid metaverseObjectId, params ConnectedSystemObject[] accounts) =>
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObjectId))
            .ReturnsAsync(accounts.ToList());

    private static PasswordSynchronisationTarget Target(int connectedSystemId, string name) => new()
    {
        ConnectedSystemId = connectedSystemId,
        ConnectedSystemName = name,
        TargetObjectTypeId = UserObjectTypeId,
        TimeToLive = TimeSpan.FromDays(7)
    };

    private static ConnectedSystemObject Account(int connectedSystemId, int typeId) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = connectedSystemId,
        TypeId = typeId
    };

    [Test]
    public async Task QueuePasswordChange_AsksForDeliveryAsync()
    {
        // Queueing without asking for delivery would leave a password change sitting until the worker's idle
        // housekeeping happened to notice it, which is up to a minute of somebody's old password still working.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"), Target(4, "HR Portal"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId), Account(4, UserObjectTypeId));

        await _server.QueuePasswordChangeAsync(
            metaverseObjectId, "Ada Lovelace", "a-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deliveryRequests, Has.Exactly(1).Items, "One request covers every system fanned out to.");
            Assert.That(_deliveryRequests[0], Is.Null, "Fan-out reaches several systems, so the request names none of them.");
        }
    }

    [Test]
    public async Task QueuePasswordChange_NothingQueued_AsksForNothingAsync()
    {
        // No system is configured for Password Synchronisation, so there is nothing to deliver and no reason to
        // put a pass in the Operations queue.
        var metaverseObjectId = Guid.NewGuid();

        await _server.QueuePasswordChangeAsync(
            metaverseObjectId, "Ada Lovelace", "a-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        Assert.That(_deliveryRequests, Is.Empty);
    }

    [Test]
    public async Task ReleaseForDelivery_SomethingReleased_AsksForDeliveryOfThatSystemAsync()
    {
        // Requirement 3's drain: enabling a system must actually deliver what accumulated while it was disabled,
        // not merely mark it deliverable.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));
        await _server.QueuePasswordChangeAsync(
            metaverseObjectId, "Ada Lovelace", "a-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);
        _deliveryRequests.Clear();

        var change = _syncRepository.PendingPasswordChanges.Values.Single();
        change.Status = PendingPasswordChangeStatus.Parked;

        await _server.ReleaseForDeliveryAsync(3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deliveryRequests, Has.Exactly(1).Items);
            Assert.That(_deliveryRequests[0], Is.EqualTo(3), "The trigger knows which system it released work on.");
        }
    }

    [Test]
    public async Task ReleaseForDelivery_NothingReleased_AsksForNothingAsync()
    {
        await _server.ReleaseForDeliveryAsync(3);

        Assert.That(_deliveryRequests, Is.Empty);
    }

    [Test]
    public async Task QueuePasswordChange_QueuesOneChangePerEnabledSystemAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"), Target(4, "HR Portal"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId), Account(4, UserObjectTypeId));

        var result = await _server.QueuePasswordChangeAsync(
            metaverseObjectId, "Ada Lovelace", "a-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Targets, Has.Count.EqualTo(2));
            Assert.That(_syncRepository.PendingPasswordChanges, Has.Count.EqualTo(2));
            Assert.That(_syncRepository.PendingPasswordChanges.Values.Select(c => c.ConnectedSystemId),
                Is.EquivalentTo(new[] { 3, 4 }));
        }
    }

    [Test]
    public async Task QueuePasswordChange_StoresThePasswordEncryptedAsync()
    {
        // The one thing that must never be true of this table: a readable password sitting in it.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));

        await _server.QueuePasswordChangeAsync(
            metaverseObjectId, "Ada Lovelace", "Correct-Horse-Battery-Staple",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        var queued = _syncRepository.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued.EncryptedPassword, Is.Not.EqualTo("Correct-Horse-Battery-Staple"));
            Assert.That(queued.EncryptedPassword, Does.Not.Contain("Battery"));
            Assert.That(_protection.UnprotectPassword(queued.EncryptedPassword), Is.EqualTo("Correct-Horse-Battery-Staple"));
        }
    }

    [Test]
    public async Task QueuePasswordChange_ForASystemTheIdentityHasNoAccountIn_StillQueuesAsync()
    {
        // Resolved Decision 2: a password arriving before provisioning waits rather than failing, bounded by the
        // time to live, so the provisioning race resolves itself.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"));
        ArrangeAccounts(metaverseObjectId);

        var result = await _server.QueuePasswordChangeAsync(
            metaverseObjectId, "Ada Lovelace", "a-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Targets, Has.Count.EqualTo(1));
            Assert.That(result.Targets[0].ConnectedSystemObjectId, Is.Null);
            Assert.That(_syncRepository.PendingPasswordChanges.Values.Single().ConnectedSystemObjectId, Is.Null);
        }
    }

    [Test]
    public async Task QueuePasswordChange_IgnoresAccountsOfAnotherObjectTypeAsync()
    {
        // An identity can hold a Connected System Object of another type in the same system; a password belongs
        // to the account, and picking the wrong object would aim the change at something that is not one.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"));
        ArrangeAccounts(metaverseObjectId, Account(3, GroupObjectTypeId));

        var result = await _server.QueuePasswordChangeAsync(
            metaverseObjectId, "Ada Lovelace", "a-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        Assert.That(result.Targets.Single().ConnectedSystemObjectId, Is.Null,
            "The group object is not this identity's account in that system.");
    }

    [Test]
    public async Task QueuePasswordChange_NeverQueuesForASystemThatIsNotEnabledAsync()
    {
        // The identity has an account there, but nobody switched Password Synchronisation on for it. Queueing
        // anyway would accumulate passwords for a system the administrator never opted in.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId), Account(9, UserObjectTypeId));

        var result = await _server.QueuePasswordChangeAsync(
            metaverseObjectId, "Ada Lovelace", "a-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Targets, Has.Count.EqualTo(1));
            Assert.That(result.Targets[0].ConnectedSystemId, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task QueuePasswordChange_WithNoEnabledSystems_RecordsAnExplicitNoOpAsync()
    {
        // Requirement 14. Silence here would let an administrator believe a password propagated when nothing
        // was even attempted.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));

        var result = await _server.QueuePasswordChangeAsync(
            metaverseObjectId, "Ada Lovelace", "a-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.NoTargets, Is.True);
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
            Assert.That(_createdActivities, Has.Count.EqualTo(1),
                "The change is recorded even though it reached nothing.");
            Assert.That(result.ActivityId, Is.EqualTo(_createdActivities[0].Id));
        }
    }

    [Test]
    public async Task QueuePasswordChange_RecordsTheChangeUnderItsOwnActivityCategoryAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));

        await _server.QueuePasswordChangeAsync(
            metaverseObjectId, "Ada Lovelace", "a-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.PasswordSynchronisation));
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetPassword),
                "The existing operation type is reused rather than a second one introduced.");
            Assert.That(activity.MetaverseObjectId, Is.EqualTo(metaverseObjectId));
            Assert.That(activity.TargetName, Is.EqualTo("Ada Lovelace"));
        }
    }

    [Test]
    public async Task QueuePasswordChange_NeverRecordsThePasswordOnTheActivityAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));

        await _server.QueuePasswordChangeAsync(
            metaverseObjectId, "Ada Lovelace", "Correct-Horse-Battery-Staple",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        var activity = _createdActivities.Single();
        var serialised = $"{activity.TargetName} {activity.Message} {activity.TargetContext}";
        Assert.That(serialised, Does.Not.Contain("Correct-Horse").And.Not.Contain("Battery"));
    }

    [Test]
    public async Task QueuePasswordChange_TwiceForTheSameTarget_CoalescesToOneChangeAsync()
    {
        // Requirement 8, end to end through the server rather than at the repository: the queue holds the
        // latest intended password per target, not a replayable sequence.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));

        await _server.QueuePasswordChangeAsync(metaverseObjectId, "Ada Lovelace", "first-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);
        await _server.QueuePasswordChangeAsync(metaverseObjectId, "Ada Lovelace", "second-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        var queued = _syncRepository.PendingPasswordChanges.Values.Single();
        Assert.That(_protection.UnprotectPassword(queued.EncryptedPassword), Is.EqualTo("second-password"),
            "The newer password supersedes the older one rather than queueing behind it.");
    }

    [Test]
    public async Task QueuePasswordChange_SetsTheExpiryFromTheTargetSystemsTimeToLiveAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var target = Target(3, "Corporate AD");
        target.TimeToLive = TimeSpan.FromDays(30);
        ArrangeTargets(target);
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));

        await _server.QueuePasswordChangeAsync(metaverseObjectId, "Ada Lovelace", "a-password",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, null, CancellationToken.None);

        var queued = _syncRepository.PendingPasswordChanges.Values.Single();
        Assert.That(queued.ExpiresAt - queued.CreatedAt, Is.EqualTo(TimeSpan.FromDays(30)));
    }

    [Test]
    public async Task QueuePasswordChange_CarriesTheExpiryBehaviourOntoTheQueuedChangeAsync()
    {
        // Per change rather than per system: an administrator setting a password on somebody's behalf can
        // require a change at next sign-in, whereas a password the person chose must not.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));

        await _server.QueuePasswordChangeAsync(metaverseObjectId, "Ada Lovelace", "a-password",
            PasswordExpiryBehaviour.RequireChangeAtNextSignIn, null, CancellationToken.None);

        Assert.That(_syncRepository.PendingPasswordChanges.Values.Single().ExpiryBehaviour,
            Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
    }
}
