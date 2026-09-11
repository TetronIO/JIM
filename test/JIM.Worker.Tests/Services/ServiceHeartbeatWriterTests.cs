// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Operations;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// The heartbeat writer's contract with its host loop: it is called every iteration and must be cheap when
/// throttled, prune its own service's old rows once, and never let a database failure reach the caller.
/// </summary>
[TestFixture]
public class ServiceHeartbeatWriterTests
{
    private static readonly DateTime StartedAt = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISystemRepository> _mockSystemRepository = null!;
    private JimApplication _jim = null!;
    private DateTime _now;
    private RecordingSink _sink = null!;
    private Logger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSystemRepository = new Mock<ISystemRepository>();
        _mockRepository.Setup(r => r.System).Returns(_mockSystemRepository.Object);
        _mockSystemRepository.Setup(r => r.UpsertServiceHeartbeatAsync(It.IsAny<ServiceHeartbeat>())).Returns(Task.CompletedTask);
        _mockSystemRepository.Setup(r => r.PruneServiceHeartbeatsAsync(It.IsAny<JimService>(), It.IsAny<DateTime>())).ReturnsAsync(0);
        _jim = new JimApplication(_mockRepository.Object);
        _now = StartedAt;
        _sink = new RecordingSink();
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        _jim.Dispose();
        _logger.Dispose();
    }

    private ServiceHeartbeatWriter NewWriter(JimService service = JimService.WorkerSync) =>
        new(service, "host-a1b2c3", "host", "0.15.0", StartedAt, () => _now, _logger);

    private Task WriteAsync(ServiceHeartbeatWriter writer, string? currentWork = null, DateTime? currentWorkStartedAt = null, string? detail = null) =>
        writer.WriteAsync(_jim, currentWork, currentWorkStartedAt, detail, CancellationToken.None);

    [Test]
    public async Task WriteAsync_FirstCall_UpsertsEveryField()
    {
        var writer = NewWriter();
        ServiceHeartbeat? written = null;
        _mockSystemRepository.Setup(r => r.UpsertServiceHeartbeatAsync(It.IsAny<ServiceHeartbeat>()))
            .Callback<ServiceHeartbeat>(h => written = h)
            .Returns(Task.CompletedTask);

        await WriteAsync(writer, "Full Import: Corporate Directory", StartedAt.AddMinutes(-2), "1 task in flight");

        Assert.That(written, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(written!.Service, Is.EqualTo(JimService.WorkerSync));
            Assert.That(written.InstanceId, Is.EqualTo("host-a1b2c3"));
            Assert.That(written.HostName, Is.EqualTo("host"));
            Assert.That(written.Version, Is.EqualTo("0.15.0"));
            Assert.That(written.StartedAt, Is.EqualTo(StartedAt));
            Assert.That(written.LastSeenAt, Is.EqualTo(_now));
            Assert.That(written.CurrentWork, Is.EqualTo("Full Import: Corporate Directory"));
            Assert.That(written.CurrentWorkStartedAt, Is.EqualTo(StartedAt.AddMinutes(-2)));
            Assert.That(written.LastProgressAt, Is.Null, "no service can yet tell progress from liveness; see the writer");
            Assert.That(written.Detail, Is.EqualTo("1 task in flight"));
        }
    }

    [Test]
    public async Task WriteAsync_TwoCallsWithinTheInterval_UpsertsOnce()
    {
        var writer = NewWriter();

        await WriteAsync(writer);
        _now = _now.AddSeconds(4);
        await WriteAsync(writer, "Full Import: Corporate Directory");

        _mockSystemRepository.Verify(r => r.UpsertServiceHeartbeatAsync(It.IsAny<ServiceHeartbeat>()), Times.Once);
    }

    [Test]
    public async Task WriteAsync_SecondCallAfterTheInterval_UpsertsAgain()
    {
        var writer = NewWriter();

        await WriteAsync(writer);
        _now = _now.Add(ServiceHeartbeatWriter.Interval);
        await WriteAsync(writer);

        _mockSystemRepository.Verify(r => r.UpsertServiceHeartbeatAsync(It.IsAny<ServiceHeartbeat>()), Times.Exactly(2));
    }

    [Test]
    public async Task WriteAsync_ThrottledCall_DoesNotTouchTheRepository()
    {
        var writer = NewWriter();
        await WriteAsync(writer);
        _mockSystemRepository.Invocations.Clear();

        _now = _now.AddSeconds(1);
        await WriteAsync(writer);

        Assert.That(_mockSystemRepository.Invocations, Is.Empty);
    }

    [Test]
    public async Task WriteAsync_FirstWrite_PrunesThisServicesRowsOlderThanADay()
    {
        var writer = NewWriter(JimService.Scheduler);

        await WriteAsync(writer);

        _mockSystemRepository.Verify(r => r.PruneServiceHeartbeatsAsync(JimService.Scheduler, _now - ServiceHeartbeatWriter.PruneAge), Times.Once);
    }

    [Test]
    public async Task WriteAsync_LaterWrites_DoNotPruneAgain()
    {
        var writer = NewWriter();

        await WriteAsync(writer);
        _now = _now.Add(ServiceHeartbeatWriter.Interval);
        await WriteAsync(writer);
        _now = _now.Add(ServiceHeartbeatWriter.Interval);
        await WriteAsync(writer);

        _mockSystemRepository.Verify(r => r.PruneServiceHeartbeatsAsync(It.IsAny<JimService>(), It.IsAny<DateTime>()), Times.Once);
    }

    [Test]
    public async Task WriteAsync_PruneFails_StillUpsertsAndRetriesThePruneNextTime()
    {
        var writer = NewWriter();
        _mockSystemRepository.SetupSequence(r => r.PruneServiceHeartbeatsAsync(It.IsAny<JimService>(), It.IsAny<DateTime>()))
            .ThrowsAsync(new InvalidOperationException("table missing"))
            .ReturnsAsync(2);

        await WriteAsync(writer);
        _now = _now.Add(ServiceHeartbeatWriter.Interval);
        await WriteAsync(writer);

        using (Assert.EnterMultipleScope())
        {
            _mockSystemRepository.Verify(r => r.UpsertServiceHeartbeatAsync(It.IsAny<ServiceHeartbeat>()), Times.Exactly(2));
            _mockSystemRepository.Verify(r => r.PruneServiceHeartbeatsAsync(It.IsAny<JimService>(), It.IsAny<DateTime>()), Times.Exactly(2));
        }
    }

    [Test]
    public async Task WriteAsync_UpsertThrows_SwallowsAndLogsAWarning()
    {
        var writer = NewWriter();
        _mockSystemRepository.Setup(r => r.UpsertServiceHeartbeatAsync(It.IsAny<ServiceHeartbeat>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        await WriteAsync(writer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sink.Messages(LogEventLevel.Warning), Has.Count.EqualTo(1));
            Assert.That(_sink.Messages(LogEventLevel.Warning)[0], Does.Contain("WorkerSync"));
        }
    }

    [Test]
    public async Task WriteAsync_RepeatedFailures_WarnOnceThenRecoveryIsLogged()
    {
        // A database outage lasting an hour would otherwise produce 720 identical warnings per service; the first
        // one is the news, the rest are noise, and the recovery is news again.
        var writer = NewWriter();
        _mockSystemRepository.SetupSequence(r => r.UpsertServiceHeartbeatAsync(It.IsAny<ServiceHeartbeat>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"))
            .ThrowsAsync(new InvalidOperationException("database unavailable"))
            .Returns(Task.CompletedTask);

        await WriteAsync(writer);
        _now = _now.Add(ServiceHeartbeatWriter.Interval);
        await WriteAsync(writer);
        _now = _now.Add(ServiceHeartbeatWriter.Interval);
        await WriteAsync(writer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sink.Messages(LogEventLevel.Warning), Has.Count.EqualTo(1));
            Assert.That(_sink.Messages(LogEventLevel.Debug), Has.Some.Contains("2 consecutive"));
            Assert.That(_sink.Messages(LogEventLevel.Information), Has.Some.Contains("recovered"));
        }
    }

    [Test]
    public async Task WriteAsync_UpsertThrows_NextIntervalTriesAgain()
    {
        var writer = NewWriter();
        _mockSystemRepository.Setup(r => r.UpsertServiceHeartbeatAsync(It.IsAny<ServiceHeartbeat>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        await WriteAsync(writer);
        _now = _now.Add(ServiceHeartbeatWriter.Interval);
        await WriteAsync(writer);

        _mockSystemRepository.Verify(r => r.UpsertServiceHeartbeatAsync(It.IsAny<ServiceHeartbeat>()), Times.Exactly(2));
    }

    [Test]
    public void WriteAsync_Cancelled_PropagatesCancellation()
    {
        // Shutdown is the one thing the writer must not swallow: the host is waiting for its loop to stop.
        var writer = NewWriter();
        _mockSystemRepository.Setup(r => r.UpsertServiceHeartbeatAsync(It.IsAny<ServiceHeartbeat>()))
            .ThrowsAsync(new OperationCanceledException());

        Assert.ThrowsAsync<OperationCanceledException>(() => writer.WriteAsync(_jim, null, null, null, CancellationToken.None));
    }

    private sealed class RecordingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }

        public List<string> Messages(LogEventLevel level)
        {
            lock (_events)
                return _events.Where(e => e.Level == level).Select(e => e.RenderMessage(CultureInfo.InvariantCulture)).ToList();
        }
    }
}
