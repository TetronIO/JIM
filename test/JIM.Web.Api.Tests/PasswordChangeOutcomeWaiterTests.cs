// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The wait a dialog or a REST caller puts on a queued password change (#1635): it returns the moment every target
/// has settled, woken by the notification relay, and falls back to polling when the relay is silent or down.
/// <para>
/// Driven through a real <see cref="JimApplication"/> over mocked repositories rather than a stubbed reader, so the
/// waiter is exercised with the same read the portal and the REST endpoint use; a row's status flipped in the mock
/// is exactly what the Password Delivery Service does to the queue.
/// </para>
/// </summary>
[TestFixture]
public class PasswordChangeOutcomeWaiterTests
{
    private const int CorporateAdId = 3;

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private Guid _activityId;
    private List<PendingPasswordChange> _rows = null!;
    private int _reads;
    private FakeNotifications _notifications = null!;
    private PasswordChangeOutcomeWaiter _waiter = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _activityId = Guid.NewGuid();
        _reads = 0;
        _rows =
        [
            new PendingPasswordChange
            {
                Id = Guid.NewGuid(),
                ActivityId = _activityId,
                ConnectedSystemId = CorporateAdId,
                MetaverseObjectId = Guid.NewGuid(),
                Status = PendingPasswordChangeStatus.Pending,
                EncryptedPassword = "$JIMPW$v1$ciphertext",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            }
        ];

        var repository = new Mock<IRepository>();
        var activityRepo = new Mock<IActivityRepository>();
        var connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        var syncRepo = new Mock<ISyncRepository>();

        activityRepo.Setup(r => r.GetActivityAsync(_activityId))
            .ReturnsAsync(() => new Activity { Id = _activityId, Created = DateTime.UtcNow, MetaverseObjectId = Guid.NewGuid() });
        activityRepo.Setup(r => r.GetPasswordSynchronisationOutcomesAsync(_activityId)).ReturnsAsync([]);
        connectedSystemRepo.Setup(r => r.GetPasswordSynchronisationTargetsAsync()).ReturnsAsync(
        [
            new PasswordSynchronisationTarget { ConnectedSystemId = CorporateAdId, ConnectedSystemName = "Corporate AD", Enabled = true }
        ]);
        // The rows are the mutable state a test drives: the delivery service parks, expires or deletes them, and
        // the waiter is meant to notice.
        syncRepo.Setup(r => r.GetPasswordChangesByActivityAsync(_activityId))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref _reads);
                return [.. _rows];
            });

        repository.Setup(r => r.Activity).Returns(activityRepo.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystemRepo.Object);

        _application = new JimApplication(repository.Object, syncRepository: syncRepo.Object);
        _notifications = new FakeNotifications();
        _waiter = new PasswordChangeOutcomeWaiter(
            new FakeJimApplicationFactory(_application),
            _notifications,
            _notifications,
            new Mock<ILogger<PasswordChangeOutcomeWaiter>>().Object);
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    /// <summary>
    /// What the delivery service does when a target refuses a password: the row stays, marked Parked.
    /// </summary>
    private void ParkTheRow()
    {
        lock (_rows)
            _rows[0].Status = PendingPasswordChangeStatus.Parked;
    }

    [Test]
    public async Task WaitForOutcomesAsync_AlreadySettled_ReturnsWithoutWaitingAsync()
    {
        ParkTheRow();
        var stopwatch = Stopwatch.StartNew();

        var outcomes = await _waiter.WaitForOutcomesAsync(_activityId, Generous, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes, Is.Not.Null);
            Assert.That(outcomes!.IsSettled, Is.True);
            Assert.That(outcomes.Targets[0].State, Is.EqualTo(PasswordChangeTargetState.Parked));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)),
                "A settled change must be answered from the first read, not after a poll interval.");
            Assert.That(_reads, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task WaitForOutcomesAsync_TargetSettlesAfterANotification_ReturnsOnTheEventAsync()
    {
        // Real-time is available, so the poll runs at its slow safety-net interval; only the event can bring the
        // answer back inside the assertion window.
        _notifications.IsRealTimeAvailable = true;
        var stopwatch = Stopwatch.StartNew();

        var wait = _waiter.WaitForOutcomesAsync(_activityId, Generous, CancellationToken.None);
        await Task.Delay(200);
        ParkTheRow();
        _notifications.RaisePasswordChangeChanged(CorporateAdId);

        var outcomes = await wait;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes!.IsSettled, Is.True);
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)),
                "The notification should have woken the waiter long before the 5 s safety-net poll.");
        }
    }

    [Test]
    public async Task WaitForOutcomesAsync_ActivityProgressForTheChange_WakesTheWaiterAsync()
    {
        _notifications.IsRealTimeAvailable = true;
        var stopwatch = Stopwatch.StartNew();

        var wait = _waiter.WaitForOutcomesAsync(_activityId, Generous, CancellationToken.None);
        await Task.Delay(200);
        ParkTheRow();
        _notifications.RaiseActivityProgressChanged(_activityId);

        var outcomes = await wait;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes!.IsSettled, Is.True);
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
        }
    }

    [Test]
    public async Task WaitForOutcomesAsync_NoEvents_SettlesOnThePollingFallbackAsync()
    {
        // The relay is down, so the waiter must find the answer by itself at the fast poll interval.
        _notifications.IsRealTimeAvailable = false;
        var stopwatch = Stopwatch.StartNew();

        var wait = _waiter.WaitForOutcomesAsync(_activityId, Generous, CancellationToken.None);
        await Task.Delay(200);
        ParkTheRow();

        var outcomes = await wait;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes!.IsSettled, Is.True);
            Assert.That(_reads, Is.GreaterThanOrEqualTo(2), "The answer can only have come from a re-read.");
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)),
                "Without real-time updates the poll runs every second, not every five.");
        }
    }

    [Test]
    public async Task WaitForOutcomesAsync_TimeoutElapses_ReturnsTheLatestUnsettledOutcomesAsync()
    {
        _notifications.IsRealTimeAvailable = false;

        var outcomes = await _waiter.WaitForOutcomesAsync(_activityId, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes, Is.Not.Null, "A change that exists is always described, settled or not.");
            Assert.That(outcomes!.IsSettled, Is.False);
            Assert.That(outcomes.Targets[0].State, Is.EqualTo(PasswordChangeTargetState.Queued));
        }
    }

    [Test]
    public void WaitForOutcomesAsync_CallerCancels_ThrowsRatherThanReturningAnAnswer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(),
            () => _waiter.WaitForOutcomesAsync(_activityId, Generous, cts.Token));
    }

    [Test]
    public async Task WaitForOutcomesAsync_NoSuchChange_ReturnsNullAsync()
    {
        var outcomes = await _waiter.WaitForOutcomesAsync(Guid.NewGuid(), Generous, CancellationToken.None);

        Assert.That(outcomes, Is.Null);
    }

    [Test]
    public async Task WaitForOutcomesAsync_Finished_UnsubscribesFromTheRelayAsync()
    {
        ParkTheRow();

        await _waiter.WaitForOutcomesAsync(_activityId, Generous, CancellationToken.None);
        await _waiter.WaitForOutcomesAsync(_activityId, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_notifications.PasswordChangeSubscribers, Is.Zero,
                "A singleton that leaks a handler per wait grows for the life of the process.");
            Assert.That(_notifications.ActivityProgressSubscribers, Is.Zero);
        }
    }

    private sealed class FakeJimApplicationFactory(JimApplication application) : IJimApplicationFactory
    {
        public JimApplication Create() => application;
    }

    /// <summary>
    /// A relay the test raises by hand, counting subscribers so a leak is visible.
    /// </summary>
    private sealed class FakeNotifications : IUiNotificationService, IPasswordChangeNotifications
    {
        private Action<int>? _passwordChangeChanged;
        private Action<Guid>? _activityProgressChanged;

        public event Action<WorkerTaskChangeNotification>? WorkerTaskChanged;

        public event Action<Guid>? ActivityProgressChanged
        {
            add => _activityProgressChanged += value;
            remove => _activityProgressChanged -= value;
        }

        public event Action<int>? PasswordChangeChanged
        {
            add => _passwordChangeChanged += value;
            remove => _passwordChangeChanged -= value;
        }

        public event Action<bool>? RealTimeAvailabilityChanged;

        public bool IsRealTimeAvailable { get; set; } = true;

        public int PasswordChangeSubscribers => _passwordChangeChanged?.GetInvocationList().Length ?? 0;

        public int ActivityProgressSubscribers => _activityProgressChanged?.GetInvocationList().Length ?? 0;

        public void RaisePasswordChangeChanged(int connectedSystemId) => _passwordChangeChanged?.Invoke(connectedSystemId);

        public void RaiseActivityProgressChanged(Guid activityId) => _activityProgressChanged?.Invoke(activityId);

        // Declared by the interface; referenced here so the compiler does not warn about events never raised.
        public void RaiseOthers()
        {
            WorkerTaskChanged?.Invoke(null!);
            RealTimeAvailabilityChanged?.Invoke(true);
        }
    }
}
