// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Decides when a list's loading indicator shows: only once a load has been running for longer than a threshold,
/// and then until every overlapping load has finished. Most loads finish well inside the threshold, and an indicator
/// that appeared for each of them would flash on every keystroke and every scroll; a load that does not finish
/// inside it (a search over a very large Connector Space) would otherwise leave the reader looking at stale rows
/// with nothing to say the list is working.
/// </summary>
/// <remarks>
/// Overlapping loads (the virtualiser fetching two windows at once) share one busy period: the threshold is measured
/// from the first of them, and the indicator stays until the last. A load that throws, including one cancelled
/// because the reader moved on, ends its part of the busy period exactly as one that returns does.
/// Not thread-safe: a component tracks its loads on its renderer's synchronisation context, as Blazor runs them.
/// </remarks>
public sealed class DelayedLoadingIndicator : IDisposable
{
    /// <summary>
    /// How long a load may run before the indicator shows. Long enough that an ordinary window read or search
    /// (tens of milliseconds) shows nothing at all, short enough that a reader who has stopped typing is told within
    /// the moment they start to wonder.
    /// </summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMilliseconds(300);

    private readonly Func<CancellationToken, Task> _waitForThreshold;
    private int _loadsInFlight;
    private CancellationTokenSource? _busyPeriod;

    /// <summary>Creates an indicator with the <see cref="DefaultThreshold"/>.</summary>
    public DelayedLoadingIndicator() : this(DefaultThreshold)
    {
    }

    /// <summary>Creates an indicator that shows once a load has run for <paramref name="threshold"/>.</summary>
    public DelayedLoadingIndicator(TimeSpan threshold) : this(token => Task.Delay(threshold, token))
    {
    }

    /// <summary>
    /// Creates an indicator whose threshold is the completion of <paramref name="waitForThreshold"/>, started once
    /// per busy period and cancelled when that period ends. For tests, which pass the threshold by hand.
    /// </summary>
    public DelayedLoadingIndicator(Func<CancellationToken, Task> waitForThreshold)
    {
        _waitForThreshold = waitForThreshold;
    }

    /// <summary>Whether the indicator should be showing now.</summary>
    public bool IsVisible { get; private set; }

    /// <summary>Raised whenever <see cref="IsVisible"/> changes, so the owning component can re-render.</summary>
    public event Action? VisibilityChanged;

    /// <summary>Runs <paramref name="load"/> as part of the current busy period, starting one if none is running.</summary>
    public async Task<T> TrackAsync<T>(Func<Task<T>> load)
    {
        if (_loadsInFlight++ == 0)
            StartBusyPeriod();

        try
        {
            return await load();
        }
        finally
        {
            if (--_loadsInFlight == 0)
                EndBusyPeriod();
        }
    }

    private void StartBusyPeriod()
    {
        var busyPeriod = new CancellationTokenSource();
        _busyPeriod = busyPeriod;
        _ = ShowAfterThresholdAsync(busyPeriod.Token);
    }

    private async Task ShowAfterThresholdAsync(CancellationToken busyPeriodEnded)
    {
        try
        {
            await _waitForThreshold(busyPeriodEnded);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // A wait that ignored its token can still complete after its period ended; only the live period may show.
        if (!busyPeriodEnded.IsCancellationRequested)
            SetVisible(true);
    }

    private void EndBusyPeriod()
    {
        _busyPeriod?.Cancel();
        _busyPeriod?.Dispose();
        _busyPeriod = null;
        SetVisible(false);
    }

    private void SetVisible(bool visible)
    {
        if (IsVisible == visible)
            return;

        IsVisible = visible;
        VisibilityChanged?.Invoke();
    }

    /// <summary>Ends any running busy period's wait. The indicator raises nothing further.</summary>
    public void Dispose()
    {
        VisibilityChanged = null;
        _busyPeriod?.Cancel();
        _busyPeriod?.Dispose();
        _busyPeriod = null;
    }
}
