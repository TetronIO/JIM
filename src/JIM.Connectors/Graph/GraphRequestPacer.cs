// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>
/// Spaces Microsoft Graph writes so JIM stays inside the tenant's write quota and leaves room for every other
/// application in the tenant, and spaces them further apart while Graph reports the quota is under pressure.
/// </summary>
/// <remarks>
/// Microsoft publishes a write quota of 3,000 requests per 150 seconds per application and tenant (20 per second) and
/// 18,000 per 5 minutes per tenant across every application. Directory reads are budgeted separately, in resource
/// units, and are not paced: they are honoured on a 429 instead. Writes are spaced evenly rather than allowed to burst,
/// so a long export never takes a tenant's whole allowance at once.
/// </remarks>
internal sealed class GraphRequestPacer
{
    /// <summary>The application and tenant write quota: 3,000 requests per 150 seconds.</summary>
    public const double WriteQuotaPerSecond = 20;

    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Lock _gate = new();
    private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;
    private double _slowdownFactor = 1;

    /// <param name="writesPerSecond">The steady write rate, above zero and no more than the write quota.</param>
    /// <param name="utcNow">Clock seam, so pacing is testable without waiting.</param>
    /// <param name="delay">Wait seam, likewise.</param>
    public GraphRequestPacer(double writesPerSecond, Func<DateTimeOffset>? utcNow = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (writesPerSecond is <= 0 or > WriteQuotaPerSecond)
            throw new ArgumentOutOfRangeException(nameof(writesPerSecond), $"The write rate must be above 0 and no more than {WriteQuotaPerSecond} per second.");

        WritesPerSecond = writesPerSecond;
        _interval = TimeSpan.FromSeconds(1 / writesPerSecond);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    public double WritesPerSecond { get; }

    /// <summary>How many times further apart writes are spaced than the configured rate; 1 when Graph reports no pressure.</summary>
    public double SlowdownFactor
    {
        get
        {
            lock (_gate)
                return _slowdownFactor;
        }
    }

    /// <summary>
    /// Waits until the quota allows <paramref name="writes"/> more writes, reserving them so a later request waits for
    /// these to be spent. A <c>$batch</c> reserves a slot for each write it carries.
    /// </summary>
    public async Task WaitForWriteSlotsAsync(int writes, CancellationToken cancellationToken)
    {
        if (writes <= 0)
            return;

        TimeSpan wait;
        lock (_gate)
        {
            var now = _utcNow();
            var start = _nextSlot > now ? _nextSlot : now;
            wait = start - now;
            _nextSlot = start + _interval * (writes * _slowdownFactor);
        }

        if (wait > TimeSpan.Zero)
            await _delay(wait, cancellationToken);
    }

    /// <summary>
    /// Takes Graph's <c>x-ms-throttle-limit-percentage</c> from a response. Graph sends it only once more than 80% of a
    /// limit is used (1.0 meaning the limit is reached and throttling has begun), so its absence means the pressure has
    /// passed and the configured rate applies again.
    /// </summary>
    public void ObserveThrottleLimitPercentage(double? limitPercentage)
    {
        var factor = limitPercentage switch
        {
            null or < 0.8 => 1,
            < 1.0 => 2,
            _ => 4
        };

        lock (_gate)
            _slowdownFactor = factor;
    }
}
