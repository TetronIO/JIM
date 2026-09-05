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
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
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
    private PasswordSynchronisationServer _server = null!;

    [SetUp]
    public void SetUp()
    {
        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _protection = new TestCredentialProtection();
        _createdActivities = [];

        _connectedSystemRepository
            .Setup(r => r.GetPasswordSynchronisationTargetsAsync())
            .ReturnsAsync([]);
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync([]);

        _server = new PasswordSynchronisationServer(
            _syncRepository,
            () => _connectedSystemRepository.Object,
            () => new Mock<JIM.Data.Repositories.IActivityRepository>().Object,
            () => _protection,
            // These fixtures never reach a Connector: they exercise queueing and one-change delivery with a
            // Connector handed in directly. Resolving one here would be answering a question they do not ask.
            _ => throw new NotSupportedException("This fixture does not resolve Connectors."),
            (activity, initiatedBy, initiatedByApiKey) =>
            {
                // Mirrors the real Activity server: an Activity attributed to neither a person nor an API key is
                // refused. A fake that accepted one let #1529 through the whole unit suite.
                if (initiatedBy == null && initiatedByApiKey == null)
                    throw new InvalidOperationException(
                        "Activity must be attributed to a security principal. InitiatedByType has not been set.");
                _createdActivities.Add(activity);
                return Task.CompletedTask;
            },
            activity =>
            {
                activity.InitiatedByType = ActivityInitiatorType.System;
                activity.InitiatedByName = "System";
                _createdActivities.Add(activity);
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
    }

    private void ArrangeTargets(params PasswordSynchronisationTarget[] targets) =>
        _connectedSystemRepository
            .Setup(r => r.GetPasswordSynchronisationTargetsAsync())
            .ReturnsAsync(targets.ToList());

    private void ArrangeAccounts(Guid metaverseObjectId, params ConnectedSystemObject[] accounts) =>
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObjectId))
            .ReturnsAsync(accounts.ToList());

    /// <summary>
    /// The administrator these fixtures queue changes as. Production always has a principal (a signed-in
    /// administrator, or the API key an automation authenticated with), and an Activity attributed to neither is
    /// refused by the Activity server, so passing null here would describe a state the application cannot reach.
    /// </summary>
    private static readonly MetaverseObject TestPrincipal = new() { Id = Guid.NewGuid() };

    private static PasswordSynchronisationTarget Target(int connectedSystemId, string name, bool enabled = true) => new()
    {
        ConnectedSystemId = connectedSystemId,
        ConnectedSystemName = name,
        TargetObjectTypeId = UserObjectTypeId,
        Enabled = enabled,
        TimeToLive = TimeSpan.FromDays(7)
    };

    private static ConnectedSystemObject Account(int connectedSystemId, int typeId) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = connectedSystemId,
        TypeId = typeId
    };

    [Test]
    public async Task QueuePasswordChange_LeavesEveryRowDueNowAsync()
    {
        // Nothing here asks for delivery any more (#1635): the rows themselves are what the Password Delivery
        // Service is woken by, so what queueing owes the service is rows that are Pending, unclaimed and due now.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"), Target(4, "HR Portal"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId), Account(4, UserObjectTypeId));

        await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

        var rows = _syncRepository.PendingPasswordChanges.Values.ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Select(r => r.IsDue(DateTime.UtcNow)), Is.All.True);
            Assert.That(rows.Select(r => r.ClaimedBy), Is.All.Null, "Nothing has claimed a change that has only just been queued.");
        }
    }

    [Test]
    public async Task ReleaseForDelivery_SomethingReleased_MakesTheRowDueAgainAsync()
    {
        // Requirement 3's drain: enabling a system must actually deliver what accumulated while it was disabled,
        // not merely mark it deliverable. The row update is what wakes the service, so the row is what to check.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));
        await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

        var change = _syncRepository.PendingPasswordChanges.Values.Single();
        change.Status = PendingPasswordChangeStatus.Parked;
        change.AttemptCount = 3;

        var released = await _server.ReleaseForDeliveryAsync(3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.EqualTo(1));
            Assert.That(change.IsDue(DateTime.UtcNow), Is.True, "Released work is due now, for the service's next wake.");
            Assert.That(change.AttemptCount, Is.Zero);
        }
    }

    [Test]
    public async Task ReleaseForDelivery_NothingParked_ReleasesNothingAndDoesNotThrowAsync()
    {
        // A system switched on after a spell off has nothing parked: what it has is everything queued while it was
        // off, already Pending and already due. The service finds those on its next wake because the system is
        // now among those with work due; nothing here needs to happen for that.
        var released = await _server.ReleaseForDeliveryAsync(3);

        Assert.That(released, Is.Zero);
    }

    [Test]
    public async Task QueuePasswordChange_QueuesOneChangePerEnabledSystemAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"), Target(4, "HR Portal"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId), Account(4, UserObjectTypeId));

        var result = await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

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

        await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "Correct-Horse-Battery-Staple",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

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

        var result = await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

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

        var result = await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

        Assert.That(result.Targets.Single().ConnectedSystemObjectId, Is.Null,
            "The group object is not this identity's account in that system.");
    }

    [Test]
    public async Task QueuePasswordChange_NeverQueuesForASystemNobodyConfiguredAsync()
    {
        // The identity has an account there, but nobody configured Password Synchronisation for it at all.
        // Queueing anyway would accumulate passwords for a system the administrator never opted in to.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD"));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId), Account(9, UserObjectTypeId));

        var result = await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Targets, Has.Count.EqualTo(1));
            Assert.That(result.Targets[0].ConnectedSystemId, Is.EqualTo(3));
        }
    }

    /// <summary>
    /// Requirement 2: configured but switched off accumulates, it does not discard. This is the difference
    /// between an administrator disabling a system for a maintenance window and every password changed during
    /// that window silently never reaching it, which is the exact failure Password Synchronisation exists to
    /// prevent. Requirement 3's drain on enable also has nothing to drain unless the change was queued here.
    /// </summary>
    [Test]
    public async Task QueuePasswordChange_ForAConfiguredButDisabledSystem_StillQueuesAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD", enabled: false));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));

        var result = await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_syncRepository.PendingPasswordChanges, Has.Count.EqualTo(1),
                "a switched-off system accumulates the change rather than losing it");
            Assert.That(result.NoTargets, Is.False);
            Assert.That(result.Targets[0].Enabled, Is.False,
                "the caller is told the change is queued and held, not queued and on its way");
        }
    }

    /// <summary>
    /// The Activity is the durable record, read long after the queue rows are gone, so it has to distinguish
    /// "this system has the password" from "this system will get it when somebody switches it on". Both are
    /// "queued" to the queue and they are entirely different to the person reading the Activity.
    /// </summary>
    [Test]
    public void DescribeQueueOutcome_WithASystemThatIsSwitchedOff_SaysTheChangeIsHeld()
    {
        var message = PasswordSynchronisationServer.DescribeQueueOutcome(
        [
            new PasswordQueueTargetOutcome { ConnectedSystemId = 3, ConnectedSystemName = "Corporate AD", Enabled = true },
            new PasswordQueueTargetOutcome { ConnectedSystemId = 4, ConnectedSystemName = "Contractor LDAP", Enabled = false }
        ], PendingPasswordChangeOrigin.Propagated);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("Corporate AD"));
            Assert.That(message, Does.Contain("Held until Password Synchronisation is enabled on Contractor LDAP"));
        }
    }

    [Test]
    public void DescribeQueueOutcome_WithEverySystemTaking_SaysNothingAboutHolding()
    {
        var message = PasswordSynchronisationServer.DescribeQueueOutcome(
            [new PasswordQueueTargetOutcome { ConnectedSystemId = 3, ConnectedSystemName = "Corporate AD", Enabled = true }],
            PendingPasswordChangeOrigin.Propagated);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("Corporate AD"));
            Assert.That(message, Does.Not.Contain("Held"));
        }
    }

    /// <summary>
    /// Requirement 14, and the wording matters: "configured", not "enabled". Once a switched-off system
    /// accumulates rather than discards, the only case where nothing at all is queued is one where nobody has
    /// configured Password Synchronisation anywhere, and saying "enabled" would send an administrator to the
    /// wrong control.
    /// </summary>
    [Test]
    public void DescribeQueueOutcome_WithNoTargets_SaysNothingWasQueuedAnywhere()
    {
        var message = PasswordSynchronisationServer.DescribeQueueOutcome([], PendingPasswordChangeOrigin.Propagated);

        Assert.That(message, Is.EqualTo(
            "No Connected System is configured for Password Synchronisation, so this password was not queued for delivery anywhere."));
    }

    /// <summary>
    /// An explicit set is never held (#1635, decision D1), so its message names the accounts and says nothing
    /// about switched-off systems, even where one of them is.
    /// </summary>
    [Test]
    public void DescribeQueueOutcome_ForAnExplicitSet_NamesTheAccountsAndNeverSaysHeld()
    {
        var message = PasswordSynchronisationServer.DescribeQueueOutcome(
        [
            new PasswordQueueTargetOutcome { ConnectedSystemId = 3, ConnectedSystemName = "Corporate AD", Enabled = true },
            new PasswordQueueTargetOutcome { ConnectedSystemId = 4, ConnectedSystemName = "Contractor LDAP", Enabled = false }
        ], PendingPasswordChangeOrigin.Explicit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.StartWith("Password set requested for 2 accounts"));
            Assert.That(message, Does.Contain("Corporate AD").And.Contain("Contractor LDAP"));
            Assert.That(message, Does.Not.Contain("Held"));
        }
    }

    /// <summary>
    /// The change is still queued, even where every target is switched off (requirement 2). The alternative is a
    /// special case that decides for itself when delivery is pointless; delivery re-reads each system's enabled
    /// state anyway, which is the one place that judgement belongs.
    /// </summary>
    [Test]
    public async Task QueuePasswordChange_ForADisabledSystem_StillQueuesAPendingRowAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        ArrangeTargets(Target(3, "Corporate AD", enabled: false));
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));

        await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

        // Held rather than special-cased: the row is Pending like any other, and delivery re-reads the system's
        // enabled state, which is the one place that judgement belongs.
        Assert.That(_syncRepository.PendingPasswordChanges.Values.Single().Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
    }

    [Test]
    public async Task QueuePasswordChange_WithNoEnabledSystems_RecordsAnExplicitNoOpAsync()
    {
        // Requirement 14. Silence here would let an administrator believe a password propagated when nothing
        // was even attempted.
        var metaverseObjectId = Guid.NewGuid();
        ArrangeAccounts(metaverseObjectId, Account(3, UserObjectTypeId));

        var result = await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

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

        await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

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

        await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "Correct-Horse-Battery-Staple",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

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

        await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "first-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);
        await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "second-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

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

        await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

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

        await _server.SetPasswordAsync(new SetPasswordRequest
            {
                MetaverseObjectId = metaverseObjectId,
                DisplayName = "Ada Lovelace",
                Password = "a-password",
                ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
                InitiatedBy = TestPrincipal
            }, CancellationToken.None);

        Assert.That(_syncRepository.PendingPasswordChanges.Values.Single().ExpiryBehaviour,
            Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
    }
}
