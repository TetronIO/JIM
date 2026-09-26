// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data;
using JIM.Models.Exceptions;
using JIM.Models.Operations;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Holds a service at start-up until the database server accepts a connection, instead of letting the first
/// database call fail and the service exit. Every JIM service starts through this, so none of them depends on its
/// supervisor starting PostgreSQL first: a pod starts all its containers at once, and an external database can be
/// briefly unreachable when JIM starts.
/// </summary>
/// <remarks>
/// Attempts are retried after 1, 2, 4 and 8 seconds, then every 15 seconds, each failure logged at Information
/// with its attempt number, the time spent so far and the reason. When the budget is spent, one Fatal line is
/// logged and <see cref="Models.Exceptions.DatabaseUnavailableException"/> is thrown, so the service stops and its
/// supervisor starts it again. A failure that waiting cannot fix is thrown by
/// <see cref="IRepository.TryConnectAsync"/> straight away and is not caught here.
/// </remarks>
internal sealed class DatabaseStartupWait
{
    private readonly IRepository _repository;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;

    /// <param name="repository">The repository whose database is waited for.</param>
    /// <param name="utcNow">The clock; null takes <see cref="DateTime.UtcNow"/>. Tests advance it instead of waiting.</param>
    /// <param name="delay">Waits between attempts; null takes <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="logger">Where progress is reported; null takes the ambient Serilog logger.</param>
    public DatabaseStartupWait(IRepository repository, Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, ILogger? logger = null)
    {
        _repository = repository;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
        _logger = logger ?? Log.ForContext<DatabaseStartupWait>();
    }

    /// <summary>
    /// Returns once the database server accepts a connection.
    /// </summary>
    /// <param name="budget">How long to keep trying before giving up.</param>
    /// <param name="whileWaiting">Called before each pause between attempts, so a host can keep its container
    /// health heartbeat fresh while it waits; null for none.</param>
    /// <param name="cancellationToken">Stops the wait when the service is shutting down.</param>
    public async Task WaitAsync(TimeSpan budget, Func<CancellationToken, Task>? whileWaiting, CancellationToken cancellationToken)
    {
        var startedAt = _utcNow();

        for (var attempt = 1; ; attempt++)
        {
            var result = await _repository.TryConnectAsync(cancellationToken);
            var elapsed = _utcNow() - startedAt;

            if (result.IsConnected)
            {
                if (attempt > 1)
                    _logger.Information("Connected to the database after {Attempts} attempts ({ElapsedSeconds}s).",
                        attempt, Seconds(elapsed));
                return;
            }

            var remaining = budget - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                _logger.Fatal("The database was not reachable within {BudgetSeconds}s ({Attempts} attempts); stopping so that the service is restarted. Last error: {Reason:l}",
                    Seconds(budget), attempt, result.FailureReason);
                throw new DatabaseUnavailableException(
                    $"The database was not reachable within {Seconds(budget)}s ({attempt} attempts). Last error: {result.FailureReason}");
            }

            // The last pause is cut to what remains, so the final attempt lands on the budget rather than past it.
            var delay = DelayAfter(attempt) < remaining ? DelayAfter(attempt) : remaining;
            _logger.Information("The database is not reachable yet (attempt {Attempt}, {ElapsedSeconds}s elapsed): {Reason:l}. Retrying in {DelaySeconds}s.",
                attempt, Seconds(elapsed), result.FailureReason, Seconds(delay));

            if (whileWaiting != null)
                await whileWaiting(cancellationToken);

            await _delay(delay, cancellationToken);
        }
    }

    /// <summary>1, 2, 4 and 8 seconds after the first four attempts, then every 15 seconds.</summary>
    private static TimeSpan DelayAfter(int attempt) =>
        attempt <= 4 ? TimeSpan.FromSeconds(1 << (attempt - 1)) : TimeSpan.FromSeconds(15);

    private static long Seconds(TimeSpan duration) => (long)Math.Round(duration.TotalSeconds);
}
