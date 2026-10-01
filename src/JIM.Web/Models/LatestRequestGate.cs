// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Debounces a request made on every keystroke and lets only the newest one answer. Each call waits out the delay
/// (cancelled by the next call, so a burst of typing sends one request), runs its work, and applies the result only if
/// no later call has been made since. That last check is the out-of-order guard: replies are not guaranteed to arrive
/// in the order they were asked, and a slow reply about an earlier text must not paint over the current one.
/// </summary>
/// <remarks>
/// Built for a Blazor Server component's synchronisation context (calls arrive one at a time), but the request counter
/// is read and written atomically, so it does not rely on that. Dispose it with the component: a pending request is
/// cancelled and nothing more is applied.
/// </remarks>
public sealed class LatestRequestGate : IDisposable
{
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _pending;
    private long _latestRequest;
    private bool _disposed;

    /// <param name="delay">How long a request waits for a newer one before it is sent.</param>
    public LatestRequestGate(TimeSpan delay)
    {
        _delay = delay;
    }

    /// <summary>
    /// Waits out the delay, runs <paramref name="work"/>, and hands its result to <paramref name="applyIfLatest"/>
    /// only when this is still the newest request and the gate has not been disposed.
    /// </summary>
    /// <returns>Whether the result was applied.</returns>
    public async Task<bool> RunAsync<T>(Func<CancellationToken, Task<T>> work, Action<T> applyIfLatest)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(applyIfLatest);

        if (_disposed)
            return false;

        var request = Interlocked.Increment(ref _latestRequest);
        var cancellation = new CancellationTokenSource();
        var superseded = Interlocked.Exchange(ref _pending, cancellation);
        superseded?.Cancel();
        superseded?.Dispose();

        try
        {
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, cancellation.Token);

            var result = await work(cancellation.Token);
            if (_disposed || request != Interlocked.Read(ref _latestRequest))
                return false;

            applyIfLatest(result);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Superseded during the delay, or the gate was disposed: there is nothing to apply.
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var pending = Interlocked.Exchange(ref _pending, null);
        pending?.Cancel();
        pending?.Dispose();
    }
}
