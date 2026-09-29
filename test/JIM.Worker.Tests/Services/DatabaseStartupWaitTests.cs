// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Exceptions;
using JIM.Models.Operations;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// The start-up wait every service runs before its first database work: it must return at once when the server is
/// up, retry on a fixed back-off while it is not, give up with one Fatal line when the budget is spent, and never
/// swallow a failure that waiting cannot fix.
/// </summary>
[TestFixture]
public class DatabaseStartupWaitTests
{
    private static readonly DateTime StartedAt = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private const string Reason = "jim.database:5432: Connection refused";

    private Mock<IRepository> _mockRepository = null!;
    private DateTime _now;
    private List<TimeSpan> _delays = null!;
    private RecordingSink _sink = null!;
    private Logger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _now = StartedAt;
        _delays = [];
        _sink = new RecordingSink();
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
    }

    [TearDown]
    public void TearDown() => _logger.Dispose();

    /// <summary>A wait whose delays advance the test clock instead of sleeping, and are recorded.</summary>
    private DatabaseStartupWait NewWait() => new(_mockRepository.Object, () => _now, (delay, _) =>
    {
        _delays.Add(delay);
        _now += delay;
        return Task.CompletedTask;
    }, _logger);

    private void SetUpAttempts(params DatabaseConnectionResult[] results)
    {
        var queue = new Queue<DatabaseConnectionResult>(results);
        _mockRepository.Setup(r => r.TryConnectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
    }

    private static DatabaseConnectionResult[] FailuresThenSuccess(int failures) =>
        [.. Enumerable.Repeat(DatabaseConnectionResult.Failed(Reason), failures), DatabaseConnectionResult.Connected];

    [Test]
    public async Task WaitAsync_DatabaseReachable_ReturnsWithoutWaitingAsync()
    {
        SetUpAttempts(DatabaseConnectionResult.Connected);

        await NewWait().WaitAsync(TimeSpan.FromMinutes(5), null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            _mockRepository.Verify(r => r.TryConnectAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(_delays, Is.Empty);
            Assert.That(_sink.Messages(LogEventLevel.Information), Is.Empty, "a database that is already up is not news");
        }
    }

    [Test]
    public async Task WaitAsync_DatabaseReachableAfterFailures_RetriesOnTheBackOffAsync()
    {
        SetUpAttempts(FailuresThenSuccess(6));

        await NewWait().WaitAsync(TimeSpan.FromMinutes(5), null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            _mockRepository.Verify(r => r.TryConnectAsync(It.IsAny<CancellationToken>()), Times.Exactly(7));
            Assert.That(_delays, Is.EqualTo(new[] { 1, 2, 4, 8, 15, 15 }.Select(s => TimeSpan.FromSeconds(s))));
        }
    }

    [Test]
    public async Task WaitAsync_DatabaseReachableAfterFailures_LogsEachAttemptAndTheConnectionAsync()
    {
        SetUpAttempts(FailuresThenSuccess(2));

        await NewWait().WaitAsync(TimeSpan.FromMinutes(5), null, CancellationToken.None);

        var information = _sink.Messages(LogEventLevel.Information);
        Assert.That(information, Is.EqualTo(new[]
        {
            $"The database is not reachable yet (attempt 1, 0s elapsed): {Reason}. Retrying in 1s.",
            $"The database is not reachable yet (attempt 2, 1s elapsed): {Reason}. Retrying in 2s.",
            "Connected to the database after 3 attempts (3s)."
        }));
    }

    [Test]
    public void WaitAsync_BudgetSpent_LogsFatalAndThrowsDatabaseUnavailable()
    {
        SetUpAttempts(DatabaseConnectionResult.Failed(Reason));

        var ex = Assert.ThrowsAsync<DatabaseUnavailableException>(
            () => NewWait().WaitAsync(TimeSpan.FromSeconds(60), null, CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            // 1 + 2 + 4 + 8 + 15 + 15 + 15 = 60: the last pause is cut to what remains, so the final attempt lands on
            // the budget rather than past it.
            _mockRepository.Verify(r => r.TryConnectAsync(It.IsAny<CancellationToken>()), Times.Exactly(8));
            Assert.That(_delays, Is.EqualTo(new[] { 1, 2, 4, 8, 15, 15, 15 }.Select(s => TimeSpan.FromSeconds(s))));
            Assert.That(ex!.Message, Does.Contain(Reason), "the administrator needs the last error, not just that it gave up");
            Assert.That(_sink.Messages(LogEventLevel.Fatal), Is.EqualTo(new[]
            {
                $"The database was not reachable within 60s (8 attempts); stopping so that the service is restarted. Last error: {Reason}"
            }));
        }
    }

    [Test]
    public void WaitAsync_SlowAttempts_CountTowardsTheBudget()
    {
        // An unreachable host is often not refused but silent, so each attempt runs to the connection timeout. That
        // time is spent budget, or a wait meant to last five minutes would last far longer.
        _mockRepository.Setup(r => r.TryConnectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                _now += TimeSpan.FromSeconds(15);
                return DatabaseConnectionResult.Failed(Reason);
            });

        Assert.ThrowsAsync<DatabaseUnavailableException>(
            () => NewWait().WaitAsync(TimeSpan.FromSeconds(60), null, CancellationToken.None));

        // 15 + 1 + 15 + 2 + 15 + 4 = 52 before the fourth attempt, which ends at 67: past the budget.
        _mockRepository.Verify(r => r.TryConnectAsync(It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Test]
    public async Task WaitAsync_WhileWaiting_CalledBeforeEachPauseAsync()
    {
        SetUpAttempts(FailuresThenSuccess(3));
        var calls = 0;

        await NewWait().WaitAsync(TimeSpan.FromMinutes(5), _ =>
        {
            calls++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.That(calls, Is.EqualTo(3));
    }

    [Test]
    public void WaitAsync_FailureWaitingCannotFix_PropagatesWithoutRetrying()
    {
        // Rejected credentials will not start working in five minutes; the service must stop with the real error.
        _mockRepository.Setup(r => r.TryConnectAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("password authentication failed for user \"jim\""));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => NewWait().WaitAsync(TimeSpan.FromMinutes(5), null, CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            _mockRepository.Verify(r => r.TryConnectAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(_delays, Is.Empty);
        }
    }

    [Test]
    public void WaitAsync_Cancelled_StopsWaiting()
    {
        SetUpAttempts(DatabaseConnectionResult.Failed(Reason));
        using var cts = new CancellationTokenSource();
        var wait = new DatabaseStartupWait(_mockRepository.Object, () => _now, (_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, _logger);

        Assert.That(async () => await wait.WaitAsync(TimeSpan.FromMinutes(5), null, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
        _mockRepository.Verify(r => r.TryConnectAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task WaitForDatabaseAsync_DatabaseReachable_ReturnsAsync()
    {
        // The public entry point hosts call: the wait runs against the application's own repository.
        SetUpAttempts(DatabaseConnectionResult.Connected);
        using var jim = new JimApplication(_mockRepository.Object);

        await jim.WaitForDatabaseAsync(JimApplication.DefaultDatabaseWaitBudget, null, CancellationToken.None);

        _mockRepository.Verify(r => r.TryConnectAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task IsDatabaseReachableAsync_ServerAnswers_ReturnsTrueAsync()
    {
        SetUpAttempts(DatabaseConnectionResult.Connected);
        using var jim = new JimApplication(_mockRepository.Object);

        Assert.That(await jim.IsDatabaseReachableAsync(CancellationToken.None), Is.True);
    }

    [Test]
    public async Task IsDatabaseReachableAsync_ServerDown_ReturnsFalseAsync()
    {
        // The quiet check a secondary loop makes while the main loop's wait does the reporting.
        SetUpAttempts(DatabaseConnectionResult.Failed(Reason));
        using var jim = new JimApplication(_mockRepository.Object);

        Assert.That(await jim.IsDatabaseReachableAsync(CancellationToken.None), Is.False);
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
