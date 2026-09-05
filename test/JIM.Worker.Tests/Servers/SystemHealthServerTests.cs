// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Operations;
using JIM.Utilities;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// State derivation for <see cref="SystemHealthServer.GetServiceHealthAsync"/>: each threshold at and around its
/// boundary, the never-reported case, the no-progress case, the worst-state roll-up, the fixed service order, and
/// the newest instance winning when a service has more than one row.
/// </summary>
[TestFixture]
public class SystemHealthServerTests
{
    private static readonly DateTime AsOf = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISystemRepository> _mockSystemRepository = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSystemRepository = new Mock<ISystemRepository>();
        _mockRepository.Setup(r => r.System).Returns(_mockSystemRepository.Object);
        _jim = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown() => _jim.Dispose();

    private void GivenHeartbeats(params ServiceHeartbeat[] heartbeats) =>
        _mockSystemRepository.Setup(r => r.GetLatestServiceHeartbeatsAsync()).ReturnsAsync(heartbeats.ToList());

    private static ServiceHeartbeat Heartbeat(JimService service, TimeSpan age, string instanceId = "host-a1b2c3",
        string? currentWork = null, DateTime? lastProgressAt = null) => new()
    {
        Id = 1,
        Service = service,
        InstanceId = instanceId,
        HostName = "host",
        Version = "0.15.0",
        StartedAt = AsOf.AddHours(-1),
        LastSeenAt = AsOf - age,
        CurrentWork = currentWork,
        CurrentWorkStartedAt = currentWork == null ? null : AsOf.AddMinutes(-30),
        LastProgressAt = lastProgressAt,
        Detail = "detail"
    };

    private async Task<ServiceHealth> HealthOfAsync(JimService service)
    {
        var report = await _jim.SystemHealth.GetServiceHealthAsync(AsOf);
        return report.Services.Single(s => s.Service == service);
    }

    [Test]
    public async Task GetServiceHealthAsync_NoRows_EveryServiceIsNotSeenAndNeverReported()
    {
        GivenHeartbeats();

        var report = await _jim.SystemHealth.GetServiceHealthAsync(AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Services.Select(s => s.Service), Is.EqualTo(new[] { JimService.WorkerSync, JimService.Scheduler }));
            Assert.That(report.Services.Select(s => s.State), Is.All.EqualTo(ServiceHealthState.NotSeen));
            Assert.That(report.Services.Select(s => s.Reason), Is.All.EqualTo("Never reported"));
            Assert.That(report.Services.Select(s => s.LastSeenAt), Is.All.Null);
            Assert.That(report.Overall, Is.EqualTo(ServiceHealthState.NotSeen));
        }
    }

    [Test]
    public async Task GetServiceHealthAsync_HeartbeatWithinInterval_Running()
    {
        GivenHeartbeats(Heartbeat(JimService.WorkerSync, TimeSpan.FromSeconds(2)));

        var health = await HealthOfAsync(JimService.WorkerSync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(health.State, Is.EqualTo(ServiceHealthState.Running));
            Assert.That(health.Reason, Is.EqualTo("Last seen 2 seconds ago"));
        }
    }

    [Test]
    public async Task GetServiceHealthAsync_HeartbeatAtExactlyThreeIntervals_StillRunning()
    {
        GivenHeartbeats(Heartbeat(JimService.WorkerSync, SystemHealthServer.HeartbeatInterval * 3));

        var health = await HealthOfAsync(JimService.WorkerSync);

        Assert.That(health.State, Is.EqualTo(ServiceHealthState.Running));
    }

    [Test]
    public async Task GetServiceHealthAsync_HeartbeatJustOverThreeIntervals_Stale()
    {
        GivenHeartbeats(Heartbeat(JimService.WorkerSync, SystemHealthServer.HeartbeatInterval * 3 + TimeSpan.FromSeconds(1)));

        var health = await HealthOfAsync(JimService.WorkerSync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(health.State, Is.EqualTo(ServiceHealthState.Stale));
            Assert.That(health.Reason, Is.EqualTo("Last seen 16 seconds ago; expected every 5 seconds"));
        }
    }

    [Test]
    public async Task GetServiceHealthAsync_WorkerHeartbeatJustUnderSixtySeconds_Stale()
    {
        GivenHeartbeats(Heartbeat(JimService.WorkerPasswordDelivery, TimeSpan.FromSeconds(59)));

        var health = await HealthOfAsync(JimService.WorkerPasswordDelivery);

        Assert.That(health.State, Is.EqualTo(ServiceHealthState.Stale));
    }

    [Test]
    public async Task GetServiceHealthAsync_WorkerHeartbeatAtExactlySixtySeconds_NotSeen()
    {
        GivenHeartbeats(Heartbeat(JimService.WorkerSync, TimeSpan.FromSeconds(60)));

        var health = await HealthOfAsync(JimService.WorkerSync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(health.State, Is.EqualTo(ServiceHealthState.NotSeen));
            Assert.That(health.Reason, Is.EqualTo("Last seen 1 minute ago; expected within 60 seconds"));
        }
    }

    [Test]
    public async Task GetServiceHealthAsync_SchedulerHeartbeatAtNinetySeconds_StaleNotNotSeen()
    {
        GivenHeartbeats(Heartbeat(JimService.Scheduler, TimeSpan.FromSeconds(90)));

        var health = await HealthOfAsync(JimService.Scheduler);

        Assert.That(health.State, Is.EqualTo(ServiceHealthState.Stale));
    }

    [Test]
    public async Task GetServiceHealthAsync_SchedulerHeartbeatAtExactlyTwoMinutes_NotSeen()
    {
        GivenHeartbeats(Heartbeat(JimService.Scheduler, TimeSpan.FromSeconds(120)));

        var health = await HealthOfAsync(JimService.Scheduler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(health.State, Is.EqualTo(ServiceHealthState.NotSeen));
            Assert.That(health.Reason, Is.EqualTo("Last seen 2 minutes ago; expected within 120 seconds"));
        }
    }

    [Test]
    public async Task GetServiceHealthAsync_CurrentWorkWithProgressOlderThanTenMinutes_NoProgress()
    {
        GivenHeartbeats(Heartbeat(JimService.WorkerSync, TimeSpan.FromSeconds(2),
            currentWork: "Full Import: Corporate Directory", lastProgressAt: AsOf.AddMinutes(-11)));

        var health = await HealthOfAsync(JimService.WorkerSync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(health.State, Is.EqualTo(ServiceHealthState.NoProgress));
            Assert.That(health.Reason, Is.EqualTo("Full Import: Corporate Directory has made no progress for 11 minutes"));
        }
    }

    [Test]
    public async Task GetServiceHealthAsync_CurrentWorkWithProgressAtExactlyTenMinutes_Running()
    {
        GivenHeartbeats(Heartbeat(JimService.WorkerSync, TimeSpan.FromSeconds(2),
            currentWork: "Full Import: Corporate Directory", lastProgressAt: AsOf.AddMinutes(-10)));

        var health = await HealthOfAsync(JimService.WorkerSync);

        Assert.That(health.State, Is.EqualTo(ServiceHealthState.Running));
    }

    [Test]
    public async Task GetServiceHealthAsync_CurrentWorkWithoutProgressTimestamp_NeverNoProgress()
    {
        // A service that cannot tell progress from liveness leaves LastProgressAt null; judging it "no progress"
        // on the strength of a long-running task would be a false alarm on every big Full Import.
        GivenHeartbeats(Heartbeat(JimService.WorkerSync, TimeSpan.FromSeconds(2),
            currentWork: "Full Import: Corporate Directory", lastProgressAt: null));

        var health = await HealthOfAsync(JimService.WorkerSync);

        Assert.That(health.State, Is.EqualTo(ServiceHealthState.Running));
    }

    [Test]
    public async Task GetServiceHealthAsync_IdleWithOldProgressTimestamp_Running()
    {
        GivenHeartbeats(Heartbeat(JimService.WorkerSync, TimeSpan.FromSeconds(2),
            currentWork: null, lastProgressAt: AsOf.AddHours(-2)));

        var health = await HealthOfAsync(JimService.WorkerSync);

        Assert.That(health.State, Is.EqualTo(ServiceHealthState.Running));
    }

    [Test]
    public async Task GetServiceHealthAsync_NotSeenAndNoProgress_NotSeenWins()
    {
        // A dead process's last words about its work are not a wedged task; they are a dead process.
        GivenHeartbeats(Heartbeat(JimService.WorkerSync, TimeSpan.FromMinutes(5),
            currentWork: "Full Import: Corporate Directory", lastProgressAt: AsOf.AddMinutes(-20)));

        var health = await HealthOfAsync(JimService.WorkerSync);

        Assert.That(health.State, Is.EqualTo(ServiceHealthState.NotSeen));
    }

    [Test]
    public async Task GetServiceHealthAsync_MixedStates_OverallIsTheWorst()
    {
        GivenHeartbeats(
            Heartbeat(JimService.WorkerSync, TimeSpan.FromSeconds(2)),
            Heartbeat(JimService.WorkerPasswordDelivery, TimeSpan.FromSeconds(2),
                currentWork: "Password delivery: Corporate Directory", lastProgressAt: AsOf.AddMinutes(-30)),
            Heartbeat(JimService.Scheduler, TimeSpan.FromSeconds(30)));

        var report = await _jim.SystemHealth.GetServiceHealthAsync(AsOf);

        Assert.That(report.Overall, Is.EqualTo(ServiceHealthState.NoProgress));
    }

    [Test]
    public async Task GetServiceHealthAsync_AllRunning_OverallRunning()
    {
        GivenHeartbeats(
            Heartbeat(JimService.WorkerSync, TimeSpan.FromSeconds(1)),
            Heartbeat(JimService.WorkerPasswordDelivery, TimeSpan.FromSeconds(1)),
            Heartbeat(JimService.Scheduler, TimeSpan.FromSeconds(1)));

        var report = await _jim.SystemHealth.GetServiceHealthAsync(AsOf);

        Assert.That(report.Overall, Is.EqualTo(ServiceHealthState.Running));
    }

    [Test]
    public async Task GetServiceHealthAsync_RowsInAnyOrder_ServicesOrderedWorkerSyncPasswordDeliveryScheduler()
    {
        GivenHeartbeats(
            Heartbeat(JimService.Scheduler, TimeSpan.FromSeconds(1)),
            Heartbeat(JimService.WorkerPasswordDelivery, TimeSpan.FromSeconds(1)),
            Heartbeat(JimService.WorkerSync, TimeSpan.FromSeconds(1)));

        var report = await _jim.SystemHealth.GetServiceHealthAsync(AsOf);

        Assert.That(report.Services.Select(s => s.Service), Is.EqualTo(new[]
        {
            JimService.WorkerSync, JimService.WorkerPasswordDelivery, JimService.Scheduler
        }));
    }

    [Test]
    public async Task GetServiceHealthAsync_UnexpectedServiceHasReported_IncludedAlongsideTheExpectedOnes()
    {
        // Password delivery is not on the expected list until its service exists, but a heartbeat from it is
        // still shown: a report that hid a service which had actually spoken would be the misleading one.
        GivenHeartbeats(Heartbeat(JimService.WorkerPasswordDelivery, TimeSpan.FromSeconds(1)));

        var report = await _jim.SystemHealth.GetServiceHealthAsync(AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Services.Select(s => s.Service), Is.EqualTo(new[]
            {
                JimService.WorkerSync, JimService.WorkerPasswordDelivery, JimService.Scheduler
            }));
            Assert.That(report.Services.Single(s => s.Service == JimService.WorkerPasswordDelivery).State, Is.EqualTo(ServiceHealthState.Running));
            Assert.That(report.Overall, Is.EqualTo(ServiceHealthState.NotSeen));
        }
    }

    [Test]
    public async Task GetServiceHealthAsync_ExpectedServicesOnly_PasswordDeliveryIsNotExpectedYet()
    {
        Assert.That(SystemHealthServer.ExpectedServices, Is.EqualTo(new[] { JimService.WorkerSync, JimService.Scheduler }));
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetServiceHealthAsync_TwoRowsForOneService_NewestInstanceWins()
    {
        GivenHeartbeats(
            Heartbeat(JimService.WorkerSync, TimeSpan.FromMinutes(20), instanceId: "host-old"),
            Heartbeat(JimService.WorkerSync, TimeSpan.FromSeconds(3), instanceId: "host-new"));

        var health = await HealthOfAsync(JimService.WorkerSync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(health.InstanceId, Is.EqualTo("host-new"));
            Assert.That(health.State, Is.EqualTo(ServiceHealthState.Running));
        }
    }

    [Test]
    public async Task GetServiceHealthAsync_Always_CopiesHeartbeatFieldsAndStampsTheReport()
    {
        var heartbeat = Heartbeat(JimService.Scheduler, TimeSpan.FromSeconds(4), currentWork: "Advancing schedules");
        GivenHeartbeats(heartbeat);

        var report = await _jim.SystemHealth.GetServiceHealthAsync(AsOf);
        var health = report.Services.Single(s => s.Service == JimService.Scheduler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.GeneratedAt, Is.EqualTo(AsOf));
            Assert.That(report.WebVersion, Is.EqualTo(JimVersion.Current));
            Assert.That(health.InstanceId, Is.EqualTo(heartbeat.InstanceId));
            Assert.That(health.HostName, Is.EqualTo(heartbeat.HostName));
            Assert.That(health.Version, Is.EqualTo(heartbeat.Version));
            Assert.That(health.StartedAt, Is.EqualTo(heartbeat.StartedAt));
            Assert.That(health.LastSeenAt, Is.EqualTo(heartbeat.LastSeenAt));
            Assert.That(health.CurrentWork, Is.EqualTo(heartbeat.CurrentWork));
            Assert.That(health.CurrentWorkStartedAt, Is.EqualTo(heartbeat.CurrentWorkStartedAt));
            Assert.That(health.Detail, Is.EqualTo(heartbeat.Detail));
        }
    }

    [Test]
    public void Thresholds_AsDocumented()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SystemHealthServer.HeartbeatInterval, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(SystemHealthServer.StaleAfter, Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(SystemHealthServer.NotSeenAfter(JimService.WorkerSync), Is.EqualTo(TimeSpan.FromSeconds(60)));
            Assert.That(SystemHealthServer.NotSeenAfter(JimService.WorkerPasswordDelivery), Is.EqualTo(TimeSpan.FromSeconds(60)));
            Assert.That(SystemHealthServer.NotSeenAfter(JimService.Scheduler), Is.EqualTo(TimeSpan.FromSeconds(120)));
            Assert.That(SystemHealthServer.NoProgressAfter, Is.EqualTo(TimeSpan.FromMinutes(10)));
        }
    }
}
