// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Worker.Tests.Services;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Where one password change stands at each Connected System (#1635): the read a caller waiting on a change polls,
/// merged from the queue rows still carrying the change and the delivery Activities recorded under it. Each state
/// in <see cref="PasswordChangeTargetState"/> is pinned to the row or Activity shape that produces it.
/// </summary>
[TestFixture]
public class PasswordChangeOutcomesTests
{
    private const int CorporateAdId = 3;
    private const int HrPortalId = 4;
    private static readonly DateTime Created = new(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);

    private JIM.InMemoryData.SyncRepository _syncRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private PasswordSynchronisationServer _server = null!;
    private Activity? _changeActivity;
    private List<PasswordSynchronisationEventOutcome> _outcomes = null!;
    private List<PasswordSynchronisationTarget> _targets = null!;
    private Guid _metaverseObjectId;

    [SetUp]
    public void SetUp()
    {
        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _outcomes = [];
        _metaverseObjectId = Guid.NewGuid();
        _changeActivity = new Activity { Id = Guid.NewGuid(), Created = Created, MetaverseObjectId = _metaverseObjectId };
        _targets =
        [
            new PasswordSynchronisationTarget { ConnectedSystemId = CorporateAdId, ConnectedSystemName = "Corporate AD", Enabled = true },
            new PasswordSynchronisationTarget { ConnectedSystemId = HrPortalId, ConnectedSystemName = "HR Portal", Enabled = true }
        ];

        _activityRepository.Setup(r => r.GetActivityAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _changeActivity?.Id == id ? _changeActivity : null);
        _activityRepository.Setup(r => r.GetPasswordSynchronisationOutcomesAsync(It.IsAny<Guid>()))
            .ReturnsAsync(() => _outcomes.ToList());
        _connectedSystemRepository.Setup(r => r.GetPasswordSynchronisationTargetsAsync())
            .ReturnsAsync(() => _targets.ToList());

        _server = new PasswordSynchronisationServer(
            _syncRepository,
            () => _connectedSystemRepository.Object,
            () => _activityRepository.Object,
            () => new TestCredentialProtection(),
            _ => throw new NotSupportedException("This fixture does not resolve Connectors."),
            (_, _, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
    }

    private async Task<PendingPasswordChange> RowAsync(int connectedSystemId, Action<PendingPasswordChange>? adjust = null)
    {
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = _metaverseObjectId,
            ConnectedSystemId = connectedSystemId,
            EncryptedPassword = "$JIMPW$v1$ciphertext",
            CreatedAt = Created,
            ExpiresAt = Created.AddDays(7),
            ActivityId = _changeActivity!.Id
        };
        adjust?.Invoke(change);
        await _syncRepository.QueuePasswordChangesAsync([change]);
        return _syncRepository.PendingPasswordChanges[change.Id];
    }

    private void Outcome(int connectedSystemId, string name, ActivityStatus status, string? message, string? error, DateTime occurredAt) =>
        _outcomes.Add(new PasswordSynchronisationEventOutcome
        {
            ActivityId = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemName = name,
            Status = status,
            Message = message,
            ErrorMessage = error,
            OccurredAt = occurredAt
        });

    private async Task<PasswordChangeTargetOutcome> TargetAsync(int connectedSystemId)
    {
        var outcomes = await _server.GetChangeOutcomesAsync(_changeActivity!.Id);
        return outcomes!.Targets.Single(t => t.ConnectedSystemId == connectedSystemId);
    }

    [Test]
    public async Task GetChangeOutcomesAsync_ActivityDoesNotExist_ReturnsNullAsync()
    {
        var outcomes = await _server.GetChangeOutcomesAsync(Guid.NewGuid());

        Assert.That(outcomes, Is.Null);
    }

    [Test]
    public async Task GetChangeOutcomesAsync_CarriesTheChangesIdentityAndCreationAsync()
    {
        await RowAsync(CorporateAdId);

        var outcomes = await _server.GetChangeOutcomesAsync(_changeActivity!.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes!.ActivityId, Is.EqualTo(_changeActivity.Id));
            Assert.That(outcomes.MetaverseObjectId, Is.EqualTo(_metaverseObjectId));
            Assert.That(outcomes.Created, Is.EqualTo(Created));
        }
    }

    [Test]
    public async Task GetChangeOutcomesAsync_PendingRowNotYetAttempted_IsQueuedAndNotSettledAsync()
    {
        await RowAsync(CorporateAdId);

        var outcomes = await _server.GetChangeOutcomesAsync(_changeActivity!.Id);

        var target = outcomes!.Targets.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.State, Is.EqualTo(PasswordChangeTargetState.Queued));
            Assert.That(target.ConnectedSystemName, Is.EqualTo("Corporate AD"));
            Assert.That(target.AttemptCount, Is.Zero);
            Assert.That(target.Message, Is.Null);
            Assert.That(target.NextAttemptAt, Is.Null);
            Assert.That(outcomes.IsSettled, Is.False, "A caller waiting on this change has something to wait for.");
        }
    }

    [Test]
    public async Task GetChangeOutcomesAsync_PendingRowOnAPausedSystem_IsHeldAndSettledAsync()
    {
        _targets.Single(t => t.ConnectedSystemId == CorporateAdId).Enabled = false;
        await RowAsync(CorporateAdId);

        var outcomes = await _server.GetChangeOutcomesAsync(_changeActivity!.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes!.Targets.Single().State, Is.EqualTo(PasswordChangeTargetState.Held));
            Assert.That(outcomes.IsSettled, Is.True, "Nothing will happen until somebody switches the system on; nobody should be held for it.");
        }
    }

    /// <summary>
    /// Decision D1 (#1635): an administrator's explicit set is delivered on a paused system, so it is queued and
    /// on its way, never held.
    /// </summary>
    [Test]
    public async Task GetChangeOutcomesAsync_ExplicitRowOnAPausedSystem_IsQueuedNotHeldAsync()
    {
        _targets.Single(t => t.ConnectedSystemId == CorporateAdId).Enabled = false;
        await RowAsync(CorporateAdId, r => r.Origin = PendingPasswordChangeOrigin.Explicit);

        var outcomes = await _server.GetChangeOutcomesAsync(_changeActivity!.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes!.Targets.Single().State, Is.EqualTo(PasswordChangeTargetState.Queued));
            Assert.That(outcomes.IsSettled, Is.False, "The Password Delivery Service is about to claim it; a caller can wait for that.");
        }
    }

    [Test]
    public async Task GetChangeOutcomesAsync_ExplicitRowOnAnUnconfiguredSystem_TakesTheNameFromTheSystemAsync()
    {
        _targets.Clear();
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemHeaderAsync(CorporateAdId))
            .ReturnsAsync(new ConnectedSystemHeader { Id = CorporateAdId, Name = "Corporate AD" });
        await RowAsync(CorporateAdId, r => r.Origin = PendingPasswordChangeOrigin.Explicit);

        var target = await TargetAsync(CorporateAdId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.ConnectedSystemName, Is.EqualTo("Corporate AD"));
            Assert.That(target.State, Is.EqualTo(PasswordChangeTargetState.Queued));
        }
    }

    [Test]
    public async Task GetChangeOutcomesAsync_ClaimedRow_IsDeliveringAndNotSettledAsync()
    {
        await RowAsync(CorporateAdId, r => r.Claim("worker-1a2b3c4d", Created.AddSeconds(1)));

        var outcomes = await _server.GetChangeOutcomesAsync(_changeActivity!.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes!.Targets.Single().State, Is.EqualTo(PasswordChangeTargetState.Delivering));
            Assert.That(outcomes.IsSettled, Is.False);
        }
    }

    [Test]
    public async Task GetChangeOutcomesAsync_PendingRowAfterAnAttempt_IsRetryingWithTheNextAttemptAsync()
    {
        var attemptedAt = Created.AddSeconds(2);
        await RowAsync(CorporateAdId, r =>
        {
            r.AttemptCount = 1;
            r.LastAttemptedAt = attemptedAt;
            r.NextRetryAt = attemptedAt.AddMinutes(5);
            r.FailureReason = PasswordSetFailureReason.Transient;
            r.TargetMessage = "Server unavailable";
        });

        var outcomes = await _server.GetChangeOutcomesAsync(_changeActivity!.Id);

        var target = outcomes!.Targets.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.State, Is.EqualTo(PasswordChangeTargetState.Retrying));
            Assert.That(target.NextAttemptAt, Is.EqualTo(attemptedAt.AddMinutes(5)));
            Assert.That(target.Message, Is.EqualTo("Server unavailable"));
            Assert.That(target.OccurredAt, Is.EqualTo(attemptedAt));
            Assert.That(target.AttemptCount, Is.EqualTo(1));
            Assert.That(outcomes.IsSettled, Is.True, "The next attempt is minutes away; a caller is told when, not held.");
        }
    }

    [Test]
    public async Task GetChangeOutcomesAsync_ParkedRow_IsParkedWithTheTargetsWordsAsync()
    {
        await RowAsync(CorporateAdId, r =>
        {
            r.Status = PendingPasswordChangeStatus.Parked;
            r.AttemptCount = 1;
            r.LastAttemptedAt = Created.AddSeconds(2);
            r.FailureReason = PasswordSetFailureReason.PolicyRejection;
            r.TargetMessage = "Password does not meet complexity requirements";
        });

        var target = await TargetAsync(CorporateAdId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.State, Is.EqualTo(PasswordChangeTargetState.Parked));
            Assert.That(target.Message, Is.EqualTo("Password does not meet complexity requirements"));
            Assert.That(target.NextAttemptAt, Is.Null);
        }
    }

    [Test]
    public async Task GetChangeOutcomesAsync_ParkedRowWithoutTargetWords_FallsBackToTheReasonAsync()
    {
        await RowAsync(CorporateAdId, r =>
        {
            r.Status = PendingPasswordChangeStatus.Parked;
            r.FailureReason = PasswordSetFailureReason.UnsupportedOperation;
        });

        var target = await TargetAsync(CorporateAdId);

        Assert.That(target.Message, Is.EqualTo("UnsupportedOperation"));
    }

    [Test]
    public async Task GetChangeOutcomesAsync_ExpiredRow_IsExpiredAsync()
    {
        await RowAsync(CorporateAdId, r => r.Status = PendingPasswordChangeStatus.Expired);

        Assert.That((await TargetAsync(CorporateAdId)).State, Is.EqualTo(PasswordChangeTargetState.Expired));
    }

    [Test]
    public async Task GetChangeOutcomesAsync_CancelledRow_IsCancelledAsync()
    {
        await RowAsync(CorporateAdId, r => r.Status = PendingPasswordChangeStatus.Cancelled);

        Assert.That((await TargetAsync(CorporateAdId)).State, Is.EqualTo(PasswordChangeTargetState.Cancelled));
    }

    [Test]
    public async Task GetChangeOutcomesAsync_NoRowAndASuccessfulOutcome_IsSetAsync()
    {
        // The row is deleted when the password lands; the child Activity is all that says it did.
        var setAt = Created.AddSeconds(1);
        Outcome(CorporateAdId, "Corporate AD", ActivityStatus.Complete, "Password set on Corporate AD.", null, setAt);

        var outcomes = await _server.GetChangeOutcomesAsync(_changeActivity!.Id);

        var target = outcomes!.Targets.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.State, Is.EqualTo(PasswordChangeTargetState.Set));
            Assert.That(target.Message, Is.EqualTo("Password set on Corporate AD."));
            Assert.That(target.OccurredAt, Is.EqualTo(setAt));
            Assert.That(target.AttemptCount, Is.EqualTo(1));
            Assert.That(outcomes.IsSettled, Is.True);
        }
    }

    [Test]
    public async Task GetChangeOutcomesAsync_SetAfterARefusal_CountsEveryAttemptAndReadsTheNewestAsync()
    {
        Outcome(CorporateAdId, "Corporate AD", ActivityStatus.FailedWithError, null, "Server unavailable", Created.AddSeconds(1));
        Outcome(CorporateAdId, "Corporate AD", ActivityStatus.Complete, "Password set on Corporate AD.", null, Created.AddMinutes(5));

        var target = await TargetAsync(CorporateAdId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.State, Is.EqualTo(PasswordChangeTargetState.Set));
            Assert.That(target.AttemptCount, Is.EqualTo(2));
            Assert.That(target.OccurredAt, Is.EqualTo(Created.AddMinutes(5)));
        }
    }

    [Test]
    public async Task GetChangeOutcomesAsync_NoRowAndAFailedOutcome_IsParkedWithTheErrorAsync()
    {
        // A refusal with no row behind it: the row has since been removed by retention after parking, and the last
        // thing known is the refusal.
        Outcome(CorporateAdId, "Corporate AD", ActivityStatus.FailedWithError, null, "Password not set on Corporate AD: Too short.", Created.AddSeconds(1));

        var target = await TargetAsync(CorporateAdId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.State, Is.EqualTo(PasswordChangeTargetState.Parked));
            Assert.That(target.Message, Is.EqualTo("Password not set on Corporate AD: Too short."));
        }
    }

    [Test]
    public async Task GetChangeOutcomesAsync_RowPresent_TheRowWinsOverOlderOutcomesAsync()
    {
        // A row still carrying the change is the authority on what JIM intends next; the Activities describe what
        // already happened.
        Outcome(CorporateAdId, "Corporate AD", ActivityStatus.FailedWithError, null, "Server unavailable", Created.AddSeconds(1));
        await RowAsync(CorporateAdId, r =>
        {
            r.AttemptCount = 1;
            r.LastAttemptedAt = Created.AddSeconds(1);
            r.NextRetryAt = Created.AddMinutes(5);
        });

        Assert.That((await TargetAsync(CorporateAdId)).State, Is.EqualTo(PasswordChangeTargetState.Retrying));
    }

    [Test]
    public async Task GetChangeOutcomesAsync_MixedTargets_IsUnsettledWhileAnyIsQueuedAsync()
    {
        Outcome(CorporateAdId, "Corporate AD", ActivityStatus.Complete, "Password set on Corporate AD.", null, Created.AddSeconds(1));
        await RowAsync(HrPortalId);

        var outcomes = await _server.GetChangeOutcomesAsync(_changeActivity!.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes!.Targets.Select(t => t.State), Is.EqualTo(new[] { PasswordChangeTargetState.Set, PasswordChangeTargetState.Queued }));
            Assert.That(outcomes.IsSettled, Is.False);
        }
    }

    [Test]
    public async Task GetChangeOutcomesAsync_OrdersTargetsByConnectedSystemNameAsync()
    {
        await RowAsync(HrPortalId);
        await RowAsync(CorporateAdId);

        var outcomes = await _server.GetChangeOutcomesAsync(_changeActivity!.Id);

        Assert.That(outcomes!.Targets.Select(t => t.ConnectedSystemName), Is.EqualTo(new[] { "Corporate AD", "HR Portal" }));
    }

    [Test]
    public async Task GetChangeOutcomesAsync_SystemNoLongerConfigured_TakesTheNameFromTheActivityAsync()
    {
        _targets.Clear();
        Outcome(CorporateAdId, "Corporate AD (retired)", ActivityStatus.Complete, "Password set.", null, Created.AddSeconds(1));

        var target = await TargetAsync(CorporateAdId);

        Assert.That(target.ConnectedSystemName, Is.EqualTo("Corporate AD (retired)"));
    }

    [Test]
    public async Task GetChangeOutcomesAsync_RowsOfAnotherChange_AreNotIncludedAsync()
    {
        await RowAsync(CorporateAdId, r => r.ActivityId = Guid.NewGuid());

        var outcomes = await _server.GetChangeOutcomesAsync(_changeActivity!.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes!.Targets, Is.Empty);
            Assert.That(outcomes.IsSettled, Is.True);
        }
    }
}
