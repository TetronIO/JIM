// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors;

/// <summary>
/// The cancellation a write already under way is given (#2083): it follows the export run's own cancellation only once a
/// grace period has passed. A Connector stops starting writes as soon as the run is cancelled, but lets the one under way
/// finish so its result is recorded rather than lost; this bounds that wait, so a write that would never finish (a
/// statement blocked on a row lock, a provider's long retry waits) cannot hold the cancellation up for ever.
/// </summary>
internal sealed class InFlightCancellation : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration _registration;

    /// <param name="runCancellation">The export run's cancellation.</param>
    /// <param name="gracePeriod">How long after the run is cancelled a write under way may run on; zero or less for none.</param>
    public InFlightCancellation(CancellationToken runCancellation, TimeSpan gracePeriod)
    {
        GracePeriod = gracePeriod;
        _registration = runCancellation.Register(() =>
        {
            if (gracePeriod <= TimeSpan.Zero)
                _source.Cancel();
            else
                _source.CancelAfter(gracePeriod);
        });
    }

    /// <summary>
    /// How long after the run is cancelled a write under way may run on.
    /// </summary>
    public TimeSpan GracePeriod { get; }

    /// <summary>
    /// The token to give a write once it is started.
    /// </summary>
    public CancellationToken Token => _source.Token;

    /// <summary>
    /// Whether the grace period has run out, so a write cancelled now was abandoned rather than failing on its own.
    /// </summary>
    public bool IsCancellationRequested => _source.IsCancellationRequested;

    public void Dispose()
    {
        _registration.Dispose();
        _source.Dispose();
    }
}
