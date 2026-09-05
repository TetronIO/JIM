// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Operations;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Writes one service instance's <see cref="ServiceHeartbeat"/>. A host creates one per service at startup and calls
/// <see cref="WriteAsync"/> from the same place it touches its container health-check file, every loop iteration;
/// the writer decides when a write is actually due, so callers never reason about timing.
/// </summary>
/// <remarks>
/// Two promises to the host loop. It is cheap when throttled (a clock read and a comparison, no allocation, no
/// database). And it never throws a database failure into the caller: a heartbeat is telemetry about the loop, and
/// a telemetry failure that stopped the loop would be the outage it exists to report. Cancellation is the exception
/// to that: on shutdown the host is waiting for its loop to stop, so <see cref="OperationCanceledException"/> is
/// allowed through.
/// </remarks>
public sealed class ServiceHeartbeatWriter
{
    /// <summary>
    /// The minimum gap between writes. Matches <see cref="SystemHealthServer.HeartbeatInterval"/>, which the
    /// reader's thresholds are multiples of.
    /// </summary>
    public static readonly TimeSpan Interval = SystemHealthServer.HeartbeatInterval;

    /// <summary>
    /// How old a row for this service must be before this instance's first write removes it. A day comfortably
    /// outlives any stale-heartbeat threshold, so a row is never pruned while a reader could still be judging it,
    /// and the table stays a handful of rows however often the service restarts.
    /// </summary>
    public static readonly TimeSpan PruneAge = TimeSpan.FromHours(24);

    private readonly JimService _service;
    private readonly string _instanceId;
    private readonly string _hostName;
    private readonly string _version;
    private readonly DateTime _startedAt;
    private readonly Func<DateTime> _utcNow;
    private readonly ILogger _logger;

    private DateTime? _lastWriteAt;
    private bool _pruned;
    private int _consecutiveFailures;

    /// <param name="service">The service this instance runs.</param>
    /// <param name="instanceId">Host name plus a short per-process id; the row key together with the service.</param>
    /// <param name="hostName">The machine or container name, as the service reports it.</param>
    /// <param name="version">The JIM version the process runs (<see cref="Utilities.JimVersion.Current"/>).</param>
    /// <param name="startedAt">When the process started (UTC).</param>
    /// <param name="utcNow">The clock; null takes <see cref="DateTime.UtcNow"/>. Tests advance it to cross the interval.</param>
    /// <param name="logger">Where failures are reported; null takes the ambient Serilog logger.</param>
    public ServiceHeartbeatWriter(JimService service, string instanceId, string hostName, string version, DateTime startedAt,
        Func<DateTime>? utcNow = null, ILogger? logger = null)
    {
        _service = service;
        _instanceId = instanceId;
        _hostName = hostName;
        _version = version;
        _startedAt = startedAt;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _logger = logger ?? Log.ForContext<ServiceHeartbeatWriter>();
    }

    /// <summary>
    /// A writer describing the current process: this machine's name, a fresh per-process id, the running JIM
    /// version and now as the start time. What every host wants unless a test is pinning the values.
    /// </summary>
    public static ServiceHeartbeatWriter ForThisProcess(JimService service) =>
        new(service, NewInstanceId(), Environment.MachineName, JimVersion.Current, DateTime.UtcNow);

    /// <summary>
    /// Host name plus eight hex characters of a fresh GUID: enough to tell two processes on one host apart (a
    /// restart, or two Workers side by side) while staying readable on a status card.
    /// </summary>
    public static string NewInstanceId() => $"{Environment.MachineName}-{Guid.NewGuid():N}"[..(Environment.MachineName.Length + 9)];

    public JimService Service => _service;

    public string InstanceId => _instanceId;

    /// <summary>
    /// Records the heartbeat if one is due (at most once per <see cref="Interval"/>); otherwise returns at once.
    /// The first write that reaches the database also prunes this service's rows older than <see cref="PruneAge"/>.
    /// </summary>
    /// <param name="jim">The host loop's application instance, whose repository the row is written through.</param>
    /// <param name="currentWork">What the service is doing, in an administrator's words; null when idle.</param>
    /// <param name="currentWorkStartedAt">When that work began (UTC); null when idle.</param>
    /// <param name="detail">Anything else worth showing beside the state (queue counts, why it is waiting); null for nothing.</param>
    /// <param name="cancellationToken">The host's stopping token.</param>
    public async Task WriteAsync(JimApplication jim, string? currentWork, DateTime? currentWorkStartedAt, string? detail, CancellationToken cancellationToken)
    {
        var now = _utcNow();
        if (_lastWriteAt is { } lastWriteAt && now - lastWriteAt < Interval)
            return;

        // Stamped before the attempt, so a failing database is retried once per interval rather than on every
        // iteration of a loop that may spin many times a second.
        _lastWriteAt = now;
        cancellationToken.ThrowIfCancellationRequested();

        if (!_pruned)
        {
            // Pruned before the first write rather than at construction, because the database may not be reachable
            // (or migrated) when the host constructs the writer. A failed prune is retried on the next write, which
            // costs nothing: the rows it would remove are a day old and can wait.
            try
            {
                var removed = await jim.Repository.System.PruneServiceHeartbeatsAsync(_service, now - PruneAge);
                _pruned = true;
                if (removed > 0)
                    _logger.Information("ServiceHeartbeatWriter: Removed {Count} {Service} heartbeat row(s) older than {PruneAge}.", removed, _service, PruneAge);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deliberately broad (filtered form): see the class remarks. Whatever the database did, the loop
                // this writer reports on must keep running.
                ReportFailure("prune", ex);
            }
        }

        var heartbeat = new ServiceHeartbeat
        {
            Service = _service,
            InstanceId = _instanceId,
            HostName = _hostName,
            Version = _version,
            StartedAt = _startedAt,
            LastSeenAt = now,
            CurrentWork = currentWork,
            CurrentWorkStartedAt = currentWorkStartedAt,
            // Always null for now. No service can yet tell progress from liveness: the Activity model carries
            // ObjectsProcessed but no timestamp that moves with it, and the Worker Task heartbeat moves whether or
            // not the task advances. SystemHealthServer therefore never reaches NoProgress. When a progress
            // timestamp exists, thread it through here and the state lights up with no reader change.
            LastProgressAt = null,
            Detail = detail
        };

        try
        {
            await jim.Repository.System.UpsertServiceHeartbeatAsync(heartbeat);

            if (_consecutiveFailures > 0)
            {
                _logger.Information("ServiceHeartbeatWriter: {Service} heartbeat writes recovered after {Count} consecutive failure(s).", _service, _consecutiveFailures);
                _consecutiveFailures = 0;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad (filtered form): see the class remarks.
            ReportFailure("write", ex);
        }
    }

    /// <summary>
    /// Logs a failed heartbeat operation: the first at Warning, the rest of an unbroken run at Debug. An hour-long
    /// database outage would otherwise be 720 identical warnings per service, and the one that matters is the first.
    /// </summary>
    private void ReportFailure(string operation, Exception ex)
    {
        _consecutiveFailures++;
        if (_consecutiveFailures == 1)
        {
            _logger.Warning(ex,
                "ServiceHeartbeatWriter: {Service} heartbeat {Operation} failed; the service keeps running and retries every {IntervalSeconds} seconds. Further consecutive failures are logged at Debug until a write succeeds.",
                _service, operation, (int)Interval.TotalSeconds);
        }
        else
        {
            _logger.Debug(ex, "ServiceHeartbeatWriter: {Service} heartbeat {Operation} failed again ({Count} consecutive failures).",
                _service, operation, _consecutiveFailures);
        }
    }
}
